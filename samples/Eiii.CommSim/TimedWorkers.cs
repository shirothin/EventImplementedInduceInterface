using System.Threading.Channels;

namespace EventImplementedInduceInterface.CommSim;

/// <summary>時間方式の受信 Worker への入力。データ・時刻の通知・応答待ちの開始を、同じ Channel の中で到着順に扱う。</summary>
public abstract record RxInput;

/// <summary>受信したチャンク。<paramref name="ReceivedAt"/> は到着した時刻で、Worker が取り出した時刻ではない。</summary>
/// <param name="Text">受信データ（制御文字を含んでよい）。</param>
/// <param name="ReceivedAt">到着した時刻。</param>
public sealed record Chunk(string Text, DateTimeOffset ReceivedAt) : RxInput;

/// <summary>周期的な時刻の通知。後続のデータが来なくても無通信を検出するために使う。</summary>
/// <param name="Now">Tick を生成した時刻。</param>
public sealed record Tick(DateTimeOffset Now) : RxInput;

/// <summary>要求を送ったことの通知（応答待ちの開始）。</summary>
/// <param name="RequestedAt">要求を送った時刻。</param>
public sealed record ResponseExpected(DateTimeOffset RequestedAt) : RxInput;

/// <summary>
/// 時間方式の受信 Worker（RxInput → TimedFrame）。OSD 取得のような終端マーカーのない応答のモデル。
/// 通常のコマンド応答（CR 終端）には Step 6 の <see cref="ReceiverTask"/> を使う。方式の選択はアプリケーションの責務。
/// </summary>
/// <remarks>
/// Chunk と Tick は同じ Channel に到着順で積まれる。Tick を処理する時点で、その Tick より前に到着した Chunk は処理済みになる。
/// 判定には Chunk の受信時刻と Tick の生成時刻を使うので、キューで待たされても確定する境界は変わらない。
/// 既知の制約（未検証）: 送り手の WriteAsync 自体が満杯で待たされた場合、待っている Chunk と、後から TryWrite された Tick の
/// 投入順序は Channel の実装に依存する。この状況での動作は検証していない。
/// </remarks>
public sealed class TimedReceiverTask(int capacity, TimeoutFramer framer, TaskToCallback<TimedFrame>? callback = null)
    : EiiiTask<RxInput, TimedFrame>(capacity, callback)
{
    /// <summary>入力の種類に応じて framer を呼び、確定したフレームを出力する。</summary>
    protected override async ValueTask OnRequestAsync(RxInput request, CancellationToken cancellationToken)
    {
        IReadOnlyList<TimedFrame> frames;
        switch (request)
        {
            case Chunk chunk:
                frames = framer.Feed(chunk.Text, chunk.ReceivedAt);
                break;
            case Tick tick:
                frames = framer.Poll(tick.Now);
                break;
            case ResponseExpected expected:
                framer.ExpectResponse(expected.RequestedAt);
                return;
            default:
                throw new ArgumentException($"未知の入力: {request}", nameof(request));
        }
        foreach (var frame in frames) await EmitAsync(frame, cancellationToken);
    }

    /// <summary>入力完了時、残ったデータを Flush として出力する。</summary>
    protected override async ValueTask OnInputCompletedAsync(CancellationToken cancellationToken)
    {
        if (framer.Flush() is { } tail) await EmitAsync(tail, cancellationToken);
    }
}

/// <summary>OSD 応答の解析結果。内部の CR で行に分ける。</summary>
/// <param name="Lines">CR で分けた行。</param>
/// <param name="End">フレームが確定した理由。</param>
/// <param name="Length">連結したデータの長さ。</param>
/// <remarks>終端マーカーがないため、どの終了理由でも応答の完全性は保証されない。</remarks>
public sealed record OsdResponse(IReadOnlyList<string> Lines, FrameEnd End, int Length);

/// <summary>時間方式のフレームを OSD 応答として解析する。純粋な処理。</summary>
public static class OsdParser
{
    /// <summary>フレームを行に分ける。</summary>
    /// <param name="frame">時間方式で確定したフレーム。</param>
    /// <returns>解析結果。</returns>
    public static OsdResponse Parse(TimedFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var lines = frame.Text.Length == 0 ? [] : frame.Text.Split('\r');
        return new OsdResponse(lines, frame.End, frame.Text.Length);
    }
}

/// <summary>OSD 応答の解析 Worker（TimedFrame → OsdResponse）。</summary>
/// <param name="capacity">解析待ちフレームのキュー容量。</param>
/// <param name="work">解析前に行う時間のかかる処理の模擬（null なら即座に解析する）。</param>
/// <param name="callback">解析結果ごとに呼ばれるコールバック。</param>
public sealed class OsdParserTask(int capacity, Func<TimedFrame, CancellationToken, Task>? work = null, TaskToCallback<OsdResponse>? callback = null)
    : EiiiTask<TimedFrame, OsdResponse>(capacity, callback)
{
    /// <summary>フレームを解析して結果を出力する。</summary>
    protected override async ValueTask OnRequestAsync(TimedFrame request, CancellationToken cancellationToken)
    {
        if (work is not null) await work(request, cancellationToken);
        await EmitAsync(OsdParser.Parse(request), cancellationToken);
    }
}

/// <summary>.NET 標準の <see cref="PeriodicTimer"/> で Tick を送る。</summary>
public static class TickSource
{
    /// <summary>
    /// 周期ごとに Tick を TryWrite する。キューが満杯なら、その回の Tick は送らない（データではなく、次の Tick が同じ判定を行うため）。
    /// </summary>
    /// <param name="port">送り先（通常は TimedReceiverTask の Input）。</param>
    /// <param name="period">周期。判定の遅れは最大でこの周期程度になる。</param>
    /// <param name="timeProvider">時刻の取得元。Chunk の受信時刻と同じものを使う。</param>
    /// <param name="cancellationToken">停止に使うトークン。</param>
    /// <returns>停止したときに完了する Task。</returns>
    public static async Task RunAsync(ChannelWriter<RxInput> port, TimeSpan period, TimeProvider timeProvider, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(port);
        ArgumentNullException.ThrowIfNull(timeProvider);
        using var timer = new PeriodicTimer(period, timeProvider);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                port.TryWrite(new Tick(timeProvider.GetUtcNow()));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}
