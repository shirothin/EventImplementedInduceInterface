using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Eiii.Tests;

/// <summary>
/// LinkTo の接続（第二次改良 ⑤）。接続は Worker の生存期間を通じて一度だけで、正常終了したときだけ指定に従って完了を伝播する。
/// 下流の入力が開いていることは、検査用の値を書き込まずに WaitToWriteAsync で確かめる（出力の件数に混ぜないため）。
/// </summary>
public class LinkToTests
{
    private static List<int> Drain(ChannelReader<int> reader)
    {
        var values = new List<int>();
        while (reader.TryRead(out var value)) values.Add(value);
        return values;
    }

    [Fact]
    public async Task 二度目のLinkToはInvalidOperationExceptionで最初の接続のまま出力が届く()
    {
        var first = Channel.CreateUnbounded<int>();
        var second = Channel.CreateUnbounded<int>();
        var task = new TestTask(4);
        task.LinkTo(first.Writer);

        Assert.Throws<InvalidOperationException>(() => task.LinkTo(second.Writer));

        task.Input.TryWrite(1);
        task.Start();
        Assert.True(await task.CompleteAsync().WaitAsync(Wait.Timeout));
        Assert.Equal([10], Drain(first.Reader));
        Assert.Empty(Drain(second.Reader));
    }

    [Fact]
    public void 同じ接続先でも二度目のLinkToはInvalidOperationException()
    {
        var target = Channel.CreateUnbounded<int>();
        var task = new TestTask(4);
        task.LinkTo(target.Writer);

        Assert.Throws<InvalidOperationException>(() => task.LinkTo(target.Writer));
    }

    [Fact]
    public async Task 複数のスレッドから同時にLinkToしても成功は1回だけで出力はその接続先に届く()
    {
        // 補助試験: 同時接続の競合を確率的に起こし、不変条件（成功は 1 回、出力は勝者の接続先だけ）を確かめる
        for (int round = 0; round < 50; round++)
        {
            var task = new TestTask(4);
            var targets = Enumerable.Range(0, 4).Select(_ => Channel.CreateUnbounded<int>()).ToArray();
            using var go = new ManualResetEventSlim();
            var attempts = targets.Select(t => Task.Run(() =>
            {
                go.Wait();
                try { task.LinkTo(t.Writer); return true; }
                catch (InvalidOperationException) { return false; }
            })).ToArray();
            go.Set();
            var succeeded = await Task.WhenAll(attempts).WaitAsync(Wait.Timeout);

            Assert.Equal(1, succeeded.Count(s => s));
            task.Input.TryWrite(1);
            task.Start();
            Assert.True(await task.CompleteAsync().WaitAsync(Wait.Timeout));
            var winner = Array.IndexOf(succeeded, true);
            for (int i = 0; i < targets.Length; i++)
                Assert.Equal(i == winner ? [10] : [], Drain(targets[i].Reader));
        }
    }

