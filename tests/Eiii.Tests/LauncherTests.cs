using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Eiii;

namespace Eiii.Tests;

/// <summary>
/// Launcher（第二次改良 ⑥: 単一の入力先へ要求を投入し、その結果をイベントで通知する）と、Callback。
/// 接続を保持するのは Launcher 自身で、Attach の戻り値は解除が必要なときにだけ使う。
/// </summary>
public class LauncherTests
{
    private static List<int> Drain(ChannelReader<int> reader)
    {
        var values = new List<int>();
        while (reader.TryRead(out var value)) values.Add(value);
        return values;
    }

    [Fact]
    public void 接続していなければRequestはfalseで観測者は確定したAcceptedを受け取る()
    {
        var launcher = new EiiiLauncher<int>();
        bool? observed = null;
        launcher.Requested += (_, e) => observed = e.Accepted;

        Assert.False(launcher.IsAttached);
        Assert.False(launcher.Request(1));
        Assert.False(observed);
    }

    [Fact]
    public async Task 接続するとRequestで入力に投入されワーカーが処理しコールバックに結果が届く()
    {
        var results = new ConcurrentQueue<int>();
        var task = new TestTask(8, callback: new TaskToCallback<int>(results.Enqueue));   // 明示的なデリゲート生成
        var launcher = new EiiiLauncher<int>();
        launcher.Attach(task.Input);
        Assert.True(launcher.IsAttached);

        // 1) Request の時点では入力キューに積まれるだけで、処理もコールバックもまだ起きない
        for (int i = 1; i <= 3; i++) Assert.True(launcher.Request(i));
        Assert.Equal(3, task.PendingCount);
        Assert.Empty(results);

        // 2) ワーカーを起動すると順に処理され、出力がコールバックに届く
        task.Start();
        Assert.True(launcher.Request(4));
        await Wait.UntilAsync(() => results.Count == 4);

        Assert.Equal([10, 20, 30, 40], results);
        await task.StopAsync();
    }

    [Fact]
    public void 接続中にAttachするとInvalidOperationExceptionで最初の接続のまま()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var launcher = new EiiiLauncher<int>();
        launcher.Attach(first.Writer);

        Assert.Throws<InvalidOperationException>(() => launcher.Attach(second.Writer));

