using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Reports;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Reports;

public sealed class EfReportRepository(YemekhaneDbContext dbContext) : IReportRepository
{
    /// <summary>Turkiye sabit UTC+3'tur (2016'dan beri yaz saati yok); gun kirilimi bu kaydirma ile alinir.</summary>
    private const string IstanbulDayShift = "+3 hours";

    public async Task<ReportResult> QueryAsync(ReportType type, ReportQuery query,
        CancellationToken cancellationToken)
    {
        var filtered = Prepare(type, query);
        var summary = await SummarizeAsync(filtered, cancellationToken);
        var items = await ApplySort(filtered, type, query)
            .Skip((query.Page - 1) * query.PageSize)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken);
        return new ReportResult(items, query.Page, query.PageSize, summary);
    }

    public async IAsyncEnumerable<IReadOnlyList<ReportRow>> StreamBatchesAsync(
        ReportType type,
        ReportQuery query,
        int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var batch = new List<ReportRow>(batchSize);
        await foreach (var row in ApplySort(Prepare(type, query), type, query)
                           .AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            batch.Add(row);
            if (batch.Count != batchSize) continue;
            yield return batch;
            batch = new List<ReportRow>(batchSize);
        }

        if (batch.Count > 0) yield return batch;
    }

    /// <summary>
    /// Filtrelenmis satir kaynagi. Gunluk Kasa'da filtreler (tarih, ogrenci, sinif, durum...)
    /// islem satirlarina uygulanir, SONRA gun + gelir turu + iptal kirilimina gruplanir;
    /// boylece "5A sinifinin gunluk tahsilati" gibi sorular da kasa defteri olarak yanitlanir.
    /// </summary>
    private IQueryable<ReportRow> Prepare(ReportType type, ReportQuery query) =>
        type == ReportType.DailyCash
            ? DailyCash(ApplyFilters(Income(ReportType.DailyCash), ReportType.DailyCash, query))
            : ApplyFilters(Build(type, query), type, query);

    private IQueryable<ReportRow> Build(ReportType type, ReportQuery query) => type switch
    {
        ReportType.DailyAccess => Access(ReportType.DailyAccess, false),
        ReportType.MealEntitlement => Entitlements(),
        ReportType.StudentMealUsage => Usages(ReportType.StudentMealUsage),
        ReportType.ClassMeal => Usages(ReportType.ClassMeal),
        ReportType.DailyCash => Income(ReportType.DailyCash),
        ReportType.Income => Income(ReportType.Income),
        ReportType.Sms => Sms(),
        ReportType.Turnstile => Turnstiles(),
        ReportType.DeniedAccess => Access(ReportType.DeniedAccess, true),
        ReportType.CardMovements => Cards(),
        ReportType.HolidayTransfer => HolidaysAndTransfers(),
        ReportType.StudentList => StudentList(query.IncludeSensitive),
        _ => throw new ArgumentOutOfRangeException(nameof(type))
    };

    /// <summary>
    /// Sicil Listesi: her satir bir ogrenci (silinmisler sorgu filtresiyle dislanir). Sinif/sube/bolum/gorev,
    /// aktif kart ve birincil veli korele alt sorgudur: Guid? ile Guid'i join etmek EF'te kutulanip
    /// cevrilemiyordu (bkz. EfStudentRepository). TC kimlik yalnizca yetkili cagirana yazilir; yetkisizde
    /// sutun SQL'de bile secilmez (CASE WHEN @p), istemciye hic gitmez.
    /// MealCount = aktif mi (1/0): ozet satirinda "Aktif / Pasif" sayisi TotalMeals uzerinden tasinir.
    /// </summary>
    private IQueryable<ReportRow> StudentList(bool includeSensitive) =>
        dbContext.Students.AsNoTracking().Select(student => new ReportRow
        {
            Id = student.Id, Type = ReportType.StudentList, Timestamp = null, SortValue = 0d,
            ReportDate = student.RegisteredOn,
            StudentNo = student.StudentNo, FirstName = student.FirstName, LastName = student.LastName,
            Class = dbContext.Set<SchoolClass>().Where(c => c.Id == student.ClassId).Select(c => c.Name).FirstOrDefault(),
            Section = dbContext.Set<Section>().Where(c => c.Id == student.SectionId).Select(c => c.Name).FirstOrDefault(),
            Department = dbContext.Set<Department>().Where(c => c.Id == student.DepartmentId).Select(c => c.Name).FirstOrDefault(),
            Job = dbContext.Set<Job>().Where(c => c.Id == student.JobId).Select(c => c.Name).FirstOrDefault(),
            CardNo = dbContext.StudentCards.Where(c => c.StudentId == student.Id && c.IsActive)
                .OrderByDescending(c => YemekhaneDbContext.JulianDay(c.ValidFrom)).Select(c => c.CardNumber).FirstOrDefault(),
            ParentName = dbContext.Parents.Where(p => p.StudentId == student.Id && p.IsActive)
                .OrderByDescending(p => p.IsPrimary).Select(p => p.Name).FirstOrDefault(),
            ParentPhone = dbContext.Parents.Where(p => p.StudentId == student.Id && p.IsActive)
                .OrderByDescending(p => p.IsPrimary).Select(p => p.NormalizedPhone).FirstOrDefault(),
            NationalId = includeSensitive ? student.NationalId : null,
            Status = student.IsActive ? "ACTIVE" : "INACTIVE",
            MealCount = student.IsActive ? 1 : 0, AmountCents = 0L,
            MealType = null, Device = null, Decision = null, Description = null
        });

    private IQueryable<ReportRow> Access(ReportType type, bool deniedOnly)
    {
        var logs = dbContext.AccessLogs.AsNoTracking();
        if (deniedOnly) logs = logs.Where(x => x.Decision == "DENY");
        return from log in logs
               join studentValue in dbContext.Students.AsNoTracking() on log.StudentId equals (Guid?)studentValue.Id into studentJoin
               from student in studentJoin.DefaultIfEmpty()
               join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classJoin
               from schoolClass in classJoin.DefaultIfEmpty()
               join sectionValue in dbContext.Set<Section>().AsNoTracking() on student.SectionId equals (Guid?)sectionValue.Id into sectionJoin
               from section in sectionJoin.DefaultIfEmpty()
               join departmentValue in dbContext.Set<Department>().AsNoTracking() on student.DepartmentId equals (Guid?)departmentValue.Id into departmentJoin
               from department in departmentJoin.DefaultIfEmpty()
               join jobValue in dbContext.Set<Job>().AsNoTracking() on student.JobId equals (Guid?)jobValue.Id into jobJoin
               from job in jobJoin.DefaultIfEmpty()
               join mealValue in dbContext.Set<MealType>().AsNoTracking() on log.MealTypeId equals (Guid?)mealValue.Id into mealJoin
               from meal in mealJoin.DefaultIfEmpty()
               join device in dbContext.Devices.AsNoTracking() on log.DeviceId equals device.Id
               select new ReportRow
               {
                   Id = log.Id, Type = type, Timestamp = log.Timestamp,
                   SortValue = YemekhaneDbContext.JulianDay(log.Timestamp), StudentNo = student.StudentNo,
                   CardNo = log.CardNumber, FirstName = student.FirstName, LastName = student.LastName,
                   Class = schoolClass.Name, Department = department.Name, Section = section.Name, Job = job.Name,
                   MealType = meal.Name, Device = device.Name, Decision = log.Decision, Status = log.Reason,
                   Description = log.Direction + " / " + log.ReaderSource,
                   MealCount = log.Decision == "ALLOW" ? 1 : 0, AmountCents = 0L
               };
    }

    private IQueryable<ReportRow> Entitlements() =>
        from item in dbContext.MealEntitlements.AsNoTracking()
        join student in dbContext.Students.AsNoTracking() on item.StudentId equals student.Id
        join meal in dbContext.Set<MealType>().AsNoTracking() on item.MealTypeId equals meal.Id
        join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classJoin
        from schoolClass in classJoin.DefaultIfEmpty()
        join sectionValue in dbContext.Set<Section>().AsNoTracking() on student.SectionId equals (Guid?)sectionValue.Id into sectionJoin
        from section in sectionJoin.DefaultIfEmpty()
        join departmentValue in dbContext.Set<Department>().AsNoTracking() on student.DepartmentId equals (Guid?)departmentValue.Id into departmentJoin
        from department in departmentJoin.DefaultIfEmpty()
        join jobValue in dbContext.Set<Job>().AsNoTracking() on student.JobId equals (Guid?)jobValue.Id into jobJoin
        from job in jobJoin.DefaultIfEmpty()
        select new ReportRow
        {
            Id = item.Id, Type = ReportType.MealEntitlement, ReportDate = item.EntitlementDate,
            StudentNo = student.StudentNo, FirstName = student.FirstName, LastName = student.LastName,
            Class = schoolClass.Name, Department = department.Name, Section = section.Name, Job = job.Name,
            MealType = meal.Name, Decision = null, Status = item.Status, Description = item.Source,
            MealCount = item.Quantity, AmountCents = 0L,
            // EF, projeksiyonda atanmayan uyeyi filtre/siralamada cevirmeyip nesneyi yeniden kuruyor;
            // acikca null atamak sorguyu SQL'e cevrilebilir kiliyor.
            CardNo = null, Device = null
        };

    private IQueryable<ReportRow> Usages(ReportType type) =>
        from usage in dbContext.MealUsages.AsNoTracking()
        join student in dbContext.Students.AsNoTracking() on usage.StudentId equals student.Id
        join meal in dbContext.Set<MealType>().AsNoTracking() on usage.MealTypeId equals meal.Id
        join access in dbContext.AccessLogs.AsNoTracking() on usage.AccessLogId equals access.Id
        join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classJoin
        from schoolClass in classJoin.DefaultIfEmpty()
        join sectionValue in dbContext.Set<Section>().AsNoTracking() on student.SectionId equals (Guid?)sectionValue.Id into sectionJoin
        from section in sectionJoin.DefaultIfEmpty()
        join departmentValue in dbContext.Set<Department>().AsNoTracking() on student.DepartmentId equals (Guid?)departmentValue.Id into departmentJoin
        from department in departmentJoin.DefaultIfEmpty()
        join jobValue in dbContext.Set<Job>().AsNoTracking() on student.JobId equals (Guid?)jobValue.Id into jobJoin
        from job in jobJoin.DefaultIfEmpty()
        select new ReportRow
        {
            Id = usage.Id, Type = type, Timestamp = usage.UsedAt,
            SortValue = YemekhaneDbContext.JulianDay(usage.UsedAt), StudentNo = student.StudentNo,
            CardNo = access.CardNumber, FirstName = student.FirstName, LastName = student.LastName,
            Class = schoolClass.Name, Department = department.Name, Section = section.Name, Job = job.Name,
            MealType = meal.Name, Decision = access.Decision, Status = "USED", MealCount = 1,
            AmountCents = 0L,
            // EF, projeksiyonda atanmayan uyeyi filtre/siralamada cevirmeyip nesneyi yeniden kuruyor;
            // acikca null atamak sorguyu SQL'e cevrilebilir kiliyor.
            Device = null, Description = null
        };

    private IQueryable<ReportRow> Income(ReportType type) =>
        from item in dbContext.Set<IncomeTransaction>().AsNoTracking()
        join incomeType in dbContext.Set<IncomeType>().AsNoTracking() on item.IncomeTypeId equals incomeType.Id
        join studentValue in dbContext.Students.AsNoTracking() on item.StudentId equals (Guid?)studentValue.Id into studentJoin
        from student in studentJoin.DefaultIfEmpty()
        join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classJoin
        from schoolClass in classJoin.DefaultIfEmpty()
        join departmentValue in dbContext.Set<Department>().AsNoTracking() on student.DepartmentId equals (Guid?)departmentValue.Id into departmentJoin
        from department in departmentJoin.DefaultIfEmpty()
        join sectionValue in dbContext.Set<Section>().AsNoTracking() on student.SectionId equals (Guid?)sectionValue.Id into sectionJoin
        from section in sectionJoin.DefaultIfEmpty()
        join jobValue in dbContext.Set<Job>().AsNoTracking() on student.JobId equals (Guid?)jobValue.Id into jobJoin
        from job in jobJoin.DefaultIfEmpty()
        select new ReportRow
        {
            Id = item.Id, Type = type, Timestamp = item.TransactionAt,
            SortValue = YemekhaneDbContext.JulianDay(item.TransactionAt), StudentNo = student.StudentNo,
            CardNo = item.CardNumber, FirstName = student.FirstName, LastName = student.LastName,
            Class = schoolClass.Name, Department = department.Name, Section = section.Name, Job = job.Name,
            Decision = null, Status = item.IsVoided ? "VOIDED" : "ACTIVE",
            // Gelir raporunda aciklama "tur / serbest metin"; Gunluk Kasa yalnizca gelir turunu
            // tasir ki gruplama anahtari olabilsin (serbest metin her islemde farkli olabilir).
            Description = type == ReportType.DailyCash ? incomeType.Name : incomeType.Name + " / " + item.Description,
            MealCount = type == ReportType.DailyCash ? 1 : 0,
            AmountCents = item.IsVoided ? 0L : (long)YemekhaneDbContext.Round((double)item.Amount * 100d),
            // EF, projeksiyonda atanmayan uyeyi filtre/siralamada cevirmeyip nesneyi yeniden kuruyor;
            // acikca null atamak sorguyu SQL'e cevrilebilir kiliyor.
            MealType = null, Device = null
        };

    /// <summary>
    /// Kasa defteri: her satir bir Istanbul gunu x gelir turu x (aktif / iptal) toplamidir.
    /// MealCount islem sayisini, AmountCents o grubun tahsilatini tasir (iptaller 0 TL).
    /// Timestamp bos, ReportDate dolu: ekran ve disa aktarma yalnizca gun gosterir.
    /// </summary>
    private static IQueryable<ReportRow> DailyCash(IQueryable<ReportRow> transactions) =>
        transactions
            .GroupBy(x => new
            {
                Day = YemekhaneDbContext.SqliteDate(x.Timestamp!.Value, IstanbulDayShift),
                IncomeType = x.Description,
                x.Status
            })
            .Select(group => new ReportRow
            {
                Id = group.Min(x => x.Id), Type = ReportType.DailyCash, Timestamp = null,
                SortValue = group.Min(x => x.SortValue), ReportDate = group.Key.Day,
                Description = group.Key.IncomeType, Status = group.Key.Status,
                MealCount = group.Count(), AmountCents = group.Sum(x => x.AmountCents),
                Decision = null, StudentNo = null, CardNo = null, FirstName = null, LastName = null,
                Class = null, Department = null, Section = null, Job = null, MealType = null, Device = null
            });

    private IQueryable<ReportRow> Sms() =>
        from log in dbContext.SmsLogs.AsNoTracking()
        join studentValue in dbContext.Students.AsNoTracking() on log.StudentId equals (Guid?)studentValue.Id into studentJoin
        from student in studentJoin.DefaultIfEmpty()
        select new ReportRow
        {
            Id = log.Id, Type = ReportType.Sms, Timestamp = log.SentAt ?? log.CreatedAt,
            SortValue = YemekhaneDbContext.JulianDay(log.SentAt ?? log.CreatedAt),
            StudentNo = student.StudentNo, FirstName = student.FirstName, LastName = student.LastName,
            Decision = null, Status = log.Status, Description = log.Phone + " / " + log.Message,
            MealCount = 0, AmountCents = 0L,
            // EF, projeksiyonda atanmayan uyeyi filtre/siralamada cevirmeyip nesneyi yeniden kuruyor;
            // acikca null atamak sorguyu SQL'e cevrilebilir kiliyor.
            CardNo = null, Class = null, Department = null, Section = null, Job = null, MealType = null, Device = null
        };

    private IQueryable<ReportRow> Turnstiles() =>
        from turnstile in dbContext.TurnstileEvents.AsNoTracking()
        join device in dbContext.Devices.AsNoTracking() on turnstile.DeviceId equals device.Id
        join accessValue in dbContext.AccessLogs.AsNoTracking() on turnstile.AccessLogId equals (Guid?)accessValue.Id into accessJoin
        from access in accessJoin.DefaultIfEmpty()
        join studentValue in dbContext.Students.AsNoTracking() on access.StudentId equals (Guid?)studentValue.Id into studentJoin
        from student in studentJoin.DefaultIfEmpty()
        select new ReportRow
        {
            Id = turnstile.Id, Type = ReportType.Turnstile, Timestamp = turnstile.Timestamp,
            SortValue = YemekhaneDbContext.JulianDay(turnstile.Timestamp),
            StudentNo = student.StudentNo, CardNo = access.CardNumber, FirstName = student.FirstName,
            LastName = student.LastName, Device = device.Name, Decision = access.Decision,
            // Hata yoksa "OPEN / " gibi bos ayrac birakmamak icin yalnizca komut yazilir.
            Status = turnstile.Result,
            Description = turnstile.Error == null || turnstile.Error == "" ? turnstile.Command : turnstile.Command + " / " + turnstile.Error,
            MealCount = access.Decision == "ALLOW" ? 1 : 0, AmountCents = 0L,
            // EF, projeksiyonda atanmayan uyeyi filtre/siralamada cevirmeyip nesneyi yeniden kuruyor;
            // acikca null atamak sorguyu SQL'e cevrilebilir kiliyor.
            Class = null, Department = null, Section = null, Job = null, MealType = null
        };

    private IQueryable<ReportRow> Cards() =>
        from card in dbContext.StudentCards.AsNoTracking()
        join student in dbContext.Students.AsNoTracking() on card.StudentId equals student.Id
        join classValue in dbContext.Set<SchoolClass>().AsNoTracking() on student.ClassId equals (Guid?)classValue.Id into classJoin
        from schoolClass in classJoin.DefaultIfEmpty()
        join departmentValue in dbContext.Set<Department>().AsNoTracking() on student.DepartmentId equals (Guid?)departmentValue.Id into departmentJoin
        from department in departmentJoin.DefaultIfEmpty()
        join sectionValue in dbContext.Set<Section>().AsNoTracking() on student.SectionId equals (Guid?)sectionValue.Id into sectionJoin
        from section in sectionJoin.DefaultIfEmpty()
        join jobValue in dbContext.Set<Job>().AsNoTracking() on student.JobId equals (Guid?)jobValue.Id into jobJoin
        from job in jobJoin.DefaultIfEmpty()
        select new ReportRow
        {
            Id = card.Id, Type = ReportType.CardMovements, Timestamp = card.ValidFrom,
            SortValue = YemekhaneDbContext.JulianDay(card.ValidFrom),
            StudentNo = student.StudentNo, CardNo = card.CardNumber, FirstName = student.FirstName,
            LastName = student.LastName, Class = schoolClass.Name, Department = department.Name,
            Section = section.Name, Job = job.Name, Decision = null,
            Status = card.IsActive ? "ACTIVE" : "INACTIVE", Description = card.ReplacementReason,
            MealCount = 0, AmountCents = 0L,
            // EF, projeksiyonda atanmayan uyeyi filtre/siralamada cevirmeyip nesneyi yeniden kuruyor;
            // acikca null atamak sorguyu SQL'e cevrilebilir kiliyor.
            MealType = null, Device = null
        };

    private IQueryable<ReportRow> HolidaysAndTransfers()
    {
        var holidays = dbContext.Holidays.AsNoTracking().Select(item => new ReportRow
        {
            Id = item.Id, Type = ReportType.HolidayTransfer, Timestamp = null, SortValue = 0d,
            ReportDate = item.Date, StudentNo = null, CardNo = null, FirstName = null, LastName = null,
            Class = null, Department = null, Section = null, Job = null, MealType = null, Device = null,
            Decision = null, Status = item.HolidayType,
            Description = item.Name + " / " + item.TransferBehavior, MealCount = 0, AmountCents = 0L
        });
        var transfers =
            from item in dbContext.MealTransfers.AsNoTracking()
            join student in dbContext.Students.AsNoTracking() on item.StudentId equals student.Id
            join meal in dbContext.Set<MealType>().AsNoTracking() on item.MealTypeId equals meal.Id
            select new ReportRow
            {
                Id = item.Id, Type = ReportType.HolidayTransfer, Timestamp = null, SortValue = 0d,
                ReportDate = item.OriginalDate, StudentNo = student.StudentNo, CardNo = null,
                FirstName = student.FirstName, LastName = student.LastName, Class = null, Department = null,
                Section = null, Job = null, MealType = meal.Name, Device = null, Decision = null,
                Status = "TRANSFER", Description = item.Reason,
                MealCount = item.Quantity, AmountCents = 0L
            };
        return holidays.Concat(transfers);
    }

    private static IQueryable<ReportRow> ApplyFilters(IQueryable<ReportRow> rows, ReportType type, ReportQuery query)
    {
        // Sicil Listesi'nde tarih filtresi YOK SAYILIR: ekran varsayilan olarak "bugun" gonderir ve
        // kayit tarihine uygulansaydi liste her acilista bos gelirdi. Memurun sorusu "kimler kayitli",
        // "bugun kim kaydoldu" degil; ekran bunu acikca yazar.
        if (type == ReportType.StudentList) query = query with { Start = null, End = null };
        if (query.Start.HasValue)
        {
            var start = query.Start.Value;
            var date = DateOnly.FromDateTime(start.Date);
            var value = ToJulianDay(start);
            rows = UsesDate(type) ? rows.Where(x => x.ReportDate >= date) : rows.Where(x => x.SortValue >= value);
        }
        if (query.End.HasValue)
        {
            var end = query.End.Value;
            var date = DateOnly.FromDateTime(end.Date);
            var value = ToJulianDay(end);
            rows = UsesDate(type) ? rows.Where(x => x.ReportDate <= date) : rows.Where(x => x.SortValue <= value);
        }

        rows = Contains(rows, query.StudentNo, x => x.StudentNo!);
        rows = Contains(rows, query.CardNo, x => x.CardNo!);
        rows = Contains(rows, query.FirstName, x => x.FirstName!);
        rows = Contains(rows, query.LastName, x => x.LastName!);
        rows = Contains(rows, query.Class, x => x.Class!);
        rows = Contains(rows, query.Department, x => x.Department!);
        rows = Contains(rows, query.Section, x => x.Section!);
        rows = Contains(rows, query.Job, x => x.Job!);
        rows = Contains(rows, query.MealType, x => x.MealType!);
        rows = Contains(rows, query.Device, x => x.Device!);
        rows = Contains(rows, query.Decision, x => x.Decision!);
        // Sicil Listesi'nde durum TAM eslesir: "ACTIVE" icerik aramasi "INACTIVE"i de tutuyordu,
        // yani "Aktif" filtresi pasif ogrencileri de listeliyordu. Diger raporlarda durum serbest
        // metindir (orn. "Kart pasif" nedeni) ve parca eslesme dogru davranistir.
        rows = type == ReportType.StudentList
            ? Equals(rows, query.Status, x => x.Status!)
            : Contains(rows, query.Status, x => x.Status!);
        return rows;
    }

    /// <summary>Tam (buyuk/kucuk harf duyarsiz) eslesme; kod degeri tasiyan sutunlar icin.</summary>
    private static IQueryable<ReportRow> Equals(IQueryable<ReportRow> rows, string? value,
        System.Linq.Expressions.Expression<Func<ReportRow, string>> selector)
    {
        if (string.IsNullOrWhiteSpace(value)) return rows;
        var parameter = selector.Parameters[0];
        var body = System.Linq.Expressions.Expression.Equal(selector.Body,
            System.Linq.Expressions.Expression.Constant(value.Trim().ToUpperInvariant()));
        return rows.Where(System.Linq.Expressions.Expression.Lambda<Func<ReportRow, bool>>(body, parameter));
    }

    private static IQueryable<ReportRow> Contains(IQueryable<ReportRow> rows, string? value,
        System.Linq.Expressions.Expression<Func<ReportRow, string>> selector)
    {
        if (string.IsNullOrWhiteSpace(value)) return rows;
        var parameter = selector.Parameters[0];
        var body = System.Linq.Expressions.Expression.AndAlso(
            System.Linq.Expressions.Expression.NotEqual(selector.Body,
                System.Linq.Expressions.Expression.Constant(null, typeof(string))),
            System.Linq.Expressions.Expression.Call(selector.Body, nameof(string.Contains), Type.EmptyTypes,
                System.Linq.Expressions.Expression.Constant(value.Trim())));
        return rows.Where(System.Linq.Expressions.Expression.Lambda<Func<ReportRow, bool>>(body, parameter));
    }

    private static IOrderedQueryable<ReportRow> ApplySort(IQueryable<ReportRow> rows, ReportType type, ReportQuery query)
    {
        var descending = query.Descending;
        IOrderedQueryable<ReportRow> sorted = query.SortBy.ToLowerInvariant() switch
        {
            "studentno" => Order(rows, x => x.StudentNo, descending),
            "cardno" => Order(rows, x => x.CardNo, descending),
            "firstname" => Order(rows, x => x.FirstName, descending),
            "lastname" => Order(rows, x => x.LastName, descending),
            "class" => Order(rows, x => x.Class, descending),
            "department" => Order(rows, x => x.Department, descending),
            "section" => Order(rows, x => x.Section, descending),
            "job" => Order(rows, x => x.Job, descending),
            "mealtype" => Order(rows, x => x.MealType, descending),
            "device" => Order(rows, x => x.Device, descending),
            "decision" => Order(rows, x => x.Decision, descending),
            "status" => Order(rows, x => x.Status, descending),
            "mealcount" => Order(rows, x => x.MealCount, descending),
            "amount" => Order(rows, x => x.AmountCents, descending),
            // Sicil Listesi'nin dogal sirasi sinif > sube > numara ve HER ZAMAN artandir: ReportQuery
            // varsayilani Descending=true (olay raporlarinda "en yeni once" dogru), ama sinif listesi
            // 8C'den 5A'ya dogru basilirsa memur icin okunaksizdir. Ters sirayi kullanici sutun
            // basligina tiklayarak (SortBy="class") isteyebilir.
            _ when type == ReportType.StudentList =>
                rows.OrderBy(x => x.Class).ThenBy(x => x.Section).ThenBy(x => x.StudentNo),
            _ when UsesDate(type) => Order(rows, x => x.ReportDate, descending),
            _ => Order(rows, x => x.SortValue, descending)
        };
        return sorted.ThenBy(x => x.Id);
    }

    private static IOrderedQueryable<ReportRow> Order<TKey>(IQueryable<ReportRow> rows,
        System.Linq.Expressions.Expression<Func<ReportRow, TKey>> key, bool descending) =>
        descending ? rows.OrderByDescending(key) : rows.OrderBy(key);

    private static async Task<ReportSummary> SummarizeAsync(IQueryable<ReportRow> rows,
        CancellationToken cancellationToken)
    {
        var summary = await rows.GroupBy(_ => 1).Select(x => new
        {
            Total = x.Count(),
            Passed = x.Count(value => value.Decision == "ALLOW"),
            Denied = x.Count(value => value.Decision == "DENY"),
            Meals = x.Sum(value => (long)value.MealCount),
            AmountCents = x.Sum(value => value.AmountCents)
        }).SingleOrDefaultAsync(cancellationToken);
        return summary is null
            ? new ReportSummary(0, 0, 0, 0, 0m)
            : new ReportSummary(summary.Total, summary.Passed, summary.Denied, summary.Meals, summary.AmountCents / 100m);
    }

    private static bool UsesDate(ReportType type) =>
        type is ReportType.MealEntitlement or ReportType.HolidayTransfer;

    private static double ToJulianDay(DateTimeOffset value) =>
        value.ToUnixTimeMilliseconds() / 86_400_000d + 2_440_587.5d;
}
