using Yemekhane.Devices.Abstractions;
using Yemekhane.Devices.ZkTeco;
using Yemekhane.Devices.ZkTeco.Protocol;

namespace Yemekhane.UnitTests.Devices;

/// <summary>
/// <see cref="ZkProtocolSdk"/> gercek UDP soketiyle loopback'teki <see cref="FakeZkDevice"/>'a
/// karsi. Uretimde SDK hic yoktu (fabrika null veriyordu); bu testler gercek yolun -- soket,
/// paket, oturum, sifre, kullanici tablosu, role, olay akisi -- kostugunu kanitlar.
/// </summary>
public sealed class ZkProtocolSdkTests
{
    private static DeviceEndpoint Endpoint(FakeZkDevice device) =>
        new("Ethernet", IpAddress: "127.0.0.1", IpPort: device.Port);

    private static ZkProtocolSdk Sdk(int commKey = 0, TimeSpan? replyTimeout = null, List<string>? diagnostics = null,
        TimeProvider? time = null) =>
        new(new ZkUdpTransport(), commKey, replyTimeout ?? TimeSpan.FromSeconds(2),
            diagnostics: diagnostics is null ? null : diagnostics.Add, timeProvider: time);

    private static async Task<ZkProtocolSdk> ConnectedAsync(FakeZkDevice device, int commKey = 0, List<string>? diagnostics = null,
        TimeProvider? time = null)
    {
        var sdk = Sdk(commKey, diagnostics: diagnostics, time: time);
        await sdk.ConnectAsync(Endpoint(device), CancellationToken.None);
        return sdk;
    }

    [Fact]
    public async Task ConnectEstablishesSessionAndReadsDeviceInfo()
    {
        await using var device = new FakeZkDevice { DeviceName = "SC403", SerialNumber = "SN-42", Firmware = "Ver 6.60" };
        await using var sdk = await ConnectedAsync(device);

        Assert.True(sdk.IsConnected);
        var info = await sdk.GetDeviceInfoAsync(CancellationToken.None);
        Assert.NotNull(info);
        Assert.Equal("SC403", info!.Model);
        Assert.Equal("SN-42", info.SerialNumber);
        Assert.Equal("Ver 6.60", info.Firmware);
        Assert.Contains(DeviceCapability.GrantAccess, info.Capabilities);
        Assert.Equal(1, device.ConnectCount);
    }

    /// <summary>Cihaz ACK_UNAUTH dondugunde AUTH ile sifre gonderilir; yanlis sifre baglanamaz.</summary>
    [Fact]
    public async Task ConnectAuthenticatesWithCommKeyWhenDeviceDemandsIt()
    {
        await using var device = new FakeZkDevice { CommKey = 123456 };
        await using var sdk = await ConnectedAsync(device, commKey: 123456);

        Assert.True(sdk.IsConnected);
        Assert.Contains(ZkCommands.Auth, device.ReceivedCommands);
    }

    [Fact]
    public async Task ConnectFailsWithWrongCommKey()
    {
        await using var device = new FakeZkDevice { CommKey = 123456 };
        await using var sdk = Sdk(commKey: 1);

        var exception = await Assert.ThrowsAsync<ZkTecoProtocolException>(
            () => sdk.ConnectAsync(Endpoint(device), CancellationToken.None));

        Assert.Equal(ZkTecoErrorCodes.ConnectFailed, exception.ErrorCode);
        Assert.False(exception.IsTransient);
        Assert.False(sdk.IsConnected);
    }

