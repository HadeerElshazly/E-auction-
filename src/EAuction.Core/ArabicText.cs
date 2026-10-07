namespace EAuction.Core;

/// <summary>Arabic text as people search it.</summary>
public static class ArabicText
{
    /// <summary>
    /// Folds the spellings people type either way — أ/إ/آ as ا, ة as ه, ى as ي — and
    /// drops the short vowels, so «مخطط الياسمين» is found however it is typed. One
    /// implementation for every service that searches names, so the catalogue and
    /// «طلباتي» find the same auctions for the same words.
    /// </summary>
    public static string Normalise(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text.Trim().ToLowerInvariant())
        {
            if (c is >= '\u064B' and <= '\u0652') continue;   // short vowels
            sb.Append(c switch { 'أ' or 'إ' or 'آ' => 'ا', 'ة' => 'ه', 'ى' => 'ي', _ => c });
        }
        return sb.ToString();
    }
}
