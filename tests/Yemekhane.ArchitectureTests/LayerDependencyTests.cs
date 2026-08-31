using System.Xml.Linq;

namespace Yemekhane.ArchitectureTests;

public sealed class LayerDependencyTests
{
    private static readonly string[] ProductProjects =
    [
        "Yemekhane.Domain",
        "Yemekhane.Application",
        "Yemekhane.Infrastructure",
        "Yemekhane.Devices",
        "Yemekhane.Reports",
        "Yemekhane.Sync",
        "Yemekhane.Api",
        "Yemekhane.Desktop"
    ];

    public static TheoryData<string, string[]> LayerRules => new()
    {
        { "Yemekhane.Domain", [] },
        { "Yemekhane.Application", ["Yemekhane.Domain"] },
        { "Yemekhane.Infrastructure", ["Yemekhane.Application", "Yemekhane.Domain"] },
        { "Yemekhane.Devices", ["Yemekhane.Application", "Yemekhane.Domain"] },
        { "Yemekhane.Reports", ["Yemekhane.Application", "Yemekhane.Domain"] },
        { "Yemekhane.Sync", ["Yemekhane.Application", "Yemekhane.Domain"] }
    };

    [Theory]
    [MemberData(nameof(LayerRules))]
    public void CoreLayersOnlyReferenceAllowedProjects(string projectName, string[] allowedReferences)
    {
        var actualReferences = ReadProductReferences(projectName);

        Assert.Equal(allowedReferences.Order(), actualReferences.Order());
    }

    private static string[] ReadProductReferences(string projectName)
    {
        var projectPath = Path.Combine(FindSolutionRoot(), "src", projectName, $"{projectName}.csproj");
        var project = XDocument.Load(projectPath);

        return project
            .Descendants("ProjectReference")
            .Select(reference => Path.GetFileNameWithoutExtension(reference.Attribute("Include")?.Value))
            .Where(reference => reference is not null && ProductProjects.Contains(reference))
            .Cast<string>()
            .ToArray();
    }

    private static string FindSolutionRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Yemekhane.sln çözüm kökü bulunamadı.");
    }
}
