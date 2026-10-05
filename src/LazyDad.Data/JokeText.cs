using System.Globalization;
using System.Text;

namespace LazyDad.Data;

/// <summary>
/// When two jokes are the same joke: the same letters and digits, ignoring case, punctuation, quote marks, apostrophes
/// and spacing. The old model saved the same joke weeks apart with only its last "!" or "." different (53 copies by
/// 2026-10-05).
/// </summary>
public static class JokeText
{
    public static string Key(string text)
    {
        var key = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormKC))
        {
            // The Ukrainian apostrophe (м'ясо) is written three ways, and U+02BC counts as a letter: skip them all.
            if (char.IsLetterOrDigit(c) && c != 'ʼ')
                key.Append(char.ToLower(c, CultureInfo.InvariantCulture));
        }
        return key.ToString();
    }
}
