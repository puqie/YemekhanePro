using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Yemekhane.Application.Entitlements;
using Yemekhane.Application.Meals;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;
using Yemekhane.Desktop.ViewModels;
using Yemekhane.Desktop.Views;

namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// Tanimlar ekrani: sahte istemciyle ekle / yeniden adlandir / iki adimli sil / 409 mesaji,
/// saat ve ucret ayristirma, ogun cekmecesi; HTTP duzeyinde sinif ucunun DUZ JSON dizge
/// sozlesmesi; gercek ViewModel'le yerlesim (kesik sutun, dar kutu yok).
/// </summary>
[Collection(UiCollection.Name)]
public sealed class DefinitionsViewModelTests
{
    private static readonly string[] AllPermissions = ["entitlements.manage", "students.read", "students.write"];

    // ------------------------------------------------------------------ yukleme / yetki

    [Fact]
    public async Task InitializeLoadsMealsAndEveryLookupTabKeepingInactiveMealsLast()
    {
        var api = new FakeApi();
        api.Meals.Add(new(Guid.NewGuid(), "Kahvaltı", new(7, 0), new(8, 30), false, 0));
        var vm = new DefinitionsViewModel(api, AllPermissions);

        await vm.InitializeAsync();

        Assert.False(vm.HasError, vm.ErrorMessage);
        Assert.Equal(2, vm.Meals.Count);
        Assert.Equal("Öğle Yemeği", vm.Meals[0].Name);
        Assert.Equal("Kahvaltı", vm.Meals[1].Name);
        Assert.Equal("Pasif", vm.Meals[1].StatusText);
        Assert.Equal("07:00", vm.Meals[1].StartsText);
        Assert.Equal("", vm.Meals[0].StartsText);
        Assert.Equal("1 aktif, 1 pasif öğün", vm.MealsSummary);
        Assert.Equal(2, vm.Classes.Items.Count);
        Assert.Single(vm.Sections.Items);
        Assert.Empty(vm.Departments.Items);
        Assert.True(vm.Departments.IsEmpty);
        Assert.False(vm.Classes.IsEmpty);
        Assert.Equal(["classes", "sections", "departments", "jobs"], api.LoadedKinds);
    }

    [Fact]
    public async Task PermissionsGateMealsAndLookupsSeparately()
    {
        var api = new FakeApi();
        var mealsOnly = new DefinitionsViewModel(api, ["entitlements.manage"]);
        await mealsOnly.InitializeAsync();
        Assert.True(mealsOnly.CanManageMeals);
        Assert.False(mealsOnly.CanReadLookups);
        Assert.Empty(api.LoadedKinds);
        Assert.False(mealsOnly.Classes.AddCommand.CanExecute(null));

        var readOnly = new DefinitionsViewModel(new FakeApi(), ["students.read"]);
        await readOnly.InitializeAsync();
        Assert.False(readOnly.CanManageMeals);
        Assert.False(readOnly.OpenNewMealCommand.CanExecute(null));
        Assert.True(readOnly.CanReadLookups);
        Assert.False(readOnly.CanManageLookups);
        readOnly.Classes.NewName = "9Z";
        Assert.False(readOnly.Classes.AddCommand.CanExecute(null));
        Assert.Empty(readOnly.Meals);
    }

    [Fact]
    public async Task NetworkFailureShowsOfflineAndErrorWithoutFakeRows()
    {
        var vm = new DefinitionsViewModel(new FakeApi { FailLoad = true }, AllPermissions);
        await vm.InitializeAsync();
        Assert.True(vm.IsOffline);
        Assert.True(vm.HasError);
        Assert.Contains("Sunucuya ulaşılamadı", vm.ErrorMessage);
        Assert.Empty(vm.Meals);
    }

    // ------------------------------------------------------------------ sinif/sube/bolum/gorev

