using System.Threading.Channels;

namespace EventImplementedInduceInterface;

/// <summary>
/// 型付き要求（<typeparamref name="TIn"/>）を容量制限付きの <see cref="Channel{T}"/> で受け取り、
/// 1 本のワーカーで順に処理して <typeparamref name="TOut"/> を出力する。
/// </summary>
/// <typeparam name="TIn">要求（入力）の型。</typeparam>
/// <typeparam name="TOut">出力の型。</typeparam>
/// <remarks>
/// <para>同期投入は <c>Input.TryWrite</c>（満杯・入力完了なら即座に false）、
/// 非同期投入は <c>Input.WriteAsync</c>（満杯なら空きが出るまで待つ）。データを黙って破棄することはない。</para>
/// <para>バックプレッシャーによる無期限待機: 下流が未起動・停止中・Faulted のまま満杯になると、
/// <see cref="EmitAsync"/> は空きを待ち続ける。この待機は <see cref="EiiiTask.StopAsync"/> のキャンセルで解除される。</para>
/// </remarks>
public abstract class EiiiTask<TIn, TOut> : EiiiTask
{
    private readonly Channel<TIn> channel;
    private readonly TaskToCallback<TOut>? callback;
    private readonly EventHandler<object, EiiiEventArgs<TIn>> handler;
    private volatile ChannelWriter<TOut>? downstream;

    /// <summary>入力キューの容量と、出力を観測するコールバックを指定して初期化する。</summary>
    /// <param name="capacity">
    /// 入力キューに保持できる要求の最大数。検証は <see cref="BoundedChannelOptions"/> に委ねる（負数は <see cref="ArgumentOutOfRangeException"/>）。
    /// 0 はバッファを持たない受け渡しになり、ワーカーが読み取りを待っていないと <c>TryWrite</c> は false を返す。
    /// </param>
    /// <param name="callback">出力ごとにワーカー上で呼ばれるコールバック（原型の cb）。null なら呼ばない。</param>
    protected EiiiTask(int capacity, TaskToCallback<TOut>? callback = null)
    {
        channel = Channel.CreateBounded<TIn>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 満杯時に捨てない（Drop 系を選ばない）
            SingleReader = true,                      // 読み手はワーカー 1 本（Start が多重起動しないことで保証）
        });
        this.callback = callback;
        handler = new EventHandler<object, EiiiEventArgs<TIn>>(OnEvent);   // 原型 Subscribe と同じく明示的にデリゲートを生成する
    }

    /// <summary>入力口。<see cref="ChannelWriter{T}.TryWrite"/> または <see cref="ChannelWriter{T}.WriteAsync"/> で要求を投入する。</summary>
    public ChannelWriter<TIn> Input => channel.Writer;

    /// <inheritdoc/>
    public override int PendingCount => channel.Reader.Count;

    /// <inheritdoc/>
    public override void CompleteInput() => channel.Writer.TryComplete();

    /// <summary>
    /// 出力を下流の入力口へつなぐ。正常終了時は下流の入力も閉じる（完了の伝播）。
    /// 下流を指定しない場合、<see cref="EmitAsync"/> の出力はコールバックだけに渡される。
    /// </summary>
    /// <param name="target">出力の書き込み先。通常は下流タスクの <see cref="Input"/>。</param>
    public void LinkTo(ChannelWriter<TOut> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        downstream = target;
    }

    /// <summary>
    /// Launcher の要求イベントを購読する（原型 Subscribe）。同じ Launcher を重ねて購読しても 1 回分になる。
    /// 制約: Launcher と Worker は 1 対 1 で使う。1 つの Launcher を複数の Worker が購読した場合の Accepted の競合（最後の書き込みが残る）は、初期版ではサポートしない。
    /// </summary>
    /// <param name="launcher">要求を発行する Launcher。</param>
    public void Subscribe(ILauncher<TIn> launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        launcher.Raise -= handler;   // 未登録なら何もしない。デリゲートは対象インスタンスとメソッドが同じなら等しい
        launcher.Raise += handler;
    }

    /// <summary>Launcher の要求イベントの購読を解除する。</summary>
    /// <param name="launcher">購読を解除する Launcher。</param>
    public void Unsubscribe(ILauncher<TIn> launcher)
    {
        ArgumentNullException.ThrowIfNull(launcher);
        launcher.Raise -= handler;
    }

    /// <summary>
    /// 要求の到着通知を受け、入力キューへ同期投入する（原型 OnEvent。原型は処理そのものをここで実行していた）。
    /// 呼び出し元のスレッドで実行され、処理はワーカーが後で行う。sender はキューに載せない。
    /// </summary>
    private void OnEvent(object sender, EiiiEventArgs<TIn> e) => e.Accepted = channel.Writer.TryWrite(e.Value);

    /// <summary>要求を 1 件処理する（原型 <c>eventFunc</c> に相当）。ワーカー上で 1 件ずつ呼ばれる。</summary>
    /// <param name="request">入力キューから読み出した要求。</param>
    /// <param name="cancellationToken"><see cref="EiiiTask.StopAsync"/> で取り消されるトークン。</param>
    /// <returns>処理の完了を表す ValueTask。</returns>
    protected abstract ValueTask OnRequestAsync(TIn request, CancellationToken cancellationToken);

    /// <summary>入力が閉じられ、残りの要求をすべて処理した後に 1 回呼ばれる。保持中のデータを出し切る場所。</summary>
    /// <param name="cancellationToken"><see cref="EiiiTask.StopAsync"/> で取り消されるトークン。</param>
    /// <returns>処理の完了を表す ValueTask。</returns>
    protected virtual ValueTask OnInputCompletedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// 出力を下流へ渡し（満杯なら空きが出るまで待つ）、続いてコールバックを呼ぶ。
    /// コールバックは、出力が下流の Channel に受理されたこと（下流が未接続なら出力が生成されたこと）を示す。下流での処理の完了は保証しない。
    /// 下流もコールバックもない場合、出力はどこにも渡されない。
    /// </summary>
    /// <param name="value">出力する値。</param>
    /// <param name="cancellationToken">待機を取り消すトークン。通常は処理中のトークンを渡す。</param>
    /// <returns>書き込みとコールバックの完了を表す ValueTask。</returns>
    protected async ValueTask EmitAsync(TOut value, CancellationToken cancellationToken)
    {
        if (downstream is { } target) await target.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        callback?.Invoke(value);   // 明示的なデリゲート呼び出し（マルチキャストなら結合順にすべて呼ばれる）
    }

    /// <inheritdoc/>
    protected sealed override async Task RunAsync(CancellationToken cancellationToken)
    {
        await foreach (var request in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();   // ReadAllAsync は要素が続く間キャンセルを確認しない
            await OnRequestAsync(request, cancellationToken).ConfigureAwait(false);
        }
        await OnInputCompletedAsync(cancellationToken).ConfigureAwait(false);
        downstream?.TryComplete();
    }
}
