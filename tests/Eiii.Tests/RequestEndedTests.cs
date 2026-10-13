using System.Collections.Concurrent;

namespace Eiii.Tests;

/// <summary>OnRequestEnded（ProcessAsync に渡した要求ごとの終わり方の通知）。</summary>
public class RequestEndedTests
{
    private static OperationCanceledException UnrelatedCancellation()
    {
        using var other = new CancellationTokenSource();   // Worker とは無関係のトークン（要求ごとのキャンセルを模す）
        other.Cancel();
        return new OperationCanceledException(other.Token);
    }

    private static Task Settled(EiiiWorker task) => task.Execution!.ContinueWith(_ => { }, TaskScheduler.Default).WaitAsync(Wait.Timeout);

    [Fact]
    public async Task 正常に処理した要求は処理順にProcessedで通知されその要求の出力のcallbackより後に呼ばれる()
    {
        var log = new ConcurrentQueue<string>();
        var task = new TestTask(4, callback: v => log.Enqueue($"cb:{v}"), onEnded: (r, end, error) => log.Enqueue($"end:{r}:{end}:{error is null}"));
        for (int i = 1; i <= 3; i++) task.Input.TryWrite(i);

        task.Start();
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal(["cb:10", "end:1:Processed:True", "cb:20", "end:2:Processed:True", "cb:30", "end:3:Processed:True"], log);
    }

