namespace Eiii.CommSim;

/// <summary>任意電文の投入で比較する方式。</summary>
public enum CompareMode
{
    /// <summary>Delimiter / Idle / Fixed の 3 方式を順に実行して比較する。</summary>
    All,

    /// <summary>デリミタ方式だけ。</summary>
    Delimiter,

    /// <summary>Idle 方式（最後の受信からの無通信時間）だけ。</summary>
    Idle,

    /// <summary>Fixed 方式（最初の受信からの収集時間）だけ。</summary>
    Fixed,
}

/// <summary>整数の範囲（両端を含む）。</summary>
/// <param name="Min">最小値。</param>
/// <param name="Max">最大値。</param>
public readonly record struct IntRange(int Min, int Max);

/// <summary><c>custom</c> モードの設定。既定値は CommSim の既存シナリオと同じ値（記憶に基づく暫定値）。</summary>
public sealed record CustomOptions
{
    /// <summary>--text で指定した電文（エスケープ解釈後）。</summary>
    public string? Text { get; init; }

    /// <summary>--file で指定したファイルのパス。</summary>
    public string? FilePath { get; init; }

    /// <summary>比較する方式。</summary>
    public CompareMode Mode { get; init; } = CompareMode.All;

    /// <summary>デリミタ方式の区切り文字（エスケープ解釈後）。既定は CR（XPort の通常コマンド応答を想定）。</summary>
    public string Delimiter { get; init; } = "\r";

    /// <summary>乱数で分割するときのチャンク長の範囲。</summary>
    public IntRange ChunkRange { get; init; } = new(8, 40);

    /// <summary>乱数で決めるときの間欠の範囲（ms）。</summary>
    public IntRange GapRange { get; init; } = new(10, 100);

    /// <summary>明示した分割位置（文字の位置。昇順）。null なら乱数で分割する。</summary>
    public IReadOnlyList<int>? SplitPositions { get; init; }

    /// <summary>明示した間欠（ms）。2 番目以降の各チャンクの前に置く。null なら乱数で決める。</summary>
    public IReadOnlyList<int>? Gaps { get; init; }

    /// <summary>乱数の種（分割と間欠を再現するため）。</summary>
    public int Seed { get; init; } = 1;

    /// <summary>Idle 方式の無通信時間の閾値（ms）。</summary>
    public int IdleMilliseconds { get; init; } = 250;

    /// <summary>Fixed 方式の収集時間（ms）。</summary>
    public int CollectionMilliseconds { get; init; } = 2000;

    /// <summary>時間方式の MaxFrameLength（Overflow を検出する閾値）。</summary>
    public int MaxFrameLength { get; init; } = 1024;

    /// <summary>Tick の周期（ms）。</summary>
    public int TickMilliseconds { get; init; } = 25;

    /// <summary>チャンクの一覧も表示する。</summary>
    public bool Verbose { get; init; }

    /// <summary>使い方を表示して終わる。</summary>
    public bool ShowHelp { get; init; }
}

/// <summary>引数・入力の誤り。使い方を表示して終了コード 2 で終わる。</summary>
public sealed class CustomUsageException : Exception
{
    /// <summary>初期化する。</summary>
    public CustomUsageException()
    {
    }

    /// <summary>メッセージを指定して初期化する。</summary>
    /// <param name="message">誤りの内容。</param>
    public CustomUsageException(string message) : base(message)
    {
    }

    /// <summary>メッセージと原因を指定して初期化する。</summary>
    /// <param name="message">誤りの内容。</param>
    /// <param name="innerException">原因。</param>
    public CustomUsageException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
