using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Yemekhane.Application.Cards;
using Yemekhane.Application.Common;
using Yemekhane.Application.Leaves;
using Yemekhane.Application.Organization;
using Yemekhane.Application.Students;
using Yemekhane.Devices.Abstractions;

namespace Yemekhane.Desktop.Services;

public interface IStudentApiClient
{
    Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default);
    Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default);
    Task<StudentDetails> SaveAsync(Guid? id, SaveStudentRequest request, CancellationToken cancellationToken = default);
    Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default);
    Task GiveLeaveAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default);
    Task ReplaceCardAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default);
    /// <summary>
    /// Aktif karti OLMAYAN ogrenciye ilk kartini atar (POST /students/{id}/cards).
    /// Varsayilan govde: baska ekranlarin (SMS gibi) yalnizca arama icin kullandigi
    /// sahte istemciler bu ucu uygulamak zorunda kalmasin; gercek istemci ve ogrenci
    /// ekrani testleri kendi uygulamasini verir.
    /// </summary>
    Task AssignCardAsync(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bu istemci kart atamayı desteklemiyor.");

    /// <summary>
    /// Sicil karti icin tanim listeleri (sinif/sube/bolum/gorev) ve "+" ile hizli ekleme,
    /// fotograf yukle/indir/sil. Varsayilan govdeler: yalnizca arama icin kullanilan sahte
    /// istemciler (SMS ekrani gibi) bu uclari uygulamak zorunda kalmasin.
    /// </summary>
    Task<IReadOnlyList<LookupRecord>> GetLookupsAsync(LookupKind kind, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<LookupRecord>>([]);
    Task<LookupRecord> CreateLookupAsync(LookupKind kind, string name, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bu istemci tanım eklemeyi desteklemiyor.");
    Task<StudentDetails> UploadPhotoAsync(Guid studentId, string fileName, byte[] content, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bu istemci fotoğraf yüklemeyi desteklemiyor.");
    /// <summary>Fotograf yoksa (404) null.</summary>
    Task<byte[]?> DownloadPhotoAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        Task.FromResult<byte[]?>(null);
    Task DeletePhotoAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("Bu istemci fotoğraf silmeyi desteklemiyor.");
}

public sealed class StudentApiClient(HttpClient client, IJwtSession session) : IStudentApiClient
{
    public Task<PagedResult<StudentListItem>> SearchAsync(StudentQuery query, CancellationToken cancellationToken = default) =>
        GetAsync<PagedResult<StudentListItem>>("api/students?" + Query(query), cancellationToken);

    public Task<StudentDetails> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        GetAsync<StudentDetails>($"api/students/{id:D}", cancellationToken);

    public async Task<StudentDetails> SaveAsync(Guid? id, SaveStudentRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(id.HasValue ? HttpMethod.Put : HttpMethod.Post,
            id.HasValue ? $"api/students/{id:D}" : "api/students");
        message.Content = JsonContent.Create(request);
        return await SendAsync<StudentDetails>(message, cancellationToken);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Delete, $"api/students/{id:D}");
        using var response = await client.SendAsync(message, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task<IReadOnlyList<object>> LoadTabAsync(string tab, Guid studentId, CancellationToken cancellationToken = default)
    {
        if (tab == "Balance") return await LoadBalanceTabAsync(studentId, cancellationToken);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var (url, arrayProperty) = tab switch
        {
            "Cards" => ($"api/students/{studentId:D}/cards", (string?)null),
            "Parents" => ($"api/students/{studentId:D}/parents", null),
            "Entitlements" => ($"api/meal-entitlements/student/{studentId:D}?startsOn={today.AddMonths(-1):yyyy-MM-dd}&endsOn={today.AddMonths(1):yyyy-MM-dd}", null),
            "Access History" => ($"api/daily-tracking?studentId={studentId:D}&pageSize=100", "items"),
            "Leaves" => ($"api/leaves/student/{studentId:D}", null),
            "Holiday/Transfer" => ($"api/meal-transfers?studentId={studentId:D}", null),
            "Payments" => ($"api/income/transactions?studentId={studentId:D}&pageSize=100", "items"),
            "SMS History" => ($"api/sms?studentId={studentId:D}&pageSize=100", "items"),
            "Audit" => ($"api/audit-logs?entity=Student&entityId={studentId:D}&pageSize=100", "items"),
            _ => throw new ArgumentOutOfRangeException(nameof(tab))
        };
        var document = await GetAsync<JsonDocument>(url, cancellationToken);
        using (document)
        {
            var root = arrayProperty is null ? document.RootElement : document.RootElement.GetProperty(arrayProperty);
            // Bicimlendirme sekmeye ozeldir: her sekmenin hangi alanlari hangi
            // Turkce etiketle gosterecegi StudentTabFormatter'da tanimlidir.
            return root.EnumerateArray().Select(x => (object)new StudentDetailRow(StudentTabFormatter.Summarize(tab, x))).ToArray();
        }
    }

    /// <summary>
    /// Bakiye sekmesi: yanit bir liste degil, ozet + sayfali hareket listesidir. Ilk satir
    /// buyuk yazilan guncel bakiye (StudentBalanceHeadline), ardindan hareketler gelir.
    /// Hic hareket yoksa bile baslik gosterilir; "Kayıt yok" yerine acik bir aciklama satiri eklenir.
    /// </summary>
    private async Task<IReadOnlyList<object>> LoadBalanceTabAsync(Guid studentId, CancellationToken cancellationToken)
    {
        using var document = await GetAsync<JsonDocument>($"api/students/{studentId:D}/balance?pageSize=100", cancellationToken);
        var root = document.RootElement;
        var rows = new List<object>
        {
            new StudentBalanceHeadline(root.GetProperty("balance").GetDecimal(),
                root.GetProperty("available").GetDecimal(), root.GetProperty("expired").GetDecimal())
        };
        var items = root.GetProperty("entries").GetProperty("items").EnumerateArray()
            .Select(x => (object)new StudentDetailRow(StudentTabFormatter.Summarize("Balance", x))).ToList();
        rows.AddRange(items.Count > 0 ? items : [new StudentDetailRow("Henüz bakiye hareketi yok. Kasa > Bakiye Yükle ile yükleme yapılabilir.")]);
        return rows;
    }

    public async Task GiveLeaveAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Post, "api/leaves");
        message.Content = JsonContent.Create(request);
        using var response = await client.SendAsync(message, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task ReplaceCardAsync(Guid studentId, ReplaceCardRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Post, $"api/students/{studentId:D}/cards/replace");
        message.Content = JsonContent.Create(request);
        using var response = await client.SendAsync(message, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public async Task AssignCardAsync(Guid studentId, AssignCardRequest request, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Post, $"api/students/{studentId:D}/cards");
        message.Content = JsonContent.Create(request);
        using var response = await client.SendAsync(message, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    public Task<IReadOnlyList<LookupRecord>> GetLookupsAsync(LookupKind kind, CancellationToken cancellationToken = default) =>
        GetAsync<IReadOnlyList<LookupRecord>>($"api/organization/{Segment(kind)}/lookups", cancellationToken);

    /// <summary>
    /// Sinif ucu (POST classes) govde olarak DUZ JSON dizge bekler ve ClassRecord dondurur;
    /// diger uc tur {"name":...} alip LookupRecord dondurur. Tanimlar API'si baska bir
    /// ekranin alanidir, burada yalnizca tuketilir.
    /// </summary>
    public async Task<LookupRecord> CreateLookupAsync(LookupKind kind, string name, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Post, $"api/organization/{Segment(kind)}");
        if (kind == LookupKind.Class)
        {
            message.Content = JsonContent.Create(name);
            var created = await SendAsync<ClassRecord>(message, cancellationToken);
            return new LookupRecord(created.Id, created.Name, 0);
        }
        message.Content = JsonContent.Create(new SaveLookupRequest(name));
        return await SendAsync<LookupRecord>(message, cancellationToken);
    }

    public async Task<StudentDetails> UploadPhotoAsync(Guid studentId, string fileName, byte[] content, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Post, $"api/students/{studentId:D}/photo");
        using var multipart = new MultipartFormDataContent();
        var part = new ByteArrayContent(content);
        part.Headers.ContentType = new MediaTypeHeaderValue(fileName.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
        multipart.Add(part, "file", Path.GetFileName(fileName));
        message.Content = multipart;
        return await SendAsync<StudentDetails>(message, cancellationToken);
    }

    public async Task<byte[]?> DownloadPhotoAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Get, $"api/students/{studentId:D}/photo");
        using var response = await client.SendAsync(message, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public async Task DeletePhotoAsync(Guid studentId, CancellationToken cancellationToken = default)
    {
        using var message = Authorized(HttpMethod.Delete, $"api/students/{studentId:D}/photo");
        using var response = await client.SendAsync(message, cancellationToken);
        await EnsureAsync(response, cancellationToken);
    }

    private static string Segment(LookupKind kind) => kind switch
    {
        LookupKind.Class => "classes", LookupKind.Section => "sections", LookupKind.Department => "departments", LookupKind.Job => "jobs",
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private async Task<T> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var message = Authorized(HttpMethod.Get, url);
        return await SendAsync<T>(message, cancellationToken);
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage message, CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(message, cancellationToken);
        await EnsureAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidDataException("Öğrenci API yanıtı boş döndü.");
    }

    /// <summary>
    /// Yazma isteklerinde sunucunun mesajini korur: "Bu ogrenci numarasi zaten
    /// kullaniliyor." gibi bir metin kullaniciya dogrudan gosterilebilmelidir.
    /// </summary>
    private static async Task EnsureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new LoginRequiredException();
        if (!response.IsSuccessStatusCode)
            throw await ApiErrors.ReadAsync(response, cancellationToken);
    }

    private HttpRequestMessage Authorized(HttpMethod method, string url)
    {
        if (!session.IsAuthenticated) throw new LoginRequiredException();
        var message = new HttpRequestMessage(method, url);
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", session.AccessToken);
        return message;
    }

    private static string Query(StudentQuery q)
    {
        var values = new Dictionary<string, string?>
        {
            ["search"] = q.Search, ["studentNo"] = q.StudentNo, ["cardNumber"] = q.CardNumber,
            ["firstName"] = q.FirstName, ["lastName"] = q.LastName, ["classId"] = q.ClassId?.ToString(),
            ["sectionId"] = q.SectionId?.ToString(), ["departmentId"] = q.DepartmentId?.ToString(),
            ["isActive"] = q.IsActive?.ToString(CultureInfo.InvariantCulture), ["page"] = q.Page.ToString(CultureInfo.InvariantCulture),
            ["pageSize"] = q.PageSize.ToString(CultureInfo.InvariantCulture), ["className"] = q.ClassName,
            ["sectionName"] = q.SectionName, ["departmentName"] = q.DepartmentName, ["groupId"] = q.GroupId?.ToString()
        };
        return string.Join("&", values.Where(x => !string.IsNullOrWhiteSpace(x.Value))
            .Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value!)}"));
    }

}

public sealed record StudentDetailRow(string Summary);

/// <summary>
/// Bakiye sekmesinin ilk satiri: guncel bakiye buyuk, altinda kullanilabilir/yanmis ayrimi.
/// Ayri tip: StudentsView bu tipe kendi (buyuk yazili) sablonunu uygular.
/// </summary>
public sealed record StudentBalanceHeadline(decimal Balance, decimal Available, decimal Expired)
{
    private static readonly System.Globalization.CultureInfo Turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
    public string BalanceText => Balance.ToString("C2", Turkish);
    public string DetailText => Expired > 0
        ? $"Kullanılabilir: {Available.ToString("C2", Turkish)} · Süresi dolan: {Expired.ToString("C2", Turkish)}"
        : Balance < 0 ? "Bakiye ekside: iptal edilen bir yükleme daha önce harcanmış." : $"Kullanılabilir: {Available.ToString("C2", Turkish)}";
}

public interface ICardReadEventSource
{
    bool IsAvailable { get; }
    Task<CardReadEvent?> ReadNextAsync(CancellationToken cancellationToken = default);
}

public sealed class DeviceCardReadEventSource(ICardReader? reader) : ICardReadEventSource
{
    public bool IsAvailable => reader?.ConnectionState == DeviceConnectionState.Connected;
    public async Task<CardReadEvent?> ReadNextAsync(CancellationToken cancellationToken = default)
    {
        if (reader is null) return null;
        await foreach (var value in reader.ReadCardsAsync(cancellationToken)) return value;
        return null;
    }
}

public static class JwtPermissions
{
    public static IReadOnlySet<string> Read(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return new HashSet<string>();
        try
        {
            var part = token.Split('.')[1].Replace('-', '+').Replace('_', '/');
            part = part.PadRight(part.Length + (4 - part.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(part));
            if (!json.RootElement.TryGetProperty("permission", out var value)) return new HashSet<string>();
            return value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Select(x => x.GetString()!).ToHashSet(StringComparer.Ordinal)
                : new HashSet<string>([value.GetString()!], StringComparer.Ordinal);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or IndexOutOfRangeException)
        {
            return new HashSet<string>();
        }
    }
}
