using System.Threading.Channels;

namespace Eiii.Tests;

public class EiiiTaskStateTests
{
    [Fact]
    public void 起動前はExecutionがnullでIsRunningはfalse()
    {
        var task = new TestTask(4);

        Assert.Null(task.Execution);
        Assert.False(task.IsRunning);
    }

    [Fact]
    public async Task 実行中のStatusはWaitingForActivationでIsRunningはtrue()
    {
        var task = new TestTask(4);
        task.Start();
        try
        {
            Assert.Equal(TaskStatus.WaitingForActivation, task.Execution!.Status);
            Assert.True(task.IsRunning);
        }
        finally { await task.StopAsync(); }
    }

    [Fact]
    public async Task CompleteInputで受理済みを全件処理してRanToCompletionになり下流も完了する()
    {
        var task = new TestTask(8);
        var downstream = Channel.CreateUnbounded<int>();
        task.LinkTo(downstream.Writer);
        for (int i = 1; i <= 5; i++) Assert.True(task.Input.TryWrite(i));

        task.Start();
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, task.Execution.Status);
        Assert.False(task.IsRunning);
        Assert.Equal(1, task.InputCompletedCount);
        var outputs = await downstream.Reader.ReadAllAsync().ToArrayAsync();   // 下流の入力も閉じられているので列挙が終わる
        Assert.Equal([10, 20, 30, 40, 50], outputs);
    }

    [Fact]
    public async Task StopAsyncでCanceledになり未読の要求は残って再Startで処理され処理中の1件は失われる()
    {
        var entered = new TaskCompletionSource();
        var task = new TestTask(8, async (r, ct) =>
        {
            if (r != 1) return;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);   // 1 件目の処理中に止める
        });
        for (int i = 1; i <= 5; i++) task.Input.TryWrite(i);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync();

        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
        Assert.False(task.IsRunning);
        Assert.Equal(4, task.PendingCount);
        Assert.Empty(task.Processed);

        task.Start();
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal([2, 3, 4, 5], task.Processed);   // 1 は処理中に中断されたため失われた（文書化された動作）
    }

    [Fact]
    public async Task 例外でFaultedになり原因がExceptionに残り再Startで続きを処理する()
    {
        var task = new TestTask(8, (r, _) => r == 2 ? throw new InvalidOperationException("boom") : ValueTask.CompletedTask);
        for (int i = 1; i <= 3; i++) task.Input.TryWrite(i);

        task.Start();
        await task.Execution!.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.Faulted, task.Execution.Status);
        Assert.Equal("boom", task.Execution.Exception!.InnerException!.Message);
        Assert.Equal(1, task.PendingCount);

        task.Start();
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal([1, 3], task.Processed);
    }

    [Fact]
    public async Task CompleteInput後にStartするとすぐRanToCompletionになる()
    {
        var task = new TestTask(4);
        task.CompleteInput();

        task.Start();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, task.Execution.Status);
    }

    [Fact]
    public void 引数の検証は標準APIの例外に従う()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestTask(-1));
        Assert.Throws<ArgumentNullException>(() => new TestTask(1).LinkTo(null!));
    }

    [Fact]
    public void 容量0はバッファを持たずワーカーが待っていなければTryWriteはfalse()
    {
        var task = new TestTask(0);   // .NET 10 の BoundedChannel は容量 0（受け渡しのみ）を許容する

        Assert.False(task.Input.TryWrite(1));
        Assert.Equal(0, task.PendingCount);
    }
}

public class EiiiTaskBackpressureTests
{
    [Fact]
    public async Task 満杯時にTryWriteはfalseを返しWriteAsyncは空きを待つ()
    {
        var task = new TestTask(2);
        Assert.True(task.Input.TryWrite(1));
        Assert.True(task.Input.TryWrite(2));

        Assert.False(task.Input.TryWrite(3));
        var pending = task.Input.WriteAsync(3);
        Assert.False(pending.IsCompleted);

        task.Start();
        await pending.AsTask().WaitAsync(Wait.Timeout);
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal([1, 2, 3], task.Processed);
    }

    [Fact]
    public async Task 入力完了後のTryWriteはfalseでWriteAsyncはChannelClosedException()
    {
        var task = new TestTask(2);
        task.CompleteInput();

        Assert.False(task.Input.TryWrite(1));
        await Assert.ThrowsAsync<ChannelClosedException>(async () => await task.Input.WriteAsync(1));
    }

