using System.Windows.Controls;
using Xunit;

namespace Yemekhane.UnitTests.Desktop.Live;

[Collection("UI")]
public class DashboardJourney
{
    [Fact]
    public void KpiHizliIslemCanliGecisVeCihazDurumu() => LiveUiHarness.Run(ui =>
    {
        using var db = LiveDb.Open();
        var vm = ui.Dashboard;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(vm.RealtimeState)) ui.Note($"{DateTime.Now:HH:mm:ss.fff} gerçek zamanlı durum -> {vm.RealtimeState}");
        };
        ui.LoadAll();
        ui.Note($"{DateTime.Now:HH:mm:ss.fff} LoadAll sonrası: {vm.RealtimeState} / {vm.ConnectionText}");
        ui.Navigate("dashboard");
        Journey.Until(ui, () => !vm.IsLoading && vm.Snapshot is not null, "dashboard");
        var snapshot = vm.Snapshot!;
        var today = Journey.Today;

        // 1) 7 KPI SQLite ile birebir.
        var entitlements = $"FROM meal_entitlements e WHERE e.EntitlementDate = '{today}' AND e.Status = 'Active'";
        var quantity = db.Count($"SELECT COALESCE(SUM(e.Quantity), 0) {entitlements}");
        var used = db.Count($"SELECT COALESCE(SUM(e.ConsumedQuantity), 0) {entitlements}");
        Assert.Equal(db.Count("SELECT COUNT(*) FROM students WHERE IsActive = 1 AND IsDeleted = 0"), snapshot.Kpis.ActiveStudents);
        Assert.Equal(db.Count($"SELECT COUNT(DISTINCT e.StudentId) {entitlements}"), snapshot.Kpis.EntitledStudents);
        Assert.Equal(quantity, snapshot.Kpis.EntitlementQuantity);
        Assert.Equal(used, snapshot.Kpis.Used);
        Assert.Equal(Math.Max(0, quantity - used), snapshot.Kpis.Remaining);
        Assert.Equal(db.Count($"SELECT COUNT(DISTINCT l.StudentId) FROM student_leaves l JOIN students s ON s.Id = l.StudentId AND s.IsActive = 1 WHERE l.StartsOn <= '{today}' AND l.EndsOn >= '{today}'"), snapshot.Kpis.OnLeave);
        var range = LiveDb.Range("a.Timestamp", today, today);
        var todayTotal = db.Count($"SELECT COUNT(*) FROM access_logs a JOIN devices d ON d.Id = a.DeviceId WHERE {range}");
        Assert.Equal(db.Count($"SELECT COUNT(*) FROM access_logs a WHERE {range} AND a.Decision = 'DENY'"), snapshot.Kpis.Denied);
        ui.Note($"KPI: aktif {snapshot.Kpis.ActiveStudents}, hak sahibi {snapshot.Kpis.EntitledStudents}, hakediş {quantity}, kullanılan {used}, kalan {snapshot.Kpis.Remaining}, izinli {snapshot.Kpis.OnLeave}, reddedilen {snapshot.Kpis.Denied}");

        // 2) Canli gecisler son 20; sinif ozeti; son hatalar; cihaz durumu.
        Assert.Equal((int)Math.Min(20, todayTotal), snapshot.RecentAccess.Count);
        Assert.Equal(snapshot.RecentAccess.OrderByDescending(x => x.Timestamp).Select(x => x.Id), snapshot.RecentAccess.Select(x => x.Id));
        var topClass = db.Text($"SELECT COALESCE(c.Name, 'Sınıfsız') FROM meal_entitlements e JOIN students s ON s.Id = e.StudentId LEFT JOIN classes c ON c.Id = s.ClassId WHERE e.EntitlementDate = '{today}' AND e.Status = 'Active' GROUP BY COALESCE(c.Name, 'Sınıfsız') ORDER BY SUM(e.ConsumedQuantity) DESC, COALESCE(c.Name, 'Sınıfsız') LIMIT 1");
        Assert.True(snapshot.ClassUsage.Count is > 0 and <= 10);
        Assert.Equal(topClass, snapshot.ClassUsage[0].ClassName);
        Assert.Equal(used, snapshot.ClassUsage.Sum(x => x.Used));
        Assert.Equal((int)Math.Min(10, db.Count("SELECT COUNT(*) FROM device_events WHERE Severity IN ('Error', 'Critical')")), snapshot.RecentErrors.Count);
        Assert.Equal(db.Count("SELECT COUNT(*) FROM devices WHERE IsActive = 1"), snapshot.DeviceSummary.Total);
        Assert.Equal(db.Count("SELECT COUNT(*) FROM devices WHERE IsActive = 1 AND ConnectionStatus IN ('Online', 'Connected')"), snapshot.DeviceSummary.Online);
        Assert.Equal(db.Count("SELECT COUNT(*) FROM devices WHERE IsActive = 1 AND ConnectionStatus IN ('Offline', 'Disconnected')"), snapshot.DeviceSummary.Offline);
        Assert.Equal(db.Count("SELECT COUNT(*) FROM devices WHERE IsActive = 1 AND ConnectionStatus = 'Error'"), snapshot.DeviceSummary.Error);
        var host = (Grid)ui.Window.FindName("DashboardHost");
        var texts = Journey.TextsIn(ui, host).ToList();
        Assert.DoesNotContain("OK", texts);
        Assert.Contains(texts, x => x is "Geçiş onaylandı" or "İzin Verildi");
        Assert.DoesNotContain(texts, x => x is "ALLOW" or "DENY" or "Online" or "Offline" or "Error" or "Reconnecting");
        ui.Shot("dashboard-01");

