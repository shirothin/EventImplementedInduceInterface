using System.Collections.Concurrent;
using Eiii;

namespace Eiii.Tests;

/// <summary>要求 r を処理すると r を記録し、r * 10 を出力するテスト用タスク。ハンドラーの同時実行数も記録する。</summary>
internal sealed class TestTask(int capacity, Func<int, CancellationToken, ValueTask>? onRequest = null, TaskToCallback<int>? callback = null)
    : EiiiWorker<int, int>(capacity, callback)
{
    private int concurrent;
    private int maxConcurrent;

    public ConcurrentQueue<int> Processed { get; } = new();
    public int MaxConcurrent => Volatile.Read(ref maxConcurrent);
    public int InputCompletedCount;

    /// <summary>ProcessAsync に入った回数（キャンセル例外で中断した要求も含む）。</summary>
    public int EnteredCount;

    protected override async ValueTask ProcessAsync(int request, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref EnteredCount);
        var now = Interlocked.Increment(ref concurrent);
        int seen;
        while (now > (seen = Volatile.Read(ref maxConcurrent)) && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen) { }
        try
        {
            if (onRequest is not null) await onRequest(request, cancellationToken);
            Processed.Enqueue(request);
            await EmitAsync(request * 10, cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref concurrent);
        }
    }

    protected override ValueTask OnInputCompletedAsync(CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref InputCompletedCount);
        return ValueTask.CompletedTask;
    }
}

/// <summary>CTS の解放を検証するため、非公開の実行（Run）とその disposed フラグをリフレクションで読む（テスト専用）。</summary>
internal static class RunInspector
{
    private static readonly System.Reflection.FieldInfo CurrentField =
        typeof(EiiiWorker).GetField("current", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

    public static object CurrentRun(EiiiWorker task) => CurrentField.GetValue(task)!;

    public static bool IsDisposed(object run)
        => (bool)run.GetType().GetField("disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(run)!;
}

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    public static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(5, cts.Token);
    }
}
