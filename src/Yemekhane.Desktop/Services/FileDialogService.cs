using Microsoft.Win32;

namespace Yemekhane.Desktop.Services;

/// <summary>
/// Dosya secme/kaydetme diyaloglari icin arayuz.
///
/// ViewModel'in dogrudan OpenFileDialog cagirmasi, o akisin testte
/// calistirilamamasi demektir: diyalog basssiz bir kosuda acilamaz. Seam
/// sayesinde "dosya sec -> onizle -> uygula" zinciri uctan uca test edilebilir.
/// </summary>
public interface IFileDialogService
{
    /// <summary>Acilacak dosyayi sorar; kullanici vazgecerse null doner.</summary>
    string? OpenFile(string title, string filter);

    /// <summary>Kaydedilecek yolu sorar; kullanici vazgecerse null doner.</summary>
    string? SaveFile(string title, string filter, string suggestedFileName);
}

public sealed class FileDialogService : IFileDialogService
{
    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string suggestedFileName)
    {
        var dialog = new SaveFileDialog
        {
            Title = title, Filter = filter, FileName = suggestedFileName, AddExtension = true
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }
}
