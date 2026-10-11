using System.Collections.Concurrent;
using EventImplementedInduceInterface;

namespace Eiii.Tests;

/// <summary>string を受けて長さ（int）を出力する。"boom" で例外を投げる。</summary>
internal sealed class LengthTask(int capacity) : EiiiTask<string, int>(capacity)
{
    protected override ValueTask OnRequestAsync(string request, CancellationToken cancellationToken)
        => request == "boom" ? throw new InvalidOperationException("boom") : EmitAsync(request.Length, cancellationToken);
}

/// <summary>Step 4: 入出力型の異なるワーカーの一元管理。</summary>
public class RegistryTests
{
    [Fact]
    public void 入出力型の異なるワーカーを登録し名前で具体的な型のまま取り出せる()
    {
        var registry = new TaskDbContext();
        var length = new LengthTask(4);   // EiiiTask<string, int>
        var times = new TestTask(4);      // EiiiTask<int, int>

        Assert.Same(length, registry.Add("length", length));   // 戻り値は LengthTask 型
        Assert.Same(times, registry.Add("times", times));

        Assert.Same(length, registry.Get<LengthTask>("length"));
        Assert.Same(times, registry.Get<EiiiTask<int, int>>("times"));
        Assert.Same(times, registry.Get<EiiiTask>("times"));   // 基底型でも取り出せる
    }

    [Fact]
    public void 同じ名前の登録はArgumentExceptionで既存の登録は変わらない()
    {
        var registry = new TaskDbContext();
        var first = registry.Add("a", new TestTask(4));

        Assert.Throws<ArgumentException>(() => registry.Add("a", new TestTask(4)));

        Assert.Same(first, registry.Get<TestTask>("a"));
    }

    [Fact]
    public void 未登録の名前はKeyNotFoundExceptionで型違いはInvalidCastException()
    {
        var registry = new TaskDbContext();
        registry.Add("length", new LengthTask(4));

        Assert.Throws<KeyNotFoundException>(() => registry.Get<EiiiTask>("missing"));
        Assert.Throws<InvalidCastException>(() => registry.Get<TestTask>("length"));
        Assert.Throws<ArgumentNullException>(() => registry.Add<TestTask>(null!, new TestTask(4)));
    }

