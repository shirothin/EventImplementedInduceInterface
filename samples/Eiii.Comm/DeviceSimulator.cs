using System.Diagnostics;
using System.Threading.Channels;

namespace Eiii.CommSim;

/// <summary>
/// 通信機器の模擬。チャンクを Receiver の入力へ WriteAsync で送る。
/// 受信キューが満杯なら空きが出るまで待つため、Parser の遅れがここまで伝わる（バックプレッシャー）。
/// </summary>
public static class DeviceSimulator
{
    /// <summary>チャンクを順に送る。</summary>
    /// <param name="port">送信先（通常は Receiver の Input）。</param>
    /// <param name="chunks">送るチャンク。</param>
    /// <param name="trace">送信ごとに呼ばれるログ出力（チャンクと、WriteAsync で待った時間）。</param>
    /// <param name="cancellationToken">送信の取り消し。</param>
    /// <returns>すべて送り終えたときに完了する Task。</returns>
    public static async Task SendAsync(ChannelWriter<string> port, IEnumerable<string> chunks,
                                       Action<string, long>? trace = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(chunks);
        foreach (var chunk in chunks)
        {
            var waited = Stopwatch.StartNew();
            await port.WriteAsync(chunk, cancellationToken).ConfigureAwait(false);
            trace?.Invoke(chunk, waited.ElapsedMilliseconds);
        }
    }

    /// <summary>制御文字を見える形にする（ログ用）。</summary>
    /// <param name="text">元の文字列。</param>
    /// <returns>CR と LF を \r と \n に置き換えた文字列。</returns>
    public static string Show(string text) => text.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
