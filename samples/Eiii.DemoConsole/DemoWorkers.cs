using System.Globalization;
using System.Threading.Channels;
using Eiii.CommSim;

namespace Eiii.DemoConsole;

/// <summary>
/// Commander（DemoCommand → DemoCommand）: コマンドを 1 件ずつ機器へ送り、応答が確定する（または NoResponse）まで次を送らない。
/// </summary>
/// <remarks>
/// 応答待ちは <see cref="ResponseTracker"/> による非同期待機で、その間も Receiver・Parser・画面は独立して進む。
/// 停止（キャンセル）されると待機を取り消し、finally で後始末する。
/// 制約: OSD 応答が途中の長い間欠で誤分割されると、応答の途中で待機が解除され、次のコマンドが早く送られ得る。
/// </remarks>
public sealed class CommanderWorker(DemoOptions options, ResponseTracker tracker, ChannelWriter<RxInput> receiverInput, Action<DemoEvent> log)
    : EiiiWorker<DemoCommand, DemoCommand>(options.CommanderCapacity)
{
    private int completed;

    /// <summary>応答待ちを終えたコマンドの数。</summary>
    public int Completed => Volatile.Read(ref completed);

    /// <summary>コマンドを送り、応答の確定を待つ。</summary>
    protected override async ValueTask ProcessAsync(DemoCommand request, CancellationToken cancellationToken)
    {
        var now = options.Time.GetUtcNow();
        var waiting = tracker.Begin(request.Id, now);
        try
        {
            // 方式の選択（アプリケーションの責務）: 応答待ちの開始を、データより先に Receiver の入力へ入れる
            await receiverInput.WriteAsync(new ExpectResponse(request.Id, request.Kind, now), cancellationToken);
            await EmitAsync(request, cancellationToken);   // 機器の入力キューへ
            log(new DemoEvent(now, request.Id, Stage.Send, $"{Describe(request.Kind)} を機器へ送信。応答の確定を待つ（次のコマンドは送らない）"));

            var completion = await waiting.WaitAsync(cancellationToken);
            Interlocked.Increment(ref completed);
            log(new DemoEvent(options.Time.GetUtcNow(), request.Id, Stage.Wait, $"待機解除（{completion}）→ 次のコマンドへ進める"));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            log(new DemoEvent(options.Time.GetUtcNow(), request.Id, Stage.Wait, "停止により応答待ちを取り消した"));
            throw;
        }
        finally
        {
            tracker.Cancel(request.Id);   // 待機を取り残さない（解除済みなら何もしない）
        }
    }

    internal static string Describe(CommandKind kind) => kind == CommandKind.Normal ? "通常コマンド" : "OSD 取得";
}

/// <summary>架空の XPort 系機器（DemoCommand → Chunk）。受け取ったコマンドに 1 件ずつ応答する。実機の仕様ではない。</summary>
public sealed class FakeXPortDevice(DemoOptions options, Action<DemoEvent> log)
    : EiiiWorker<DemoCommand, RxInput>(options.DeviceCapacity)
{
    private int silent;
    private int longGap;
    private int responded;

    /// <summary>true なら応答しない（N キー）。</summary>
    public bool Silent { get => Volatile.Read(ref silent) == 1; set => Volatile.Write(ref silent, value ? 1 : 0); }

    /// <summary>true なら OSD 応答の途中に長い間欠を入れる（G キー）。</summary>
    public bool LongGap { get => Volatile.Read(ref longGap) == 1; set => Volatile.Write(ref longGap, value ? 1 : 0); }

    /// <summary>応答を送り終えた数。</summary>
    public int Responded => Volatile.Read(ref responded);

    /// <summary>通常コマンドの応答内容（番号で値が変わる）。</summary>
    public static string NormalResponse(int id) => string.Create(CultureInfo.InvariantCulture, $"TEMP={20 + id % 10}.5\r");

    /// <summary>コマンドに応答する。チャンクは到着時刻を付けて Receiver の入力へ書く（満杯なら待つ）。</summary>
    protected override async ValueTask ProcessAsync(DemoCommand request, CancellationToken cancellationToken)
    {
        var time = options.Time;
        if (Silent)
        {
            log(new DemoEvent(time.GetUtcNow(), request.Id, Stage.Device, "無応答モードのため応答しない"));
            return;
        }

        IReadOnlyList<(string Text, TimeSpan GapBefore)> fragments;
        TimeSpan latency;
        if (request.Kind == CommandKind.Normal)
        {
            var text = NormalResponse(request.Id);
            fragments = [(text[..5], TimeSpan.Zero), (text[5..], TimeSpan.FromMilliseconds(30))];
            latency = options.NormalLatency;
        }
        else
        {
            var list = OsdSimulator.Fragment(OsdSimulator.CreateResponse(300, request.Id), request.Id, 8, 40,
                                             TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)).ToList();
            if (LongGap) list[list.Count / 2] = (list[list.Count / 2].Text, options.LongGap);
            fragments = list;
            latency = options.OsdLatency;
        }

        await Task.Delay(latency, time, cancellationToken);
        log(new DemoEvent(time.GetUtcNow(), request.Id, Stage.Device,
            $"応答開始 {fragments.Sum(f => f.Text.Length)} 文字を {fragments.Count} チャンクで送る" +
            (request.Kind == CommandKind.Osd && LongGap ? $"（途中に {options.LongGap.TotalMilliseconds:0}ms の間欠）" : "")));
        foreach (var (text, gap) in fragments)
        {
            if (gap > TimeSpan.Zero) await Task.Delay(gap, time, cancellationToken);
            await EmitAsync(new Chunk(text, time.GetUtcNow()), cancellationToken);
        }
        Interlocked.Increment(ref responded);
    }
}

