using System.IO;
using System.Xml.Linq;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Domain.Entities;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Tanimlar → Siniflar sekmesinde tur secimi (Normal / Anasınıfı): ekleme secilen turu
/// gonderir, yeniden adlandirma satirin turuyle acilir ve secileni gonderir; sube/bolum/gorev
/// sekmeleri tur gondermez. XAML baglari da denetlenir: kutu ViewModel'de olup ekranda
/// baglanmazsa ozellik "yazilmis" gorunur, sahada secilemez.
/// </summary>
public sealed class ClassKindDefinitionsTests
{
    private static readonly string[] AllPermissions = ["entitlements.manage", "students.read", "students.write"];

    [Fact]
    public async Task OnlyTheClassTabOffersKindsAndDefaultsToNormal()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();

        Assert.True(vm.Classes.IsClassTab);
        Assert.False(vm.Sections.IsClassTab);
        Assert.False(vm.Departments.IsClassTab);
        Assert.False(vm.Jobs.IsClassTab);
        Assert.Equal(ClassKinds.Normal, vm.Classes.NewClassKind);
        Assert.Equal(["Normal sınıf", "Anasınıfı"], vm.Classes.ClassKindOptions.Select(x => x.Label));
        Assert.Equal(["Normal", "Anasinifi"], vm.Classes.ClassKindOptions.Select(x => x.Value));
    }

    [Fact]
    public async Task AddingOnTheClassTabSendsTheChosenKindAndSaysSo()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        vm.Classes.NewName = "Anasınıfı A";
        vm.Classes.NewClassKind = ClassKinds.Preschool;

        await ((AsyncCommand)vm.Classes.AddCommand).ExecuteAsync(null);

        Assert.Equal(("classes", "Anasınıfı A", "Anasinifi"), api.Created.Single());
        Assert.Equal("Anasınıfı A (Anasınıfı) eklendi.", vm.Classes.StatusMessage);
        Assert.Equal("Anasınıfı", vm.Classes.SelectedItem?.KindLabel);
        // Ust uste anasinifi eklerken secim korunur; her seferinde yeniden secmek gerekmez.
        Assert.Equal(ClassKinds.Preschool, vm.Classes.NewClassKind);
    }

    [Fact]
    public async Task NonClassTabsNeverSendAKind()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        vm.Sections.NewName = "B";

        await ((AsyncCommand)vm.Sections.AddCommand).ExecuteAsync(null);
        vm.Sections.SelectedItem = vm.Sections.Items.Single(x => x.Name == "B");
        vm.Sections.OpenRenameCommand.Execute(null);
        vm.Sections.RenameName = "C";
        await ((AsyncCommand)vm.Sections.SaveRenameCommand).ExecuteAsync(null);

        Assert.Equal(("sections", "B", (string?)null), api.Created.Single());
        Assert.Null(api.Renamed.Single().ClassKind);
        Assert.Equal("C olarak yeniden adlandırıldı.", vm.Sections.StatusMessage);
        Assert.Equal("", vm.Sections.SelectedItem?.KindLabel);
    }

    [Fact]
    public async Task RenameOpensWithTheRowsKindAndSendsTheChosenOne()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        var preschool = vm.Classes.Items.Single(x => x.Kind == ClassKinds.Preschool);
        vm.Classes.SelectedItem = preschool;

        vm.Classes.OpenRenameCommand.Execute(null);
        Assert.Equal(ClassKinds.Preschool, vm.Classes.RenameClassKind);

        vm.Classes.RenameName = "1-A";
        vm.Classes.RenameClassKind = ClassKinds.Normal;
        await ((AsyncCommand)vm.Classes.SaveRenameCommand).ExecuteAsync(null);

        var renamed = api.Renamed.Single();
        Assert.Equal((preschool.Id, "1-A", "Normal"), (renamed.Id, renamed.Name, renamed.ClassKind));
        Assert.Equal("Normal sınıf", vm.Classes.SelectedItem?.KindLabel);
    }

    [Fact]
    public void ViewBindsKindPickersAndKindColumn()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Yemekhane.sln"))) directory = directory.Parent;
        Assert.NotNull(directory);
        var xaml = File.ReadAllText(Path.Combine(directory!.FullName, "src", "Yemekhane.Desktop", "Views", "DefinitionsView.xaml"));
        var document = XDocument.Parse(xaml);
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var combos = document.Descendants(ns + "ComboBox").Select(x => (string?)x.Attribute("SelectedValue")).ToList();

        Assert.Contains("{Binding NewClassKind}", combos);
        Assert.Contains("{Binding RenameClassKind}", combos);
        var kindColumn = document.Descendants(ns + "DataGridTextColumn").Single(x => (string?)x.Attribute("Header") == "TÜR");
        Assert.Equal("{Binding KindLabel}", (string?)kindColumn.Attribute("Binding"));
        Assert.Contains("Data.IsClassTab", (string?)kindColumn.Attribute("Visibility"));
        Assert.Contains("BindingProxy", xaml, StringComparison.Ordinal);
    }

    private sealed class FakeApi : IDefinitionsApiClient
    {
        private readonly Dictionary<string, List<LookupRecord>> lookups = new(StringComparer.Ordinal)
        {
            ["classes"] = [new(Guid.NewGuid(), "5A", 12, ClassKinds.Normal), new(Guid.NewGuid(), "Anasınıfı Sabah", 8, ClassKinds.Preschool)],
            ["sections"] = [new(Guid.NewGuid(), "A", 40)],
            ["departments"] = [],
            ["jobs"] = [],
        };

        public List<(string Kind, string Name, string? ClassKind)> Created { get; } = [];
        public List<(string Kind, Guid Id, string Name, string? ClassKind)> Renamed { get; } = [];

        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(bool includeInactive, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([new(Guid.NewGuid(), "Öğle Yemeği", null, null, true, 0)]);
        public Task<MealTypeDetails> CreateMealTypeAsync(SaveMealTypeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<MealTypeDetails> UpdateMealTypeAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeactivateMealTypeAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<LookupRecord>> LookupsAsync(string kind, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LookupRecord>>(lookups[kind].ToList());

        public Task<LookupRecord> CreateLookupAsync(string kind, string name, string? classKind = null, CancellationToken cancellationToken = default)
        {
            Created.Add((kind, name, classKind));
            var created = new LookupRecord(Guid.NewGuid(), name, 0, kind == "classes" ? ClassKinds.Normalize(classKind) : null);
            lookups[kind].Add(created);
            return Task.FromResult(created);
        }

        public Task<LookupRecord> RenameLookupAsync(string kind, Guid id, string name, string? classKind = null, CancellationToken cancellationToken = default)
        {
            Renamed.Add((kind, id, name, classKind));
            var list = lookups[kind];
            var index = list.FindIndex(x => x.Id == id);
            list[index] = list[index] with { Name = name, Kind = kind == "classes" ? ClassKinds.Normalize(classKind) ?? list[index].Kind : null };
            return Task.FromResult(list[index]);
        }

        public Task DeleteLookupAsync(string kind, Guid id, CancellationToken cancellationToken = default)
        {
            lookups[kind].RemoveAll(x => x.Id == id);
            return Task.CompletedTask;
        }
    }
}
