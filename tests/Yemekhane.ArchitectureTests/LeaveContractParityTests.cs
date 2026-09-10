using System.Text.RegularExpressions;

namespace Yemekhane.ArchitectureTests;

/// <summary>
/// Masaustundeki IZIN secenekleri, sunucunun kabul ettikleriyle AYNI olmalidir.
///
/// <para>
/// "İzin Ver" dugmesi uzun sure kullaniciya HICBIR SEY SORMUYORDU: alanlar ViewModel'de
/// vardi ama hicbir XAML dosyasina bagli degildi, dolayisiyla hep BUGUN icin, turu
/// "Mazeret", davranisi "Keep" bir kayit aciliyordu. Sunucunun destekledigi "Cancel"
/// (haklari iptal et, tahsilat iade edilir) ve "NextBusinessDay" (sonraki is gunune
/// aktar) davranislari masaustunden ERISILEMEZDI.
/// </para>
/// <para>
/// Form eklendikten sonra asil risk AYRISMADIR: biri yeni bir davranis ekleyip digerini
/// guncellemezse kullanici "İzin yemek hakkı davranışı geçersiz." hatasi alir ve nedenini
/// anlayamaz. Bu test iki listeyi KAYNAKTAN okuyup karsilastirir; ViewModel ornegi
/// kurmadigi icin kurucu imzasi degisse bile kirilmaz.
/// </para>
/// </summary>
public sealed class LeaveContractParityTests
{
    [Fact]
    public void TheDesktopOffersExactlyTheBehavioursTheServerAccepts()
    {
        var root = FindRoot();

        var serverSource = File.ReadAllText(Path.Combine(root,
            "src", "Yemekhane.Application", "Leaves", "LeaveService.cs"));
        var serverMatch = Regex.Match(serverSource, @"Behaviors\s*=\s*\[([^\]]*)\]");
        Assert.True(serverMatch.Success, "LeaveService içinde Behaviors listesi bulunamadı.");
        var server = Regex.Matches(serverMatch.Groups[1].Value, @"""([^""]+)""")
            .Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        var desktopSource = File.ReadAllText(Path.Combine(root,
            "src", "Yemekhane.Desktop", "ViewModels", "StudentsViewModel.cs"));
        var desktopMatch = Regex.Match(desktopSource,
            @"LeaveBehaviors\s*\{\s*get;\s*\}\s*=\s*\[(.*?)\];", RegexOptions.Singleline);
        Assert.True(desktopMatch.Success, "StudentsViewModel içinde LeaveBehaviors listesi bulunamadı.");
        // new("Etiket", "Kod") ciftlerinin IKINCI dizesi sunucuya giden koddur.
        var desktop = Regex.Matches(desktopMatch.Groups[1].Value, @"new\(\s*""[^""]*""\s*,\s*""([^""]+)""\s*\)")
            .Select(x => x.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(server, desktop);
    }

    /// <summary>Izin formu EKRANA BAGLI olmali; alanlar bagsiz kalirsa varsayilanlar sessizce gider.</summary>
    [Theory]
    [InlineData("LeaveStartsOn")]
    [InlineData("LeaveEndsOn")]
    [InlineData("LeaveType")]
    [InlineData("LeaveBehavior")]
    public void EveryLeaveFieldIsBoundInTheView(string field)
    {
        var view = File.ReadAllText(Path.Combine(FindRoot(),
            "src", "Yemekhane.Desktop", "Views", "StudentsView.xaml"));

        Assert.Contains(field, view, StringComparison.Ordinal);
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
            directory = directory.Parent;
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Yemekhane.sln çözüm kökü bulunamadı.");
    }
}
