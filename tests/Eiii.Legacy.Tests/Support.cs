using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EventImplementedInduceInterface;

// 原型は Console とスレッドプールを共有し、時間に依存するため、テストを直列に実行する。
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]

namespace Eiii.Legacy.Tests;

/// <summary>特性テストの分類。Intended=意図された動作（維持）、Defect=欠陥（後で期待値を反転）、Unknown=意図不明。</summary>
internal static class Kind
{
    public const string Name = "Kind";
    public const string Intended = "Intended";
    public const string Defect = "Defect";
    public const string Unknown = "Unknown";
}

internal static class Legacy
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    public static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < (timeout ?? DefaultTimeout))
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    /// <summary>原型には停止 API がないため、後始末としてリフレクションで内部の CTS をキャンセルする（テスト専用）。</summary>
    public static void CancelLoop<T>(EiiiTask<T> task)
    {
        var field = typeof(EiiiTask<T>).GetField("cancelsource", BindingFlags.NonPublic | BindingFlags.Instance);
        ((CancellationTokenSource)field.GetValue(task)).Cancel();
    }
}

/// <summary>原型の TaskToCallback(string) が受け取った値を記録する。</summary>
internal sealed class CallbackRecorder
{
    private readonly ConcurrentQueue<int> values = new();
    private volatile bool throwNext;

    public TaskToCallback Callback => message =>
    {
        if (throwNext)
        {
            throwNext = false;
            throw new InvalidOperationException("callback failure");
        }
        values.Enqueue(int.Parse(message));
    };

    public int[] Values => values.ToArray();
    public int Count => values.Count;
    public bool ThrowPending => throwNext;

    public void ThrowOnNextCallback() => throwNext = true;
}

/// <summary>eventFunc の呼び出しを記録するだけのタスク。tlc=0（Single）なので周期ループは回らない。</summary>
internal sealed class EventProbe : EiiiTask<MyItem>
{
    public EventProbe() : base(_ => { }, null, 100, 0) { }

    public object Sender { get; private set; }
    public MyItem Received { get; private set; }
    public int ThreadId { get; private set; }

    internal override void eventFunc<T>(object sender, EiiiEventArgs<T> e)
    {
        Sender = sender;
        ThreadId = Environment.CurrentManagedThreadId;
        Received = e.Value as MyItem;   // メソッド側の T が MyItem だと分からないため、キャストが必要
    }
}
