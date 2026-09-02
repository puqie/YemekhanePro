using System.Windows.Controls;
using Xunit;
using Yemekhane.Application.Sms;

namespace Yemekhane.UnitTests.Desktop.Live;

/// <summary>
/// SMS Merkezi: Gonder (manuel secim, sablon, degiskenler, onay), Sablonlar (olustur,
/// ayni ad, duzenle, pasiflestir) ve Gecmis (filtreler, durum rozeti) akislari
/// GERCEK API ve tohum verisiyle surulur; her adimda ekran cekilir.
/// </summary>
[Collection("UI")]
public class SmsJourney
{
    [Fact]
    public void GonderSablonVeGecmisAkisi() => LiveUiHarness.Run(ui =>
    {
        ui.LoadAll();
        ui.Navigate("sms");
        var vm = ui.Sms;
        var tabs = ui.TabControlWith("Gönder");
        Assert.NotNull(tabs);

        // ---- Hedef turu secenekleri Turkce; API degeri İngilizce kalir.
        Assert.Equal("Manual", vm.TargetType);
        Assert.All(vm.TargetTypes, option => Assert.DoesNotContain(option.Name, new[] { "Manual", "Class", "Group", "All", "Filter" }));
        var targetCombo = ui.FindAll<ComboBox>().First(c => System.Windows.Automation.AutomationProperties.GetName(c) == "SMS hedef türü");
        Assert.Equal("Manuel seçim", targetCombo.Text);
        ui.Shot("sms-01-gonder");

        // ---- Kucuk harfle arama: sicil buyuk harf ("ADA ...") tutar, arama yine bulmali.
        vm.Search = "ada";
        vm.SearchStudentsCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.Students.Count > 0, "'ada' araması boş döndü: " + vm.SendError);
        Assert.All(vm.Students, s => Assert.Contains("ADA", s.Name, StringComparison.Ordinal));
        Assert.Contains(vm.Students, s => s.ClassName.Length > 0 && s.SectionName.Length > 0);
        ui.Note($"'ada' araması {vm.Students.Count} öğrenci; ilk: {vm.Students[0].StudentNo} {vm.Students[0].Name} {vm.Students[0].ClassName}/{vm.Students[0].SectionName}");
        ui.Shot("sms-02-arama-ada");

        // ---- Secim aramadan bagimsiz kalir.
        foreach (var choice in vm.Students.Take(4)) choice.IsSelected = true;
        Assert.Equal(4, vm.SelectedStudentCount);
        vm.Search = "ali";
        vm.SearchStudentsCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.Students.Count > 0, "'ali' araması boş döndü");
        Assert.Equal(4, vm.SelectedStudentCount);
        vm.Students[0].IsSelected = true;
        Assert.Equal(5, vm.SelectedStudentCount);
        Assert.Equal("Seçili: 5 öğrenci", vm.SelectedStudentText);

        // ---- Bos mesaj: Turkce hata, cevrimdisi rozeti YOK.
        vm.CustomMessage = "";
        vm.PreviewCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.False(vm.HasPreview);
        Assert.Equal("Mesaj metni boş olamaz.", vm.SendError);
        Assert.False(vm.IsOffline);
        ui.Shot("sms-03-bos-mesaj");

        // ---- Karakter/segment sayaci Turkce karakterlerle (UCS-2, 70 karakter/segment).
        vm.CustomMessage = "Sayın veli, öğrencinizin yemek ücreti için son gün yarın; lütfen ödemeyi yapınız. Teşekkürler.";
        Assert.Equal(vm.CustomMessage.Length, vm.CharacterCount);
        Assert.True(vm.CharacterCount > 70 && vm.CharacterCount <= 134);
        Assert.Equal(2, vm.SegmentCount);
        Assert.Contains("70", vm.EncodingHint, StringComparison.Ordinal);

