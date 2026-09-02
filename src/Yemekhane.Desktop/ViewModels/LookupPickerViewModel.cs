using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows.Input;
using Yemekhane.Application.Organization;
using Yemekhane.Desktop.Services;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Sicil kartindaki dort acilir kutunun (Sinif / Sube / Bolum / Gorev) ortak modeli:
/// liste + secim + yesil "+" ile ANINDA yeni tanim ekleme. Eski programda her kutunun
/// yaninda "+" vardi; kullanici Tanimlar ekranina gitmeden yeni sinifi burada acabiliyordu.
///
/// Ilk oge "Seçiniz" yer tutucusudur (Id = Guid.Empty): bos secim ile "hic secilmemis"
/// gorsel olarak ayni sey oldugu icin ayri bir null durumu tasinmaz; SelectedId null doner.
/// </summary>
public sealed class LookupPickerViewModel : ObservableObject
{
    public static readonly LookupRecord Placeholder = new(Guid.Empty, "Seçiniz", 0);

    private readonly IStudentApiClient api;
    private LookupRecord? selected = Placeholder;
    private bool isAdding, isLoaded;
    private string? newName, error;

    public LookupPickerViewModel(LookupKind kind, IStudentApiClient api, string? label = null)
    {
        Kind = kind; this.api = api;
        Label = label ?? OrganizationService.LookupLabel(kind);
        Items.Add(Placeholder);
        OpenAddCommand = new RelayCommand(() => { IsAdding = true; NewName = null; Error = null; });
        CancelAddCommand = new RelayCommand(() => { IsAdding = false; NewName = null; Error = null; });
        AddCommand = new AsyncCommand(AddAsync);
    }

    public LookupKind Kind { get; }
    public string Label { get; }
    public ObservableCollection<LookupRecord> Items { get; } = [];
    public LookupRecord? Selected { get => selected; set { if (Set(ref selected, value)) Raise(nameof(SelectedId)); } }
    /// <summary>Sunucuya gidecek kimlik; yer tutucu ve bos secim null'dur.</summary>
    public Guid? SelectedId => Selected is null || Selected.Id == Guid.Empty ? null : Selected.Id;
    public bool IsAdding { get => isAdding; private set => Set(ref isAdding, value); }
    public string? NewName { get => newName; set => Set(ref newName, value); }
    public string? Error { get => error; private set { if (Set(ref error, value)) Raise(nameof(HasError)); } }
    public bool HasError => !string.IsNullOrWhiteSpace(Error);
    public bool IsLoaded => isLoaded;
    public string AddPlaceholder => $"Yeni {Label.ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))} adı";

    public ICommand OpenAddCommand { get; }
    public ICommand CancelAddCommand { get; }
    public ICommand AddCommand { get; }

    /// <summary>
    /// Listeyi sunucudan yeniler; secim KORUNUR (ayni Id yeni listede de varsa). Form her
    /// acilista yeniden yuklenir: Tanimlar ekraninda eklenen bir sube burada da gorunmeli.
    /// </summary>
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var keep = SelectedId;
        IReadOnlyList<LookupRecord> loaded;
        try { loaded = await api.GetLookupsAsync(Kind, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException or LoginRequiredException or ApiRequestException)
        { Error = $"{Label} listesi alınamadı."; return; }
        Items.Clear(); Items.Add(Placeholder);
        foreach (var item in loaded.OrderBy(x => x.Name, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true)))
            Items.Add(item);
        isLoaded = true; Raise(nameof(IsLoaded));
        Select(keep);
    }

    /// <summary>
    /// Verilen kimligi secer. Kimlik listede YOKSA (tanim silinmis/pasiflenmis ya da liste
    /// henuz yuklenmemis) kayit yine de KORUNUR: "(tanımsız)" adiyla listeye eklenip secilir.
    /// Aksi halde ogrencinin mevcut sinif/bolum atamasi ilk kaydetmede sessizce SILINIRDI --
    /// kullanici yalnizca adini duzeltmis olsa bile.
    /// </summary>
    public void Select(Guid? id)
    {
        if (!id.HasValue) { Selected = Placeholder; return; }
        var match = Items.FirstOrDefault(x => x.Id == id.Value);
        if (match is null)
        {
            match = new LookupRecord(id.Value, "(tanımsız)", 0);
            Items.Add(match);
        }
        Selected = match;
    }

    public string? NameOf(Guid? id) => id.HasValue ? Items.FirstOrDefault(x => x.Id == id.Value)?.Name : null;

    /// <summary>
    /// "+" kutusundaki adi sunucuya yazar, listeye ekler ve SECER. Cakismada (409) sunucunun
    /// mesaji ("Şube adı zaten kayıtlı.") kutunun altinda AYNEN gorunur; sessiz kalmaz.
    /// </summary>
    private async Task AddAsync()
    {
        var name = NewName?.Trim();
        if (string.IsNullOrWhiteSpace(name)) { Error = $"{Label} adı boş olamaz."; return; }
        try
        {
            var created = await api.CreateLookupAsync(Kind, name);
            Items.Add(created);
            Selected = created;
            IsAdding = false; NewName = null; Error = null;
        }
        catch (ApiRequestException ex) { Error = ex.Message; }
        catch (LoginRequiredException) { Error = "Tanım eklemek için yetkiniz yok veya oturumunuz sona erdi."; }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidDataException)
        { Error = $"{Label} eklenemedi. Sunucuya ulaşılamadı."; }
    }
}
