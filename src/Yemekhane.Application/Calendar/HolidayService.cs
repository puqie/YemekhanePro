using Yemekhane.Application.Common;

namespace Yemekhane.Application.Calendar;

public sealed class HolidayService(IHolidayRepository repository)
{
    private static readonly HashSet<string> TransferBehaviors = ["Delete", "NextBusinessDay", "SpecifiedDate", "Forfeit"];
    private static readonly HashSet<string> ScopeTypes = ["AllSchool", "Class", "Group"];

    public Task<IReadOnlyList<HolidayDetails>> ListAsync(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken = default) =>
        repository.ListAsync(startsOn, endsOn, cancellationToken);

    public Task<HolidayDetails> CreateAsync(CreateHolidayRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 200) throw new RequestValidationException("Tatil adı 2-200 karakter olmalıdır.");
        if (!TransferBehaviors.Contains(request.TransferBehavior)) throw new RequestValidationException("Aktarım davranışı geçersiz.");
        if (request.Scopes.Count == 0) throw new RequestValidationException("En az bir tatil kapsamı seçilmelidir.");
        if (request.Scopes.Any(x => !ScopeTypes.Contains(x.ScopeType) || (x.ScopeType != "AllSchool" && !x.ScopeId.HasValue)))
            throw new RequestValidationException("Tatil kapsamı geçersiz.");
        return repository.CreateAsync(request with { Name = name }, cancellationToken);
    }
}