        // ---- Manuel onizleme: sayilar tutarli, ornekler secilen ogrencilere ait.
        vm.PreviewCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.True(vm.HasPreview, "Önizleme oluşmadı: " + vm.SendError);
        var preview = vm.Preview!;
        Assert.Equal(5, preview.MatchedStudents);
        Assert.Equal(preview.MatchedStudents, preview.RecipientCount + preview.NoPhoneCount + preview.DuplicatePhoneCount);
        Assert.True(preview.RecipientCount > 0, "hiç alıcı yok");
        Assert.All(preview.Examples, e => Assert.Contains(e.StudentId, vm.SelectedStudentIds));
        Assert.All(preview.Examples, e => Assert.Equal(vm.CustomMessage, e.Message));
        Assert.All(preview.Examples, e => Assert.StartsWith("+90", e.Phone, StringComparison.Ordinal));
        ui.Note($"manuel önizleme: eşleşen {preview.MatchedStudents}, alıcı {preview.RecipientCount}, telefon yok {preview.NoPhoneCount}, mükerrer {preview.DuplicatePhoneCount}");
        ui.Shot("sms-04-onizleme-manuel");

        // ---- Onay kutusu olmadan kuyruga alinamaz.
        Assert.False(vm.EnqueueCommand.CanExecute(null));
        vm.IsConfirmed = true;
        Assert.True(vm.EnqueueCommand.CanExecute(null));
        vm.EnqueueCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.NotNull(vm.EnqueueResult);
        Assert.Equal(preview.RecipientCount, vm.EnqueueResult!.QueuedCount);
        Assert.False(vm.HasPreview);
        Assert.True(vm.History.Count >= preview.RecipientCount, "kuyruğa alınan SMS'ler geçmişe düşmedi");
        ui.Shot("sms-05-kuyruga-alindi");

        // ---- Sablonlar: yeni, ayni ad, duzenle.
        tabs!.SelectedIndex = 1; ui.Pump();
        var name = "Yolculuk " + DateTime.Now.ToString("HHmmss");
        vm.NewTemplateCommand.Execute(null);
        vm.TemplateName = name;
        vm.TemplateBody = "Sayın {{ParentName}}, {{StudentName}} için son ödeme tarihi {{ExpiryDate}}, tutar {{Amount}} TL.";
        vm.SaveTemplateCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("", vm.TemplateError);
        Assert.Contains(vm.Templates, t => t.Name == name && t.IsActive);
        Assert.Contains(vm.SendTemplates, t => t.Name == name);
        ui.Shot("sms-06-sablon-kaydedildi");

        vm.NewTemplateCommand.Execute(null);
        vm.TemplateName = name; vm.TemplateBody = "Tekrar";
        vm.SaveTemplateCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Contains("zaten", vm.TemplateError, StringComparison.Ordinal);
        Assert.False(vm.IsOffline);
        ui.Shot("sms-07-sablon-ayni-ad");

        vm.SelectedTemplateRow = vm.Templates.First(t => t.Name == name);
        vm.EditTemplateCommand.Execute(null);
        Assert.Equal(name, vm.TemplateName);
        vm.TemplateBody += " Teşekkürler.";
        vm.SaveTemplateCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("", vm.TemplateError);
        Assert.Contains(vm.Templates, t => t.Name == name && t.Body.EndsWith("Teşekkürler.", StringComparison.Ordinal));

        // ---- Sablonla gonderim: eksik degisken Turkce adla soylenir; dolunca ornek mesaj dolu gelir.
        tabs.SelectedIndex = 0; ui.Pump();
        vm.UseTemplate = true;
        vm.SelectedTemplate = vm.SendTemplates.First(t => t.Name == name);
        vm.ExpiryDate = ""; vm.Amount = "250,50";
        vm.PreviewCommand.Execute(null); ui.Delay(500); ui.Pump();
        Assert.False(vm.HasPreview);
        Assert.Contains("Son tarih", vm.SendError, StringComparison.Ordinal);
        ui.Shot("sms-08-sablon-eksik-degisken");

