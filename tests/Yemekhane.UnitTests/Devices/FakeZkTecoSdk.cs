using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.ZkTeco;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// SC403 adaptor testleri icin bellek-ici sahte SDK.
///
/// Gercek <c>zkemkeeper.dll</c> baglamasi 32-bit COM bilesenidir ve bu depoda bulunmaz; adaptorun
/// yasam dongusu, zaman asimi ve yetenek denetimi mantigi bu sahte uzerinden dogrulanir.
/// </summary>
internal sealed class FakeZkTecoSdk : IZkTecoSdk
{
    private readonly Channel<CardReadEvent> _cards = Channel.CreateUnbounded<CardReadEvent>();
    private readonly ConcurrentQueue<Exception> _nextFailures = new();

    public bool IsConnected { get; private set; }
    public int ConnectCount { get; private set; }
    public int DisconnectCount { get; private set; }
    public bool IsDisposed { get; private set; }
    public List<string> Calls { get; } = [];

    /// <summary>Handshake yaniti. Null birakilirsa gecersiz yanit senaryosu test edilir.</summary>
    public DeviceInfo? DeviceInfo { get; set; } = new("SC403", "SN-TEST-1", "1.0.0",
        new HashSet<DeviceCapability>
        {
            DeviceCapability.DeviceInfo, DeviceCapability.Status, DeviceCapability.ReadCard,
            DeviceCapability.ReadUser, DeviceCapability.SendCard, DeviceCapability.SendUser,
            DeviceCapability.SyncCard, DeviceCapability.SyncUser, DeviceCapability.DeleteCard,
            DeviceCapability.GrantAccess, DeviceCapability.DenyAccess
        });

    public DeviceStatus? Status { get; set; } = new(DeviceConnectionState.Connected, DateTimeOffset.UtcNow, "READY");
    public DeviceCommandResult? CommandResult { get; set; } = new(true, "OK");
    public DeviceUser? User { get; set; }
    public string? CardOwner { get; set; }

    /// <summary>Baglanti kurulmus gibi davranmasini engeller (ConnectAsync sessizce basarisiz olur).</summary>
    public bool RefuseConnection { get; set; }

    /// <summary>Sonraki cagriyi verilen hatayla dusurur.</summary>
    public void FailNext(Exception exception) => _nextFailures.Enqueue(exception);

    public void PushCard(CardReadEvent card) => _cards.Writer.TryWrite(card);
    public void CompleteCards() => _cards.Writer.TryComplete();

    public Task ConnectAsync(DeviceEndpoint endpoint, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ConnectAsync));
        ConnectCount++;
        ThrowIfScripted();
        if (!RefuseConnection) IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        Calls.Add(nameof(DisconnectAsync));
        DisconnectCount++;
        IsConnected = false;
        return Task.CompletedTask;
    }

    public Task<DeviceInfo?> GetDeviceInfoAsync(CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetDeviceInfoAsync));
        ThrowIfScripted();
        return Task.FromResult(DeviceInfo);
    }

    public Task<DeviceStatus?> GetStatusAsync(CancellationToken cancellationToken)
    {
        Calls.Add(nameof(GetStatusAsync));
        ThrowIfScripted();
        return Task.FromResult(Status);
    }

    public async IAsyncEnumerable<CardReadEvent> ReadRealTimeCardsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls.Add(nameof(ReadRealTimeCardsAsync));
        ThrowIfScripted();
        await foreach (var card in _cards.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return card;
        }
    }

    public Task<DeviceCommandResult?> SetUserInfoAsync(DeviceUser user, CancellationToken cancellationToken)
    {
        Calls.Add(nameof(SetUserInfoAsync));
        ThrowIfScripted();
        return Task.FromResult(CommandResult);
    }

    public Task<DeviceCommandResult?> SetCardNumberAsync(string cardNumber, string externalUserId,
        CancellationToken cancellationToken)
    {
        Calls.Add($"{nameof(SetCardNumberAsync)}:{cardNumber}:{externalUserId}");
        ThrowIfScripted();
        return Task.FromResult(CommandResult);
    }

    public Task<DeviceCommandResult?> DeleteUserInfoAsync(string cardNumber, CancellationToken cancellationToken)
    {
        Calls.Add($"{nameof(DeleteUserInfoAsync)}:{cardNumber}");
        ThrowIfScripted();
        return Task.FromResult(CommandResult);
    }

    public Task<DeviceUser?> GetUserInfoAsync(string externalUserId, CancellationToken cancellationToken)
    {
        Calls.Add($"{nameof(GetUserInfoAsync)}:{externalUserId}");
        ThrowIfScripted();
        return Task.FromResult(User);
    }

    public Task<string?> GetUserIdByCardAsync(string cardNumber, CancellationToken cancellationToken)
    {
        Calls.Add($"{nameof(GetUserIdByCardAsync)}:{cardNumber}");
        ThrowIfScripted();
        return Task.FromResult(CardOwner);
    }

    public ValueTask DisposeAsync()
    {
        IsDisposed = true;
        IsConnected = false;
        _cards.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }

    private void ThrowIfScripted()
    {
        if (_nextFailures.TryDequeue(out var exception)) throw exception;
    }
}
