namespace Eiii;

/// <summary>
/// 入出力型に依存しないワーカーの寿命制御（原型 <c>EiiiTask&lt;T&gt;</c> の
/// <c>Start</c> / <c>restartTask</c> / <c>GetStatus</c> に相当）。
/// 状態は独自の列挙型を持たず、実際の Task（<see cref="Execution"/>）から観測する。
/// </summary>
/// <remarks>
/// <para>正常終了: <c>CompleteInput</c> で入力を閉じると、受理済みの要求をすべて処理してから終了する。</para>
/// <para>中断: <see cref="StopAsync"/> はキャンセルによる中断である。取り出した要求は必ず処理（<c>ProcessAsync</c>）に渡され、
/// 処理中だった要求をどう終えるかは処理の実装が決める（キャンセル例外で抜ければ、その要求は処理されないまま終わる）。
/// まだ取り出していない要求は入力キューに残り、次の <see cref="Start"/> で受理順に処理される。</para>
/// <para>終端: 正常終了（RanToCompletion）した Worker は、<see cref="Start"/> / <see cref="RestartAsync"/> を呼んでも再び実行しない
/// （入力は閉じていて二度と開けないため）。Faulted / Canceled からは再び起動できる。</para>
/// <para>生存責任: Worker を必要な期間生存させる責任は利用者にある。利用者は Worker またはその所有者への参照を保持すること。
/// <see cref="Start"/> は Worker の生存を保証しない（外部から到達できなくなった実行中の Worker は、GC に回収され得る）。
/// また、<see cref="StopAsync"/> による停止は、Worker が GC によって直ちに回収されることを保証しない。</para>
/// <para>禁止事項: ワーカー内部（要求の処理中）から自身の <see cref="StopAsync"/> / <see cref="RestartAsync"/> を
/// await してはならない。自身の終了を自身で待つためデッドロックする（検出はしない）。</para>
/// </remarks>
public abstract class EiiiWorker
{
    private readonly Lock gate = new();
    private Run? current;

    /// <summary>
    /// 現在または直近の実行を表す Task。未起動なら null。
    /// 実行中の <see cref="Task.Status"/> は <see cref="TaskStatus.WaitingForActivation"/>
    /// （<see cref="Task.Run(Func{Task})"/> が返す代理 Task のため Running にはならない）。
    /// 終了後は RanToCompletion（正常終了）、Canceled（<see cref="StopAsync"/>）、Faulted（例外。原因は <see cref="Task.Exception"/>）。
    /// </summary>
    /// <remarks>
    /// Canceled は <see cref="StopAsync"/> による停止を表す。<c>ProcessAsync</c> または <c>OnInputCompletedAsync</c> から
    /// <see cref="OperationCanceledException"/> が出たとき、その時点で停止が要求されていなければ、<see cref="InvalidOperationException"/> に包まれ
    /// （<see cref="Exception.InnerException"/> に元の例外が入る）、Faulted になる。停止が要求されていれば Canceled になる。
    /// 分類は停止要求の有無で決まり、OCE が何によって発生したかは識別しない。
    /// </remarks>
    public Task? Execution
    {
        get
        {
            lock (gate) return current?.Execution;
        }
    }

    /// <summary>
    /// <see cref="Execution"/> が存在し、まだ完了していない（<see cref="Task.IsCompleted"/> が false）とき true。
    /// 停止を要求した後でも、ワーカーが実際に終了するまでは true のまま。
    /// </summary>
    public bool IsRunning => Execution is { IsCompleted: false };

    /// <summary>入力キューに残っている（まだ読み出していない）要求の数。</summary>
    public abstract int PendingCount { get; }

    /// <summary>
    /// ワーカーを起動する。実行中（停止処理中を含む）なら何もしない（多重起動しない）。
    /// 直前の実行が正常終了（RanToCompletion）していても何もしない（終端の状態。<see cref="Execution"/> は同じ Task のまま）。
    /// Faulted / Canceled で終わった後は、新しい実行を始める。実行の経過と結果は <see cref="Execution"/> で観測する。
    /// </summary>
    public void Start()
    {
        lock (gate)
        {
            if (current is { Execution: { IsCompleted: false } or { Status: TaskStatus.RanToCompletion } }) return;
            current = new Run(gate, RunAsync);
        }
    }

    /// <summary>
    /// 呼び出し時点の実行をキャンセルで中断し、その終了とキャンセルコールバックの完了を待つ。
    /// 処理中だった要求は失われ得る。結果（Canceled / Faulted など）は <see cref="Execution"/> で観測する。
    /// </summary>
    /// <returns>ワーカーが終了したときに完了する Task。ワーカーの例外はここでは再スローしない。</returns>
    public async Task StopAsync()
    {
        Task cancelling;
        Task execution;
        lock (gate)
        {
            if (current is null) return;
            cancelling = current.RequestCancellation();
            execution = current.Execution;
        }
        await Task.WhenAll(cancelling, execution).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    /// <summary>
    /// <see cref="StopAsync"/> で呼び出し時点の実行の終了を待ってから <see cref="Start"/> する。
    /// 完了した時点で、呼び出し時点の実行は終了しており、<see cref="Execution"/> はそれより後に開始された実行を指す。
    /// ただし、呼び出し時点の実行が正常終了（RanToCompletion）していた場合は、<see cref="Start"/> と同じく新しい実行を始めない。
    /// 並行に呼ばれた場合、完了の順序は呼び出しの順序と一致するとは限らない。
    /// </summary>
    /// <returns>再起動の要求が終わったときに完了する Task。</returns>
    public async Task RestartAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Start();
    }

