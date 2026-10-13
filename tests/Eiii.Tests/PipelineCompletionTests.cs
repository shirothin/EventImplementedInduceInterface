using System.Collections.Concurrent;

namespace Eiii.Tests;

/// <summary>
/// LinkTo で直列に接続した Worker の完了と停止（第二次改良 4c-2 で RegistryTests から移した）。
/// 呼び出しの順序は、削除した Registry の一括操作と同じ（起動は下流から、完了は上流から 1 つずつ）にしている。
/// 現在の LinkTo の動作をそのまま確かめる（上流が正常終了したときにだけ下流の入力を閉じる）。
/// </summary>
public class PipelineCompletionTests
{
    [Fact]
    public async Task 上流から順にCompleteAsyncを呼ぶと残りを処理して正常終了し結果が下流のコールバックまで届く()
    {
        // 下流の入力も CompleteAsync で明示的に閉じているので、LinkTo による完了の伝播だけを確かめるテストではない
        var results = new ConcurrentQueue<int>();
        var length = new LengthTask(8);                         // 上流: string → int
        var times = new TestTask(8, callback: results.Enqueue);   // 下流: int → int * 10
        length.LinkTo(times.Input);   // ChannelWriter<int>: 上流の TOut と下流の TIn が一致しないとコンパイルできない
        foreach (var s in new[] { "a", "bb", "ccc" }) Assert.True(length.Input.TryWrite(s));

        times.Start();
        length.Start();
        await length.CompleteAsync().WaitAsync(Wait.Timeout);
        await times.CompleteAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, length.Execution!.Status);
        Assert.Equal(TaskStatus.RanToCompletion, times.Execution!.Status);
        Assert.Equal([10, 20, 30], results);
    }

    [Fact]
    public async Task 上流が例外で終わっても下流のCompleteAsyncは下流の入力を閉じ下流は受け取った分を処理して終わる()
    {
        var results = new ConcurrentQueue<int>();
        var length = new LengthTask(8);
        var times = new TestTask(8, callback: results.Enqueue);
        length.LinkTo(times.Input);
        foreach (var s in new[] { "a", "boom", "ccc" }) length.Input.TryWrite(s);

        times.Start();
        length.Start();
        await length.CompleteAsync().WaitAsync(Wait.Timeout);   // 例外を再スローしない
        await times.CompleteAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.Faulted, length.Execution!.Status);
        Assert.Equal("boom", length.Execution.Exception!.InnerException!.Message);
        Assert.Equal(1, length.PendingCount);                         // "ccc" は上流に残る
        Assert.Equal(TaskStatus.RanToCompletion, times.Execution!.Status);
        Assert.Equal([10], results);                                  // 下流は "a" の分だけ処理して終わる
    }

    [Fact]
    public async Task LinkToだけでは上流がFaultedでも下流の入力は閉じられず下流のCompleteAsyncが閉じて終わらせる()
    {
        var length = new LengthTask(8);
        var times = new TestTask(8);
        length.LinkTo(times.Input);
        length.Input.TryWrite("boom");
        times.Start();
        length.Start();
        await Wait.UntilAsync(() => length.Execution!.IsFaulted);
        await Task.Delay(100);

        Assert.True(times.IsRunning);   // 完了の伝播は正常終了時だけなので、下流はまだ入力を待っている

        await length.CompleteAsync().WaitAsync(Wait.Timeout);
        await times.CompleteAsync().WaitAsync(Wait.Timeout);   // 下流の入力を閉じるので待ち続けない

        Assert.Equal(TaskStatus.RanToCompletion, times.Execution!.Status);
    }

    [Fact]
    public async Task 下流の入力を先に閉じると上流は出力時にChannelClosedExceptionでFaultedになる()
    {
        var release = new TaskCompletionSource();
        var results = new ConcurrentQueue<int>();
        var downstream = new TestTask(8, callback: results.Enqueue);
        var upstream = new TestTask(8, async (_, _) => await release.Task);
        upstream.LinkTo(downstream.Input);
        upstream.Input.TryWrite(1);
        upstream.Start();
        downstream.Start();
        await Wait.UntilAsync(() => upstream.PendingCount == 0);   // 上流は 1 を処理中（release まで出力しない）

        var completing = CompleteDownstreamFirstAsync();             // 下流の入力が先に閉じられる
        await Wait.UntilAsync(() => downstream.Execution!.IsCompleted);
        release.SetResult();                                         // ここで上流が出力する
        await completing.WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, downstream.Execution!.Status);
        Assert.Equal(TaskStatus.Faulted, upstream.Execution!.Status);
        Assert.IsType<System.Threading.Channels.ChannelClosedException>(upstream.Execution.Exception!.InnerException);
        Assert.Empty(results);                                       // 上流の出力は下流に届かない

        async Task CompleteDownstreamFirstAsync()
        {
            await downstream.CompleteAsync();
            await upstream.CompleteAsync();
        }
    }
}
