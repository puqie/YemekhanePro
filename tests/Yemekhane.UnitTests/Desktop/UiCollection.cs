namespace Yemekhane.UnitTests.Desktop;

/// <summary>
/// WPF testleri tek bir koleksiyonda ve tek tek calisir.
///
/// Gorsel agac STA is parcaciginda kurulur, ancak WPF'in XAML/baml kaynak onbellegi
/// SUREC GENELINDEDIR: iki STA is parcacigi ayni sozlugu (DesignSystem.xaml) ayni anda
/// yuklerse bu onbellek yariya kadar dolu okunur ve "The given key 'MergedDictionaries'
/// was not present in the dictionary" gibi, uygulamayla ilgisi olmayan hatalar uretir.
/// Bu yuzden WPF'e dokunan HER test sinifi bu koleksiyona alinmalidir.
/// </summary>
[CollectionDefinition(UiCollection.Name, DisableParallelization = true)]
public sealed class UiCollection
{
    public const string Name = "UI";
}
