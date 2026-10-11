namespace Eiii.CommSim;

/// <summary>
/// Receiver: 受信チャンク（string）からフレーム（Frame）を確定して出力する Worker。
/// フレーム確定の状態（未確定データ）は <see cref="DelimiterFramer"/> に保持され、この Worker だけが使う。
/// </summary>
/// <remarks>
/// <para>正常終了（CompleteInput）: 残りのチャンクを処理した後、末尾の未確定データを IsComplete = false のフレームとして出力する。</para>
/// <para>キャンセル停止（StopAsync）: 処理中だったチャンクから確定済みで、まだ出力していないフレームは失われ得る。
/// 未確定データは framer に残り、次の Start 後に届くチャンクと連結される。</para>
/// </remarks>
public sealed class ReceiverTask(int capacity, DelimiterFramer framer, TaskToCallback<Frame>? callback = null)
    : EiiiWorker<string, Frame>(capacity, callback)
{
    /// <summary>チャンクを framer に渡し、確定したフレームを順に出力する。出力先が満杯なら WriteAsync で待つ。</summary>
    protected override async ValueTask ProcessAsync(string request, CancellationToken cancellationToken)
    {
        foreach (var frame in framer.Feed(request)) await EmitAsync(frame, cancellationToken);
    }

    /// <summary>入力完了後、末尾の未確定データを未完了フレームとして出力する。</summary>
    protected override async ValueTask OnInputCompletedAsync(CancellationToken cancellationToken)
    {
        if (framer.Flush() is { } tail) await EmitAsync(tail, cancellationToken);
    }
}

/// <summary>Parser: フレーム（Frame）を解析して結果（Reading）を出力する Worker。</summary>
/// <param name="capacity">解析待ちフレームのキュー容量。満杯なら Receiver の出力（WriteAsync）が待つ。</param>
/// <param name="work">解析前に行う時間のかかる処理の模擬（null なら即座に解析する）。</param>
/// <param name="callback">解析結果ごとに呼ばれるコールバック。</param>
public sealed class ParserTask(int capacity, Func<Frame, CancellationToken, Task>? work = null, TaskToCallback<Reading>? callback = null)
    : EiiiWorker<Frame, Reading>(capacity, callback)
{
    /// <summary>フレームを解析して結果を出力する。</summary>
    protected override async ValueTask ProcessAsync(Frame request, CancellationToken cancellationToken)
    {
        if (work is not null) await work(request, cancellationToken);
        await EmitAsync(FrameParser.Parse(request), cancellationToken);
    }
}