        // 3) Yenile.
        vm.RefreshCommand.Execute(null);
        ui.Delay(300);
        Journey.Until(ui, () => !vm.IsLoading, "yenile");
        Assert.True(vm.Snapshot!.GeneratedAt >= snapshot.GeneratedAt);

        // 4) Gercek zamanli: gercek gecis KPI ve listeyi gunceller.
        Assert.Equal("Canlı", vm.ConnectionText);
        var deviceId = Guid.Parse(db.Text("SELECT Id FROM devices WHERE Name LIKE 'Yemekhane Giri%'")!);
        var mealId = Guid.Parse(db.Text("SELECT Id FROM meal_types WHERE Name LIKE '%le Yeme%'")!);
        var usedBefore = vm.Snapshot.Kpis.Used;
        var (operationId, decision) = Journey.SimulateAccess(ui, Journey.UnusedCard(db, descending: true), deviceId, mealId);
        Assert.Equal("ALLOW", decision);
        Journey.Until(ui, () => vm.Snapshot!.RecentAccess.Count > 0 && vm.Snapshot.RecentAccess[0].Id == operationId, "canlı geçiş", 15000);
        Assert.Equal(usedBefore + 1, vm.Snapshot!.Kpis.Used);
        Assert.Equal(db.Count($"SELECT COALESCE(SUM(e.ConsumedQuantity), 0) {entitlements}"), vm.Snapshot.Kpis.Used);
        Assert.Equal(20, vm.Snapshot.RecentAccess.Count);
        ui.Shot("dashboard-02-canli");

        // 5) Hizli islemlerin 7 dugmesi dogru rotaya gider.
        Assert.Equal(7, vm.QuickActions.Count);
        // MainWindow.BaseRoute: students/new, cards ve card-reader "students" kabugunda acilir.
        var expected = new Dictionary<string, string>
        {
            ["+ Öğrenci"] = "students", ["Kart Tanımla"] = "students", ["Hakediş Ver"] = "entitlements",
            ["Tatil / Aktarım"] = "holiday-transfer", ["Kart Oku"] = "students", ["Kasa"] = "cash", ["Rapor"] = "reports"
        };
        var routes = new Dictionary<string, string>
        {
            ["+ Öğrenci"] = "students/new", ["Kart Tanımla"] = "cards", ["Hakediş Ver"] = "entitlements",
            ["Tatil / Aktarım"] = "holiday-transfer", ["Kart Oku"] = "card-reader", ["Kasa"] = "cash", ["Rapor"] = "reports"
        };
        Assert.All(vm.QuickActions, action => Assert.Equal(routes[action.Label], action.Route));
        foreach (var action in vm.QuickActions)
        {
            Assert.True(action.IsAvailable, action.Label);
            action.Command.Execute(null);
            ui.Pump(4);
            Assert.Equal(expected[action.Label], Journey.Route(ui));
            ui.Navigate("dashboard");
        }
        var buttons = ui.FindAll<Button>(host).Where(x => x.Content is string label && expected.ContainsKey(label)).ToList();
        Assert.Equal(7, buttons.Count);
        Assert.All(buttons, x => Assert.True(x.IsEnabled && x.ActualWidth > 40, $"{x.Content}: {x.ActualWidth:0}px"));
    });
}
