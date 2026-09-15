using System.Collections;
using System.Globalization;

namespace Yemekhane.Desktop.ViewModels;

/// <summary>
/// Türkçe metinleri kültüre uygun, sayı bloklarını ise sayısal olarak sıralar.
/// Böylece sınıflar 1, 2, ... 8, 10 sırasıyla görünür; 1, 10, 2 olmaz.
/// </summary>
public sealed class TurkishNaturalStringComparer : IComparer<string>, IComparer
{
    private static readonly CompareInfo Turkish = CultureInfo.GetCultureInfo("tr-TR").CompareInfo;

    public static TurkishNaturalStringComparer Instance { get; } = new();

    private TurkishNaturalStringComparer() { }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x is null) return -1;
        if (y is null) return 1;

        var left = 0;
        var right = 0;
        while (left < x.Length && right < y.Length)
        {
            if (char.IsDigit(x[left]) && char.IsDigit(y[right]))
            {
                var leftStart = left;
                var rightStart = right;
                while (left < x.Length && char.IsDigit(x[left])) left++;
                while (right < y.Length && char.IsDigit(y[right])) right++;

                var leftDigits = x.AsSpan(leftStart, left - leftStart);
                var rightDigits = y.AsSpan(rightStart, right - rightStart);
                var leftTrimmed = TrimLeadingZeros(leftDigits);
                var rightTrimmed = TrimLeadingZeros(rightDigits);
                var lengthResult = leftTrimmed.Length.CompareTo(rightTrimmed.Length);
                if (lengthResult != 0) return lengthResult;
                var digitResult = leftTrimmed.SequenceCompareTo(rightTrimmed);
                if (digitResult != 0) return digitResult;
            }
            else
            {
                var leftStart = left;
                var rightStart = right;
                while (left < x.Length && !char.IsDigit(x[left])) left++;
                while (right < y.Length && !char.IsDigit(y[right])) right++;
                var textResult = Turkish.Compare(x, leftStart, left - leftStart,
                    y, rightStart, right - rightStart, CompareOptions.IgnoreCase);
                if (textResult != 0) return textResult;
            }
        }

        return x.Length.CompareTo(y.Length);
    }

    int IComparer.Compare(object? x, object? y) => Compare(x?.ToString(), y?.ToString());

    private static ReadOnlySpan<char> TrimLeadingZeros(ReadOnlySpan<char> value)
    {
        var index = 0;
        while (index < value.Length - 1 && value[index] == '0') index++;
        return value[index..];
    }
}
