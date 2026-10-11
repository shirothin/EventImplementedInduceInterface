namespace Eiii.DemoConsole;

/// <summary>
/// コマンド直列化のための応答待ち。待機は <see cref="TaskCompletionSource{TResult}"/> による非同期待機で、スレッドを占有しない。
/// </summary>
/// <remarks>
/// 解除の条件は Receiver による応答の確定（CR / IdleTimeout / CollectionTimeout）または NoResponse。Parser の処理完了ではない。
/// 実機のプロトコルには要求 ID がないため、届いたデータがどの要求の応答かは区別できない。
/// Receiver は「その時点で待っている要求」の応答とみなして番号を付け、番号が一致したときだけ待機を解除する。
/// </remarks>
public sealed class ResponseTracker
{
    private readonly Lock gate = new();
    private int? currentId;
    private DateTimeOffset since;
    private TaskCompletionSource<Completion>? pending;

    /// <summary>現在待っている要求の番号と、待ち始めた時刻。待っていなければ null。</summary>
    public (int Id, DateTimeOffset Since)? Current
    {
        get
        {
            lock (gate) return currentId is { } id ? (id, since) : null;
        }
    }

    /// <summary>要求 <paramref name="id"/> の応答待ちを始める。前の待機が残っていれば取り消す（取り残さない）。</summary>
    /// <param name="id">追跡用の番号。</param>
    /// <param name="now">待ち始めた時刻。</param>
    /// <returns>応答が確定したときに、確定した理由で完了する Task。</returns>
    public Task<Completion> Begin(int id, DateTimeOffset now)
    {
        var next = new TaskCompletionSource<Completion>(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<Completion>? previous;
        lock (gate)
        {
            previous = pending;
            pending = next;
            currentId = id;
            since = now;
        }
        previous?.TrySetCanceled();
        return next.Task;
    }

    /// <summary>番号が現在の待機と一致すれば、待機を解除する。</summary>
    /// <param name="id">確定したフレームの番号。</param>
    /// <param name="completion">確定した理由。</param>
    /// <returns>待機を解除したら true。</returns>
    public bool Complete(int id, Completion completion)
    {
        TaskCompletionSource<Completion>? done;
        lock (gate)
        {
            if (currentId != id) return false;
            done = pending;
            pending = null;
            currentId = null;
        }
        return done?.TrySetResult(completion) ?? false;
    }

    /// <summary>要求 <paramref name="id"/> の待機が残っていれば取り消す（キャンセル・再起動・終了時の後始末）。</summary>
    /// <param name="id">追跡用の番号。</param>
    public void Cancel(int id)
    {
        TaskCompletionSource<Completion>? abandoned;
        lock (gate)
        {
            if (currentId != id) return;
            abandoned = pending;
            pending = null;
            currentId = null;
        }
        abandoned?.TrySetCanceled();
    }
}
