namespace Eiii;

/// <summary>
/// 入出力型の異なるワーカーを名前で一元管理する（原型 TaskDbContext&lt;T&gt; に相当。Phase 2 完了時に TaskDbContext から改名）。
/// ワーカーは型に依存しない基底 <see cref="EiiiWorker"/> として保持し、取り出すときに具体的な型へ戻す。
/// </summary>
/// <remarks>
/// <para>責務: 入出力型の異なるワーカーの登録、名前による検索、基本的な寿命管理だけを担う。
/// ワーカー間の依存関係、機器との通信、UI の更新、応答待ちなどの処理順序はアプリケーション側の責務とする。
/// 接続関係の解析、依存グラフ、循環の検出、スケジューリングは行わない。</para>
/// <para>一括操作（<see cref="StartAll"/> / <see cref="StopAllAsync"/> / <see cref="CompleteAllAsync"/>）は、
/// 簡単な構成のための便宜機能であり、任意の接続構成で正しい順序を保証するものではない。
/// 適用条件: ワーカーが一直線に接続（LinkTo）され、上流から下流の順に登録されていること。
/// <see cref="StopAllAsync"/> と <see cref="CompleteAllAsync"/> は登録順、<see cref="StartAll"/> は逆順に処理する。</para>
/// <para>制約: Registry は LinkTo による接続を知らない。下流を上流より先に登録すると、<see cref="CompleteAllAsync"/> は
/// 下流の入力を先に閉じるため、その後に上流が出力すると ChannelClosedException で上流が Faulted になる。
/// 分岐・合流・循環を含む構成では、アプリケーションが各ワーカーの CompleteInput / StopAsync を適切な順序で呼ぶこと。</para>
/// <para>結果の観測: 一括操作はワーカーの例外を再スローしない。各ワーカーの結果は <see cref="EiiiWorker.Execution"/> で観測する。</para>
/// </remarks>
public sealed class EiiiWorkerRegistry
{
    private readonly Lock gate = new();
    private readonly OrderedDictionary<string, EiiiWorker> workers = new(StringComparer.Ordinal);

    /// <summary>ワーカーを名前で登録する。</summary>
    /// <typeparam name="TWorker">ワーカーの具体的な型。戻り値もこの型のまま返す。</typeparam>
    /// <param name="name">登録名（大文字小文字を区別する）。</param>
    /// <param name="worker">登録するワーカー。</param>
    /// <returns>登録した <paramref name="worker"/>。</returns>
    /// <exception cref="ArgumentException">同じ名前が登録済み。既存の登録は変わらない。</exception>
    public TWorker Add<TWorker>(string name, TWorker worker) where TWorker : EiiiWorker
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(worker);
        lock (gate)
        {
            if (!workers.TryAdd(name, worker)) throw new ArgumentException($"'{name}' は登録済みです。", nameof(name));
        }
        return worker;
    }

    /// <summary>名前でワーカーを取り出す（原型 GetTaskByName）。</summary>
    /// <typeparam name="TWorker">取り出す型。基底の <see cref="EiiiWorker"/> も指定できる。</typeparam>
    /// <param name="name">登録名。</param>
    /// <returns>登録されているワーカー。</returns>
    /// <exception cref="KeyNotFoundException">未登録の名前。</exception>
    /// <exception cref="InvalidCastException">登録されているワーカーが <typeparamref name="TWorker"/> ではない。</exception>
    public TWorker Get<TWorker>(string name) where TWorker : EiiiWorker
    {
        lock (gate) return (TWorker)workers[name];
    }

    /// <summary>
    /// 登録済みのすべてのワーカーを、登録順のスナップショットとして返す（原型 GetAll）。
    /// 呼び出すたびに新しい一覧を作り、Registry はそれを保持しない。後から <see cref="Add"/> しても、取得済みの一覧は変わらない。
    /// </summary>
    /// <remarks>登録順は依存関係の順序を意味しない。起動・停止・完了の順序は、利用者が接続の構成に合わせて決める。</remarks>
    /// <returns>登録順のワーカーの一覧。</returns>
    public IReadOnlyList<EiiiWorker> GetAll() => Snapshot();

    /// <summary>登録済みのすべてのワーカーを、登録の逆順（下流から）に起動する。実行中のワーカーには何もしない。</summary>
    public void StartAll()
    {
        var snapshot = Snapshot();
        for (int i = snapshot.Length - 1; i >= 0; i--) snapshot[i].Start();
    }

    /// <summary>登録順（上流から）に、各ワーカーをキャンセルで停止し、その終了を待つ。処理中だった要求は失われ得る。</summary>
    /// <returns>すべてのワーカーの停止が終わったときに完了する Task。</returns>
    public async Task StopAllAsync()
    {
        foreach (var worker in Snapshot()) await worker.StopAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 登録順（上流から）に入力を閉じ、各ワーカーが残りを処理して終わるのを待つ（正常終了）。
    /// 上流が終わってから下流を閉じるため、上流が最後に出した出力も下流で処理される。
    /// </summary>
    /// <returns>すべてのワーカーの終了を待ち終えたときに完了する Task。</returns>
    /// <remarks>
    /// 起動していない・停止済みのワーカーは起動しない。その入力に残った要求は処理されず、PendingCount に残る。
    /// 途中のワーカーが例外で終わっても次へ進む。LinkTo による完了の伝播は正常終了時にしか起きないが、
    /// この操作が下流の入力も閉じるため、下流は入力完了を待ち続けず、それまでに受け取った分を処理して終わる。
    /// </remarks>
    public async Task CompleteAllAsync()
    {
        foreach (var worker in Snapshot())
        {
            worker.CompleteInput();
            if (worker.Execution is { } execution)
                await execution.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private EiiiWorker[] Snapshot()
    {
        lock (gate) return [.. workers.Values];
    }
}