    /// <summary>入力を閉じる。ワーカーは受理済みの要求をすべて処理してから正常終了する。閉じた入力は再び開けない。</summary>
    public abstract void CompleteInput();

    /// <summary>
    /// 入力を閉じ（<see cref="CompleteInput"/>）、呼び出した時点の実行（<see cref="Execution"/>）の終了を待つ。
    /// その実行が正常終了（RanToCompletion）すれば true、それ以外（未起動、Faulted、Canceled）は false を返す。
    /// </summary>
    /// <remarks>
    /// <para>状態の問い合わせではなく、正常な終了を要求するコマンドである。未起動でも入力は閉じる。Worker を起動はしない。
    /// Worker の例外は再スローしない。原因は <see cref="Execution"/> で確かめる。</para>
    /// <para>待つのは、<see cref="CompleteInput"/> より前に取得した実行だけである。同時に <see cref="StopAsync"/> / <see cref="RestartAsync"/> が
    /// 行われた場合、その後に始まった実行の完了は待たず、保証もしない。</para>
    /// <para>true は、対象の実行が入力を処理し終え、<c>OnInputCompletedAsync</c> を終え、下流の入力を閉じた（<c>LinkTo</c> で完了を伝播する場合）ことを表す。
    /// 業務上の成功や、下流の Worker 自身の完了は表さない。false の場合、未処理の要求が残ることも、再実行されることも保証しない。</para>
    /// <para>禁止事項: ワーカー内部から自身の <see cref="CompleteAsync"/> を await してはならない（自身の終了を自身で待つためデッドロックする）。</para>
    /// </remarks>
    /// <returns>対象の実行が正常終了したら true。</returns>
    public async Task<bool> CompleteAsync()
    {
        var execution = Execution;   // 待つ対象と判定する対象を、この 1 つの Task に固定する
        CompleteInput();
        if (execution is null) return false;
        await execution.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        return execution.Status == TaskStatus.RanToCompletion;
    }

    /// <summary>ワーカー本体。<see cref="Start"/> が新しい実行を始めるごとに 1 回、スレッドプール上で実行される。</summary>
    /// <param name="cancellationToken"><see cref="StopAsync"/> で取り消されるトークン。</param>
    /// <returns>ワーカーの終了を表す Task。</returns>
    protected abstract Task RunAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 1 回の実行。CTS の所有者であり、生成・取消・破棄をすべて自分で行う。
    /// 取消と破棄は所有者の lock（<c>gate</c>）の内側でだけ行うため、互いに競合しない。
    /// 破棄は「実行が完了」かつ「要求された取消のコールバックが完了」を満たした時点で、ちょうど 1 回行う。
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
        Justification = "CTS の解放責任は Run 自身にあり、実行と取消コールバックの完了後に ReleaseIfDone で 1 回だけ解放する。外部から任意の時点で解放させないため IDisposable は実装しない。")]
    private sealed class Run
    {
        private readonly Lock gate;
        private readonly CancellationTokenSource cancellation = new();
        private Task? cancelling;
        private bool disposed;

        /// <remarks>
        /// 初期化途中の公開を防ぐ順序: ワーカー（Task.Run のラムダ）は body と token だけを捕捉し、this を参照しない。
        /// this を継続へ渡すのは全フィールドの代入後であり、継続（ReleaseIfDone）は gate を取得するため、
        /// gate を保持して Run を構築している <see cref="Start"/> が抜けるまで実行されない。
        /// </remarks>
        public Run(Lock gate, Func<CancellationToken, Task> body)
        {
            this.gate = gate;
            var token = cancellation.Token;
            Execution = Task.Run(() => body(token));
            ReleaseWhenCompleted(Execution);
        }

        public Task Execution { get; }

        /// <summary>
        /// gate の内側で呼ぶ。CancelAsync は取消状態への遷移だけを同期的に行い、コールバックは別スレッドで実行する。
        /// 既に終了した実行は取り消さない（破棄済みの CTS に触れない）。
        /// </summary>
        public Task RequestCancellation()
        {
            if (disposed || Execution.IsCompleted) return Task.CompletedTask;
            if (cancelling is null)
            {
                cancelling = cancellation.CancelAsync();
                ReleaseWhenCompleted(cancelling);
            }
            return cancelling;
        }

        private void ReleaseWhenCompleted(Task task)
            => task.ContinueWith(static (_, state) => ((Run)state!).ReleaseIfDone(), this,
                                 CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);

        private void ReleaseIfDone()
        {
            lock (gate)
            {
                if (disposed || !Execution.IsCompleted || cancelling is { IsCompleted: false }) return;
                disposed = true;
                cancellation.Dispose();
            }
        }
    }
}
