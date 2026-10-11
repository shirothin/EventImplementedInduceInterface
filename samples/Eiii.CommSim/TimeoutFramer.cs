using System.Text;

namespace Eiii.CommSim;

/// <summary>時間方式で確定したフレームの終了理由。</summary>
public enum FrameEnd
{
    /// <summary>受信途中の無通信時間が <see cref="TimeoutOptions.IdleTimeout"/> に達した（Idle 方式）。</summary>
    IdleTimeout,

    /// <summary>最初の受信からの経過時間が <see cref="TimeoutOptions.CollectionTimeout"/> に達した（Fixed 方式）。</summary>
    CollectionTimeout,

    /// <summary>データが <see cref="TimeoutOptions.MaxFrameLength"/> を超えた。捨てずに、そこまでのデータ全体を出力する。</summary>
    Overflow,

    /// <summary>要求から <see cref="TimeoutOptions.ResponseTimeout"/> 以内に最初の受信がなかった（データなし）。</summary>
    NoResponse,

    /// <summary>入力完了時に残っていたデータ（時間条件を満たす前に終了した）。</summary>
    Flush,
}

/// <summary>
/// 時間方式で確定したフレーム。Text は受信したチャンクをそのまま連結したもので、内部の CR などの制御文字を保持する。
/// </summary>
/// <remarks>
/// 時間条件（IdleTimeout / CollectionTimeout）による確定は「その時点で受信が途切れた・打ち切った」ことしか表さず、
/// 応答を完全に受信したことの証明ではない。終端マーカーのない応答では、完全性は確認できない。
/// </remarks>
/// <param name="Text">連結したデータ。NoResponse のときは空。</param>
/// <param name="End">確定した理由。</param>
/// <param name="FirstReceivedAt">最初のチャンクの受信時刻（NoResponse のときは null）。</param>
/// <param name="LastReceivedAt">最後のチャンクの受信時刻（NoResponse のときは null）。</param>
public sealed record TimedFrame(string Text, FrameEnd End, DateTimeOffset? FirstReceivedAt, DateTimeOffset? LastReceivedAt);

/// <summary>時間方式の条件。3 つの時間は別の概念であり、互いに代用しない。</summary>
public sealed record TimeoutOptions
{
    /// <summary>
    /// 受信途中の無通信時間の閾値（Idle 方式）。最後の受信からこの時間データが来なければ、フレームを確定する。
    /// 応答の途中にこれより長い間欠があると、1 つの応答が 2 つに誤って分割される。
    /// </summary>
    public TimeSpan? IdleTimeout { get; init; }

    /// <summary>
    /// 最初の受信からの収集時間（Fixed 方式）。この時間が経てば、受信の途中でもフレームを確定する（秒単位の打ち切り）。
    /// Idle 方式と併用した場合は、先に達した条件で確定する（上限として働く）。
    /// </summary>
    public TimeSpan? CollectionTimeout { get; init; }

    /// <summary>
    /// 要求（<see cref="TimeoutFramer.ExpectResponse"/>）から最初の受信までの待機の上限。
    /// フレーム確定の閾値ではない。超えた場合は NoResponse を通知する。null なら応答待ちを監視しない。
    /// </summary>
    public TimeSpan? ResponseTimeout { get; init; }

    /// <summary>
    /// Overflow を検出する閾値（文字数）。暫定値。バッファがこの長さを超えた時点で、そこまでのデータ全体を Overflow として出力する。
    /// 厳密な出力長の上限ではない（超えた原因のチャンクまで含めるため、出力はこの長さをわずかに超え得る）。
    /// </summary>
    public int MaxFrameLength { get; init; } = 1024;
}

