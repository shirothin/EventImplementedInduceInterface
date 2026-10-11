using System.Threading.Channels;
using Eiii.CommSim;

namespace Eiii.DemoConsole;

/// <summary>
/// デモの構成（画面とは独立）。Launcher → Commander → 機器 → Receiver → Parser → Callback を組み立て、Registry に登録する。
/// 接続と実行順序はここ（アプリケーション側）で決める。Eiii ライブラリ本体は通信の知識を持たない。
/// </summary>
public sealed class DemoSystem : IAsyncDisposable
{
    private readonly DemoOptions options;
    private readonly Channel<DemoEvent> events = Channel.CreateUnbounded<DemoEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource ticking = new();
    private Task? ticks;
    private int nextId;
    private int accepted;
    private int rejected;
    private int callbacks;

    /// <summary>構成を組み立てる（まだ起動しない）。</summary>
    /// <param name="options">設定。</param>
    public DemoSystem(DemoOptions options)
    {
        this.options = options;
        Tracker = new ResponseTracker();
        Receiver = new XPortReceiverWorker(options, OnConfirmed, Log);
        Parser = new DemoParserWorker(options, Log, new TaskToCallback<DemoResult>(OnCallback));   // 型付き delegate を明示的に生成
        Device = new FakeXPortDevice(options, Log);
        Commander = new CommanderWorker(options, Tracker, Receiver.Input, Log);

        // 登録順 = 上流から下流（一括操作の適用条件）
        Registry.Add("commander", Commander);
        Registry.Add("device", Device);
        Registry.Add("receiver", Receiver);
        Registry.Add("parser", Parser);

        Commander.LinkTo(Device.Input);
        Device.LinkTo(Receiver.Input);
        Receiver.LinkTo(Parser.Input);
        Commander.Attach(Launcher);
    }

    /// <summary>Worker の一元管理。</summary>
    public EiiiWorkerRegistry Registry { get; } = new();

    /// <summary>要求を発行する入口（イベント経由で Commander の入力へ同期投入）。</summary>
    public EiiiLauncher<DemoCommand> Launcher { get; } = new();

    /// <summary>コマンド直列化の応答待ち。</summary>
    public ResponseTracker Tracker { get; }

    /// <summary>Commander。</summary>
    public CommanderWorker Commander { get; }

    /// <summary>機器（模擬）。</summary>
    public FakeXPortDevice Device { get; }

    /// <summary>Receiver。</summary>
    public XPortReceiverWorker Receiver { get; }

    /// <summary>Parser。</summary>
    public DemoParserWorker Parser { get; }

    /// <summary>設定。</summary>
    public DemoOptions Options => options;

    /// <summary>画面に表示する出来事。読み手は 1 つ（描画処理）。</summary>
    public ChannelReader<DemoEvent> Events => events.Reader;

    /// <summary>受理された要求の数。</summary>
    public int Accepted => Volatile.Read(ref accepted);

    /// <summary>拒否された要求の数（Commander のキューが満杯）。</summary>
    public int Rejected => Volatile.Read(ref rejected);

    /// <summary>Callback が呼ばれた数。</summary>
    public int Callbacks => Volatile.Read(ref callbacks);

    /// <summary>すべての Worker と Tick を起動する。</summary>
    public void Start()
    {
        Registry.StartAll();
        ticks ??= TickSource.RunAsync(Receiver.Input, options.TickPeriod, options.Time, ticking.Token);
    }

    /// <summary>コマンドを要求する。Launcher のイベントで Commander のキューへ同期投入され、満杯なら false。</summary>
    /// <param name="kind">コマンドの種類。</param>
    /// <returns>受理されたら true。</returns>
    public bool Send(CommandKind kind)
    {
        var command = new DemoCommand(Interlocked.Increment(ref nextId), kind, options.Time.GetUtcNow());
        var ok = Launcher.Request(this, command);
        if (ok) Interlocked.Increment(ref accepted);
        else Interlocked.Increment(ref rejected);
        Log(new DemoEvent(command.RequestedAt, command.Id, Stage.Request,
            $"{CommanderWorker.Describe(kind)} → {(ok ? $"受理（Commander のキュー {Commander.PendingCount}）" : "拒否（Commander のキューが満杯。再送は利用者の判断）")}"));
        return ok;
    }

