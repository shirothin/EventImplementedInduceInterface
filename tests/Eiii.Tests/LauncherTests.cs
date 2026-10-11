using System.Collections.Concurrent;
using System.Threading.Channels;
using EventImplementedInduceInterface;

namespace Eiii.Tests;

/// <summary>Step 3: イベント発行 → Channel 投入 → ワーカー処理 → コールバック。</summary>
public class LauncherTests
{
    [Fact]
    public async Task イベント発行で投入されワーカーが処理しコールバックに結果が届く()
    {
        var results = new ConcurrentQueue<int>();
        var task = new TestTask(8, callback: new TaskToCallback<int>(results.Enqueue));   // 明示的なデリゲート生成
        var launcher = new EiiiLauncher<int>();
        task.Subscribe(launcher);

        // 1) イベント発行の時点では Channel に積まれるだけで、処理もコールバックもまだ起きない
        for (int i = 1; i <= 3; i++) Assert.True(launcher.Trigger(this, i));
        Assert.Equal(3, task.PendingCount);
        Assert.Empty(results);

        // 2) ワーカーを起動すると順に処理され、出力がコールバックに届く
        task.Start();
        Assert.True(launcher.Trigger(this, 4));
        await Wait.UntilAsync(() => results.Count == 4);

        Assert.Equal([10, 20, 30, 40], results);
        await task.StopAsync();
    }

    [Fact]
    public void senderは呼び出し元としてイベントの購読者に渡る()
    {
        var task = new TestTask(4);
        var launcher = new EiiiLauncher<int>();
        task.Subscribe(launcher);
        object? observed = null;
        launcher.Raise += (sender, _) => observed = sender;   // 利用者側の観察用購読者
        var caller = new object();

        launcher.Trigger(caller, 1);

        Assert.Same(caller, observed);
    }

    [Fact]
    public void 購読者がいなければTriggerはfalseを返す()
    {
        Assert.False(new EiiiLauncher<int>().Trigger(new object(), 1));
    }

    [Fact]
    public void 満杯ではTriggerはfalseを返し受理済みのデータは残る()
    {
        var task = new TestTask(1);
        var launcher = new EiiiLauncher<int>();
        task.Subscribe(launcher);

        Assert.True(launcher.Trigger(this, 1));
        Assert.False(launcher.Trigger(this, 2));   // 満杯: 待たずに false（呼び出し元が再送を判断する）

        Assert.Equal(1, task.PendingCount);        // 受理済みの 1 は捨てられていない
    }

    [Fact]
    public void 入力完了後のTriggerはfalseを返す()
    {
        var task = new TestTask(4);
        var launcher = new EiiiLauncher<int>();
        task.Subscribe(launcher);
        task.CompleteInput();

        Assert.False(launcher.Trigger(this, 1));
    }

    [Fact]
    public void 同じLauncherを二重に購読しても1回だけ投入される()
    {
        var task = new TestTask(4);
        var launcher = new EiiiLauncher<int>();
        task.Subscribe(launcher);
        task.Subscribe(launcher);

        Assert.True(launcher.Trigger(this, 1));

        Assert.Equal(1, task.PendingCount);
    }

    [Fact]
    public void 購読を解除するとTriggerはfalseで投入されない()
    {
        var task = new TestTask(4);
        var launcher = new EiiiLauncher<int>();
        task.Subscribe(launcher);

        task.Unsubscribe(launcher);

        Assert.False(launcher.Trigger(this, 1));
        Assert.Equal(0, task.PendingCount);
    }

    [Fact]
    public async Task コールバックは下流への書き込みが完了した後に呼ばれる()
    {
        var results = new ConcurrentQueue<int>();
        var task = new TestTask(4, callback: results.Enqueue);
        var downstream = Channel.CreateBounded<int>(1);
        downstream.Writer.TryWrite(-1);   // 下流を満杯にしておく
        task.LinkTo(downstream.Writer);
        task.Input.TryWrite(1);
        task.Start();
        await Wait.UntilAsync(() => task.Processed.Count == 1);
        await Task.Delay(50);

        Assert.Empty(results);                           // 下流が満杯で EmitAsync が待っている間はコールバックされない

        Assert.True(downstream.Reader.TryRead(out _));   // 下流に空きを作る
        await Wait.UntilAsync(() => results.Count == 1);

        Assert.Equal([10], results);
        await task.StopAsync();
    }

    [Fact]
    public async Task コールバックで例外が起きるとワーカーはFaultedになる()
    {
        var task = new TestTask(4, callback: _ => throw new InvalidOperationException("callback"));
        task.Input.TryWrite(1);

        task.Start();
        await Wait.UntilAsync(() => task.Execution!.IsCompleted);

        Assert.Equal(TaskStatus.Faulted, task.Execution!.Status);
        Assert.Equal("callback", task.Execution.Exception!.InnerException!.Message);
    }

    [Fact]
    public async Task 結合したデリゲートは結合順にすべて呼ばれる()
    {
        var log = new ConcurrentQueue<string>();
        TaskToCallback<int> first = v => log.Enqueue($"A{v}");
        TaskToCallback<int> second = v => log.Enqueue($"B{v}");
        var combined = first + second;                   // マルチキャストデリゲート（Delegate.Combine）
        var task = new TestTask(4, callback: combined);
        task.Input.TryWrite(1);

        task.Start();
        await Wait.UntilAsync(() => log.Count == 2);

        Assert.Equal(["A10", "B10"], log);
        Assert.Equal(2, combined.GetInvocationList().Length);
        await task.StopAsync();
    }
}
