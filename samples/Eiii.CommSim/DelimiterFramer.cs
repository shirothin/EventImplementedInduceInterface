using System.Text;

namespace Eiii.CommSim;

/// <summary>受信データから確定したフレーム。<see cref="IsComplete"/> が false なのは、区切り文字なしで入力が終わった末尾だけ。</summary>
/// <param name="Text">区切り文字を除いたフレームの内容。</param>
/// <param name="IsComplete">区切り文字で確定したら true。</param>
public sealed record Frame(string Text, bool IsComplete);

/// <summary>
/// デリミタ方式のフレーム確定ロジック。スレッドや Worker に依存しない純粋な処理で、単体テストできる。
/// チャンクの境界はフレームの境界と無関係に扱い、区切り文字がチャンクをまたいでも検出する。
/// </summary>
/// <remarks>
/// <para>末尾の仕様: 区切り文字の来ていないデータはバッファに保持する。<see cref="Flush"/> を呼ぶと、
/// それを <c>IsComplete = false</c> のフレームとして返し、バッファを空にする（黙って捨てない）。</para>
/// <para>空のフレーム（区切り文字が連続した場合）も捨てずに、空文字列のフレームとして返す。</para>
/// <para>制約: 区切り文字が来ない限りバッファは増え続ける（最大長の制限は持たない）。スレッドセーフではない（1 つの Worker だけが使う）。</para>
/// </remarks>
public sealed class DelimiterFramer
{
    private readonly string delimiter;
    private readonly StringBuilder buffer = new();

    /// <summary>区切り文字を指定して初期化する。</summary>
    /// <param name="delimiter">フレームの区切り文字列（空は不可）。既定は CR LF。</param>
    public DelimiterFramer(string delimiter = "\r\n")
    {
        ArgumentException.ThrowIfNullOrEmpty(delimiter);
        this.delimiter = delimiter;
    }

    /// <summary>まだフレームとして確定していないデータの長さ。</summary>
    public int BufferedLength => buffer.Length;

    /// <summary>チャンクを追加し、このチャンクによって確定したフレームを順に返す。</summary>
    /// <param name="chunk">受信したデータの断片。</param>
    /// <returns>確定したフレーム（0 個以上）。</returns>
    public IReadOnlyList<Frame> Feed(string chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        buffer.Append(chunk);
        var text = buffer.ToString();
        var frames = new List<Frame>();
        int start = 0;
        int index;
        while ((index = text.IndexOf(delimiter, start, StringComparison.Ordinal)) >= 0)
        {
            frames.Add(new Frame(text[start..index], IsComplete: true));
            start = index + delimiter.Length;
        }
        buffer.Remove(0, start);
        return frames;
    }

    /// <summary>区切り文字の来ていない末尾のデータを、未完了のフレームとして取り出す。</summary>
    /// <returns>末尾のデータがあれば <c>IsComplete = false</c> のフレーム。なければ null。</returns>
    public Frame? Flush()
    {
        if (buffer.Length == 0) return null;
        var tail = new Frame(buffer.ToString(), IsComplete: false);
        buffer.Clear();
        return tail;
    }
}