    [Fact]
    public async Task 下流が満杯でもStopAsyncのキャンセルでEmitAsyncの待機が解除される()
    {
        var task = new TestTask(4);
        var downstream = Channel.CreateBounded<int>(1);
        task.LinkTo(downstream.Writer);
        task.Input.TryWrite(1);
        task.Input.TryWrite(2);   // 2 の出力で下流が満杯になり、EmitAsync が待つ
        task.Start();
        await Wait.UntilAsync(() => task.Processed.Count == 2);

        await task.StopAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
    }

    [Fact]
    public async Task 要求が途切れなくてもStopAsyncで止まる()
    {
        var task = new TestTask(1000, (_, _) => { Thread.Sleep(1); return ValueTask.CompletedTask; });   // トークンを見ないハンドラー
        for (int i = 0; i < 1000; i++) task.Input.TryWrite(i);
        task.Start();
        await Wait.UntilAsync(() => task.Processed.Count > 0);

        await task.StopAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
        Assert.True(task.PendingCount > 0);
    }
}

public class EiiiTaskConcurrencyTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public async Task Startを二重に呼んでも同じ実行のまま()
    {
        var task = new TestTask(4);
        task.Start();
        var first = task.Execution;

        task.Start();

        Assert.Same(first, task.Execution);
        await task.StopAsync();
    }

    [Fact]
    public async Task 連続RestartAsyncを並行に呼んでもワーカーは1本()
    {
        var task = new TestTask(64, async (_, ct) => await Task.Delay(1, ct));
        task.Start();

        await Task.WhenAll(task.RestartAsync(), task.RestartAsync(), task.RestartAsync()).WaitAsync(Wait.Timeout);
        for (int i = 0; i < 50; i++) await task.Input.WriteAsync(i);
        await Wait.UntilAsync(() => task.PendingCount == 0);

        Assert.True(task.IsRunning);
        Assert.Equal(1, task.MaxConcurrent);
        await task.StopAsync();
    }

    [Fact]
    public async Task StopAsyncとStartとRestartAsyncを並行に繰り返しても例外なくワーカーは1本()
    {
        var task = new TestTask(8, async (_, _) => await Task.Yield());
        using var stopFeeding = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            int i = 0;
            while (!stopFeeding.IsCancellationRequested) { task.Input.TryWrite(i++); await Task.Yield(); }
        });

        for (int i = 0; i < 2000; i++)
        {
            task.Start();
            await Task.WhenAll(
                Task.Run(task.StopAsync),
                Task.Run(task.Start),
                Task.Run(task.RestartAsync)).WaitAsync(Wait.Timeout);   // ObjectDisposedException などが出れば失敗する
        }
        stopFeeding.Cancel();
        await feeder;

        Assert.Equal(1, task.MaxConcurrent);
        task.Start();
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);
        Assert.Equal(TaskStatus.RanToCompletion, task.Execution.Status);
    }

    [Fact]
    public async Task キャンセルコールバックが別スレッドで同じタスクの状態を待ってもデッドロックしない()
    {
        TestTask task = null!;
        var entered = new TaskCompletionSource();
        task = new TestTask(4, async (_, ct) =>
        {
            // コールバック内で、別スレッドから IsRunning（内部 lock を取る）を読み、その完了を同期的に待つ。
            // lock を保持したまま Cancel() を呼ぶ実装ではここでデッドロックする。
            using var registration = ct.Register(() => Task.Run(() => task.IsRunning).Wait());
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        task.Input.TryWrite(1);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
    }

    [Fact]
    public async Task キャンセルコールバックからStartを呼んでも多重起動しない()
    {
        TestTask task = null!;
        var entered = new TaskCompletionSource();
        task = new TestTask(64, async (r, ct) =>
        {
            if (r == 0)
            {
                ct.Register(() => task.Start());   // 再入: 停止処理中の Start
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
            }
            await Task.Delay(1, ct);
        });
        task.Input.TryWrite(0);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync().WaitAsync(Wait.Timeout);
        task.Start();   // コールバックで既に起動していれば何もしない
        for (int i = 1; i <= 20; i++) await task.Input.WriteAsync(i);
        await Wait.UntilAsync(() => task.Processed.Count == 20);

        Assert.Equal(1, task.MaxConcurrent);
        await task.StopAsync();
    }

    [Fact]
    public async Task 連続RestartAsyncは呼び出し時点の実行の終了後に完了し最終的に1本だけ動く()
    {
        var task = new TestTask(64, async (_, ct) => await Task.Delay(1, ct));
        task.Start();
        var outOfOrder = 0;

        for (int round = 0; round < 200; round++)
        {
            int callSequence = 0;
            var completionOrder = new System.Collections.Concurrent.ConcurrentQueue<int>();
            var restarts = Enumerable.Range(0, 3).Select(_ => Task.Run(async () =>
            {
                var seen = task.Execution!;                          // 呼び出し時点の実行
                var called = Interlocked.Increment(ref callSequence);   // 呼び出し順の番号
                await task.RestartAsync();
                var after = task.Execution!;
                completionOrder.Enqueue(called);
                return (seen, after);
            })).ToArray();

            var results = await Task.WhenAll(restarts).WaitAsync(Wait.Timeout);

            foreach (var (seen, after) in results)
            {
                Assert.True(seen.IsCompleted);       // 呼び出し時点の実行は終了している
                Assert.NotSame(seen, after);         // 完了時点の実行はそれより後に開始されたもの
            }
            Assert.True(task.IsRunning);             // すべて完了した後の最終状態は「1 本が動作中」
            if (!completionOrder.SequenceEqual([1, 2, 3])) outOfOrder++;
        }

        for (int i = 0; i < 20; i++) await task.Input.WriteAsync(i);
        await Wait.UntilAsync(() => task.PendingCount == 0);
        Assert.Equal(1, task.MaxConcurrent);
        await task.StopAsync();
        // 完了の順序は保証しない（記録のみ）。並行に開始した 3 件の完了順が呼び出し順と異なった回数を出力する。
        output.WriteLine($"完了順序が呼び出し順と異なったラウンド: {outOfOrder}/200");
    }

    [Fact]
    public async Task キャンセルコールバックの実行中にStartしても例外にならず処理を再開できる()
    {
        var callbackRunning = new TaskCompletionSource();
        var releaseCallback = new TaskCompletionSource();
        var task = new TestTask(8, (r, ct) =>
        {
            if (r == 0)
            {
                // 登録を解除しないまま処理を終える。停止時、このコールバックがワーカー終了後も走り続ける。
                ct.Register(() => { callbackRunning.TrySetResult(); releaseCallback.Task.Wait(); });
            }
            return ValueTask.CompletedTask;
        });
        task.Input.TryWrite(0);
        task.Start();
        await Wait.UntilAsync(() => task.Processed.Count == 1);

        var stopping = task.StopAsync();
        await callbackRunning.Task.WaitAsync(Wait.Timeout);
        await Wait.UntilAsync(() => !task.IsRunning);   // ワーカーは終了、コールバックはまだ実行中

        task.Start();                                    // 旧 CTS はコールバック実行中のため破棄されない
        releaseCallback.SetResult();
        await stopping.WaitAsync(Wait.Timeout);
        await task.Input.WriteAsync(1);
        await Wait.UntilAsync(() => task.Processed.Count == 2);

        Assert.True(task.IsRunning);
        await task.StopAsync();
    }
}

public class EiiiTaskCancellationSourceTests
{
    [Fact]
    public async Task 正常終了するとその実行のCTSが破棄される()
    {
        var task = new TestTask(4);
        task.Start();
        var run = RunInspector.CurrentRun(task);

        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        await Wait.UntilAsync(() => RunInspector.IsDisposed(run));
    }

    [Fact]
    public async Task 例外で終了してもその実行のCTSが破棄される()
    {
        var task = new TestTask(4, (_, _) => throw new InvalidOperationException("boom"));
        task.Input.TryWrite(1);
        task.Start();
        var run = RunInspector.CurrentRun(task);

        await Wait.UntilAsync(() => task.Execution!.IsFaulted);

        await Wait.UntilAsync(() => RunInspector.IsDisposed(run));
    }

    [Fact]
    public async Task 停止後と再起動後に旧実行のCTSが破棄される()
    {
        var task = new TestTask(4);
        task.Start();
        var first = RunInspector.CurrentRun(task);

        await task.RestartAsync();
        var second = RunInspector.CurrentRun(task);
        await task.StopAsync();

        Assert.NotSame(first, second);
        await Wait.UntilAsync(() => RunInspector.IsDisposed(first) && RunInspector.IsDisposed(second));
    }

    [Fact]
    public async Task キャンセルコールバックの実行中はCTSを破棄せず完了後に破棄する()
    {
        var callbackRunning = new TaskCompletionSource();
        var releaseCallback = new TaskCompletionSource();
        var task = new TestTask(4, (r, ct) =>
        {
            ct.Register(() => { callbackRunning.TrySetResult(); releaseCallback.Task.Wait(); });   // 解除しない登録
            return ValueTask.CompletedTask;
        });
        task.Input.TryWrite(1);
        task.Start();
        var run = RunInspector.CurrentRun(task);
        await Wait.UntilAsync(() => task.Processed.Count == 1);

        var stopping = task.StopAsync();
        await callbackRunning.Task.WaitAsync(Wait.Timeout);
        await Wait.UntilAsync(() => task.Execution!.IsCompleted);   // ワーカーは終了、コールバックは実行中
        await Task.Delay(50);

        Assert.False(RunInspector.IsDisposed(run));                  // コールバック実行中は破棄しない
        Assert.False(stopping.IsCompleted);                          // StopAsync はコールバックの完了も待つ

        releaseCallback.SetResult();
        await stopping.WaitAsync(Wait.Timeout);
        await Wait.UntilAsync(() => RunInspector.IsDisposed(run));   // 完了後に破棄する
    }

    [Fact]
    public async Task 実行が即座に完了してもCTSは構築完了後に1回だけ破棄される()
    {
        for (int i = 0; i < 200; i++)
        {
            var task = new TestTask(1);
            task.CompleteInput();                         // RunAsync がすぐ終わり、継続が構築中に予約され得る

            task.Start();
            var run = RunInspector.CurrentRun(task);

            await task.Execution!.WaitAsync(Wait.Timeout);
            Assert.Equal(TaskStatus.RanToCompletion, task.Execution.Status);
            await Wait.UntilAsync(() => RunInspector.IsDisposed(run));
        }
    }

    [Fact]
    public async Task 終了済みの実行に対するStopAsyncは破棄済みのCTSに触れない()
    {
        var task = new TestTask(4);
        task.Start();
        var run = RunInspector.CurrentRun(task);
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);
        await Wait.UntilAsync(() => RunInspector.IsDisposed(run));

        await task.StopAsync().WaitAsync(Wait.Timeout);   // ObjectDisposedException にならない

        Assert.Equal(TaskStatus.RanToCompletion, task.Execution.Status);
    }
}

