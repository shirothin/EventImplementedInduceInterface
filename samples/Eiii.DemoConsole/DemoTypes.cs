using Eiii.CommSim;

namespace Eiii.DemoConsole;

/// <summary>コマンドの種類。種類に応じた応答の終了判定の選択はアプリケーションの責務。</summary>
public enum CommandKind
{
    /// <summary>通常コマンド。応答は CR 終端（デリミタ方式）。</summary>
    Normal,

    /// <summary>OSD 取得コマンド。応答は内部に複数の CR を含み、終端マーカーがない（時間経過方式）。</summary>
    Osd,
}

/// <summary>コマンド。<paramref name="Id"/> はアプリケーション内の追跡用で、実機プロトコルが返す識別子ではない。</summary>
/// <param name="Id">追跡用の番号。</param>
/// <param name="Kind">コマンドの種類。</param>
/// <param name="RequestedAt">要求した時刻。</param>
public sealed record DemoCommand(int Id, CommandKind Kind, DateTimeOffset RequestedAt);

/// <summary>Receiver への応答待ちの開始通知（Eiii.Comm の RxInput を拡張）。どの方式で終了を判定するかを伝える。</summary>
/// <param name="Id">追跡用の番号。</param>
/// <param name="Kind">コマンドの種類。</param>
/// <param name="SentAt">機器へ送った時刻（応答待ちの上限の起点）。</param>
public sealed record ExpectResponse(int Id, CommandKind Kind, DateTimeOffset SentAt) : RxInput;

/// <summary>Receiver がフレームを確定した理由。</summary>
public enum Completion
{
    /// <summary>CR を検出した（通常コマンド）。</summary>
    Cr,

    /// <summary>無通信時間で確定した（OSD）。</summary>
    IdleTimeout,

    /// <summary>収集時間で確定した（OSD、Fixed 方式。このデモでは使っていない）。</summary>
    CollectionTimeout,

    /// <summary>最大長を超えた（応答の途中）。</summary>
    Overflow,

    /// <summary>応答待ちの上限までに最初の受信がなかった。</summary>
    NoResponse,

    /// <summary>次のコマンドの応答待ちを始めた時点で、前の応答の未確定データが残っていた（捨てずに出す）。</summary>
    Leftover,

    /// <summary>正常終了時に残っていたデータ。</summary>
    Flush,
}

/// <summary>Receiver が確定したフレーム。TrackingId は「その時点で待っていた要求」の番号であり、実際にどの要求の応答かは保証しない。</summary>
/// <param name="TrackingId">追跡用の番号（Receiver が受け取った最後の ExpectResponse のもの）。</param>
/// <param name="Kind">確定に使った方式に対応するコマンドの種類。</param>
/// <param name="Text">受信データ（CR 終端の場合は CR を除く）。</param>
/// <param name="Completion">確定した理由。</param>
public sealed record ReceivedFrame(int TrackingId, CommandKind Kind, string Text, Completion Completion)
{
    /// <summary>コマンドの直列化を解除してよい理由か（応答の確定または NoResponse）。</summary>
    public bool EndsResponse => Completion is Completion.Cr or Completion.IdleTimeout or Completion.CollectionTimeout or Completion.NoResponse;
}

/// <summary>Parser の解析結果。</summary>
/// <param name="TrackingId">追跡用の番号。</param>
/// <param name="Kind">コマンドの種類。</param>
/// <param name="Completion">フレームが確定した理由。</param>
/// <param name="Summary">画面に出す要約。</param>
public sealed record DemoResult(int TrackingId, CommandKind Kind, Completion Completion, string Summary);

/// <summary>画面に表示する流れの段階。</summary>
public enum Stage
{
    /// <summary>Launcher による要求の発行（受理 / 拒否）。</summary>
    Request,

    /// <summary>Commander が機器へ送信した。</summary>
    Send,

    /// <summary>Commander の応答待ち（開始 / 解除）。</summary>
    Wait,

    /// <summary>機器の動作。</summary>
    Device,

    /// <summary>Receiver がチャンクを処理した。</summary>
    Chunk,

    /// <summary>Receiver がフレームを確定した。</summary>
    Frame,

    /// <summary>Parser の処理。</summary>
    Parse,

    /// <summary>Parser の出力に対する Callback。</summary>
    Callback,

    /// <summary>利用者の操作（停止・再起動・終了・設定）。</summary>
    Control,
}

/// <summary>画面に表示する 1 件の出来事。</summary>
/// <param name="At">発生した時刻。</param>
/// <param name="Id">関係する追跡用の番号（なければ null）。</param>
/// <param name="Stage">段階。</param>
/// <param name="Message">内容。</param>
public sealed record DemoEvent(DateTimeOffset At, int? Id, Stage Stage, string Message);

/// <summary>デモの設定値。値は記憶に基づく概算や、観察しやすさのための値で、実機の仕様ではない。</summary>
public sealed record DemoOptions
{
    /// <summary>Commander（コマンド待ち行列）の容量。満杯なら Request は false。</summary>
    public int CommanderCapacity { get; init; } = 8;

    /// <summary>機器の入力キューの容量。</summary>
    public int DeviceCapacity { get; init; } = 4;

    /// <summary>Receiver の入力キューの容量（チャンクと Tick が入る）。</summary>
    public int ReceiverCapacity { get; init; } = 256;

    /// <summary>Parser の入力キューの容量。</summary>
    public int ParserCapacity { get; init; } = 4;

    /// <summary>通常コマンドの応答開始までの時間。</summary>
    public TimeSpan NormalLatency { get; init; } = TimeSpan.FromMilliseconds(150);

    /// <summary>OSD 応答の開始までの時間（中継の模擬。参考値は最大約 5 秒）。</summary>
    public TimeSpan OsdLatency { get; init; } = TimeSpan.FromMilliseconds(1500);

    /// <summary>OSD 応答の無通信時間の閾値。</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>OSD の応答待ちの上限。</summary>
    public TimeSpan OsdResponseTimeout { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>通常コマンドの応答待ちの上限。</summary>
    public TimeSpan NormalResponseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>G キーで OSD 応答の途中に入れる間欠。</summary>
    public TimeSpan LongGap { get; init; } = TimeSpan.FromMilliseconds(400);

    /// <summary>Tick の周期。</summary>
    public TimeSpan TickPeriod { get; init; } = TimeSpan.FromMilliseconds(25);

    /// <summary>時刻の取得元。</summary>
    public TimeProvider Time { get; init; } = TimeProvider.System;
}