    [Fact]
    public async Task 既定では正常終了すると下流の入力が閉じ読み出し終えるとCompletionが完了する()
    {
        var downstream = Channel.CreateUnbounded<int>();
        var task = new TestTask(4);
        task.LinkTo(downstream.Writer);
        task.Input.TryWrite(1);
        task.Input.TryWrite(2);
        task.Start();

        Assert.True(await task.CompleteAsync().WaitAsync(Wait.Timeout));   // 1. 上流を正常終了させる

        Assert.False(await downstream.Writer.WaitToWriteAsync());          // 入力は閉じた
        Assert.Equal([10, 20], Drain(downstream.Reader));                  // 2. 値をすべて読み出す
        await downstream.Reader.Completion.WaitAsync(Wait.Timeout);        // 3. 読み出し終えてから Completion を確かめる
        Assert.True(downstream.Reader.Completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task propagateCompletionがfalseなら正常終了しても下流の入力は閉じず出力は届く()
    {
        var downstream = Channel.CreateUnbounded<int>();
        var task = new TestTask(4);
        task.LinkTo(downstream.Writer, propagateCompletion: false);
        task.Input.TryWrite(1);
        task.Input.TryWrite(2);
        task.Start();

        Assert.True(await task.CompleteAsync().WaitAsync(Wait.Timeout));

        Assert.Equal([10, 20], Drain(downstream.Reader));
        Assert.True(await downstream.Writer.WaitToWriteAsync());   // 上流の完了後も、下流には書き込める（検査用の値は書かない）
        Assert.False(downstream.Reader.Completion.IsCompleted);
    }

    [Fact]
    public async Task 合流ではpropagateCompletionをfalseにすると両方の上流が完了しても下流は開いたままで下流のCompleteAsyncで終わる()
    {
        var downstream = new TestTask(8);   // 下流を先に起動しておく（CompleteAsync は起動しない）
        var first = new TestTask(4);
        var second = new TestTask(4);
        first.LinkTo(downstream.Input, propagateCompletion: false);
        second.LinkTo(downstream.Input, propagateCompletion: false);
        first.Input.TryWrite(1);
        first.Input.TryWrite(2);
        second.Input.TryWrite(3);
        downstream.Start();
        first.Start();
        second.Start();

        Assert.True(await first.CompleteAsync().WaitAsync(Wait.Timeout));
        Assert.True(await second.CompleteAsync().WaitAsync(Wait.Timeout));
        await Wait.UntilAsync(() => downstream.Processed.Count == 3);

        Assert.True(downstream.IsRunning);
        Assert.True(await downstream.Input.WaitToWriteAsync());   // 下流の入力は開いたまま（検査用の値は書かない）
        Assert.True(await downstream.CompleteAsync().WaitAsync(Wait.Timeout));
        Assert.Equal(3, downstream.Processed.Count);              // 処理したのは上流からの 3 件だけ
        Assert.Equal([10, 20, 30], downstream.Processed.Order());
        Assert.True(downstream.Processed.ToList().IndexOf(10) < downstream.Processed.ToList().IndexOf(20));   // 同じ上流からの順序は保たれる
    }

    [Fact]
    public async Task 停止で終わった上流は下流の入力を閉じず再起動しても同じ接続のまま()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downstream = Channel.CreateUnbounded<int>();
        var task = new TestTask(4, async (r, ct) =>
        {
            if (r != 1) return;
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
        });
        task.LinkTo(downstream.Writer);
        task.Input.TryWrite(1);
        task.Start();
        await entered.Task.WaitAsync(Wait.Timeout);

        await task.StopAsync();

        Assert.Equal(TaskStatus.Canceled, task.Execution!.Status);
        Assert.True(await downstream.Writer.WaitToWriteAsync());   // 停止では伝播しない
        Assert.False(downstream.Reader.Completion.IsCompleted);

        task.Start();                                              // 再起動しても接続は変わらない
        task.Input.TryWrite(2);
        Assert.True(await task.CompleteAsync().WaitAsync(Wait.Timeout));
        Assert.Equal([20], Drain(downstream.Reader));              // 1 は停止で失われた
        await downstream.Reader.Completion.WaitAsync(Wait.Timeout);
    }

    [Fact]
    public async Task 接続する前の出力はcallbackだけに渡り接続した後の出力は下流に届く()
    {
        // 境界は EmitAsync が接続状態を読み取った時点。最初の出力の EmitAsync が終わった（callback が呼ばれた）ことを確かめてから接続する
        var callbacks = new ConcurrentQueue<int>();
        var firstEmitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var downstream = Channel.CreateUnbounded<int>();
        var task = new TestTask(4, callback: v =>
        {
            callbacks.Enqueue(v);
            if (v == 10) firstEmitted.TrySetResult();
        });
        task.Input.TryWrite(1);
        task.Start();
        await firstEmitted.Task.WaitAsync(Wait.Timeout);

        task.LinkTo(downstream.Writer);
        task.Input.TryWrite(2);
        Assert.True(await task.CompleteAsync().WaitAsync(Wait.Timeout));

        Assert.Equal([10, 20], callbacks);
        Assert.Equal([20], Drain(downstream.Reader));   // 接続する前の 10 は下流に渡らない
    }
}