    /// <summary>
    /// Dinleyen yoksa (yanlis IP, kapali cihaz) GECICI baglanti zaman asimi bildirilir; adaptor
    /// buna gore yeniden dener ve mesaj kullaniciya "cevap vermedi" der, "A task was canceled" degil.
    /// </summary>
    [Fact]
    public async Task ConnectTimesOutWhenNothingAnswers()
    {
        await using var silent = new FakeZkDevice { DropNextReplies = int.MaxValue };
        await using var sdk = Sdk(replyTimeout: TimeSpan.FromMilliseconds(300));

        var exception = await Assert.ThrowsAsync<ZkTecoProtocolException>(
            () => sdk.ConnectAsync(Endpoint(silent), CancellationToken.None));

        Assert.Equal(ZkTecoErrorCodes.ConnectTimeout, exception.ErrorCode);
        Assert.True(exception.IsTransient);
        Assert.Contains("cevap vermedi", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StatusReportsCountsFromFreeSizes()
    {
        await using var device = new FakeZkDevice { Records = 17, UserCapacity = 30000 };
        device.Users[1] = new ZkDeviceUser(1, "1", 555, "555");
        await using var sdk = await ConnectedAsync(device);

        var status = await sdk.GetStatusAsync(CancellationToken.None);

        Assert.NotNull(status);
        Assert.Equal(DeviceConnectionState.Connected, status!.State);
        Assert.Contains("1/30000", status.Message, StringComparison.Ordinal);
        Assert.Contains("17/", status.Message, StringComparison.Ordinal);
    }

    /// <summary>CMD_UNLOCK verisi 100 ms birimindedir (pyzk: saniye x 10). 500 ms -> 5.</summary>
    [Theory]
    [InlineData(500, 5u)]
    [InlineData(1200, 12u)]
    [InlineData(50, 1u)]
    public async Task UnlockSendsPulseInDeciseconds(int milliseconds, uint expected)
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        var result = await sdk.UnlockAsync(TimeSpan.FromMilliseconds(milliseconds), CancellationToken.None);

        Assert.True(result!.Succeeded);
        Assert.Equal([expected], device.Unlocks);
    }

    /// <summary>
    /// Kart cihaza PIN=uid ve ad=kart numarasi ile yazilir; GUID cihaza hic gitmez (sigmaz).
    /// </summary>
    [Fact]
    public async Task SetCardCreatesDeviceUserWithPinEqualToUid()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        var result = await sdk.SetCardNumberAsync("0008573921", Guid.NewGuid().ToString("D"), CancellationToken.None);

        Assert.True(result!.Succeeded);
        var user = Assert.Single(device.Users.Values);
        Assert.Equal(8573921u, user.Card);
        Assert.Equal(user.Uid.ToString(), user.Pin);
        Assert.Equal("8573921", user.Name);
    }

    [Fact]
    public async Task SetCardReusesUidForSameCard()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        await sdk.SetCardNumberAsync("1111", "a", CancellationToken.None);
        await sdk.SetCardNumberAsync("2222", "b", CancellationToken.None);
        await sdk.SetCardNumberAsync("1111", "a", CancellationToken.None);