    [Fact]
    public async Task StartAllで全ワーカーが起動しStopAllAsyncで全ワーカーが停止する()
    {
        var registry = new TaskDbContext();
        var length = registry.Add("length", new LengthTask(4));
        var times = registry.Add("times", new TestTask(4));

        registry.StartAll();
        Assert.True(length.IsRunning);
        Assert.True(times.IsRunning);

        await registry.StopAllAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.Canceled, length.Execution!.Status);
        Assert.Equal(TaskStatus.Canceled, times.Execution!.Status);
    }

    [Fact]
    public async Task CompleteAllAsyncで上流から順に残りを処理して正常終了し結果が下流のコールバックまで届く()
    {
        var results = new ConcurrentQueue<int>();
        var registry = new TaskDbContext();
        var length = registry.Add("length", new LengthTask(8));                         // 上流: string → int
        var times = registry.Add("times", new TestTask(8, callback: results.Enqueue));   // 下流: int → int * 10
        length.LinkTo(times.Input);   // ChannelWriter<int>: 上流の TOut と下流の TIn が一致しないとコンパイルできない
        foreach (var s in new[] { "a", "bb", "ccc" }) Assert.True(length.Input.TryWrite(s));

        registry.StartAll();
        await registry.CompleteAllAsync().WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, length.Execution!.Status);
        Assert.Equal(TaskStatus.RanToCompletion, times.Execution!.Status);
        Assert.Equal([10, 20, 30], results);
    }

    [Fact]
    public async Task CompleteAllAsyncは上流が例外で終わっても次へ進み下流は入力完了を待ち続けず受け取った分を処理して終わる()
    {
        var results = new ConcurrentQueue<int>();
        var registry = new TaskDbContext();
        var length = registry.Add("length", new LengthTask(8));
        var times = registry.Add("times", new TestTask(8, callback: results.Enqueue));
        length.LinkTo(times.Input);
        foreach (var s in new[] { "a", "boom", "ccc" }) length.Input.TryWrite(s);

        registry.StartAll();
        await registry.CompleteAllAsync().WaitAsync(Wait.Timeout);   // 例外を再スローしない

        Assert.Equal(TaskStatus.Faulted, length.Execution!.Status);
        Assert.Equal("boom", length.Execution.Exception!.InnerException!.Message);
        Assert.Equal(1, length.PendingCount);                         // "ccc" は上流に残る
        Assert.Equal(TaskStatus.RanToCompletion, times.Execution!.Status);
        Assert.Equal([10], results);                                  // 下流は "a" の分だけ処理して終わる
    }

    [Fact]
    public async Task LinkToだけでは上流がFaultedでも下流の入力は閉じられずCompleteAllAsyncが閉じて終わらせる()
    {
        var registry = new TaskDbContext();
        var length = registry.Add("length", new LengthTask(8));
        var times = registry.Add("times", new TestTask(8));
        length.LinkTo(times.Input);
        length.Input.TryWrite("boom");
        registry.StartAll();
        await Wait.UntilAsync(() => length.Execution!.IsFaulted);
        await Task.Delay(100);

        Assert.True(times.IsRunning);   // 完了の伝播は正常終了時だけなので、下流はまだ入力を待っている

        await registry.CompleteAllAsync().WaitAsync(Wait.Timeout);   // 下流の入力を閉じるので待ち続けない

        Assert.Equal(TaskStatus.RanToCompletion, times.Execution!.Status);
    }

    [Fact]
    public async Task 下流を先に登録するとCompleteAllAsyncは上流をChannelClosedExceptionでFaultedにする()
    {
        var release = new TaskCompletionSource();
        var results = new ConcurrentQueue<int>();
        var registry = new TaskDbContext();
        var downstream = registry.Add("downstream", new TestTask(8, callback: results.Enqueue));   // 逆順に登録（制約違反）
        var upstream = registry.Add("upstream", new TestTask(8, async (_, _) => await release.Task));
        upstream.LinkTo(downstream.Input);
        upstream.Input.TryWrite(1);
        registry.StartAll();
        await Wait.UntilAsync(() => upstream.PendingCount == 0);   // 上流は 1 を処理中（release まで出力しない）

        var completing = registry.CompleteAllAsync();               // 登録順なので下流の入力が先に閉じられる
        await Wait.UntilAsync(() => downstream.Execution!.IsCompleted);
        release.SetResult();                                         // ここで上流が出力する
        await completing.WaitAsync(Wait.Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, downstream.Execution!.Status);
        Assert.Equal(TaskStatus.Faulted, upstream.Execution!.Status);
        Assert.IsType<System.Threading.Channels.ChannelClosedException>(upstream.Execution.Exception!.InnerException);
        Assert.Empty(results);                                       // 上流の出力は下流に届かない
    }

    [Fact]
    public async Task 起動していないワーカーはCompleteAllAsyncで起動されず未処理が残る()
    {
        var registry = new TaskDbContext();
        var idle = registry.Add("idle", new TestTask(4));
        idle.Input.TryWrite(1);

        await registry.CompleteAllAsync().WaitAsync(Wait.Timeout);

        Assert.Null(idle.Execution);
        Assert.Equal(1, idle.PendingCount);
    }

    [Fact]
    public void 複数スレッドから異なる名前を同時に登録してもすべて登録される()
    {
        var registry = new TaskDbContext();

        Parallel.For(0, 200, i => registry.Add($"w{i}", new TestTask(1)));

        for (int i = 0; i < 200; i++) Assert.NotNull(registry.Get<TestTask>($"w{i}"));
    }
}
