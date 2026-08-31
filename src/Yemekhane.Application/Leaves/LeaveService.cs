using Yemekhane.Application.Common;

namespace Yemekhane.Application.Leaves;

public sealed class LeaveService(ILeaveRepository repository)
{
    private static readonly HashSet<string> Behaviors = ["Keep", "Cancel", "NextBusinessDay"];

    public Task<LeaveDetails> CreateAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default)
    {
        if (request.EndsOn < request.StartsOn) throw new RequestValidationException("İzin bitiş tarihi başlangıç tarihinden önce olamaz.");
        if (string.IsNullOrWhiteSpace(request.LeaveType)) throw new RequestValidationException("İzin türü zorunludur.");
        if (!Behaviors.Contains(request.EntitlementBehavior)) throw new RequestValidationException("İzin yemek hakkı davranışı geçersiz.");
        return repository.CreateAndApplyAsync(request with { LeaveType = request.LeaveType.Trim() }, cancellationToken);
    }

    public Task<bool> IsOnLeaveAsync(Guid studentId, DateOnly calendarDate, CancellationToken cancellationToken = default) =>
        repository.IsOnLeaveAsync(studentId, calendarDate, cancellationToken);
    public Task<IReadOnlyList<LeaveDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken = default) => repository.ListAsync(studentId, cancellationToken);
}