public class EiiiTaskRestartStopTests
{
    [Fact]
    public async Task RestartAsyncとStopAsyncを同時に呼んでも多重起動や破棄済みCTSの例外がなく停止処理が完了する()
    {
        var task = new TestTask(16, async (_, ct) => await Task.Yield());
        var runs = new System.Collections.Concurrent.ConcurrentDictionary<object, bool>();
        using var stopFeeding = new CancellationTokenSource();
        var feeder = Task.Run(async () =>
        {
            int i = 0;
            while (!stopFeeding.IsCancellationRequested) { task.Input.TryWrite(i++); await Task.Yield(); }
        });
        task.Start();

        for (int round = 0; round < 1000; round++)
        {
            var operations = new[]
            {
                Task.Run(task.RestartAsync),
                Task.Run(task.StopAsync),
                Task.Run(task.RestartAsync),
                Task.Run(task.StopAsync),
            };
            await Task.WhenAll(operations).WaitAsync(Wait.Timeout);   // 例外・停止処理の異常（終わらない）があれば失敗
            if (task.Execution is not null) runs.TryAdd(RunInspector.CurrentRun(task), true);
            // 最終状態（起動中か停止中か）は呼び出しの競合で決まり、順序は保証しない
        }
        stopFeeding.Cancel();
        await feeder;
        await task.StopAsync();

        Assert.Equal(1, task.MaxConcurrent);
        await Wait.UntilAsync(() => runs.Keys.All(RunInspector.IsDisposed));   // 観測したすべての実行の CTS が解放される
    }
}
