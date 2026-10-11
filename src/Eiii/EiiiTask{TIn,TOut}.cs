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
    private volatile ChannelWriter<TOut>? downstream;

    /// <summary>入力キューの容量を指定して初期化する。</summary>
    /// <param name="capacity">
    /// 入力キューに保持できる要求の最大数。検証は <see cref="BoundedChannelOptions"/> に委ねる（負数は <see cref="ArgumentOutOfRangeException"/>）。
    /// 0 はバッファを持たない受け渡しになり、ワーカーが読み取りを待っていないと <c>TryWrite</c> は false を返す。
    /// </param>
    protected EiiiTask(int capacity)
    {
        channel = Channel.CreateBounded<TIn>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 満杯時に捨てない（Drop 系を選ばない）
            SingleReader = true,                      // 読み手はワーカー 1 本（Start が多重起動しないことで保証）
        });
    }

    /// <summary>入力口。<see cref="ChannelWriter{T}.TryWrite"/> または <see cref="ChannelWriter{T}.WriteAsync"/> で要求を投入する。</summary>
    public ChannelWriter<TIn> Input => channel.Writer;

    /// <inheritdoc/>
    public override int PendingCount => channel.Reader.Count;

    /// <inheritdoc/>
    public override void CompleteInput() => channel.Writer.TryComplete();

    /// <summary>
    /// 出力を下流の入力口へつなぐ。正常終了時は下流の入力も閉じる（完了の伝播）。
    /// 下流を指定しない場合、<see cref="EmitAsync"/> の出力はどこにも渡されない。
    /// </summary>
    /// <param name="target">出力の書き込み先。通常は下流タスクの <see cref="Input"/>。</param>
    public void LinkTo(ChannelWriter<TOut> target)
    {
        ArgumentNullException.ThrowIfNull(target);
        downstream = target;
    }

    /// <summary>要求を 1 件処理する（原型 <c>eventFunc</c> に相当）。ワーカー上で 1 件ずつ呼ばれる。</summary>
    /// <param name="request">入力キューから読み出した要求。</param>
    /// <param name="cancellationToken"><see cref="EiiiTask.StopAsync"/> で取り消されるトークン。</param>
    /// <returns>処理の完了を表す ValueTask。</returns>
    protected abstract ValueTask OnRequestAsync(TIn request, CancellationToken cancellationToken);

    /// <summary>入力が閉じられ、残りの要求をすべて処理した後に 1 回呼ばれる。保持中のデータを出し切る場所。</summary>
    /// <param name="cancellationToken"><see cref="EiiiTask.StopAsync"/> で取り消されるトークン。</param>
    /// <returns>処理の完了を表す ValueTask。</returns>
    protected virtual ValueTask OnInputCompletedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>出力を下流へ渡す。下流が満杯なら空きが出るまで待つ。</summary>
    /// <param name="value">出力する値。</param>
    /// <param name="cancellationToken">待機を取り消すトークン。通常は処理中のトークンを渡す。</param>
    /// <returns>書き込みの完了を表す ValueTask。</returns>
    protected ValueTask EmitAsync(TOut value, CancellationToken cancellationToken)
        => downstream is { } target ? target.WriteAsync(value, cancellationToken) : ValueTask.CompletedTask;

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
