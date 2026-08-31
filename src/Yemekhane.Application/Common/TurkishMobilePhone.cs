namespace Yemekhane.Application.Common;

public static class TurkishMobilePhone
{
    public static string Normalize(string? phone)
    {
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        // Her dal ulusal 10 haneye indirgenir; mobil ön eki (5) tek yerde denetlenir.
        // Onceden yalnizca 10 haneli dal kontrol ediyordu ve sabit hatlar (0212...) kabul ediliyordu.
        var national = digits switch
        {
            { Length: 12 } when digits.StartsWith("90", StringComparison.Ordinal) => digits[2..],
            { Length: 11 } when digits.StartsWith('0') => digits[1..],
            { Length: 10 } => digits,
            _ => throw new RequestValidationException("Telefon geçerli bir Türkiye mobil numarası olmalıdır.")
        };

        if (!national.StartsWith('5'))
            throw new RequestValidationException("Telefon geçerli bir Türkiye mobil numarası olmalıdır.");

        return $"+90{national}";
    }
}
