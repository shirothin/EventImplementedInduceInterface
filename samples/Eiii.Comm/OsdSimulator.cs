using System.Text;
using System.Threading.Channels;

namespace Eiii.CommSim;

/// <summary>
/// OSD 取得コマンドの応答を返す機器の模擬。値は記憶に基づく概算（約 300 文字、断片間 数十～100ms、応答まで最大約 5 秒）で、確定仕様ではない。
/// </summary>
public static class OsdSimulator
{
    /// <summary>内部に CR を含む、画面表示風の応答文字列を作る（終端マーカーはない）。</summary>
    /// <param name="approximateLength">おおよその長さ。</param>
    /// <param name="screenId">画面を区別するための番号。</param>
    /// <returns>内部に複数の CR を含む文字列。</returns>
    public static string CreateResponse(int approximateLength = 300, int screenId = 1)
    {
        var text = new StringBuilder();
        for (int line = 1; text.Length < approximateLength; line++)
            text.Append(System.Globalization.CultureInfo.InvariantCulture, $"SCR{screenId} L{line:00}: ").Append('*', 14).Append('\r');
        return text.ToString();
    }

    /// <summary>文字列を不規則な大きさのチャンクに分け、それぞれの前に置く間欠を割り当てる。</summary>
    /// <param name="text">分割する文字列。</param>
    /// <param name="seed">乱数の種（再現性のため）。</param>
    /// <param name="minChunk">チャンクの最小長。</param>
    /// <param name="maxChunk">チャンクの最大長。</param>
    /// <param name="minGap">間欠の最小値。</param>
    /// <param name="maxGap">間欠の最大値。</param>
    /// <returns>(チャンク, その前の間欠) の列。最初のチャンクの間欠は 0。</returns>
    public static IReadOnlyList<(string Text, TimeSpan GapBefore)> Fragment(
        string text, int seed, int minChunk, int maxChunk, TimeSpan minGap, TimeSpan maxGap)
    {
        ArgumentNullException.ThrowIfNull(text);
#pragma warning disable CA5394 // 暗号用途ではない（模擬データの再現可能な分割）
        var random = new Random(seed);
        var result = new List<(string, TimeSpan)>();
        for (int i = 0; i < text.Length;)
        {
            var size = Math.Min(random.Next(minChunk, maxChunk + 1), text.Length - i);
            var gap = i == 0 ? TimeSpan.Zero : minGap + TimeSpan.FromMilliseconds(random.NextDouble() * (maxGap - minGap).TotalMilliseconds);
#pragma warning restore CA5394
            result.Add((text.Substring(i, size), gap));
            i += size;
        }
        return result;
    }

    /// <summary>
    /// 間欠を置きながらチャンクを送る。受信時刻はチャンクが到着した（WriteAsync を呼ぶ）時点で付け、キューで待った時間は含めない。
    /// </summary>
    /// <param name="port">送り先（通常は TimedReceiverTask の Input）。</param>
    /// <param name="fragments">(チャンク, その前の間欠) の列。</param>
    /// <param name="timeProvider">時刻の取得元。Tick と同じものを使う。</param>
    /// <param name="trace">送信ごとのログ出力（チャンク, 受信時刻）。</param>
    /// <returns>すべて送り終えたときに完了する Task。</returns>
    public static async Task SendAsync(ChannelWriter<RxInput> port, IEnumerable<(string Text, TimeSpan GapBefore)> fragments,
                                       TimeProvider timeProvider, Action<string, DateTimeOffset>? trace = null)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(timeProvider);
        foreach (var (text, gap) in fragments)
        {
            if (gap > TimeSpan.Zero) await Task.Delay(gap, timeProvider).ConfigureAwait(false);
            var receivedAt = timeProvider.GetUtcNow();
            await port.WriteAsync(new Chunk(text, receivedAt)).ConfigureAwait(false);
            trace?.Invoke(text, receivedAt);
        }
    }
}
