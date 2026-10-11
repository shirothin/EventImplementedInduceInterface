using System.Text;

namespace Eiii.CommSim;

/// <summary>投入する電文を取得する。</summary>
/// <remarks>
/// <para>ファイルの扱い: 内容をそのまま 1 つの電文として使う。CR / LF / CRLF の変換も、末尾の改行の除去もしない
/// （エディターが保存時に付けた末尾の改行も電文に含まれる）。</para>
/// <para>文字コード: BOM があれば BOM に従う（UTF-8 / UTF-16 / UTF-32）。なければ UTF-8 として厳密に読み、
/// UTF-8 として不正なバイトがあれば誤りにする（Shift_JIS などの別の文字コードやバイナリは対象外）。</para>
/// </remarks>
public static class CustomInput
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>--text または --file から電文を取得する。</summary>
    /// <param name="options">設定。</param>
    /// <returns>電文。</returns>
    /// <exception cref="CustomUsageException">ファイルを読めない、UTF-8 として不正、または空。</exception>
    public static string Load(CustomOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string text;
        if (options.Text is not null)
        {
            text = options.Text;
        }
        else
        {
            try
            {
                text = File.ReadAllText(options.FilePath!, StrictUtf8);
            }
            catch (DecoderFallbackException ex)
            {
                throw new CustomUsageException($"{options.FilePath} を UTF-8 として読めない（別の文字コードやバイナリは対象外）", ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new CustomUsageException($"{options.FilePath} を読めない: {ex.Message}", ex);
            }
        }
        if (text.Length == 0) throw new CustomUsageException("投入する電文が空");
        return text;
    }
}
