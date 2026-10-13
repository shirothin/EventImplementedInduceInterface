namespace Eiii;

/// <summary>
/// 入出力型の異なるワーカーを名前で一元管理する（原型 TaskDbContext&lt;T&gt; に相当。Phase 2 完了時に TaskDbContext から改名）。
/// ワーカーは型に依存しない基底 <see cref="EiiiWorker"/> として保持し、取り出すときに具体的な型へ戻す。
/// </summary>
/// <remarks>
/// <para>責務: 入出力型の異なるワーカーの登録（<see cref="Add"/>）、名前による取得（<see cref="Get"/>）、登録順の列挙（<see cref="GetAll"/>）だけを担う。
/// ワーカーの起動・停止・完了には関与しない。原型 TaskDbContext と同じく、リソースを提供するだけで、タスク間の調停や制御はしない。</para>
/// <para>起動・停止・完了の順序、ワーカー間の依存関係、機器との通信、UI の更新、応答待ちなどはアプリケーション側の責務とする。
/// 接続関係の解析、依存グラフ、循環の検出、スケジューリングは行わない。登録順は依存関係の順序を意味しない。</para>
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

    private EiiiWorker[] Snapshot()
    {
        lock (gate) return [.. workers.Values];
    }
}
