using System.Runtime.CompilerServices;
using Xunit.Abstractions;

namespace Eiii.Tests;

/// <summary>
/// 外部参照を失った Worker が GC で回収されるかの測定（第二次改良 ③ の設計審査用）。
/// 結果が未確定の条件は判定せず、観測値を出力する。GC は回収の時刻を保証しないため、一度生存していたことだけでリークとは断定しない。
/// Worker の生成と参照の破棄は NoInlining の別メソッドで行い、テスト自身が強い参照を持たないようにする（Debug ビルドでのローカル変数の寿命延長を避ける）。
/// Execution の Task はテスト側で保持しない（参照経路が変わるため）。
/// </summary>
[Trait("Category", "GcMeasurement")]
public class GcReachabilityTests(ITestOutputHelper output)
{
    private static bool AliveAfterFullGc(WeakReference weak)
    {
        for (int i = 0; i < 3; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        GC.Collect();
        return weak.IsAlive;
    }

    private void Report(string condition, bool alive)
        => output.WriteLine($"[GC] {condition}: {(alive ? "Alive（回収されなかった）" : "Collected（回収された）")}"
                            + $" / Configuration={(IsDebugBuild() ? "Debug" : "Release")}");

    private static bool IsDebugBuild()
    {
#if DEBUG
        return true;
#else
        return false;
#endif
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateStoppedWorker()
    {
        var task = new TestTask(4);
        task.Start();
        task.Input.TryWrite(1);
        Wait.UntilAsync(() => task.Processed.Count == 1).GetAwaiter().GetResult();
        task.StopAsync().GetAwaiter().GetResult();
        return new WeakReference(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateRunningIdleWorker()
    {
        var task = new TestTask(4);
        task.Start();
        task.Input.TryWrite(1);   // 1 件処理させてから、キューが空の読み出し待ちに戻す
        Wait.UntilAsync(() => task.Processed.Count == 1).GetAwaiter().GetResult();
        Thread.Sleep(100);
        return new WeakReference(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateRunningDelayWorker()
    {
        var entered = new ManualResetEventSlim();
        var task = new TestTask(4, async (_, ct) =>
        {
            entered.Set();
            await Task.Delay(TimeSpan.FromMinutes(10), ct);   // 有限の待機はタイマーを作る（Infinite はタイマーを作らない）
        });
        task.Start();
        task.Input.TryWrite(1);
        Assert.True(entered.Wait(Wait.Timeout));
        Thread.Sleep(100);
        return new WeakReference(task);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference Worker, System.Threading.Channels.ChannelWriter<int> Writer) CreateRunningIdleWorkerKeepingWriter()
    {
        var task = new TestTask(4);
        task.Start();
        task.Input.TryWrite(1);
        Wait.UntilAsync(() => task.Processed.Count == 1).GetAwaiter().GetResult();
        Thread.Sleep(100);
        return (new WeakReference(task), task.Input);   // 外部には入力口（ChannelWriter）だけを残す
    }

    [Fact]
    public void 実行中でChannelの読み出しを待っていて外部がChannelWriterだけを持つWorker()
    {
        var (weak, writer) = CreateRunningIdleWorkerKeepingWriter();
        Report("4. 実行中（Channel の読み出し待ち）・外部は ChannelWriter だけを保持", AliveAfterFullGc(weak));
        GC.KeepAlive(writer);
    }

    [Fact]
    public void 停止済みで外部参照のないWorker()
    {
        Report("1. 停止済み・外部参照なし", AliveAfterFullGc(CreateStoppedWorker()));
    }

    [Fact]
    public void 実行中でChannelの読み出しを待っていて外部参照のないWorker()
    {
        Report("2. 実行中（Channel の読み出し待ち）・外部参照なし", AliveAfterFullGc(CreateRunningIdleWorker()));
    }

    [Fact]
    public void 実行中でTaskDelayを待っていて外部参照のないWorker()
    {
        Report("2'. 実行中（ProcessAsync が Task.Delay(10 分) を待機中）・外部参照なし（参考）", AliveAfterFullGc(CreateRunningDelayWorker()));
    }

    [Fact]
    public async Task 実行中で所有者が参照を持っているWorkerは回収されない()
    {
        var task = new TestTask(4);
        task.Start();
        task.Input.TryWrite(1);
        await Wait.UntilAsync(() => task.Processed.Count == 1);
        var weak = new WeakReference(task);

        var alive = AliveAfterFullGc(weak);
        Report("3. 実行中（Channel の読み出し待ち）・所有者が参照を保持", alive);
        Assert.True(alive);   // 強い参照がある間は回収されない（GC の規則）

        await task.StopAsync();
        GC.KeepAlive(task);
    }
}