        Assert.True(launcher.Request(1));
        Assert.Equal([1], Drain(first.Reader));
        Assert.Empty(Drain(second.Reader));
    }

    [Fact]
    public async Task 戻り値をDisposeすると接続が外れるがワーカーは止まらず入力も閉じない()
    {
        var task = new TestTask(4);
        task.Start();
        var launcher = new EiiiLauncher<int>();
        var attachment = launcher.Attach(task.Input);

        attachment.Dispose();

        Assert.False(launcher.IsAttached);
        Assert.False(launcher.Request(1));
        Assert.Equal(0, task.PendingCount);                   // 配送されていない
        Assert.True(task.IsRunning);                          // ワーカーは止めない
        Assert.True(await task.Input.WaitToWriteAsync());     // 入力も閉じない
        await task.StopAsync();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AttachAndDiscard(EiiiLauncher<int> launcher, ChannelWriter<int> target) => _ = launcher.Attach(target);

    [Fact]
    public void 戻り値を保持しなくてもGCで接続は外れずLauncherが接続を持ち続ける()
    {
        var target = Channel.CreateUnbounded<int>();
        var launcher = new EiiiLauncher<int>();   // Launcher への強い参照は持ったまま
        AttachAndDiscard(launcher, target.Writer);

        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        Assert.True(launcher.IsAttached);
        Assert.True(launcher.Request(1));
        Assert.Equal([1], Drain(target.Reader));
        GC.KeepAlive(launcher);
    }

    [Fact]
    public void Disposeは二度呼んでも安全で古い接続のDisposeは新しい接続を外さない()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var launcher = new EiiiLauncher<int>();
        var old = launcher.Attach(first.Writer);
        old.Dispose();
        old.Dispose();

        launcher.Attach(second.Writer);
        old.Dispose();   // 古い接続の Dispose

        Assert.True(launcher.IsAttached);
        Assert.True(launcher.Request(1));
        Assert.Equal([1], Drain(second.Reader));
    }

    [Fact]
    public void 解除した後に別の接続先へ接続し直せる()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var launcher = new EiiiLauncher<int>();
        var attachment = launcher.Attach(first.Writer);
        Assert.True(launcher.Request(1));
        attachment.Dispose();

        launcher.Attach(second.Writer);
        Assert.True(launcher.Request(2));

        Assert.Equal([1], Drain(first.Reader));
        Assert.Equal([2], Drain(second.Reader));
    }

    [Fact]
    public void 観測者は登録の順序によらず配送で確定したAcceptedを受け取る()
    {
        var target = Channel.CreateBounded<int>(1);
        var launcher = new EiiiLauncher<int>();
        var before = new List<(int, bool)>();
        var after = new List<(int, bool)>();
        launcher.Requested += (_, e) => before.Add((e.Value, e.Accepted));   // 接続より前に登録
        launcher.Attach(target.Writer);
        launcher.Requested += (_, e) => after.Add((e.Value, e.Accepted));    // 接続より後に登録

        Assert.True(launcher.Request(1));
        Assert.False(launcher.Request(2));   // 満杯

        Assert.Equal([(1, true), (2, false)], before);
        Assert.Equal([(1, true), (2, false)], after);
    }

    [Fact]
    public void 観測者が例外を投げると呼び出し元に伝わるが要求はすでに配送されている()
    {
        var target = Channel.CreateUnbounded<int>();
        var launcher = new EiiiLauncher<int>();
        launcher.Attach(target.Writer);
        launcher.Requested += (_, _) => throw new InvalidOperationException("observer");

        var ex = Assert.Throws<InvalidOperationException>(() => launcher.Request(1));

        Assert.Equal("observer", ex.Message);
        Assert.Equal([1], Drain(target.Reader));   // 配送はイベントより前に済んでいる
    }

    [Fact]
    public void senderは引数なしならLauncher自身で指定すればその値が渡りnullはArgumentNullException()
    {
        var launcher = new EiiiLauncher<int>();
        launcher.Attach(Channel.CreateUnbounded<int>().Writer);
        var senders = new List<object>();
        launcher.Requested += (sender, _) => senders.Add(sender);
        var caller = new object();

        launcher.Request(1);
        launcher.Request(caller, 2);

        Assert.Same(launcher, senders[0]);
        Assert.Same(caller, senders[1]);
        Assert.Throws<ArgumentNullException>(() => launcher.Request(null!, 3));
    }

    [Fact]
    public void 満杯や入力完了後のRequestはfalseで受理済みのデータは残る()
    {
        var task = new TestTask(1);
        var launcher = new EiiiLauncher<int>();
        launcher.Attach(task.Input);

        Assert.True(launcher.Request(1));
        Assert.False(launcher.Request(2));   // 満杯: 待たずに false（呼び出し元が再送を判断する）
        Assert.Equal(1, task.PendingCount);  // 受理済みの 1 は捨てられていない

        task.CompleteInput();
        Assert.False(launcher.Request(3));   // 入力完了後
    }

    [Fact]
    public async Task 複数のスレッドから同時にAttachしても成功は1回だけで要求はその接続先に届く()
    {
        // 補助試験: 同時接続の競合を確率的に起こし、不変条件（成功は 1 回、配送は勝者の接続先だけ）を確かめる
        for (int round = 0; round < 50; round++)
        {
            var launcher = new EiiiLauncher<int>();
            var targets = Enumerable.Range(0, 4).Select(_ => Channel.CreateUnbounded<int>()).ToArray();
            using var go = new ManualResetEventSlim();
            var attempts = targets.Select(t => Task.Run(() =>
            {
                go.Wait();
                try { launcher.Attach(t.Writer); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            go.Set();
            var succeeded = await Task.WhenAll(attempts).WaitAsync(Wait.Timeout);

            Assert.Equal(1, succeeded.Count(s => s));
            Assert.True(launcher.Request(1));
            var winner = Array.IndexOf(succeeded, true);
            for (int i = 0; i < targets.Length; i++)
                Assert.Equal(i == winner ? [1] : [], Drain(targets[i].Reader));
        }
    }

    [Fact]
    public async Task Disposeと並行するRequestは例外を出さずtrueの要求だけが一度ずつ配送されDispose後に始めた要求は配送されない()
    {
        // 補助試験。Dispose より前に始まった要求が、Dispose の戻った後に書き込むことは許す（ここでは検査しない）
        var target = Channel.CreateUnbounded<int>();
        var launcher = new EiiiLauncher<int>();
        var attachment = launcher.Attach(target.Writer);
        int disposeReturned = 0;
        var outcomes = new ConcurrentQueue<(int Id, bool StartedAfterDispose, bool Accepted)>();
        using var stop = new CancellationTokenSource();
        var requester = Task.Run(() =>
        {
            int id = 0;
            while (!stop.IsCancellationRequested)
            {
                var startedAfterDispose = Volatile.Read(ref disposeReturned) == 1;
                id++;
                outcomes.Enqueue((id, startedAfterDispose, launcher.Request(id)));
            }
        });
        await Wait.UntilAsync(() => outcomes.Count >= 200);

        attachment.Dispose();
        Volatile.Write(ref disposeReturned, 1);
        await Wait.UntilAsync(() => outcomes.Count(o => o.StartedAfterDispose) >= 200);
        stop.Cancel();
        await requester.WaitAsync(Wait.Timeout);   // 例外が出ていればここで失敗する

        var delivered = Drain(target.Reader);
        Assert.Equal(delivered.Count, delivered.Distinct().Count());                                  // 一度ずつ
        Assert.Equal(outcomes.Where(o => o.Accepted).Select(o => o.Id).Order(), delivered.Order());   // true の要求だけが配送された
        Assert.All(outcomes.Where(o => o.StartedAfterDispose), o => Assert.False(o.Accepted));        // Dispose の後に始めた要求は配送されない
        Assert.False(launcher.IsAttached);
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
