namespace Yemekhane.Application.Leaves;

public sealed record CreateLeaveRequest(Guid StudentId, DateOnly StartsOn, DateOnly EndsOn, string LeaveType,
    string? Description, string EntitlementBehavior, Guid CreatedBy);
public sealed record LeaveDetails(Guid Id, Guid StudentId, DateOnly StartsOn, DateOnly EndsOn, string LeaveType,
    string? Description, string EntitlementBehavior);

public interface ILeaveRepository
{
    Task<LeaveDetails> CreateAndApplyAsync(CreateLeaveRequest request, CancellationToken cancellationToken);
    Task<bool> IsOnLeaveAsync(Guid studentId, DateOnly calendarDate, CancellationToken cancellationToken);
    Task<IReadOnlyList<LeaveDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken);
}
