namespace Yemekhane.Application.Parents;

public sealed record ParentDetails(Guid Id, Guid StudentId, string Name, string Phone, string? Relationship, bool IsPrimary, bool IsActive);
public sealed record SaveParentRequest(string Name, string Phone, string? Relationship = null, bool IsPrimary = true);

public interface IParentRepository
{
    Task<IReadOnlyList<ParentDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken);
    Task<ParentDetails> AddAsync(Guid studentId, SaveParentRequest request, CancellationToken cancellationToken);
    Task<ParentDetails?> UpdateAsync(Guid parentId, SaveParentRequest request, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid parentId, CancellationToken cancellationToken);
}