    [Fact]
    public async Task 停止を内部で処理して正常にreturnした要求はProcessedで通知されWorkerはCanceledになる()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new TestTask(4, async (_, ct) =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }   // HeavyWorkModel 型: 業務上は「停止」でも、Eiii から見れば正常に return した
        });
        task.Input.TryWrite(1);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync();

        Assert.Equal([(1, RequestEnd.Processed, (Exception?)null)], task.Ended);
        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
    }

    [Fact]
    public async Task 停止でキャンセル例外を投げた要求はStoppedで通知されStopAsyncの完了時点で通知が済んでいる()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = new TestTask(4, async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        task.Input.TryWrite(1);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync();

        var (request, end, error) = Assert.Single(task.Ended);   // StopAsync の完了時点で、処理中だった要求への通知は済んでいる
        Assert.Equal(1, request);
        Assert.Equal(RequestEnd.Stopped, end);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
    }

    [Fact]
    public async Task 通常の例外を投げた要求はFailedで元の例外が通知されWorkerの例外は従来と同じ()
    {
        var boom = new InvalidOperationException("boom");
        var task = new TestTask(4, (_, _) => throw boom);
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal([(1, RequestEnd.Failed, (Exception?)boom)], task.Ended);
        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        Assert.Same(boom, task.Execution.Exception!.InnerException);
    }

    [Fact]
    public async Task 停止以外のキャンセル例外はFailedで元のOCEが通知されWorkerは包まれた例外でFaultedになる()
    {
        var oce = UnrelatedCancellation();
        var task = new TestTask(4, (_, _) => throw oce);
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal([(1, RequestEnd.Failed, (Exception?)oce)], task.Ended);
        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var wrapped = Assert.IsType<InvalidOperationException>(task.Execution.Exception!.InnerException);
        Assert.Same(oce, wrapped.InnerException);
    }

    [Fact]
    public async Task callbackの例外はProcessAsyncの一部としてFailedで通知される()
    {
        var boom = new InvalidOperationException("callback");
        var task = new TestTask(4, callback: _ => throw boom);
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal([(1, RequestEnd.Failed, (Exception?)boom)], task.Ended);
        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
    }

    [Fact]
    public async Task Processedの通知でフックが例外を投げるとFaultedになり二重通知もProcessAsyncの再実行もない()
    {
        var hookError = new InvalidOperationException("hook");
        var task = new TestTask(4, onEnded: (r, _, _) => { if (r == 1) throw hookError; });
        for (int i = 1; i <= 3; i++) task.Input.TryWrite(i);

        task.Start();
        await Settled(task);

        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var wrapped = Assert.IsType<InvalidOperationException>(task.Execution.Exception!.InnerException);
        Assert.Contains("OnRequestEnded(Processed)", wrapped.Message);
        Assert.Same(hookError, wrapped.InnerException);
        Assert.Equal([(1, RequestEnd.Processed, (Exception?)null)], task.Ended);

        task.Start();   // 再 Start しても、要求 1 は再実行されない（取り出し済みのため）
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal(3, task.EnteredCount);
        Assert.Equal([1, 2, 3], task.Processed);
        Assert.Equal([1, 2, 3], task.Ended.Select(e => e.Request));
    }

    [Fact]
    public async Task Stoppedの通知でフックが例外を投げるとCanceledではなくFaultedになる()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hookError = new InvalidOperationException("hook");
        var task = new TestTask(4, async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }, onEnded: (_, _, _) => throw hookError);
        task.Input.TryWrite(1);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync();

        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var wrapped = Assert.IsType<InvalidOperationException>(task.Execution.Exception!.InnerException);
        Assert.Contains("OnRequestEnded(Stopped)", wrapped.Message);
        Assert.Same(hookError, wrapped.InnerException);
        Assert.Single(task.Ended);
    }

    [Fact]
    public async Task フックがキャンセル例外を投げてもCanceledにならずFaultedになる()
    {
        var hookOce = UnrelatedCancellation();
        var task = new TestTask(4, onEnded: (_, _, _) => throw hookOce);
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var wrapped = Assert.IsType<InvalidOperationException>(task.Execution.Exception!.InnerException);
        Assert.Same(hookOce, wrapped.InnerException);
    }

    [Fact]
    public async Task Failedの通知でフックが例外を投げると処理の例外とフックの例外の両方が残る()
    {
        var boom = new InvalidOperationException("boom");
        var hookError = new InvalidOperationException("hook");
        var task = new TestTask(4, (_, _) => throw boom, onEnded: (_, _, _) => throw hookError);
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var causes = task.Execution.Exception!.Flatten().InnerExceptions;
        Assert.Equal(2, causes.Count);
        Assert.Same(boom, causes[0]);   // 先頭は、フックがなかった場合に Worker が投げたはずの例外
        var wrapped = Assert.IsType<InvalidOperationException>(causes[1]);
        Assert.Contains("OnRequestEnded(Failed)", wrapped.Message);
        Assert.Same(hookError, wrapped.InnerException);
        Assert.Single(task.Ended);
    }

    [Fact]
    public async Task 停止以外のキャンセル例外の通知でフックが例外を投げると包まれたOCEとフックの例外の両方が残る()
    {
        var oce = UnrelatedCancellation();
        var hookError = new InvalidOperationException("hook");
        var task = new TestTask(4, (_, _) => throw oce, onEnded: (_, _, _) => throw hookError);
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var causes = task.Execution.Exception!.Flatten().InnerExceptions;
        Assert.Equal(2, causes.Count);
        Assert.Same(oce, Assert.IsType<InvalidOperationException>(causes[0]).InnerException);
        Assert.Same(hookError, Assert.IsType<InvalidOperationException>(causes[1]).InnerException);
        Assert.Equal([(1, RequestEnd.Failed, (Exception?)oce)], task.Ended);
    }

    [Fact]
    public async Task 停止と再起動を繰り返しても通知は処理に入った要求ごとに一度だけ届く()
    {
        const int cycles = 5;
        const int total = 15;
        var entered = new SemaphoreSlim(0);
        var blocking = true;
        var task = new TestTask(16, async (_, ct) =>
        {
            if (!Volatile.Read(ref blocking)) return;
            entered.Release();
            await Task.Delay(Timeout.Infinite, ct);   // 停止でキャンセル例外を投げる（Stopped）
        });
        for (int i = 1; i <= total; i++) Assert.True(task.Input.TryWrite(i));

        for (int c = 0; c < cycles; c++)
        {
            task.Start();
            Assert.True(await entered.WaitAsync(Wait.Timeout));
            await task.StopAsync();
            // フックが正常に戻り、実行の終了後に投入も再 Start もしていない時点で、受理した数 = 通知の数 + PendingCount
            Assert.Equal(total, task.Ended.Count + task.PendingCount);
            Assert.Equal(task.EnteredCount, task.Ended.Count);
        }

        Volatile.Write(ref blocking, false);
        task.Start();
        task.CompleteInput();
        await task.Execution!.WaitAsync(Wait.Timeout);

        Assert.Equal(Enumerable.Range(1, total), task.Ended.Select(e => e.Request));
        Assert.Equal(cycles, task.Ended.Count(e => e.End == RequestEnd.Stopped));
        Assert.Equal(total - cycles, task.Ended.Count(e => e.End == RequestEnd.Processed));
    }

    [Fact]
    public async Task 停止要求の後に要求ごとのキャンセル例外が出るとStoppedで通知されWorkerもCanceledになる()
    {
        // 停止要求が先、分類が後: 捕捉した時点で停止が要求されているので Stopped（通知と Worker の終了状態が一致する）
        var oce = UnrelatedCancellation();
        TestTask task = null!;
        task = new TestTask(4, (_, _) =>
        {
            _ = task.StopAsync();   // 待たない（StopAsync は最初の await より前に停止を要求する）
            throw oce;
        });
        task.Input.TryWrite(1);

        task.Start();
        await Settled(task);

        Assert.Equal([(1, RequestEnd.Stopped, (Exception?)oce)], task.Ended);
        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
    }

    [Fact]
    public async Task Failedと分類した後に停止要求が割り込んでもWorkerはFaultedで通知と一致する()
    {
        // 分類が先、停止要求が後: フックの中で待たせている間に停止を要求し、その後でフックを解放する
        var oce = UnrelatedCancellation();
        var opts = TaskCreationOptions.RunContinuationsAsynchronously;
        TaskCompletionSource hookEntered = new(opts), release = new(opts);
        var workerToken = CancellationToken.None;
        var task = new TestTask(4, (_, ct) =>
        {
            workerToken = ct;
            throw oce;
        }, onEnded: (_, end, _) =>
        {
            if (end != RequestEnd.Failed) return;
            hookEntered.SetResult();
            if (!release.Task.Wait(Wait.Timeout)) throw new TimeoutException("release されなかった");   // フックは同期メソッドなのでブロックして待つ
        });
        task.Input.TryWrite(1);
        task.Start();
        await hookEntered.Task.WaitAsync(Wait.Timeout);

        var stopping = task.StopAsync();   // ここでは await しない（フックの復帰を待ってデッドロックするため）
        Assert.True(workerToken.IsCancellationRequested);   // 停止要求は、分類の後・Worker の例外の伝播の前に入った
        release.SetResult();
        await stopping.WaitAsync(Wait.Timeout);

        Assert.Equal([(1, RequestEnd.Failed, (Exception?)oce)], task.Ended);
        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        var wrapped = Assert.IsType<InvalidOperationException>(task.Execution.Exception!.InnerException);
        Assert.Same(oce, wrapped.InnerException);
    }
}
