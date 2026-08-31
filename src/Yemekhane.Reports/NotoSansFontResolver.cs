using System.Reflection;
using PdfSharp.Fonts;

namespace Yemekhane.Reports;

internal sealed class NotoSansFontResolver : IFontResolver
{
    public const string FamilyName = "Yemekhane Noto Sans";
    private const string RegularFace = "NotoSans#Regular";
    private const string BoldFace = "NotoSans#Bold";
    private static readonly Assembly Assembly = typeof(NotoSansFontResolver).Assembly;

    public byte[]? GetFont(string faceName) => faceName switch
    {
        RegularFace => ReadResource("NotoSans-Regular.ttf"),
        BoldFace => ReadResource("NotoSans-Bold.ttf"),
        _ => null
    };

    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        string.Equals(familyName, FamilyName, StringComparison.OrdinalIgnoreCase)
            ? new FontResolverInfo(isBold ? BoldFace : RegularFace, false, isItalic)
            : null;

    private static byte[] ReadResource(string fileName)
    {
        var name = Assembly.GetManifestResourceNames().Single(x => x.EndsWith(fileName, StringComparison.Ordinal));
        using var stream = Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Gömülü font bulunamadı: {fileName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
