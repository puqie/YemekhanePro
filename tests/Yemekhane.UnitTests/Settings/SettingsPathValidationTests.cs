using Yemekhane.Application.Settings;

namespace Yemekhane.UnitTests.Settings;

/// <summary>
/// Yedek ve log dizinleri dosya sistemine yazan ve silen ayarlardir. settings.manage yetkisi
/// olan bir kullanicinin bu yollari sistem dizinlerine yonlendirebilmesi engellenmelidir.
/// </summary>
public sealed class SettingsPathValidationTests
{
    [Theory]
    [InlineData(@"relative\path")]
    [InlineData(@"..\..\Windows\System32")]
    [InlineData(@"C:\Backup\..\..\Windows\System32")]
    [InlineData(@"\\sunucu\paylasim")]
    public void BackupPathRejectsUnsafeLocations(string path)
    {
        var request = Request() with { Backup = Request().Backup with { Path = path } };

        Assert.Throws<ArgumentException>(() => SettingsValidation.Validate(request));
    }

    [Theory]
    [InlineData(@"relative\path")]
    [InlineData(@"..\..\Windows\System32")]
    [InlineData(@"\\sunucu\paylasim")]
    public void LogPathRejectsUnsafeLocations(string path)
    {
        var request = Request() with { Logs = Request().Logs with { Path = path } };

        Assert.Throws<ArgumentException>(() => SettingsValidation.Validate(request));
    }

    [Theory]
    [InlineData(@"C:\YemekhanePro\Backups")]
    [InlineData(@"D:\Yedek")]
    [InlineData(null)]
    [InlineData("")]
    public void AbsoluteLocalPathsRemainAccepted(string? path)
    {
        // Gecerli mutlak yollar ve "varsayilani kullan" anlamina gelen bos deger kabul edilmelidir.
        var request = Request() with { Backup = Request().Backup with { Path = path } };

        SettingsValidation.Validate(request);
    }

    private static SaveSettingsRequest Request() => new(new("Test Okulu", null, null, null),
        new("https://sms.example/", "Bearer", "user", "OKUL", 30, null),
        new(true, "Daily", DayOfWeek.Sunday, new TimeOnly(2, 0), 14, @"C:\Backup"),
        new("https://sync.example/", "device-1", 5, true, null), new("Information", 30, null));
}