    [Fact]
    public async Task AddCreatesSelectsAndClearsTheInputBox()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        var tab = vm.Sections;
        Assert.False(tab.AddCommand.CanExecute(null));

        tab.NewName = "  F ";
        Assert.True(tab.AddCommand.CanExecute(null));
        await ((AsyncCommand)tab.AddCommand).ExecuteAsync(null);

        Assert.Equal(("sections", "F"), api.LastCreate);
        Assert.Equal("", tab.NewName);
        Assert.Equal(2, tab.Items.Count);
        Assert.NotNull(tab.SelectedItem);
        Assert.Equal("F", tab.SelectedItem!.Name);
        Assert.Equal("F eklendi.", tab.StatusMessage);
        Assert.False(tab.HasError);
    }

    [Fact]
    public async Task DuplicateNameShowsServerConflictMessageVerbatim()
    {
        var api = new FakeApi { CreateError = new ApiRequestException("Şube adı zaten kayıtlı.", HttpStatusCode.Conflict) };
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        vm.Sections.NewName = "A";
        await ((AsyncCommand)vm.Sections.AddCommand).ExecuteAsync(null);
        Assert.Equal("Şube adı zaten kayıtlı.", vm.Sections.ErrorMessage);
        Assert.Equal("A", vm.Sections.NewName);
    }

    [Fact]
    public async Task RenameOpensWithCurrentNameAndKeepsSelectionAfterReload()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        var tab = vm.Classes;
        Assert.False(tab.OpenRenameCommand.CanExecute(null));
        tab.SelectedItem = tab.Items.Single(x => x.Name == "5A");
        var id = tab.SelectedItem!.Id;

        tab.OpenRenameCommand.Execute(null);
        Assert.True(tab.IsRenameOpen);
        Assert.Equal("5A", tab.RenameName);
        tab.RenameName = "5-A";
        await ((AsyncCommand)tab.SaveRenameCommand).ExecuteAsync(null);

        Assert.Equal(("classes", id, "5-A"), api.LastRename);
        Assert.False(tab.IsRenameOpen);
        Assert.Equal(id, tab.SelectedItem?.Id);
        Assert.Equal("5-A", tab.SelectedItem?.Name);
        Assert.Contains("yeniden adlandırıldı", tab.StatusMessage);
    }

    [Fact]
    public async Task DeleteIsTwoStepAndCancelDisarms()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        var tab = vm.Classes;
        tab.SelectedItem = tab.Items.Single(x => x.Name == "6B");
        Assert.Equal("Sil", tab.DeleteButtonText);

        await ((AsyncCommand)tab.DeleteCommand).ExecuteAsync(null);
        Assert.True(tab.IsDeleteArmed);
        Assert.Equal("Silmeyi Onayla", tab.DeleteButtonText);
        Assert.Empty(api.Deleted);

        tab.CancelDeleteCommand.Execute(null);
        Assert.False(tab.IsDeleteArmed);
        Assert.Empty(api.Deleted);

        await ((AsyncCommand)tab.DeleteCommand).ExecuteAsync(null);
        // Baska satira gecmek de onayi dusurur: onay bir onceki satir icin verilmisti.
        tab.SelectedItem = tab.Items.Single(x => x.Name == "5A");
        Assert.False(tab.IsDeleteArmed);

        await ((AsyncCommand)tab.DeleteCommand).ExecuteAsync(null);
        await ((AsyncCommand)tab.DeleteCommand).ExecuteAsync(null);
        Assert.Single(api.Deleted);
        Assert.Equal("classes", api.Deleted[0].Kind);
        Assert.Single(tab.Items);
        Assert.Null(tab.SelectedItem);
        Assert.Equal("5A silindi.", tab.StatusMessage);
    }

    [Fact]
    public async Task DeleteConflictShowsTheServerMessageVerbatimAndDisarms()
    {
        const string message = "Sınıf 12 öğrencide kullanılıyor; önce öğrencileri başka bir tanıma taşıyın.";
        var api = new FakeApi { DeleteError = new ApiRequestException(message, HttpStatusCode.Conflict) };
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        vm.Classes.SelectedItem = vm.Classes.Items[0];

        await ((AsyncCommand)vm.Classes.DeleteCommand).ExecuteAsync(null);
        await ((AsyncCommand)vm.Classes.DeleteCommand).ExecuteAsync(null);

        Assert.Equal(message, vm.Classes.ErrorMessage);
        Assert.False(vm.Classes.IsDeleteArmed);
        Assert.Equal(2, vm.Classes.Items.Count);
    }

    // ------------------------------------------------------------------ ogunler

    [Theory]
    [InlineData("", true, null)]
    [InlineData("   ", true, null)]
    [InlineData("08:00", true, "08:00")]
    [InlineData("8:00", true, "08:00")]
    [InlineData("23:59", true, "23:59")]
    [InlineData("24:00", false, null)]
    [InlineData("8", false, null)]
    [InlineData("0800", false, null)]
    [InlineData("abc", false, null)]
    [InlineData("08:60", false, null)]
    public void TimeParsingAcceptsOnlyHourMinute(string text, bool ok, string? expected)
    {
        Assert.Equal(ok, DefinitionsViewModel.TryParseTime(text, out var time));
        Assert.Equal(expected, time?.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("", true, 0)]
    [InlineData("0", true, 0)]
    [InlineData("0,00", true, 0)]
    [InlineData("250,50", true, 250.50)]
    [InlineData("250.50", true, 250.50)]
    [InlineData("1.250,50", true, 1250.50)]
    [InlineData("1250", true, 1250)]
    [InlineData("₺ 99,9", true, 99.9)]
    [InlineData("12,345", false, 0)]
    [InlineData("abc", false, 0)]
    [InlineData("-5", false, 0)]
    public void PriceParsingFollowsTurkishAmountRulesButAllowsZero(string text, bool ok, decimal expected)
    {
        Assert.Equal(ok, DefinitionsViewModel.TryParsePrice(text, out var price));
        if (ok) Assert.Equal(expected, price);
    }

    [Fact]
    public void MealValidationReportsTheFirstProblemInTurkish()
    {
        Assert.Contains("2-100", DefinitionsViewModel.ValidateMeal("A", "", "", ""));
        Assert.Contains("Başlangıç saati", DefinitionsViewModel.ValidateMeal("Öğle", "abc", "", ""));
        Assert.Contains("Bitiş saati SS:dd", DefinitionsViewModel.ValidateMeal("Öğle", "11:00", "x", ""));
        Assert.Contains("birlikte", DefinitionsViewModel.ValidateMeal("Öğle", "11:00", "", ""));
        Assert.Contains("sonra olmalıdır", DefinitionsViewModel.ValidateMeal("Öğle", "13:00", "11:00", ""));
        Assert.Contains("Ücret", DefinitionsViewModel.ValidateMeal("Öğle", "11:00", "13:00", "abc"));
        Assert.Contains("100.000", DefinitionsViewModel.ValidateMeal("Öğle", "11:00", "13:00", "100.001"));
        Assert.Null(DefinitionsViewModel.ValidateMeal("Öğle", "11:00", "13:00", "250,50"));
        Assert.Null(DefinitionsViewModel.ValidateMeal("Öğle", "", "", ""));
    }

    [Fact]
    public async Task NewMealSendsParsedValuesClosesDrawerAndSelectsSavedRow()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();

        vm.OpenNewMealCommand.Execute(null);
        Assert.True(vm.IsMealOpen);
        Assert.Equal("Yeni Öğün", vm.MealFormTitle);
        vm.MealName = "Akşam Yemeği"; vm.MealStartsAt = "17:30"; vm.MealEndsAt = "19:00"; vm.MealPriceText = "250.50";
        await ((AsyncCommand)vm.SaveMealCommand).ExecuteAsync(null);

        var sent = Assert.Single(api.CreatedMeals);
        Assert.Equal("Akşam Yemeği", sent.Name);
        Assert.Equal(new TimeOnly(17, 30), sent.StartsAt);
        Assert.Equal(new TimeOnly(19, 0), sent.EndsAt);
        Assert.Equal(250.50m, sent.Price);
        Assert.True(sent.IsActive);
        Assert.False(vm.IsMealOpen);
        Assert.Equal("Akşam Yemeği", vm.SelectedMeal?.Name);
        Assert.Equal(250.50m, vm.SelectedMeal?.Price);
        Assert.Equal("Akşam Yemeği kaydedildi.", vm.StatusMessage);
    }

    [Fact]
    public async Task InvalidMealStaysInDrawerWithMessageAndNothingIsSent()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        vm.OpenNewMealCommand.Execute(null);
        vm.MealName = "Akşam"; vm.MealStartsAt = "17:30"; vm.MealEndsAt = "";
        await ((AsyncCommand)vm.SaveMealCommand).ExecuteAsync(null);
        Assert.True(vm.IsMealOpen);
        Assert.True(vm.HasMealError);
        Assert.Contains("birlikte", vm.MealError);
        Assert.Empty(api.CreatedMeals);
    }

    [Fact]
    public async Task EditMealPrefillsFromSelectionAndUpdatesById()
    {
        var api = new FakeApi();
        var meal = new MealTypeDetails(Guid.NewGuid(), "Kahvaltı", new(7, 0), new(8, 30), true, 120);
        api.Meals.Add(meal);
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        Assert.False(vm.OpenEditMealCommand.CanExecute(null));
        vm.SelectedMeal = vm.Meals.Single(x => x.Id == meal.Id);

        vm.OpenEditMealCommand.Execute(null);
        Assert.Equal("Öğünü Düzenle", vm.MealFormTitle);
        Assert.Equal("Kahvaltı", vm.MealName);
        Assert.Equal("07:00", vm.MealStartsAt);
        Assert.Equal("08:30", vm.MealEndsAt);
        Assert.Equal("120,00", vm.MealPriceText);
        vm.MealPriceText = "135,25"; vm.MealIsActive = false;
        await ((AsyncCommand)vm.SaveMealCommand).ExecuteAsync(null);

        var (id, request) = Assert.Single(api.UpdatedMeals);
        Assert.Equal(meal.Id, id);
        Assert.Equal(135.25m, request.Price);
        Assert.False(request.IsActive);
        Assert.Equal(meal.Id, vm.SelectedMeal?.Id);
        Assert.Equal("Pasif", vm.SelectedMeal?.StatusText);
    }

    [Fact]
    public async Task ServerValidationMessageReachesTheDrawer()
    {
        var api = new FakeApi { CreateMealError = new ApiRequestException("Öğün adı zaten kayıtlı.", HttpStatusCode.Conflict) };
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        vm.OpenNewMealCommand.Execute(null);
        vm.MealName = "Öğle Yemeği";
        await ((AsyncCommand)vm.SaveMealCommand).ExecuteAsync(null);
        Assert.True(vm.IsMealOpen);
        Assert.Equal("Öğün adı zaten kayıtlı.", vm.MealError);
    }

    [Fact]
    public async Task DeactivateOnlyForActiveMealAndReloadsList()
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        await vm.InitializeAsync();
        Assert.False(vm.DeactivateMealCommand.CanExecute(null));
        vm.SelectedMeal = vm.Meals[0];
        Assert.True(vm.DeactivateMealCommand.CanExecute(null));

        await ((AsyncCommand)vm.DeactivateMealCommand).ExecuteAsync(null);

        Assert.Single(api.DeactivatedMeals);
        Assert.Equal("Pasif", vm.Meals.Single().StatusText);
        Assert.Equal("Öğle Yemeği pasifleştirildi.", vm.StatusMessage);
        Assert.False(vm.DeactivateMealCommand.CanExecute(null));
    }

    // ------------------------------------------------------------------ HTTP sozlesmesi

    [Fact]
    public async Task ClassCreateSendsPlainJsonStringWhileOthersSendNameObject()
    {
        var requests = new List<(HttpMethod Method, string Path, string Body)>();
        var handler = new RecordingHandler(async request =>
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync();
            requests.Add((request.Method, request.RequestUri!.PathAndQuery, body));
            var json = request.RequestUri.AbsolutePath.EndsWith("/classes", StringComparison.Ordinal)
                ? JsonSerializer.Serialize(new { id = Guid.NewGuid(), name = "5A", isActive = true })
                : JsonSerializer.Serialize(new { id = Guid.NewGuid(), name = "Fen", studentCount = 0 });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        });
        var client = new DefinitionsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new StaticSession());

        var created = await client.CreateLookupAsync("classes", "5A");
        await client.CreateLookupAsync("departments", "Fen");
        await client.RenameLookupAsync("jobs", Guid.Empty, "Aşçı");

        Assert.Equal("5A", created.Name);
        Assert.Equal(0, created.StudentCount);
        Assert.Equal("/api/organization/classes", requests[0].Path);
        Assert.Equal("\"5A\"", requests[0].Body);
        Assert.Equal("/api/organization/departments", requests[1].Path);
        Assert.Equal("{\"name\":\"Fen\"}", requests[1].Body);
        Assert.Equal(HttpMethod.Put, requests[2].Method);
        Assert.Equal("/api/organization/jobs/00000000-0000-0000-0000-000000000000", requests[2].Path);
        // System.Text.Json Turkce harfleri \u kacisiyla yazar; ayristirip karsilastirmak yeterli.
        Assert.Equal("Aşçı", JsonDocument.Parse(requests[2].Body).RootElement.GetProperty("name").GetString());
        Assert.All(handler.Requests, r => Assert.Equal("Bearer", r.Headers.Authorization?.Scheme));
    }

    [Fact]
    public async Task ConflictProblemTitleBecomesTheExceptionMessage()
    {
        const string title = "Sınıf 12 öğrencide kullanılıyor; önce öğrencileri başka bir tanıma taşıyın.";
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(JsonSerializer.Serialize(new { title, status = 409 }), Encoding.UTF8, "application/problem+json")
        }));
        var client = new DefinitionsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new StaticSession());

        var error = await Assert.ThrowsAsync<ApiRequestException>(() => client.DeleteLookupAsync("classes", Guid.NewGuid()));

        Assert.Equal(title, error.Message);
        Assert.Equal(HttpStatusCode.Conflict, error.StatusCode);
        Assert.Equal(HttpMethod.Delete, handler.Requests.Single().Method);
    }

    [Fact]
    public async Task MealTypeListAsksForInactiveWhenRequested()
    {
        var handler = new RecordingHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("[]", Encoding.UTF8, "application/json")
        }));
        var client = new DefinitionsApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") }, new StaticSession());
        await client.MealTypesAsync(includeInactive: true);
        await client.MealTypesAsync(includeInactive: false);
        Assert.Equal("/api/meal-types?includeInactive=true", handler.Requests[0].RequestUri!.PathAndQuery);
        Assert.Equal("/api/meal-types?includeInactive=false", handler.Requests[1].RequestUri!.PathAndQuery);
    }

    // ------------------------------------------------------------------ hakedis cekmecesi bedeli

    [Fact]
    public async Task GrantDrawerShowsMealPriceAndPreviewTotalOnlyWhenPriced()
    {
        var api = new FakeEntitlementApi();
        var vm = new MealEntitlementsViewModel(api, ["entitlements.manage", "entitlements.bulk"]);
        await vm.InitializeAsync();
        Assert.Equal("Öğle Yemeği", vm.GrantMeal?.Name);
        Assert.True(vm.HasGrantMealPrice);
        Assert.Equal("Öğün bedeli: ₺250,00", vm.GrantMealPriceText);

        vm.TargetType = "All"; vm.QuantityText = "2";
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);
        Assert.True(vm.HasPreview, vm.PreviewMessage);
        // 3 ogrenci x 5 gun = 15 hak, gunluk 2 adet, 250 TL -> 7.500 TL
        Assert.Equal(7500m, vm.PreviewTotal);
        Assert.True(vm.HasPreviewTotal);
        Assert.Equal("Toplam bedel: ₺7.500,00", vm.PreviewTotalText);

        vm.GrantMeal = vm.MealTypes.Single(x => x.Name == "Kahvaltı");
        Assert.False(vm.HasGrantMealPrice);
        Assert.Equal("", vm.GrantMealPriceText);
        Assert.False(vm.HasPreview);
        Assert.False(vm.HasPreviewTotal);
        await ((AsyncCommand)vm.PreviewCommand).ExecuteAsync(null);
        Assert.True(vm.HasPreview);
        Assert.False(vm.HasPreviewTotal);
        Assert.Equal("", vm.PreviewTotalText);
    }

    [Fact]
    public async Task OpeningGrantDrawerRefreshesPricesChangedInDefinitions()
    {
        var api = new FakeEntitlementApi { LunchPrice = 0 };
        var vm = new MealEntitlementsViewModel(api, ["entitlements.manage", "entitlements.bulk"]);
        await vm.InitializeAsync();
        Assert.False(vm.HasGrantMealPrice);
        var before = vm.MealTypes.ToList();

        // Kullanici Tanimlar'da ucreti girdi; Hakedis ekrani yeniden baslatilmadi.
        api.LunchPrice = 250;
        vm.OpenGrantCommand.Execute(null);
        await UntilAsync(() => vm.HasGrantMealPrice);

        Assert.Equal("Öğün bedeli: ₺250,00", vm.GrantMealPriceText);
        Assert.Equal(2, vm.MealTypes.Count);
        Assert.Equal("Öğle Yemeği", vm.GrantMeal?.Name);
        // Degismeyen kayit (Kahvalti) ayni nesne olarak kalir: liste bosaltilip doldurulmadi.
        Assert.Same(before[1], vm.MealTypes[1]);
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.True(condition());
    }

    // ------------------------------------------------------------------ yerlesim

    [Fact]
    public void ViewWithRealViewModelHasNoClippedHeadersOrNarrowInputs() => UiThread.Run(() =>
    {
        var api = new FakeApi();
        var vm = new DefinitionsViewModel(api, AllPermissions);
        vm.InitializeAsync().GetAwaiter().GetResult();
        var view = new DefinitionsView { DataContext = vm };
        var host = UiThread.Host(view, 1600, 900);
        Layout(host);

        var mealGrid = Assert.IsType<DataGrid>(view.FindName("MealsGrid"));
        Assert.Equal(5, mealGrid.Columns.Count);
        AssertHeadersFit(mealGrid);
        // Ucret sutunu Turkce para bicimiyle (₺, virgul) cizilir; ham "250" ya da "250.00" degil.
        Assert.Contains(Descendants(view).OfType<TextBlock>(), t => t.Text == "₺250,00");

        // Sinif sekmesi: ekleme kutusu kullanilabilir genislikte, "Henüz kayıt yok" gizli.
        vm.SelectedTabIndex = 1; Layout(host);
        var inputs = Descendants(view).OfType<TextBox>().Where(t => t.ActualWidth > 0).ToList();
        Assert.NotEmpty(inputs);
        Assert.All(inputs, t => Assert.True(t.ActualWidth >= 80, $"dar kutu: {t.ActualWidth:F0}px"));
        var lookupGrid = Descendants(view).OfType<DataGrid>().Single(g => g.ActualWidth > 0 && g != mealGrid);
        Assert.Equal(2, lookupGrid.Columns.Count);
        AssertHeadersFit(lookupGrid);
        var empty = Descendants(view).OfType<TextBlock>().Single(t => t.Text == "Henüz kayıt yok" && t.IsDescendantOf(lookupGrid.Parent));
        Assert.Equal(Visibility.Collapsed, empty.Visibility);

        // Bolumler sekmesi bos: "Henüz kayıt yok" gorunur.
        vm.SelectedTabIndex = 3; Layout(host);
        var emptyDepartments = Descendants(view).OfType<TextBlock>().Where(t => t.Text == "Henüz kayıt yok" && t.ActualWidth > 0).ToList();
        Assert.Single(emptyDepartments);

        // Cekmece acilinca alanlar olculebilir ve etiketli.
        vm.SelectedTabIndex = 0; vm.OpenNewMealCommand.Execute(null); Layout(host);
        var drawerInputs = Descendants(view).OfType<TextBox>().Where(t => t.ActualWidth > 0).ToList();
        Assert.Equal(4, drawerInputs.Count);
        Assert.All(drawerInputs, t => Assert.True(t.ActualWidth >= 80, $"dar kutu: {t.ActualWidth:F0}px"));
    });

    private static void Layout(FrameworkElement host)
    {
        host.Measure(new Size(1600, 900)); host.Arrange(new Rect(0, 0, 1600, 900)); host.UpdateLayout();
    }

    private static void AssertHeadersFit(DataGrid grid)
    {
        var clipped = new List<string>();
        foreach (var header in Descendants(grid).OfType<DataGridColumnHeader>())
        {
            if (header.Content is not string title) continue;
            var text = Descendants(header).OfType<TextBlock>().FirstOrDefault();
            if (text is null) continue;
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            if (text.DesiredSize.Width + header.Padding.Left + header.Padding.Right > header.ActualWidth + 0.5)
                clipped.Add($"{title}: {text.DesiredSize.Width:F0}px > {header.ActualWidth:F0}px");
        }
        Assert.True(clipped.Count == 0, "kesik başlık: " + string.Join(", ", clipped));
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grandChild in Descendants(child)) yield return grandChild;
        }
    }

    // ------------------------------------------------------------------ sahteler

    private sealed class StaticSession : IJwtSession
    {
        public string? AccessToken => "token";
        public bool IsAuthenticated => true;
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Requests.Add(request); return respond(request); }
    }

    private sealed class FakeApi : IDefinitionsApiClient
    {
        private readonly Dictionary<string, List<LookupRecord>> lookups = new(StringComparer.Ordinal)
        {
            ["classes"] = [new(Guid.NewGuid(), "5A", 12), new(Guid.NewGuid(), "6B", 0)],
            ["sections"] = [new(Guid.NewGuid(), "A", 40)],
            ["departments"] = [],
            ["jobs"] = [],
        };
        public List<MealTypeDetails> Meals { get; } = [new(Guid.NewGuid(), "Öğle Yemeği", null, null, true, 250)];
        public List<string> LoadedKinds { get; } = [];
        public List<(string Kind, Guid Id)> Deleted { get; } = [];
        public List<SaveMealTypeRequest> CreatedMeals { get; } = [];
        public List<(Guid Id, SaveMealTypeRequest Request)> UpdatedMeals { get; } = [];
        public List<Guid> DeactivatedMeals { get; } = [];
        public (string Kind, string Name)? LastCreate { get; private set; }
        public (string Kind, Guid Id, string Name)? LastRename { get; private set; }
        public bool FailLoad { get; init; }
        public Exception? CreateError { get; init; }
        public Exception? DeleteError { get; init; }
        public Exception? CreateMealError { get; init; }

        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(bool includeInactive, CancellationToken cancellationToken = default)
        {
            if (FailLoad) throw new HttpRequestException("bağlantı yok");
            return Task.FromResult<IReadOnlyList<MealTypeDetails>>(Meals.Where(m => includeInactive || m.IsActive).ToList());
        }
        public Task<MealTypeDetails> CreateMealTypeAsync(SaveMealTypeRequest request, CancellationToken cancellationToken = default)
        {
            if (CreateMealError is not null) throw CreateMealError;
            CreatedMeals.Add(request);
            var created = new MealTypeDetails(Guid.NewGuid(), request.Name, request.StartsAt, request.EndsAt, request.IsActive, request.Price);
            Meals.Add(created); return Task.FromResult(created);
        }
        public Task<MealTypeDetails> UpdateMealTypeAsync(Guid id, SaveMealTypeRequest request, CancellationToken cancellationToken = default)
        {
            UpdatedMeals.Add((id, request));
            var index = Meals.FindIndex(m => m.Id == id);
            var updated = new MealTypeDetails(id, request.Name, request.StartsAt, request.EndsAt, request.IsActive, request.Price);
            Meals[index] = updated; return Task.FromResult(updated);
        }
        public Task DeactivateMealTypeAsync(Guid id, CancellationToken cancellationToken = default)
        {
            DeactivatedMeals.Add(id);
            var index = Meals.FindIndex(m => m.Id == id);
            Meals[index] = Meals[index] with { IsActive = false };
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<LookupRecord>> LookupsAsync(string kind, CancellationToken cancellationToken = default)
        {
            if (FailLoad) throw new HttpRequestException("bağlantı yok");
            LoadedKinds.Add(kind);
            return Task.FromResult<IReadOnlyList<LookupRecord>>(lookups[kind].ToList());
        }
        public Task<LookupRecord> CreateLookupAsync(string kind, string name, CancellationToken cancellationToken = default)
        {
            if (CreateError is not null) throw CreateError;
            LastCreate = (kind, name);
            var created = new LookupRecord(Guid.NewGuid(), name, 0);
            lookups[kind].Add(created); return Task.FromResult(created);
        }
        public Task<LookupRecord> RenameLookupAsync(string kind, Guid id, string name, CancellationToken cancellationToken = default)
        {
            LastRename = (kind, id, name);
            var list = lookups[kind]; var index = list.FindIndex(x => x.Id == id);
            list[index] = list[index] with { Name = name }; return Task.FromResult(list[index]);
        }
        public Task DeleteLookupAsync(string kind, Guid id, CancellationToken cancellationToken = default)
        {
            if (DeleteError is not null) throw DeleteError;
            Deleted.Add((kind, id)); lookups[kind].RemoveAll(x => x.Id == id); return Task.CompletedTask;
        }
    }

    private sealed class FakeEntitlementApi : IMealEntitlementApiClient
    {
        private readonly Guid lunchId = Guid.NewGuid();
        private readonly MealTypeDetails breakfast = new(Guid.NewGuid(), "Kahvaltı", null, null, true, 0);
        public decimal LunchPrice { get; set; } = 250;
        public Task<MealEntitlementPage> SearchAsync(MealEntitlementQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult(new MealEntitlementPage([], query.Page, query.PageSize, 0, new MealEntitlementSummary(0, 0, 0)));
        public Task<IReadOnlyList<MealTypeDetails>> MealTypesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<MealTypeDetails>>([new(lunchId, "Öğle Yemeği", null, null, true, LunchPrice), breakfast]);
        public Task<IReadOnlyList<ClassRecord>> ClassesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ClassRecord>>([]);
        public Task<IReadOnlyList<GroupRecord>> GroupsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<GroupRecord>>([]);
        public Task<EntitlementPreview> PreviewAsync(EntitlementGrantRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new EntitlementPreview(3, 5, 15, 15, 0, "TOKEN"));
        public Task<BulkEntitlementResult> ApplyAsync(ApplyEntitlementGrantRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<CancelEntitlementsResult> CancelAsync(CancelEntitlementsRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
