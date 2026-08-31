namespace Yemekhane.Application.Calendar;

public sealed record HolidayScopeRequest(string ScopeType, Guid? ScopeId = null);
public sealed record CreateHolidayRequest(DateOnly Date, string Name, string HolidayType, string? Description,
    string TransferBehavior, IReadOnlyCollection<HolidayScopeRequest> Scopes);
public sealed record HolidayDetails(Guid Id, DateOnly Date, string Name, string HolidayType, string? Description,
    string TransferBehavior, IReadOnlyCollection<HolidayScopeRequest> Scopes);

public interface IHolidayRepository : ICalendarClosureProvider
{
    Task<HolidayDetails> CreateAsync(CreateHolidayRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<HolidayDetails>> ListAsync(DateOnly startsOn, DateOnly endsOn, CancellationToken cancellationToken);
}