        vm.ExpiryDate = "15.09.2026";
        vm.PreviewCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.True(vm.HasPreview, "Şablonlu önizleme oluşmadı: " + vm.SendError);
        var example = vm.Preview!.Examples[0];
        Assert.Contains("15.09.2026", example.Message, StringComparison.Ordinal);
        Assert.Contains("250,50", example.Message, StringComparison.Ordinal);
        Assert.Contains(example.StudentName, example.Message, StringComparison.Ordinal);
        Assert.Contains(example.ParentName, example.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", example.Message, StringComparison.Ordinal);
        ui.Shot("sms-09-onizleme-sablon");
        vm.IsConfirmed = true;
        vm.EnqueueCommand.Execute(null); ui.Delay(2500); ui.Pump();
        Assert.NotNull(vm.EnqueueResult);
        Assert.Equal("", vm.SendError);

        // ---- Pasiflestir: Gonder listesinden duser; "Pasifleri goster" ile geri gorunur.
        tabs.SelectedIndex = 1; ui.Pump();
        vm.SelectedTemplateRow = vm.Templates.First(t => t.Name == name);
        vm.EditTemplateCommand.Execute(null);
        vm.DeactivateTemplateCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("", vm.TemplateError);
        Assert.DoesNotContain(vm.Templates, t => t.Name == name);
        Assert.DoesNotContain(vm.SendTemplates, t => t.Name == name);
        vm.IncludeInactive = true; ui.Delay(1500); ui.Pump();
        Assert.Contains(vm.Templates, t => t.Name == name && !t.IsActive);
        Assert.DoesNotContain(vm.SendTemplates, t => t.Name == name);
        ui.Shot("sms-10-pasifler");
        vm.IncludeInactive = false; ui.Delay(1000); ui.Pump();

        // ---- Gecmis: mock saglayici gonderir; filtreler ve durum rozeti.
        tabs.SelectedIndex = 2; ui.Pump();
        ui.Delay(5000);
        vm.HistoryStudent = ""; vm.HistoryPhone = ""; vm.HistoryProvider = ""; vm.HistoryStatus = "";
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.HistoryTotal > 0, "SMS geçmişi boş");
        Assert.Contains(vm.History, h => h.Status == SmsLogStatuses.Sent);
        ui.Shot("sms-11-gecmis");
        var texts = ui.FindAll<TextBlock>().Select(t => t.Text).ToList();
        Assert.Contains(texts, t => t is "Gönderildi" or "Bekliyor" or "Gönderiliyor");
        Assert.DoesNotContain(texts, t => t is "Sent" or "Pending" or "RetryScheduled");

        var sample = vm.History.First(h => h.Status == SmsLogStatuses.Sent);
        vm.HistoryPhone = sample.Phone;
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.History.Count > 0, "telefon filtresi boş döndü");
        Assert.All(vm.History, h => Assert.Equal(sample.Phone, h.Phone));

        vm.HistoryPhone = ""; vm.HistoryStatus = SmsLogStatuses.Sent;
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.History.Count > 0);
        Assert.All(vm.History, h => Assert.Equal(SmsLogStatuses.Sent, h.Status));

        vm.HistoryStatus = ""; vm.HistoryProvider = sample.Provider ?? "";
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.History.Count > 0);
        Assert.All(vm.History, h => Assert.Equal(sample.Provider, h.Provider));

        vm.HistoryProvider = ""; vm.HistoryStudent = "ADA";
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.True(vm.History.Count > 0, "öğrenci adı filtresi boş döndü");

        // Gecersiz telefon: sunucunun Turkce dogrulama mesaji ekrana ulasir, cevrimdisi sayilmaz.
        vm.HistoryStudent = ""; vm.HistoryPhone = "12";
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.NotEqual("", vm.HistoryError);
        Assert.NotEqual("SMS servisine ulaşılamadı.", vm.HistoryError);
        Assert.False(vm.IsOffline);
        ui.Note("geçersiz telefon filtresi mesajı: " + vm.HistoryError);
        ui.Shot("sms-12-gecmis-filtre-hatasi");
        vm.HistoryPhone = "";
        vm.RefreshHistoryCommand.Execute(null); ui.Delay(1500); ui.Pump();
        Assert.Equal("", vm.HistoryError);
    });
}
