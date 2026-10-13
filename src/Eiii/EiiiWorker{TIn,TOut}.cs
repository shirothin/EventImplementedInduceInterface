using System.Runtime.ExceptionServices;
using System.Threading.Channels;

namespace Eiii;

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
/// <see cref="EmitAsync"/> は空きを待ち続ける。この待機は <see cref="EiiiWorker.StopAsync"/> のキャンセルで解除される。</para>
/// </remarks>
public abstract class EiiiWorker<TIn, TOut> : EiiiWorker
{
    private readonly Channel<TIn> channel;
    private readonly TaskToCallback<TOut>? callback;
    private Link? link;   // 一度だけ設定する。設定は Interlocked.CompareExchange、読み取りは Volatile.Read

    /// <summary>入力キューの容量と、出力を観測するコールバックを指定して初期化する。</summary>
    /// <param name="capacity">
    /// 入力キューに保持できる要求の最大数。検証は <see cref="BoundedChannelOptions"/> に委ねる（負数は <see cref="ArgumentOutOfRangeException"/>）。
    /// 0 はバッファを持たない受け渡しになり、ワーカーが読み取りを待っていないと <c>TryWrite</c> は false を返す。
    /// </param>
    /// <param name="callback">出力ごとにワーカー上で呼ばれるコールバック（原型の cb）。null なら呼ばない。</param>
    protected EiiiWorker(int capacity, TaskToCallback<TOut>? callback = null)
    {
        channel = Channel.CreateBounded<TIn>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,   // 満杯時に捨てない（Drop 系を選ばない）
            SingleReader = true,                      // 読み手はワーカー 1 本（Start が多重起動しないことで保証）
        });
        this.callback = callback;
    }

    /// <summary>
    /// 入力口。<see cref="ChannelWriter{T}.TryWrite"/> または <see cref="ChannelWriter{T}.WriteAsync"/> で要求を投入する。
    /// <see cref="EiiiLauncher{T}.Attach"/> の接続先にもできる（要求する側に Worker を見せずに投入させる）。
    /// </summary>
    public ChannelWriter<TIn> Input => channel.Writer;

    /// <inheritdoc/>
    public override int PendingCount => channel.Reader.Count;

    /// <inheritdoc/>
    public override void CompleteInput() => channel.Writer.TryComplete();

    /// <summary>
    /// 出力を下流の入力口へつなぐ。接続は Worker の生存期間を通じて一度だけで、変更も解除もできない
    /// （Canceled / Faulted から再起動しても接続は変わらない）。
    /// 下流を指定しない場合、<see cref="EmitAsync"/> の出力はコールバックだけに渡される。
    /// </summary>
    /// <remarks>
    /// <para>完了の伝播: 正常終了したときだけ、<paramref name="propagateCompletion"/> が true なら下流の入力を閉じる。
    /// 停止（Canceled）と異常終了（Faulted）では伝播しない。複数の上流が同じ下流に接続する場合（合流）は false を指定し、
    /// 下流の入力はすべての上流が終わった後に利用者が閉じる。</para>
    /// <para>実行中の接続: <see cref="EmitAsync"/> は、呼び出し中に取得した接続状態を使う。接続を取得できなかった出力はコールバックにだけ渡される。
    /// <see cref="LinkTo"/> と並行して行われた出力に接続が適用されるかは保証しない。確実に配送するには、出力を始める前（Start の前）に接続すること。</para>
    /// </remarks>
    /// <param name="target">出力の書き込み先。通常は下流タスクの <see cref="Input"/>。</param>
    /// <param name="propagateCompletion">正常終了したときに下流の入力を閉じるなら true（既定）。</param>
    /// <exception cref="InvalidOperationException">すでに接続している（同じ接続先を指定した場合も含む）。</exception>
    public void LinkTo(ChannelWriter<TOut> target, bool propagateCompletion = true)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (Interlocked.CompareExchange(ref link, new Link(target, propagateCompletion), null) is not null)
            throw new InvalidOperationException("LinkTo は 1 つの Worker につき一度だけ呼べる（接続の変更や解除はできない）。");
    }

    /// <summary>接続先と完了の伝播の指定。両方を 1 つのオブジェクトとして一度に設定し、読む側が片方だけを見ることがないようにする。</summary>
    private sealed record Link(ChannelWriter<TOut> Target, bool PropagateCompletion);

    /// <summary>要求を 1 件処理する（原型 <c>eventFunc</c> に相当）。ワーカー上で 1 件ずつ呼ばれる。</summary>
    /// <remarks>
    /// ワーカーが入力キューから取り出した要求は、停止要求の有無にかかわらず、このメソッドに一度だけ渡される。
    /// 停止要求と競合した場合は、取り消し済みの <paramref name="cancellationToken"/> が渡されることがある。
    /// このメソッドの入口で <see cref="CancellationToken.IsCancellationRequested"/> を確認すると、その時点で停止が要求されているかを判別できる。
    /// 処理を続けるか中断するかは、このメソッドが決める。キューに残った要求は、再 Start されるまで処理されない。
    /// </remarks>
    /// <param name="request">入力キューから読み出した要求。</param>
    /// <param name="cancellationToken"><see cref="EiiiWorker.StopAsync"/> で取り消されるトークン。</param>
    /// <returns>処理の完了を表す ValueTask。</returns>
    protected abstract ValueTask ProcessAsync(TIn request, CancellationToken cancellationToken);

    /// <summary>
    /// 入力が完了し、キューが空になったときに呼び出される。保持中のデータを出し切る場所。
    /// 処理中に例外が発生して Worker が Faulted となり、再度起動された場合は、複数回呼び出されることがある。
    /// </summary>
    /// <param name="cancellationToken"><see cref="EiiiWorker.StopAsync"/> で取り消されるトークン。</param>
    /// <returns>処理の完了を表す ValueTask。</returns>
    protected virtual ValueTask OnInputCompletedAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;

    /// <summary>
    /// <see cref="ProcessAsync"/> に渡した要求 1 件の終わり方を通知する。<see cref="ProcessAsync"/> の呼び出しごとに一度だけ呼ばれる。
    /// キューに残った要求と、<see cref="OnInputCompletedAsync"/> については呼ばれない。
    /// </summary>
    /// <remarks>
    /// <para><see cref="ProcessAsync"/> が終わった後、実行ループの中で同期的に呼び出される。フックが戻るまで、次の要求の処理は始まらない。
    /// その要求の処理中に出力した値の callback は、この時点ですべて済んでいる。</para>
    /// <para><see cref="RequestEnd.Processed"/> は <see cref="ProcessAsync"/> が return したことだけを表し、業務上の成功は表さない。
    /// 停止以外のキャンセル例外と停止要求が同時に起きた場合は、捕捉した時点で停止が要求されていれば <see cref="RequestEnd.Stopped"/> になる。
    /// 分類と Worker の終了状態（Processed・Stopped なら継続または Canceled、Failed なら Faulted）は一致する。</para>
    /// <para>Eiii が保証するのは、このメソッドを一度呼ぶことまでである。通知を受け取って記録できたかどうかは利用者の責務とする。
    /// 「受理した数 = 通知の数 + PendingCount」は、フックが正常に戻り、実行の終了後に投入も再 Start もなく、呼び出しを漏れなく数えた場合に成り立つ。</para>
    /// <para>このメソッドが例外を投げると、Worker は Faulted になる。例外は <see cref="InvalidOperationException"/> に包まれ、メッセージに通知の種類が入る。
    /// <see cref="RequestEnd.Failed"/> の通知で失敗した場合は、処理の例外と包んだ例外の両方を持つ <see cref="AggregateException"/> になる
    /// （確認は <c>Execution.Exception.Flatten().InnerExceptions</c> で行う）。同じ要求への再通知や、<see cref="ProcessAsync"/> の再実行はしない。</para>
    /// <para>禁止事項: このメソッドから自身の <see cref="EiiiWorker.StopAsync"/> / <see cref="EiiiWorker.RestartAsync"/> を待ってはならない（デッドロックする）。
    /// <see cref="EmitAsync"/> の呼び出しはサポートしない（フックの終了時点で、非同期の出力の完了を保証できないため）。出力は <see cref="ProcessAsync"/> の中で行う。</para>
    /// </remarks>
    /// <param name="request">終わった要求。</param>
    /// <param name="ending">終わり方。</param>
    /// <param name="exception"><see cref="ProcessAsync"/> から出た元の例外（包む前のもの）。<see cref="RequestEnd.Processed"/> なら null。</param>
    protected virtual void OnRequestEnded(TIn request, RequestEnd ending, Exception? exception) { }

    /// <summary>
    /// 出力を下流へ渡し（満杯なら空きが出るまで待つ）、続いてコールバックを呼ぶ。
    /// コールバックは、出力が下流の Channel に受理されたこと（下流が未接続なら出力が生成されたこと）を示す。下流での処理の完了は保証しない。
    /// 下流もコールバックもない場合、出力はどこにも渡されない。
    /// </summary>
    /// <remarks>
    /// 接続先がある場合は、出力値の書き込みが完了してから callback を呼び出します。接続先に空きがない場合、書き込み可能になるまで非同期に待機することがあります。
    /// 書き込みがキャンセルされた場合や接続先が閉じられている場合、callback は呼び出されません。
    /// </remarks>
    /// <param name="value">出力する値。</param>
    /// <param name="cancellationToken">待機を取り消すトークン。通常は処理中のトークンを渡す。</param>
    /// <returns>書き込みとコールバックの完了を表す ValueTask。</returns>
    protected async ValueTask EmitAsync(TOut value, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref link) is { } current) await current.Target.WriteAsync(value, cancellationToken).ConfigureAwait(false);
        callback?.Invoke(value);   // 明示的なデリゲート呼び出し（マルチキャストなら結合順にすべて呼ばれる）
    }

    /// <inheritdoc/>
    protected sealed override async Task RunAsync(CancellationToken cancellationToken)
    {
        // ReadAllAsync は次の要素を取り出してから yield するため、その後でキャンセルを確認すると取り出した要求を捨ててしまう。
        // 停止の確認は取り出す前に行い、取り出した要求は必ず ProcessAsync に渡す（確認と取り出しの間に停止が割り込めば、取り消し済みのトークンで渡る）
        var reader = channel.Reader;
        try
        {
            while (await reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!reader.TryRead(out var request)) break;
                    await ProcessAndNotifyAsync(request, cancellationToken).ConfigureAwait(false);
                }
            }
            await OnInputCompletedAsync(cancellationToken).ConfigureAwait(false);
            if (Volatile.Read(ref link) is { PropagateCompletion: true } current) current.Target.TryComplete();   // 接続先と指定は同じ Link から読む
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            // async メソッドから OCE が出ると、発生源にかかわらず Task は Canceled になる。Canceled を StopAsync による停止だけに限るため、
            // 捕捉した時点で停止が要求されていなければ Faulted にする（OCE の発生源は識別しない。元の OCE は InnerException に残す）
            throw NonStopCancellation(ex);
        }
    }

    /// <summary>
    /// 要求を 1 件処理し、その終わり方を <see cref="OnRequestEnded"/> で一度だけ通知してから、Worker の終了につながる例外を伝える。
    /// フックに渡す元の例外（error）と、Worker が投げる例外（fault）を分ける。
    /// </summary>
    private async Task ProcessAndNotifyAsync(TIn request, CancellationToken cancellationToken)
    {
        RequestEnd end;
        Exception? error = null;
        Exception? fault = null;
        try
        {
            await ProcessAsync(request, cancellationToken).ConfigureAwait(false);
            end = RequestEnd.Processed;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            end = RequestEnd.Stopped;
            error = fault = ex;
        }
        catch (Exception ex)
        {
            end = RequestEnd.Failed;
            error = ex;
            // Failed と判定した OCE は、その場で包む。外側で包むと、通知の後に停止が割り込んだとき Canceled になり、通知と食い違う
            fault = ex is OperationCanceledException ? NonStopCancellation(ex) : ex;
        }

        try
        {
            OnRequestEnded(request, end, error);   // 処理本体の try の外で呼ぶ（フックの失敗を処理の失敗として二重に通知しない）
        }
        catch (Exception hookEx)   // OCE も含めて包む（包まないと Canceled に紛れる）
        {
            var wrapped = new InvalidOperationException($"OnRequestEnded({end}) が例外を投げた。", hookEx);
            throw end == RequestEnd.Failed ? new AggregateException(fault!, wrapped) : wrapped;
        }

        if (fault is not null) ExceptionDispatchInfo.Throw(fault);
    }

    private static InvalidOperationException NonStopCancellation(Exception cancellation)
        => new("Worker の停止以外のキャンセルで処理が終了した。", cancellation);
}