/// <summary>
/// Receiver（RxInput → ReceivedFrame）: 送ったコマンドの種類に応じて、CR 終端（DelimiterFramer）と時間経過（TimeoutFramer）を切り替える。
/// </summary>
/// <remarks>
/// <para>確定したフレームには「その時点で待っている要求」の番号を付ける。実機プロトコルに要求 ID がないため、
/// NoResponse の後に遅れて届いた応答や、誤分割された応答の残りは、次のコマンドの応答として扱われ得る（区別できない）。</para>
/// <para>次の応答待ちを始めるとき、前の応答の未確定データが残っていれば Leftover として出力する（捨てない）。</para>
/// </remarks>
public sealed class XPortReceiverWorker : EiiiWorker<RxInput, ReceivedFrame>
{
    private readonly DemoOptions options;
    private readonly Action<ReceivedFrame> confirmed;
    private readonly Action<DemoEvent> log;
    private readonly DelimiterFramer delimiter = new("\r");
    private readonly TimeoutFramer timeout;
    private int trackingId;
    private CommandKind kind = CommandKind.Normal;
    private DateTimeOffset? normalDeadline;
    private int chunks;
    private int frames;

    /// <summary>初期化する。</summary>
    /// <param name="options">設定。</param>
    /// <param name="confirmed">フレームを確定した時点で呼ばれる（Parser への受け渡しより前）。コマンド直列化の解除に使う。</param>
    /// <param name="log">画面への出来事の通知。</param>
    public XPortReceiverWorker(DemoOptions options, Action<ReceivedFrame> confirmed, Action<DemoEvent> log)
        : base(options.ReceiverCapacity)
    {
        this.options = options;
        this.confirmed = confirmed;
        this.log = log;
        timeout = new TimeoutFramer(new TimeoutOptions { IdleTimeout = options.IdleTimeout, ResponseTimeout = options.OsdResponseTimeout });
    }

    /// <summary>処理したチャンクの数。</summary>
    public int Chunks => Volatile.Read(ref chunks);

    /// <summary>確定したフレームの数。</summary>
    public int Frames => Volatile.Read(ref frames);

    /// <summary>現在の方式（画面表示用）。</summary>
    public string Mode => kind == CommandKind.Normal ? "CR 終端" : $"OSD Idle {options.IdleTimeout.TotalMilliseconds:0}ms";

