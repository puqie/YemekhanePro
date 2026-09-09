using System.Globalization;
using Yemekhane.Application.Common;
using Yemekhane.Domain.Entities;

namespace Yemekhane.Application.Calendar;

public sealed class CalendarService(ICalendarRepository repository)
{
    private static readonly HashSet<string> ScopeTypes = ["Class", "Group"];
    private static readonly HashSet<string> Behaviors = ["Keep", "Cancel", "NextBusinessDay", "SpecifiedDate", "Forfeit"];

    public Task<IReadOnlyCollection<CalendarScopeOption>> ListScopesAsync(CancellationToken cancellationToken = default) =>
        repository.ListScopesAsync(cancellationToken);

    /// <param name="classKind">
    /// Sinif turu suzgeci ("Normal" / "Anasinifi"); bos birakilirsa herkes sayilir. Mutfaga
    /// verilen gunluk sayi ilkokulundur; anasinifi ayri ucretlendirildigi icin karismamalidir.
    /// </param>
    public Task<MonthlyCalendar> GetMonthAsync(string month, string? scopeType, Guid? scopeId,
        string? classKind = null, CancellationToken cancellationToken = default)
    {
        if (!DateOnly.TryParseExact(month, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            throw new RequestValidationException("Ay yyyy-AA biçiminde olmalıdır.");
        return repository.GetMonthAsync(value, Scope(scopeType, scopeId, classKind), cancellationToken);
    }

    public Task<CalendarDayDetails> GetDayAsync(DateOnly date, string? scopeType, Guid? scopeId,
        string? classKind = null, CancellationToken cancellationToken = default) =>
        repository.GetDayAsync(date, Scope(scopeType, scopeId, classKind), cancellationToken);

    public Task<CalendarExceptionItem> CreateExceptionAsync(CreateScheduleExceptionRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = Scope(request.ScopeType == "AllSchool" ? null : request.ScopeType,
            request.ScopeType == "AllSchool" ? null : request.ScopeId);
        if (string.IsNullOrWhiteSpace(request.ExceptionType) || request.ExceptionType.Length > 100)
            throw new RequestValidationException("İstisna türü zorunludur ve en fazla 100 karakter olabilir.");
        if (!Behaviors.Contains(request.EntitlementBehavior))
            throw new RequestValidationException("Hakediş davranışı geçersiz.");
        if (request.EntitlementBehavior == "SpecifiedDate" && request.TargetDate is null)
            throw new RequestValidationException("Belirli tarih davranışı için hedef tarih zorunludur.");
        return repository.CreateExceptionAsync(request with { ExceptionType = request.ExceptionType.Trim() }, cancellationToken);
    }

    private static CalendarScope? Scope(string? scopeType, Guid? scopeId, string? classKind = null)
    {
        // Normalize bilinmeyen turde null doner; bos istek ile hatali istegi ayirmak icin
        // once bosluk denetlenir.
        string? kind = null;
        if (!string.IsNullOrWhiteSpace(classKind))
            kind = ClassKinds.Normalize(classKind)
                ?? throw new RequestValidationException("Sınıf türü Normal ya da Anasinifi olmalıdır.");
        if (string.IsNullOrWhiteSpace(scopeType))
        {
            if (scopeId.HasValue) throw new RequestValidationException("Kapsam türü olmadan kapsam kimliği kullanılamaz.");
            // Kapsam yok ama TUR suzgeci varsa yine de bir kapsam uretilir: "tüm okul,
            // yalnizca ilkokul" gecerli bir istektir.
            return kind is null ? null : new CalendarScope("AllSchool", null, kind);
        }
        if (!ScopeTypes.Contains(scopeType) || !scopeId.HasValue)
            throw new RequestValidationException("Kapsam Class veya Group olmalı ve kapsam kimliği içermelidir.");
        return new CalendarScope(scopeType, scopeId, kind);
    }
}
