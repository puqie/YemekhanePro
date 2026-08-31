using System.Reflection;

namespace Yemekhane.Desktop;

/// <summary>
/// Uygulama surumu. Kullanici destek isterken bu degeri okuyabilmelidir;
/// yalnizca dosya ozelliklerinde bulunmasi yeterli degildir.
/// </summary>
public static class AppVersion
{
    /// <summary>Arayuzde gosterilecek surum metni (ornegin "1.0.3").</summary>
    public static string Display { get; } = Resolve();

    private static string Resolve()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        // Derleme meta verisi ("1.0.3+a1b2c3") kullaniciya gosterilmez; yalnizca surum numarasi kalir.
        var plus = informational.IndexOf('+', StringComparison.Ordinal);
        return plus < 0 ? informational : informational[..plus];
    }
}