/// <summary>
/// 終端マーカーのない応答（OSD 取得などの特殊コマンド）を、時間の条件で 1 フレームに確定するロジック。
/// Worker やスレッドに依存しない純粋な処理で、時刻はすべて引数で受け取る。
/// </summary>
/// <remarks>
/// <para>時刻の扱い: チャンクの到着の判定には <see cref="Feed"/> に渡す受信時刻を使い、無通信の判定には <see cref="Poll"/> に渡す時刻を使う。
/// Worker がキューから取り出した時刻は使わない。そのため、キューで待たされて処理が遅れても、確定する境界は変わらない。</para>
/// <para>無通信の検出: 後続のデータが来なくても、<see cref="Poll"/> を定期的に呼べば検出できる。
/// <see cref="Poll"/> に渡す時刻以前に到着したチャンクは、すべて <see cref="Feed"/> 済みであることが前提（呼び出し側が順序を保証する）。</para>
/// <para>最大長の超過: 黙って捨てず、そこまでのデータ全体（最大長をわずかに超え得る）を Overflow として出力する。
/// 以降のデータは新しいフレームとして集めるため、同じ応答が分割される。</para>
/// <para>既知の制約:
/// (1) 時間条件による確定は電文の完全性を保証しない。
/// (2) MaxFrameLength は厳密な出力長の上限ではなく、Overflow を検出する閾値である。
/// (3) 経過時間の判定に実時刻（呼び出し側が渡す DateTimeOffset）を使うため、時計の補正（NTP など）の影響を受け得る。
/// (4) スレッドセーフではない（1 つの Worker だけが使う）。受信時刻は単調に増える前提。</para>
/// </remarks>
public sealed class TimeoutFramer
{
    private readonly TimeoutOptions options;
    private readonly StringBuilder buffer = new();
    private DateTimeOffset firstAt;
    private DateTimeOffset lastAt;
    private DateTimeOffset? responseDeadline;

    /// <summary>条件を指定して初期化する。</summary>
    /// <param name="options">IdleTimeout と CollectionTimeout の少なくとも一方が必要。</param>
    public TimeoutFramer(TimeoutOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.IdleTimeout is null && options.CollectionTimeout is null)
            throw new ArgumentException("IdleTimeout と CollectionTimeout の少なくとも一方を指定してください。", nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxFrameLength, 1, nameof(options));
        this.options = options;
    }

    /// <summary>まだ確定していないデータの長さ。</summary>
    public int BufferedLength => buffer.Length;

    /// <summary>要求を送ったことを知らせ、最初の受信を待ち始める。</summary>
    /// <param name="requestedAt">要求を送った時刻。</param>
    public void ExpectResponse(DateTimeOffset requestedAt)
    {
        if (options.ResponseTimeout is { } timeout) responseDeadline = requestedAt + timeout;
    }

    /// <summary>受信したチャンクを追加し、このチャンクの受信時刻までに確定したフレームを返す。</summary>
    /// <param name="text">受信したデータの断片（制御文字を含んでよい）。</param>
    /// <param name="receivedAt">チャンクが到着した時刻（キューから取り出した時刻ではない）。</param>
    /// <returns>確定したフレーム（0 個以上）。</returns>
    public IReadOnlyList<TimedFrame> Feed(string text, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(text);
        var frames = new List<TimedFrame>();
        CloseIfDue(receivedAt, frames);   // 受信時刻の時点で、前のフレームの時間条件が満たされていれば先に確定する

        if (buffer.Length == 0) firstAt = receivedAt;
        buffer.Append(text);
        lastAt = receivedAt;
        responseDeadline = null;          // 応答が始まった

        if (buffer.Length > options.MaxFrameLength) frames.Add(Take(FrameEnd.Overflow));
        return frames;
    }

    /// <summary>時刻 <paramref name="now"/> の時点で時間条件を満たしたフレームや NoResponse を返す（後続のデータがなくても検出できる）。</summary>
    /// <param name="now">判定する時刻。</param>
    /// <returns>確定したフレーム（0 個以上）。</returns>
    public IReadOnlyList<TimedFrame> Poll(DateTimeOffset now)
    {
        var frames = new List<TimedFrame>();
        CloseIfDue(now, frames);
        return frames;
    }

    /// <summary>残っているデータを Flush として取り出す（入力完了時）。</summary>
    /// <returns>データがあればフレーム、なければ null。</returns>
    public TimedFrame? Flush() => buffer.Length > 0 ? Take(FrameEnd.Flush) : null;

    private void CloseIfDue(DateTimeOffset at, List<TimedFrame> frames)
    {
        if (buffer.Length == 0)
        {
            if (responseDeadline is { } deadline && at >= deadline)
            {
                frames.Add(new TimedFrame("", FrameEnd.NoResponse, null, null));
                responseDeadline = null;
            }
            return;
        }

        var idleDeadline = options.IdleTimeout is { } idle ? lastAt + idle : DateTimeOffset.MaxValue;
        var collectionDeadline = options.CollectionTimeout is { } collection ? firstAt + collection : DateTimeOffset.MaxValue;
        if (at >= idleDeadline || at >= collectionDeadline)
            frames.Add(Take(idleDeadline <= collectionDeadline ? FrameEnd.IdleTimeout : FrameEnd.CollectionTimeout));
    }

    private TimedFrame Take(FrameEnd end)
    {
        var frame = new TimedFrame(buffer.ToString(), end, firstAt, lastAt);
        buffer.Clear();
        return frame;
    }
}
