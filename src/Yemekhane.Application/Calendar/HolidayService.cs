using System.Globalization;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Calendar;

public sealed class HolidayService(IHolidayRepository repository)
{
    /// <summary>Bir istekte yazilabilecek en fazla gun (bir yariyil tatili ~16 gun; 62 iki aylik yaz kapanisina yeter).</summary>
    public const int MaxRangeDays = 62;

    private static readonly HashSet<string> TransferBehaviors = ["Delete", "NextBusinessDay", "SpecifiedDate", "Forfeit"];
    private static readonly HashSet<string> ScopeTypes = ["AllSchool", "Class", "Group"];
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public Task<IReadOnlyList<HolidayDetails>> ListAsync(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken = default) =>
        repository.ListAsync(startsOn, endsOn, cancellationToken);

    public async Task<HolidayDetails> CreateAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 200) throw new RequestValidationException("Tatil adı 2-200 karakter olmalıdır.");
        if (!TransferBehaviors.Contains(request.TransferBehavior)) throw new RequestValidationException("Aktarım davranışı geçersiz.");
        if (request.Scopes is null || request.Scopes.Count == 0) throw new RequestValidationException("En az bir tatil kapsamı seçilmelidir.");
        if (request.Scopes.Any(x => !ScopeTypes.Contains(x.ScopeType) || (x.ScopeType != "AllSchool" && !x.ScopeId.HasValue)))
            throw new RequestValidationException("Tatil kapsamı geçersiz.");
        if (request.EndDate is { } end && end < request.Date)
            throw new RequestValidationException("Tatil bitiş tarihi başlangıçtan önce olamaz.");
        if (request.DayCount > MaxRangeDays)
            throw new RequestValidationException($"Bir tatil aralığı en fazla {MaxRangeDays} gün olabilir; daha uzun kapanışı parçalara bölün.");

        // Ayni gun + ayni kapsam ikinci kez yazilmaz: ekranda ust uste "Tatil" rozetleri birikiyor,
        // toplu uygulama ayni gunu iki kez isliyordu. "Tum okul" her kapsami kapsar.
        var existing = await repository.ListAsync(request.Date, request.LastDate, cancellationToken).ConfigureAwait(false);
        var clash = existing.FirstOrDefault(holiday => holiday.Scopes.Any(scope => request.Scopes.Any(wanted => Overlaps(scope, wanted))));
        if (clash is not null)
            throw new EntityConflictException(
                $"{clash.Date.ToString("d MMMM yyyy", Turkish)} tarihinde bu kapsamda zaten tatil var: {clash.Name}. Önce onu silin ya da tarihi değiştirin.");

        return await repository.CreateAsync(request with { Name = name }, cancellationToken).ConfigureAwait(false);
    }

    /// <param name="wholeGroup">Aralikli tatilde tum gunleri; tek gunluk tatilde yalnizca o satiri siler.</param>
    public async Task<int> DeleteAsync(Guid id, bool wholeGroup, CancellationToken cancellationToken = default)
    {
        var deleted = await repository.DeleteAsync(id, wholeGroup, cancellationToken).ConfigureAwait(false);
        if (deleted == 0) throw new EntityNotFoundException("Tatil kaydı bulunamadı; silinmiş olabilir.");
        return deleted;
    }

    private static bool Overlaps(HolidayScopeRequest existing, HolidayScopeRequest wanted) =>
        existing.ScopeType == "AllSchool" || wanted.ScopeType == "AllSchool" ||
        (existing.ScopeType == wanted.ScopeType && existing.ScopeId == wanted.ScopeId);
}
