using Microsoft.EntityFrameworkCore;
using Yemekhane.Application.Devices;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Infrastructure.Devices;

/// <summary>
/// Kartlarin cihazlara yuklenmesini kart-cihaz cifti bazinda takip eder.
///
/// Tasarim kararı: durum kart basina degil, kart-cihaz cifti basina tutulur. Tek bir bayrak,
/// uc turnikeden ikisine yuklenmis bir karti "yuklendi" gosterirdi; ogrenci ucuncu kapida
/// reddedilirken sistemde hicbir sorun gorunmezdi.
/// </summary>
public sealed class DeviceCardSyncService(YemekhaneDbContext db, TimeProvider clock) : IDeviceCardSyncService
{
    public async Task QueueCardAsync(Guid cardId, CancellationToken cancellationToken)
    {
        var card = await db.StudentCards.AsNoTracking().SingleOrDefaultAsync(x => x.Id == cardId, cancellationToken)
            ?? throw new InvalidOperationException($"Kart bulunamadı: {cardId}");
        var deviceIds = await ActiveCardDeviceIdsAsync(cancellationToken);
        var existing = await db.DeviceCardStates.Where(x => x.CardId == cardId).ToListAsync(cancellationToken);

        foreach (var deviceId in deviceIds)
        {
            var state = existing.SingleOrDefault(x => x.DeviceId == deviceId);
            if (state is null)
            {
                db.Add(new DeviceCardState
                {
                    DeviceId = deviceId, CardId = cardId, StudentId = card.StudentId,
                    CardNumber = card.CardNumber, Status = DeviceCardSyncStatus.Pending
                });
                continue;
            }

            // Zaten yuklenmis bir kart yeniden kuyruga alinmaz; aksi halde her cagri
            // cihaza gereksiz yazma trafigi uretirdi.
            if (state.Status is DeviceCardSyncStatus.Loaded) continue;
            state.Status = DeviceCardSyncStatus.Pending;
            state.CardNumber = card.CardNumber;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task QueueRemovalAsync(Guid cardId, CancellationToken cancellationToken)
    {
        var states = await db.DeviceCardStates.Where(x => x.CardId == cardId).ToListAsync(cancellationToken);
        foreach (var state in states)
        {
            // Cihaza hic ulasmamis bir kart icin silme gondermeye gerek yoktur.
            state.Status = state.Status == DeviceCardSyncStatus.Pending
                ? DeviceCardSyncStatus.Removed
                : DeviceCardSyncStatus.PendingRemoval;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<PendingDeviceCard>> GetPendingAsync(Guid deviceId, int limit,
        CancellationToken cancellationToken) =>
        await (from state in db.DeviceCardStates.AsNoTracking()
               join student in db.Students.IgnoreQueryFilters().AsNoTracking()
                   on state.StudentId equals student.Id
               where state.DeviceId == deviceId
                   && (state.Status == DeviceCardSyncStatus.Pending || state.Status == DeviceCardSyncStatus.PendingRemoval)
               orderby state.AttemptCount, state.CardNumber
               select new PendingDeviceCard(state.CardId, state.StudentId, state.CardNumber,
                   student.FirstName + " " + student.LastName,
                   state.Status == DeviceCardSyncStatus.PendingRemoval, state.AttemptCount))
            .Take(limit)
            .ToListAsync(cancellationToken);

    public Task MarkLoadedAsync(Guid deviceId, Guid cardId, CancellationToken cancellationToken) =>
        UpdateAsync(deviceId, cardId, state =>
        {
            state.Status = DeviceCardSyncStatus.Loaded;
            state.LastSyncedAt = clock.GetUtcNow();
            state.AttemptCount = 0;
            state.LastError = null;
        }, cancellationToken);

    public Task MarkRemovedAsync(Guid deviceId, Guid cardId, CancellationToken cancellationToken) =>
        UpdateAsync(deviceId, cardId, state =>
        {
            state.Status = DeviceCardSyncStatus.Removed;
            state.LastSyncedAt = clock.GetUtcNow();
            state.AttemptCount = 0;
            state.LastError = null;
        }, cancellationToken);

    public Task MarkFailedAsync(Guid deviceId, Guid cardId, string failure, bool isPermanent,
        CancellationToken cancellationToken) =>
        UpdateAsync(deviceId, cardId, state =>
        {
            state.AttemptCount++;
            state.LastError = failure;
            // Gecici hata bekleyen durumda kalir ve yeniden denenir; kalici hata denenmez
            // ama Failed olarak gorunur kalir ki operator mudahale edebilsin.
            if (isPermanent) state.Status = DeviceCardSyncStatus.Failed;
        }, cancellationToken);

    public async Task<IReadOnlyList<DeviceCardStatusRow>> GetCardStatusAsync(Guid cardId,
        CancellationToken cancellationToken) =>
        await (from state in db.DeviceCardStates.AsNoTracking()
               join device in db.Devices.AsNoTracking() on state.DeviceId equals device.Id
               where state.CardId == cardId
               orderby device.Name
               select new DeviceCardStatusRow(device.Id, device.Name, state.Status, state.LastSyncedAt,
                   state.AttemptCount, state.LastError))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DeviceCardSummary>> GetDeviceSummariesAsync(CancellationToken cancellationToken) =>
        await (from device in db.Devices.AsNoTracking()
               where device.IsActive
               orderby device.Name
               select new DeviceCardSummary(device.Id, device.Name,
                   db.DeviceCardStates.Count(x => x.DeviceId == device.Id && x.Status == DeviceCardSyncStatus.Loaded),
                   db.DeviceCardStates.Count(x => x.DeviceId == device.Id
                       && (x.Status == DeviceCardSyncStatus.Pending || x.Status == DeviceCardSyncStatus.PendingRemoval)),
                   db.DeviceCardStates.Count(x => x.DeviceId == device.Id && x.Status == DeviceCardSyncStatus.Failed)))
            .ToListAsync(cancellationToken);

    private async Task<List<Guid>> ActiveCardDeviceIdsAsync(CancellationToken cancellationToken) =>
        await db.Devices.AsNoTracking()
            .Where(device => device.IsActive && device.DeviceType == "SF300")
            .Select(device => device.Id)
            .ToListAsync(cancellationToken);

    private async Task UpdateAsync(Guid deviceId, Guid cardId, Action<DeviceCardState> apply,
        CancellationToken cancellationToken)
    {
        var state = await db.DeviceCardStates
            .SingleOrDefaultAsync(x => x.DeviceId == deviceId && x.CardId == cardId, cancellationToken);
        if (state is null) return;
        apply(state);
        await db.SaveChangesAsync(cancellationToken);
    }
}