    /// <summary>R: すべての Worker をキャンセルで停止し、再起動する。処理中のデータは失われ得る。</summary>
    /// <returns>再起動が終わったときに完了する Task。</returns>
    public async Task RestartAsync()
    {
        Log(new DemoEvent(options.Time.GetUtcNow(), null, Stage.Control, "R: StopAllAsync（キャンセル）→ StartAll。処理中のデータは失われ得る"));
        await Registry.StopAllAsync();
        Log(new DemoEvent(options.Time.GetUtcNow(), null, Stage.Control,
            $"停止後: " + string.Join(" / ", Workers.Select(w => $"{w.Name}={Describe(w.Worker)} 残{w.Worker.PendingCount}"))));
        Registry.StartAll();
        Log(new DemoEvent(options.Time.GetUtcNow(), null, Stage.Control, "再起動した。キューに残っていた要求・チャンクは続きから処理される"));
    }

    /// <summary>Q: 上流から入力を閉じ、残りをすべて処理して正常終了する。</summary>
    /// <returns>終了したときに完了する Task。</returns>
    public async Task CompleteAsync()
    {
        Log(new DemoEvent(options.Time.GetUtcNow(), null, Stage.Control, "Q: CompleteAllAsync（入力を閉じ、受理済みの要求をすべて処理して終了）"));
        await Registry.CompleteAllAsync();
        await StopTicksAsync();
        Log(new DemoEvent(options.Time.GetUtcNow(), null, Stage.Control,
            "終了: " + string.Join(" / ", Workers.Select(w => $"{w.Name}={Describe(w.Worker)}"))));
        events.Writer.TryComplete();
    }

    /// <summary>画面表示用の Worker 一覧（上流から順）。</summary>
    public IReadOnlyList<(string Name, EiiiWorker Worker, int Capacity)> Workers =>
    [
        ("commander", Commander, options.CommanderCapacity),
        ("device", Device, options.DeviceCapacity),
        ("receiver", Receiver, options.ReceiverCapacity),
        ("parser", Parser, options.ParserCapacity),
    ];

    /// <summary>実際の Task の状態を、人間向けの表示に変換する。</summary>
    /// <param name="worker">対象の Worker。</param>
    /// <returns>状態の表示。</returns>
    public static string Describe(EiiiWorker worker) => worker.Execution switch
    {
        null => "未起動",
        { IsCompleted: false } => "稼働中",
        { Status: TaskStatus.RanToCompletion } => "正常終了",
        { Status: TaskStatus.Canceled } => "停止(キャンセル)",
        { Exception: { } e } => $"異常終了: {e.InnerException?.Message}",
        _ => "不明",
    };

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        await StopTicksAsync();
        ticking.Dispose();
    }

    private async Task StopTicksAsync()
    {
        await ticking.CancelAsync();
        if (ticks is not null) await ticks;
    }

    private void OnConfirmed(ReceivedFrame frame)
    {
        if (frame.EndsResponse) Tracker.Complete(frame.TrackingId, frame.Completion);
    }

    private void OnCallback(DemoResult result)
    {
        Interlocked.Increment(ref callbacks);
        Log(new DemoEvent(options.Time.GetUtcNow(), result.TrackingId, Stage.Callback, $"Callback で結果を受け取った: {result.Summary}"));
    }

    /// <summary>利用者の操作（設定の切り替えなど）を画面のログに出す。</summary>
    /// <param name="message">内容。</param>
    public void Note(string message) => Log(new DemoEvent(options.Time.GetUtcNow(), null, Stage.Control, message));

    private void Log(DemoEvent e) => events.Writer.TryWrite(e);
}
