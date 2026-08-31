using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Yemekhane.Application.Audit;
using Yemekhane.Application.Common;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.Management;
using Yemekhane.Domain.Entities;
using Yemekhane.Infrastructure.Persistence;

namespace Yemekhane.Api.Devices;

public sealed record DeviceWriteRequest(string Name, string DeviceType, string ConnectionType,
    string? IpAddress, int? Port, string? ComPort, int? BaudRate, bool IsActive, bool AutoConnect,
    bool HasTurnstile, string? Location, string Direction);

public sealed record DeviceDto(Guid Id, string Name, string DeviceType, string ConnectionType,
    string Endpoint, string? IpAddress, int? Port, string? ComPort, int? BaudRate, bool IsActive,
    bool AutoConnect, bool HasTurnstile, string? Location, string Direction, string Status,
    DateTimeOffset? LastConnectedAt, DateTimeOffset? LastStatusAt, string? Model, string? SerialNumber,
    string? Firmware, bool IsSimulator);

public sealed record DeviceActionResult(bool Succeeded, string Status, string Message,
    string? ErrorCode = null, DeviceDto? Device = null);

public sealed record DeviceLogDto(Guid Id, DateTimeOffset Timestamp, string EventType, string Severity,
    string Message, string? PayloadJson);

public sealed partial class DeviceAdministrationService(
    YemekhaneDbContext db, DeviceManager manager, IDeviceAdapterFactory factory,
    IAuditService audit, TimeProvider timeProvider, IHostEnvironment environment)
{
    private static readonly HashSet<string> Types = ["SF300", "ComReader", "EthernetReader", "Simulator"];
    private static readonly HashSet<string> Directions = ["Entry", "Exit", "Bidirectional"];
    public bool IsSimulatorAllowed => environment.IsDevelopment();

    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken cancellationToken) =>
        await db.Devices.AsNoTracking().OrderByDescending(x => x.IsActive).ThenBy(x => x.Name)
            .Select(x => ToDto(x)).ToListAsync(cancellationToken);

    public async Task<DeviceDto> GetAsync(Guid id, CancellationToken cancellationToken) =>
        ToDto(await FindAsync(id, cancellationToken));

    public async Task<DeviceDto> CreateAsync(DeviceWriteRequest request, CancellationToken cancellationToken)
    {
        var normalized = await ValidateAsync(request, null, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var entity = new Device
        {
            Id = Guid.NewGuid(), CreatedAt = now, ConnectionStatus = "Disconnected",
            Name = normalized.Name, DeviceType = normalized.DeviceType,
            ConnectionType = normalized.ConnectionType, Direction = normalized.Direction
        };
        Apply(entity, normalized, now);
        db.Devices.Add(entity);
        audit.Record(new AuditEntry("Device.Create", nameof(Device), entity.Id.ToString(),
            $"{entity.Name} cihazı oluşturuldu.", After: ToDto(entity)));
        await db.SaveChangesAsync(cancellationToken);
        Register(entity);
        return ToDto(entity);
    }

    public async Task<DeviceDto> UpdateAsync(Guid id, DeviceWriteRequest request, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        var before = ToDto(entity);
        var normalized = await ValidateAsync(request, id, cancellationToken);
        await manager.UnregisterAsync(id, cancellationToken);
        Apply(entity, normalized, timeProvider.GetUtcNow());
        entity.ConnectionStatus = "Disconnected";
        // Damgayi ilerleterek ucusta olan bayat durum yazimlarinin bu karari ezmesini engelliyoruz.
        entity.LastStatusAt = timeProvider.GetUtcNow();
        audit.Record(new AuditEntry("Device.Update", nameof(Device), id.ToString(),
            $"{entity.Name} cihaz yapılandırması güncellendi.", Before: before, After: ToDto(entity)));
        await db.SaveChangesAsync(cancellationToken);
        Register(entity);
        return ToDto(entity);
    }

    public async Task<DeviceDto> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        if (!entity.IsActive) return ToDto(entity);
        await manager.UnregisterAsync(id, cancellationToken);
        entity.IsActive = false;
        entity.AutoConnect = false;
        entity.ConnectionStatus = "Disconnected";
        entity.UpdatedAt = timeProvider.GetUtcNow();
        audit.Record(new AuditEntry("Device.Deactivate", nameof(Device), id.ToString(),
            $"{entity.Name} cihazı pasifleştirildi."));
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<DeviceActionResult> ExecuteAsync(Guid id, string action, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(id, cancellationToken);
        if (!entity.IsActive) throw new RequestValidationException("Pasif cihazda bağlantı işlemi yapılamaz.");
        if (!manager.TryGetDevice(id, out _)) Register(entity);
        try
        {
            string message;
            switch (action)
            {
                case "connect":
                case "reconnect":
                    var info = action == "connect"
                        ? await manager.ConnectAsync(id, cancellationToken)
                        : await manager.ReconnectAsync(id, cancellationToken);
                    entity.Model = info.Model;
                    entity.SerialNumber = info.SerialNumber;
                    entity.Firmware = info.Firmware;
                    entity.LastConnectedAt = timeProvider.GetUtcNow();
                    entity.LastStatusAt = entity.LastConnectedAt;
                    entity.ConnectionStatus = "Connected";
                    message = action == "connect" ? "Cihaz bağlandı." : "Cihaz yeniden bağlandı.";
                    break;
                case "disconnect":
                    await manager.DisconnectAsync(id, cancellationToken);
                    entity.ConnectionStatus = "Disconnected";
                    entity.LastStatusAt = timeProvider.GetUtcNow();
                    message = "Cihaz bağlantısı kesildi.";
                    break;
                case "test":
                case "status":
                    var status = await manager.GetStatusAsync(id, cancellationToken);
                    entity.ConnectionStatus = StatusName(status.State);
                    entity.LastStatusAt = status.CheckedAt;
                    message = status.Message ?? "Cihaz durumu alındı.";
                    break;
                default:
                    throw new RequestValidationException("Desteklenmeyen cihaz işlemi.");
            }
            await AddEventAsync(entity, action, "Information", message, null, cancellationToken);
            return new DeviceActionResult(true, entity.ConnectionStatus, message, Device: ToDto(entity));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        {
            entity.ConnectionStatus = "Error";
            entity.LastStatusAt = timeProvider.GetUtcNow();
            var errorCode = exception is DeviceConnectionException deviceException ? deviceException.ErrorCode : "DEVICE_ACTION_FAILED";
            await AddEventAsync(entity, action, "Error", exception.Message, errorCode, cancellationToken);
            return new DeviceActionResult(false, entity.ConnectionStatus, exception.Message, errorCode, ToDto(entity));
        }
    }

    public async Task<IReadOnlyList<DeviceLogDto>> LogsAsync(Guid id, int take, CancellationToken cancellationToken)
    {
        _ = await FindAsync(id, cancellationToken);
        if (take is < 1 or > 500) throw new RequestValidationException("Log adedi 1-500 arasında olmalıdır.");
        return await db.DeviceEvents.AsNoTracking().Where(x => x.DeviceId == id)
            .OrderByDescending(x => x.Timestamp).Take(take)
            .Select(x => new DeviceLogDto(x.Id, x.Timestamp, x.EventType, x.Severity, x.Message, x.PayloadJson))
            .ToListAsync(cancellationToken);
    }

    private async Task<DeviceWriteRequest> ValidateAsync(DeviceWriteRequest request, Guid? id,
        CancellationToken cancellationToken)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
            throw new RequestValidationException("Cihaz adı zorunludur ve en fazla 100 karakter olabilir.");
        if (!Types.Contains(request.DeviceType)) throw new RequestValidationException("Cihaz türü geçersiz.");
        if (request.DeviceType == "Simulator" && !environment.IsDevelopment())
            throw new RequestValidationException("Simulator cihazları yalnızca Development ortamında oluşturulabilir.");
        if (!Directions.Contains(request.Direction)) throw new RequestValidationException("Geçiş yönü geçersiz.");
        if (request.Location?.Length > 150) throw new RequestValidationException("Konum en fazla 150 karakter olabilir.");

        var isCom = request.DeviceType == "ComReader";
        var isSimulator = request.DeviceType == "Simulator";
        var connection = isCom ? "COM" : isSimulator ? "Simulator" : "Ethernet";
        string? ip = null;
        int? port = null;
        string? com = null;
        int? baud = null;
        if (connection == "Ethernet")
        {
            ip = request.IpAddress?.Trim();
            if (!IPAddress.TryParse(ip, out _)) throw new RequestValidationException("Geçerli bir IP adresi girilmelidir.");
            if (request.Port is < 1 or > 65535) throw new RequestValidationException("Port 1-65535 arasında olmalıdır.");
            port = request.Port;
            if (await db.Devices.AnyAsync(x => x.Id != id && x.IpAddress == ip && x.IpPort == port, cancellationToken))
                throw new RequestValidationException("Bu IP ve port başka bir cihazda kullanılıyor.");
        }
        else if (connection == "COM")
        {
            com = request.ComPort?.Trim().ToUpperInvariant();
            if (com is null || !ComPortPattern().IsMatch(com)) throw new RequestValidationException("COM portu COM1-COM256 biçiminde olmalıdır.");
            if (request.BaudRate is < 300 or > 4_000_000) throw new RequestValidationException("Baud hızı 300-4000000 arasında olmalıdır.");
            baud = request.BaudRate;
            if (await db.Devices.AnyAsync(x => x.Id != id && x.ComPort == com && x.BaudRate == baud, cancellationToken))
                throw new RequestValidationException("Bu COM portu ve baud hızı başka bir cihazda kullanılıyor.");
        }
        if (await db.Devices.AnyAsync(x => x.Id != id && x.Name == name, cancellationToken))
            throw new RequestValidationException("Bu cihaz adı kullanılıyor.");
        return request with { Name = name, ConnectionType = connection, IpAddress = ip, Port = port,
            ComPort = com, BaudRate = baud, Location = request.Location?.Trim() };
    }

    private async Task<Device> FindAsync(Guid id, CancellationToken cancellationToken) =>
        await db.Devices.SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
        ?? throw new KeyNotFoundException($"Cihaz bulunamadı: {id}");

    private void Register(Device entity)
    {
        if (!entity.IsActive) return;
        var adapter = factory.Create(Configuration(entity));
        // Register false donerse (ayni Id zaten kayitli) adaptor yoneticiye devredilmemistir.
        // Windows'ta seri portlar exclusive oldugundan sizan handle o COM portunu
        // surec yeniden baslayana kadar kilitler; bu yuzden burada elden cikariyoruz.
        var registered = false;
        try
        {
            registered = manager.Register(adapter, new DeviceRegistrationOptions(entity.IsActive, entity.AutoConnect));
        }
        finally
        {
            if (!registered) DisposeQuietly(adapter);
        }
    }

    private static void DisposeQuietly(IDevice adapter)
    {
        try
        {
            adapter.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Kayit zaten basarisiz; temizlik hatasi asil hatayi golgelememeli.
        }
    }

    private async Task AddEventAsync(Device entity, string type, string severity, string message,
        string? errorCode, CancellationToken cancellationToken)
    {
        entity.UpdatedAt = timeProvider.GetUtcNow();
        db.DeviceEvents.Add(new DeviceEvent { DeviceId = entity.Id, Timestamp = entity.UpdatedAt.Value,
            EventType = type, Severity = severity, Message = message,
            PayloadJson = errorCode is null ? null : JsonSerializer.Serialize(new { ErrorCode = errorCode }) });
        audit.Record(new AuditEntry($"Device.{type}", nameof(Device), entity.Id.ToString(), message,
            After: new { entity.ConnectionStatus, ErrorCode = errorCode }));
        await db.SaveChangesAsync(cancellationToken);
    }

    internal static DeviceAdapterConfiguration Configuration(Device x) => new(x.Id, x.Name, x.DeviceType,
        x.ConnectionType, x.ComPort, x.BaudRate, x.IpAddress, x.IpPort, x.HasTurnstile);

    private static void Apply(Device x, DeviceWriteRequest r, DateTimeOffset now)
    {
        x.Name = r.Name; x.DeviceType = r.DeviceType; x.ConnectionType = r.ConnectionType;
        x.IpAddress = r.IpAddress; x.IpPort = r.Port; x.ComPort = r.ComPort; x.BaudRate = r.BaudRate;
        x.IsActive = r.IsActive; x.AutoConnect = r.AutoConnect; x.HasTurnstile = r.HasTurnstile;
        x.Location = r.Location; x.Direction = r.Direction; x.UpdatedAt = now;
    }

    private static DeviceDto ToDto(Device x) => new(x.Id, x.Name, x.DeviceType, x.ConnectionType,
        x.ConnectionType == "COM" ? $"{x.ComPort} / {x.BaudRate}" : x.ConnectionType == "Ethernet" ? $"{x.IpAddress}:{x.IpPort}" : "Development simulator",
        x.IpAddress, x.IpPort, x.ComPort, x.BaudRate, x.IsActive, x.AutoConnect, x.HasTurnstile,
        x.Location, x.Direction, x.ConnectionStatus, x.LastConnectedAt, x.LastStatusAt, x.Model,
        x.SerialNumber, x.Firmware, x.DeviceType == "Simulator");

    internal static string StatusName(DeviceConnectionState state) => state == DeviceConnectionState.Faulted ? "Error" : state.ToString();

    [GeneratedRegex("^COM(?:[1-9]|[1-9][0-9]|1[0-9]{2}|2[0-4][0-9]|25[0-6])$", RegexOptions.IgnoreCase)]
    private static partial Regex ComPortPattern();
}
