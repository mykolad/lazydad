using System.Globalization;
using System.Security.Cryptography;
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

    /// <summary>SHA-256 of <see cref="Key"/>: 32 bytes, short enough to index (a key can be as long as the joke).</summary>
    public static byte[] Hash(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(Key(text)));

    public const int HashLength = 32;
}
