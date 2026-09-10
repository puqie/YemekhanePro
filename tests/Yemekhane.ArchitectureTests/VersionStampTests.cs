using System.Text.RegularExpressions;

namespace Yemekhane.ArchitectureTests;

/// <summary>
/// Directory.Build.props icindeki &lt;Version&gt; TEK surum kaynagidir.
///
/// Kurulum uretilirken betik bunu -p:Version ile gecersiz kilar, ama kullanici
/// kendi "dotnet build"ini yaptiginda O DEGER gorunur. Bir kez geride kaldi:
/// kurulumlar 1.3.22 iken depodaki sabit 1.2.0'da durmustu ve program kendini
/// 1.2.0 diye tanitiyordu -- sahibi "surum geri gitti, veri mi kayboldu?" diye
/// hakli olarak endiselendi.
///
/// Bu testler sabit bir SAYIYA baglanmaz (her surumde testi guncellemek gerekirdi);
/// degerin var, bicimli ve YER TUTUCU OLMADIGINI dogrular.
/// </summary>
public sealed class VersionStampTests
{
    private static string Version()
    {
        var props = File.ReadAllText(Path.Combine(FindRoot(), "Directory.Build.props"));
        var match = Regex.Match(props, @"<Version>([^<]+)</Version>");
        Assert.True(match.Success, "Directory.Build.props içinde <Version> bulunamadı.");
        return match.Groups[1].Value.Trim();
    }

    /// <summary>Surum uc parcali olmalidir; kurulum ve MSI bu bicimi bekler.</summary>
    [Fact]
    public void TheVersionIsAThreePartNumber() =>
        Assert.Matches(@"^\d+\.\d+\.\d+$", Version());

    /// <summary>
    /// Sablondan kalan yer tutucular kabul edilmez: bunlar "surum unutuldu"
    /// isaretidir ve tam olarak yasanan karisikligi dogurur.
    /// </summary>
    [Theory]
    [InlineData("0.0.0")]
    [InlineData("1.0.0")]
    public void TheVersionIsNotAPlaceholder(string placeholder) =>
        Assert.NotEqual(placeholder, Version());

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Yemekhane.sln çözüm kökü bulunamadı.");
    }
}