    /// <summary>入力の種類に応じて framer を呼び、確定したフレームを Parser へ渡す。</summary>
    protected override async ValueTask ProcessAsync(RxInput request, CancellationToken cancellationToken)
    {
        var output = new List<ReceivedFrame>();
        switch (request)
        {
            case ExpectResponse expect:
                if (delimiter.Flush() is { } d) output.Add(new ReceivedFrame(trackingId, CommandKind.Normal, d.Text, Completion.Leftover));
                if (timeout.Flush() is { } t) output.Add(new ReceivedFrame(trackingId, CommandKind.Osd, t.Text, Completion.Leftover));
                trackingId = expect.Id;
                kind = expect.Kind;
                if (kind == CommandKind.Osd)
                {
                    timeout.ExpectResponse(expect.SentAt);
                    normalDeadline = null;
                }
                else
                {
                    normalDeadline = expect.SentAt + options.NormalResponseTimeout;
                }
                break;

            case Chunk chunk:
                Interlocked.Increment(ref chunks);
                var delay = options.Time.GetUtcNow() - chunk.ReceivedAt;
                log(new DemoEvent(chunk.ReceivedAt, trackingId, Stage.Chunk,
                    $"チャンク {chunk.Text.Length} 文字（CR {chunk.Text.Count(c => c == '\r')} 個）到着→処理 {delay.TotalMilliseconds:0}ms"));
                if (kind == CommandKind.Normal)
                {
                    normalDeadline = null;
                    output.AddRange(delimiter.Feed(chunk.Text).Select(f => new ReceivedFrame(trackingId, kind, f.Text, Completion.Cr)));
                }
                else
                {
                    output.AddRange(timeout.Feed(chunk.Text, chunk.ReceivedAt).Select(Map));
                }
                break;

            case Tick tick:
                if (kind == CommandKind.Osd)
                {
                    output.AddRange(timeout.Poll(tick.Now).Select(Map));
                }
                else if (normalDeadline is { } deadline && tick.Now >= deadline)
                {
                    normalDeadline = null;
                    output.Add(new ReceivedFrame(trackingId, kind, "", Completion.NoResponse));
                }
                break;
        }

        foreach (var frame in output)
        {
            Interlocked.Increment(ref frames);
            log(new DemoEvent(options.Time.GetUtcNow(), frame.TrackingId, Stage.Frame,
                $"フレーム確定 [{frame.Completion}] {frame.Text.Length} 文字" + (frame.EndsResponse ? "" : "（応答の確定ではない）")));
            confirmed(frame);                                   // ここで直列化を解除（Parser の処理完了は待たない）
            await EmitAsync(frame, cancellationToken);          // Parser のキューが満杯なら待つ
        }
    }

    /// <summary>入力完了時、残っているデータを Flush として出す。</summary>
    protected override async ValueTask OnInputCompletedAsync(CancellationToken cancellationToken)
    {
        if (delimiter.Flush() is { } d) await EmitAsync(new ReceivedFrame(trackingId, CommandKind.Normal, d.Text, Completion.Flush), cancellationToken);
        if (timeout.Flush() is { } t) await EmitAsync(new ReceivedFrame(trackingId, CommandKind.Osd, t.Text, Completion.Flush), cancellationToken);
    }

    private ReceivedFrame Map(TimedFrame frame) => new(trackingId, CommandKind.Osd, frame.Text, frame.End switch
    {
        FrameEnd.IdleTimeout => Completion.IdleTimeout,
        FrameEnd.CollectionTimeout => Completion.CollectionTimeout,
        FrameEnd.Overflow => Completion.Overflow,
        FrameEnd.NoResponse => Completion.NoResponse,
        _ => Completion.Flush,
    });
}

/// <summary>Parser（ReceivedFrame → DemoResult）。処理遅延を変えてバックプレッシャーを観察できる。</summary>
public sealed class DemoParserWorker(DemoOptions options, Action<DemoEvent> log, TaskToCallback<DemoResult> callback)
    : EiiiWorker<ReceivedFrame, DemoResult>(options.ParserCapacity, callback)
{
    private int delayMilliseconds;
    private int parsed;

    /// <summary>1 件あたりの模擬的な処理時間（S キー）。</summary>
    public int DelayMilliseconds { get => Volatile.Read(ref delayMilliseconds); set => Volatile.Write(ref delayMilliseconds, value); }

    /// <summary>解析を終えた数。</summary>
    public int Parsed => Volatile.Read(ref parsed);

    /// <summary>フレームを解析し、結果を出力する（出力ごとに Callback が呼ばれる）。</summary>
    protected override async ValueTask ProcessAsync(ReceivedFrame request, CancellationToken cancellationToken)
    {
        var delay = DelayMilliseconds;
        log(new DemoEvent(options.Time.GetUtcNow(), request.TrackingId, Stage.Parse, $"解析開始（処理時間 {delay}ms）"));
        if (delay > 0) await Task.Delay(TimeSpan.FromMilliseconds(delay), options.Time, cancellationToken);

        var summary = request.Completion switch
        {
            Completion.NoResponse => "応答なし",
            _ when request.Kind == CommandKind.Normal =>
                FrameParser.Parse(new Frame(request.Text, request.Completion == Completion.Cr)) is { Error: null } r
                    ? $"{r.Key}={r.Value}"
                    : $"解析できない \"{DeviceSimulator.Show(request.Text)}\"",
            _ => $"OSD {OsdParser.Parse(new TimedFrame(request.Text, FrameEnd.Flush, null, null)).Lines.Count} 行 / {request.Text.Length} 文字（完全性は未保証）",
        };
        Interlocked.Increment(ref parsed);
        log(new DemoEvent(options.Time.GetUtcNow(), request.TrackingId, Stage.Parse, $"解析完了 [{request.Completion}] {summary}"));
        await EmitAsync(new DemoResult(request.TrackingId, request.Kind, request.Completion, summary), cancellationToken);
    }
}
