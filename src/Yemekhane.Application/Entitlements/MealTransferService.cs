using Yemekhane.Application.Calendar;
using Yemekhane.Application.Common;

namespace Yemekhane.Application.Entitlements;

public sealed class MealTransferService(IMealTransferRepository repository, BusinessDayService businessDayService)
{
    public Task<IReadOnlyList<MealTransferDetails>> ListAsync(Guid studentId, CancellationToken cancellationToken = default) =>
        repository.ListAsync(studentId, cancellationToken);

    public async Task<MealTransferResult> TransferAsync(TransferMealEntitlementsRequest request, CancellationToken cancellationToken = default)
    {
        var ids = request.EntitlementIds.Distinct().ToArray();
        if (ids.Length == 0) throw new RequestValidationException("Aktarılacak en az bir yemek hakkı seçilmelidir.");
        if (string.IsNullOrWhiteSpace(request.Reason)) throw new RequestValidationException("Aktarım nedeni zorunludur.");
        if (request.TargetMode is not ("NextBusinessDay" or "SpecifiedDate")) throw new RequestValidationException("Aktarım hedef tipi geçersiz.");
        if (request.TargetMode == "SpecifiedDate" && !request.TargetDate.HasValue) throw new RequestValidationException("Hedef tarih zorunludur.");

        var candidates = await repository.GetCandidatesAsync(ids, cancellationToken);
        if (candidates.Count != ids.Length) throw new EntityConflictException("Seçilen haklardan biri bulunamadı, kullanıldı veya daha önce aktarıldı.");
        var commands = new List<EntitlementTransferCommand>(candidates.Count);
        foreach (var source in candidates)
        {
            var target = request.TargetMode == "NextBusinessDay"
                ? await businessDayService.GetNextBusinessDayAsync(source.OriginalDate, request.Scope, cancellationToken)
                : request.TargetDate!.Value;
            if (target <= source.OriginalDate) throw new RequestValidationException("Hedef tarih kaynak tarihten sonra olmalıdır.");
            commands.Add(new EntitlementTransferCommand(source, target));
        }
        return await repository.TransferAsync(commands, request.Reason.Trim(), request.CreatedBy, cancellationToken);
    }
}
