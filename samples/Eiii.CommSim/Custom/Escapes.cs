using System.Globalization;
using System.Text;

namespace Eiii.CommSim;

/// <summary>
/// コマンドラインで制御文字を指定するためのエスケープ表記を解釈する。
/// <c>\r</c> → CR、<c>\n</c> → LF、<c>\t</c> → TAB、<c>\\</c> → \、<c>\xHH</c> → U+00HH（16 進 2 桁）。それ以外の <c>\</c> の後の文字は誤り。
/// </summary>
public static class Escapes
{
    /// <summary>エスケープ表記を解釈する。</summary>
    /// <param name="text">コマンドラインで受け取った文字列。</param>
    /// <returns>解釈後の文字列。</returns>
    /// <exception cref="FormatException">不明なエスケープ、末尾の \、16 進 2 桁でない \x。</exception>
    public static string Unescape(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c != '\\')
            {
                sb.Append(c);
                continue;
            }
            if (i + 1 >= text.Length) throw new FormatException($"末尾の \\ の後に文字がない（位置 {i}）");
            var next = text[++i];
            switch (next)
            {
                case 'r': sb.Append('\r'); break;
                case 'n': sb.Append('\n'); break;
                case 't': sb.Append('\t'); break;
                case '\\': sb.Append('\\'); break;
                case 'x':
                    if (text.Length - (i + 1) < 2 ||
                        !int.TryParse(text.AsSpan(i + 1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code))
                        throw new FormatException($"\\x の後に 16 進 2 桁がない（位置 {i - 1}）");
                    sb.Append((char)code);
                    i += 2;
                    break;
                default:
                    throw new FormatException($"不明なエスケープ \\{next}（位置 {i - 1}）。使えるのは \\r \\n \\t \\\\ \\xHH");
            }
        }
        return sb.ToString();
    }

    /// <summary>制御文字を見える形に戻す（表示用）。</summary>
    /// <param name="text">元の文字列。</param>
    /// <returns>CR / LF / TAB / \ をエスケープ表記にした文字列。</returns>
    public static string Show(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal)
                   .Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);
    }
}