        Assert.Equal(2, device.Users.Count);
        Assert.Equal(new ushort[] { 1, 2 }, device.Users.Keys.Order().ToArray());
    }

    /// <summary>Yeni firmware 72 baytlik kayit da desteklenir; boyut cihazdan olculur.</summary>
    [Fact]
    public async Task SetCardUsesExtendedRecordWhenDeviceReportsIt()
    {
        await using var device = new FakeZkDevice { UserRecordSize = ZkProtocolSdk.ExtendedUserRecordSize };
        device.Users[7] = new ZkDeviceUser(7, "7", 999, "999");
        await using var sdk = await ConnectedAsync(device);

        var result = await sdk.SetCardNumberAsync("4242", "x", CancellationToken.None);

        Assert.True(result!.Succeeded);
        Assert.Equal(ZkProtocolSdk.ExtendedUserRecordSize, sdk.UserRecordSize);
        Assert.Equal(4242u, device.Users[8].Card);
    }

    [Theory]
    [InlineData("ABC123")]
    [InlineData("99999999999")]
    [InlineData("0")]
    public async Task InvalidCardIsPermanentError(string card)
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        var exception = await Assert.ThrowsAsync<ZkTecoProtocolException>(
            () => sdk.SetCardNumberAsync(card, "x", CancellationToken.None));

        Assert.Equal("ZK_INVALID_CARD", exception.ErrorCode);
        Assert.True(DeviceErrorCodes.IsPermanent(exception.ErrorCode));
        Assert.Empty(device.Users);
    }

    /// <summary>
    /// Cihaz reddi ISTISNA olmalidir: kart itme dongusu sonucun Succeeded alanina bakmaz. Basarisiz
    /// sonuc donulseydi reddedilen kart "yuklendi" isaretlenir ve ogrenci turnikeden gecemezdi.
    /// </summary>
    [Fact]
    public async Task DeviceRejectionIsThrownNotReturnedAsFailedResult()
    {
        await using var device = new FakeZkDevice { RejectUserWrites = true };
        await using var sdk = await ConnectedAsync(device);

        var exception = await Assert.ThrowsAsync<ZkTecoProtocolException>(
            () => sdk.SetCardNumberAsync("1234", "x", CancellationToken.None));

        Assert.Equal("ZK_COMMAND_REJECTED", exception.ErrorCode);
        Assert.False(exception.IsTransient);
    }

    [Fact]
    public async Task DeleteCardRemovesDeviceUser()
    {
        await using var device = new FakeZkDevice();
        device.Users[3] = new ZkDeviceUser(3, "3", 777, "777");
        await using var sdk = await ConnectedAsync(device);

        var result = await sdk.DeleteUserInfoAsync("777", CancellationToken.None);

        Assert.True(result!.Succeeded);
        Assert.Empty(device.Users);
    }

    /// <summary>Cihazda olmayan karti silmek amaca zaten ulasmistir; hata degildir.</summary>
    [Fact]
    public async Task DeleteUnknownCardSucceeds()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        var result = await sdk.DeleteUserInfoAsync("777", CancellationToken.None);

        Assert.True(result!.Succeeded);
        Assert.DoesNotContain(ZkCommands.DeleteUser, device.ReceivedCommands);
    }

    [Fact]
    public async Task GetUserIdByCardReturnsPin()
    {
        await using var device = new FakeZkDevice();
        device.Users[9] = new ZkDeviceUser(9, "9", 4444, "4444");
        await using var sdk = await ConnectedAsync(device);

        Assert.Equal("9", await sdk.GetUserIdByCardAsync("4444", CancellationToken.None));
        Assert.Null(await sdk.GetUserIdByCardAsync("5555", CancellationToken.None));
    }

    /// <summary>
    /// Kart okutuldugunda cihaz PIN gonderir; SDK bunu kullanici tablosundan KART NUMARASINA
    /// cevirir. Adaptor ve erisim karari kart numarasi bekler.
    /// </summary>
    [Fact]
    public async Task RealTimeAttendanceResolvesPinToCardNumber()
    {
        await using var device = new FakeZkDevice();
        device.Users[5] = new ZkDeviceUser(5, "5", 8573921, "8573921");
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 9, 20, 22, TimeSpan.FromHours(3)));
        await using var sdk = await ConnectedAsync(device, time: clock);

        var read = ReadOneAsync(sdk);
        await WaitForRegistrationAsync(device);
        await device.EmitAttendanceAsync(5, new DateTime(2026, 9, 7, 12, 30, 15));

        var card = await read;
        Assert.Equal("8573921", card.CardNumber);
        Assert.Equal("SC403", card.ReaderSource);
        // Damga cihaz saati (7 Eylul 12:30) DEGIL, alis ani: sahada cihaz saati 83 dk gerideydi.
        Assert.Equal(clock.Now, card.Timestamp);
        // Varsayilan abonelik gecis kaydi + kart numarasi olayidir (0x0001 | 0x0400 = 1025). Sabit
        // Default uzerinden degil, acik degerle dogrulanir: sabit degisirse test de degismesin.
        Assert.Equal([1025u], device.RegisteredEventFlags);
    }

    /// <summary>
    /// SAHADA OLCULDU (SC403/M fw 6.60): cihaz okutulan kartin numarasini 0x0400 olayiyla dogrudan
    /// verir. Kullanici tablosuna bakmadan yukselmeli -- kayitsiz kartlar da boyle gelir ve erisim
    /// karari "Kart tanimsiz" diyebilir.
    /// </summary>
    [Fact]
    public async Task CardNumberEventYieldsCardWithoutUserTable()
    {
        await using var device = new FakeZkDevice();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 9, 20, 21, TimeSpan.FromHours(3)));
        await using var sdk = await ConnectedAsync(device, time: clock);

        var read = ReadOneAsync(sdk);
        await WaitForRegistrationAsync(device);
        await device.EmitCardNumberAsync(8247129);

        var card = await read;
        Assert.Equal("8247129", card.CardNumber);
        Assert.Equal("SC403", card.ReaderSource);
        Assert.Equal(clock.Now, card.Timestamp);
        Assert.DoesNotContain(ZkCommands.DataWriteReadRequest, device.ReceivedCommands);
    }

    /// <summary>
    /// SAHADA OLCULDU: kayitli kartta once 0x0400 (kart), ~1,3 sn sonra 0x0001 (gecis kaydi) gelir.
    /// Ikincisi ayni okutmanin yankisidir; yukselseydi ogun hakki iki kez duserdi. Pencere gecince
    /// gelen gecis kaydi (kart olayi UDP'de kaybolmus olabilir) yine yukselir.
    /// </summary>
    [Fact]
    public async Task AttendanceEchoAfterCardNumberEventIsSuppressedButLaterAttendanceIsNot()
    {
        await using var device = new FakeZkDevice();
        device.Users[3030] = new ZkDeviceUser(3030, "3030", 8247129, "8247129");
        device.Users[7] = new ZkDeviceUser(7, "7", 5555, "5555");
        var diagnostics = new List<string>();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 9, 20, 21, TimeSpan.FromHours(3)));
        await using var sdk = await ConnectedAsync(device, diagnostics: diagnostics, time: clock);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var stream = sdk.ReadRealTimeCardsAsync(timeout.Token).GetAsyncEnumerator(timeout.Token);
        var first = stream.MoveNextAsync();
        await WaitForRegistrationAsync(device);
        await device.EmitCardNumberAsync(8247129);
        Assert.True(await first);
        Assert.Equal("8247129", stream.Current.CardNumber);

        clock.Advance(TimeSpan.FromMilliseconds(1300));
        var second = stream.MoveNextAsync();
        await device.EmitAttendanceAsync(3030, new DateTime(2026, 9, 8, 7, 57, 30));
        await WaitUntilAsync(() => diagnostics.Any(note => note.Contains("yankisi", StringComparison.Ordinal)));

        clock.Advance(TimeSpan.FromSeconds(10));
        await device.EmitAttendanceAsync(7, new DateTime(2026, 9, 8, 7, 57, 45));
        Assert.True(await second);
        Assert.Equal("5555", stream.Current.CardNumber);
    }

    /// <summary>
    /// Sahada eski programin yukledigi 444 kullanici yuzunden cihaz kendi basina "Tesekkurler" deyip
    /// gecis veriyordu. Bellek temizleme her kullaniciyi DELETE_USER ile siler ve sayiyi doner.
    /// </summary>
    [Fact]
    public async Task ClearUsersDeletesEveryUserAndReportsTheCount()
    {
        await using var device = new FakeZkDevice();
        device.Users[1] = new ZkDeviceUser(1, "1", 111, "111");
        device.Users[2] = new ZkDeviceUser(2, "2", 222, "222");
        device.Users[3030] = new ZkDeviceUser(3030, "3030", 8247129, "8247129");
        await using var sdk = await ConnectedAsync(device);

        var count = await sdk.ClearUsersAsync(CancellationToken.None);

        Assert.Equal(3, count);
        Assert.Empty(device.Users);
        Assert.Equal(3, device.ReceivedCommands.Count(command => command == ZkCommands.DeleteUser));
        Assert.Equal(0, await sdk.ClearUsersAsync(CancellationToken.None));
    }

    /// <summary>Alarm olayi (sahada kod 55 goruldu) kart degildir; atlanir, akis surer.</summary>
    [Fact]
    public async Task AlarmEventIsIgnored()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        var read = ReadOneAsync(sdk);
        await WaitForRegistrationAsync(device);
        await device.EmitAlarmAsync(55);
        await device.EmitCardNumberAsync(8247129);

        Assert.Equal("8247129", (await read).CardNumber);
    }

    /// <summary>Cihaz saati bilgisayardan cok sapmissa (sahada 83 dk) bir kez tanilama notu dusulur.</summary>
    [Fact]
    public async Task DeviceClockSkewIsReportedOnce()
    {
        await using var device = new FakeZkDevice();
        device.Users[1] = new ZkDeviceUser(1, "1", 100, "100");
        var diagnostics = new List<string>();
        var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 8, 9, 20, 22, TimeSpan.FromHours(3)));
        await using var sdk = await ConnectedAsync(device, diagnostics: diagnostics, time: clock);

        var events = ReadManyAsync(sdk, 2);
        await WaitForRegistrationAsync(device);
        await device.EmitAttendanceAsync(1, new DateTime(2026, 9, 8, 7, 57, 30));
        await device.EmitAttendanceAsync(1, new DateTime(2026, 9, 8, 7, 57, 31));

        Assert.Equal(2, (await events).Count);
        Assert.Single(diagnostics, note => note.Contains("saati", StringComparison.Ordinal));
    }

    /// <summary>Sahada yakalanan ham baytlar: "59 D7 7D 00 00" kart olayi = 8247129.</summary>
    [Fact]
    public void FieldCapturedCardNumberBytesParse()
    {
        Assert.True(ZkProtocolSdk.TryParseCardNumber([0x59, 0xD7, 0x7D, 0x00, 0x00], out var card));
        Assert.Equal(8247129u, card);
        Assert.False(ZkProtocolSdk.TryParseCardNumber([0x59, 0xD7, 0x7D], out _));
        Assert.False(ZkProtocolSdk.TryParseCardNumber([0, 0, 0, 0, 0], out _));
    }

    /// <summary>Sahada yakalanan ham baytlar: gecis kaydi PIN 3030, cihaz saati 08.09.2026 07:57:30.</summary>
    [Fact]
    public void FieldCapturedAttendanceBytesParse()
    {
        byte[] data = [0xD6, 0x0B, 0x00, 0x00, 0x1A, 0x09, 0x08, 0x07, 0x39, 0x1E];
        Assert.True(ZkProtocolSdk.TryParseAttendance(data, out var pin, out var time));
        Assert.Equal("3030", pin);
        Assert.Equal(new DateTime(2026, 9, 8, 7, 57, 30), time.DateTime);
    }

    [Fact]
    public async Task RealTimeAttendanceHandlesExtendedLayout()
    {
        await using var device = new FakeZkDevice { UserRecordSize = ZkProtocolSdk.ExtendedUserRecordSize };
        device.Users[12] = new ZkDeviceUser(12, "12", 31337, "31337");
        await using var sdk = await ConnectedAsync(device);

        var read = ReadOneAsync(sdk);
        await WaitForRegistrationAsync(device);
        await device.EmitAttendanceExtendedAsync("12", new DateTime(2026, 1, 2, 3, 4, 5));

        Assert.Equal("31337", (await read).CardNumber);
    }

    /// <summary>
    /// Tabloda olmayan PIN (kart cihaza baska yoldan yazilmis) DUSURULMEZ: PIN kart yerine gecer
    /// ve kaynak "SC403:pin" olur; erisim karari bunu taninmayan kart olarak reddeder ve gunluge duser.
    /// </summary>
    [Fact]
    public async Task UnknownPinIsSurfacedNotDropped()
    {
        await using var device = new FakeZkDevice();
        var diagnostics = new List<string>();
        await using var sdk = await ConnectedAsync(device, diagnostics: diagnostics);

        var read = ReadOneAsync(sdk);
        await WaitForRegistrationAsync(device);
        await device.EmitAttendanceAsync(77, DateTime.Now);

        var card = await read;
        Assert.Equal("77", card.CardNumber);
        Assert.Equal("SC403:pin", card.ReaderSource);
        Assert.Contains(diagnostics, note => note.Contains("77", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GarbageEventIsSkippedWithDiagnostic()
    {
        await using var device = new FakeZkDevice();
        device.Users[1] = new ZkDeviceUser(1, "1", 100, "100");
        var diagnostics = new List<string>();
        await using var sdk = await ConnectedAsync(device, diagnostics: diagnostics);

        var read = ReadOneAsync(sdk);
        await WaitForRegistrationAsync(device);
        await device.EmitGarbageEventAsync();
        await device.EmitAttendanceAsync(1, DateTime.Now);

        Assert.Equal("100", (await read).CardNumber);
        Assert.Contains(diagnostics, note => note.Contains("cozulemeyen", StringComparison.Ordinal));
    }

    /// <summary>Buyuk kullanici tablosu PREPARE_DATA + READ_BUFFER parcalariyla okunur (pyzk read_with_buffer).</summary>
    [Fact]
    public async Task ChunkedUserTableIsReadAcrossPackets()
    {
        await using var device = new FakeZkDevice { ChunkedUserRead = true };
        for (ushort uid = 1; uid <= 120; uid++)
        {
            device.Users[uid] = new ZkDeviceUser(uid, uid.ToString(), 10000u + uid, (10000u + uid).ToString());
        }

        await using var sdk = await ConnectedAsync(device);

        Assert.Equal("120", await sdk.GetUserIdByCardAsync("10120", CancellationToken.None));
        Assert.Equal("1", await sdk.GetUserIdByCardAsync("10001", CancellationToken.None));
        Assert.Contains(ZkCommands.ReadBuffer, device.ReceivedCommands);
        Assert.Contains(ZkCommands.FreeData, device.ReceivedCommands);
    }

    /// <summary>UDP'de kaybolan yanit GECICI hatadir: adaptor yeniden dener, kalici saymaz.</summary>
    [Fact]
    public async Task LostReplyIsTransientTimeout()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = Sdk(replyTimeout: TimeSpan.FromMilliseconds(300));
        await sdk.ConnectAsync(Endpoint(device), CancellationToken.None);
        device.DropNextReplies = 1;

        var exception = await Assert.ThrowsAsync<ZkTecoProtocolException>(
            () => sdk.UnlockAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None));

        Assert.Equal(ZkProtocolSdk.ReplyTimeoutCode, exception.ErrorCode);
        Assert.True(exception.IsTransient);
        Assert.True(sdk.IsConnected);
    }

    [Fact]
    public async Task DisconnectSendsExitAndCommandsFailAfterwards()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        await sdk.DisconnectAsync(CancellationToken.None);

        Assert.False(sdk.IsConnected);
        Assert.Equal(1, device.ExitCount);
        var exception = await Assert.ThrowsAsync<ZkTecoProtocolException>(
            () => sdk.UnlockAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None));
        Assert.Equal(ZkTecoErrorCodes.Disconnected, exception.ErrorCode);
    }

    [Fact]
    public async Task ReconnectAfterDisconnectWorks()
    {
        await using var device = new FakeZkDevice();
        await using var sdk = await ConnectedAsync(device);

        await sdk.DisconnectAsync(CancellationToken.None);
        await sdk.ConnectAsync(Endpoint(device), CancellationToken.None);

        Assert.True(sdk.IsConnected);
        Assert.Equal(2, device.ConnectCount);
        Assert.True((await sdk.UnlockAsync(TimeSpan.FromMilliseconds(500), CancellationToken.None))!.Succeeded);
    }

    // ---- gercek zamanli olay yerlesimi (saf) -------------------------------------------------

    [Fact]
    public void AttendanceParserReadsLegacyTenByteLayout()
    {
        byte[] data = [0x05, 0x00, 0x01, 0x00, 26, 9, 7, 12, 30, 15];

        Assert.True(ZkProtocolSdk.TryParseAttendance(data, out var pin, out var time));
        Assert.Equal("5", pin);
        Assert.Equal(new DateTime(2026, 9, 7, 12, 30, 15), time.DateTime);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(11)]
    public void AttendanceParserRejectsUnknownLengths(int length) =>
        Assert.False(ZkProtocolSdk.TryParseAttendance(new byte[length], out _, out _));

    [Theory]
    [InlineData(280, 10, 0, 28)]
    [InlineData(720, 10, 0, 72)]
    [InlineData(0, 0, 0, 28)]
    [InlineData(144, 0, 144, 72)]
    public void RecordSizeDetectionFollowsReference(int total, int users, int available, int expected) =>
        Assert.Equal(expected, ZkProtocolSdk.DetectRecordSize(total, users, available));

    // ---- yardimcilar ------------------------------------------------------------------------

    private static async Task<CardReadEvent> ReadOneAsync(ZkProtocolSdk sdk)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var card in sdk.ReadRealTimeCardsAsync(timeout.Token))
        {
            return card;
        }

        throw new InvalidOperationException("Olay gelmedi.");
    }

    private static async Task WaitForRegistrationAsync(FakeZkDevice device)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (device.RegisteredEventFlags.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.NotEmpty(device.RegisteredEventFlags);
    }

    private static async Task<List<CardReadEvent>> ReadManyAsync(ZkProtocolSdk sdk, int count)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var cards = new List<CardReadEvent>();
        await foreach (var card in sdk.ReadRealTimeCardsAsync(timeout.Token))
        {
            cards.Add(card);
            if (cards.Count == count) return cards;
        }

        throw new InvalidOperationException($"{count} olay beklendi, {cards.Count} geldi.");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.True(condition(), "Beklenen kosul zamaninda olusmadi.");
    }

    /// <summary>Elle ilerletilen saat: yanki penceresi ve damga, gercek zamana bagli kalmadan sinanir.</summary>
    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; private set; } = now;
        public void Advance(TimeSpan by) => Now += by;
        public override DateTimeOffset GetUtcNow() => Now.ToUniversalTime();
    }
}
