using System.Collections.Concurrent;
using EventImplementedInduceInterface;
using EventImplementedInduceInterface.CommSim;

namespace Eiii.CommSim.Tests;

/// <summary>Receiver → Parser を Channel で接続した結合テスト。</summary>
public class PipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(5, cts.Token);
    }

    private sealed class Pipeline
    {
        public Pipeline(int receiverCapacity, int parserCapacity, Func<Frame, CancellationToken, Task>? work = null)
        {
            Receiver = Registry.Add("receiver", new ReceiverTask(receiverCapacity, new DelimiterFramer()));
            Parser = Registry.Add("parser", new ParserTask(parserCapacity, work, Readings.Enqueue));
            Receiver.LinkTo(Parser.Input);
        }

        public TaskDbContext Registry { get; } = new();
        public ReceiverTask Receiver { get; }
        public ParserTask Parser { get; }
        public ConcurrentQueue<Reading> Readings { get; } = new();
    }

    [Fact]
    public async Task 分割されたチャンクから復元したフレームを解析し正常終了で末尾の未確定データも結果に残る()
    {
        var p = new Pipeline(8, 4);
        p.Registry.StartAll();

        await DeviceSimulator.SendAsync(p.Receiver.Input, ["TEMP=2", "3.5\r", "\nHUM=4", "0\r\nPRES=10", "13\r\nTAIL=7"]);
        await p.Registry.CompleteAllAsync().WaitAsync(Timeout);

        Assert.Equal(TaskStatus.RanToCompletion, p.Receiver.Execution!.Status);
        Assert.Equal(TaskStatus.RanToCompletion, p.Parser.Execution!.Status);
        var readings = p.Readings.ToArray();
        Assert.Equal(["TEMP", "HUM", "PRES"], readings.Take(3).Select(r => r.Key));
        Assert.Equal([23.5, 40, 1013], readings.Take(3).Select(r => r.Value!.Value));
        Assert.Equal("TAIL=7", readings[3].Raw);       // 末尾は捨てずに、エラー付きの結果として届く
        Assert.NotNull(readings[3].Error);
    }

    [Fact]
    public async Task ReceiverはParserの処理を待たずに独立してフレームを確定する()
    {
        var gate = new TaskCompletionSource();
        var p = new Pipeline(8, 8, (_, ct) => gate.Task.WaitAsync(ct));   // Parser は解放されるまで解析を始めない
        p.Registry.StartAll();

        await DeviceSimulator.SendAsync(p.Receiver.Input, ["A=1\r\nB=", "2\r\nC=3\r\n"]);
        await UntilAsync(() => p.Receiver.PendingCount == 0 && p.Parser.PendingCount == 2);   // 1 件は解析中、2 件は待ち

        Assert.Empty(p.Readings);                      // Parser はまだ 1 件も終えていないが、Receiver は全チャンクを処理済み

        gate.SetResult();
        await p.Registry.CompleteAllAsync().WaitAsync(Timeout);
        Assert.Equal(["A", "B", "C"], p.Readings.Select(r => r.Key));
    }

    [Fact]
    public async Task Parserが遅いとChannelの容量制限で機器の送信まで待たされる()
    {
        var gate = new TaskCompletionSource();
        var p = new Pipeline(receiverCapacity: 1, parserCapacity: 1, (_, ct) => gate.Task.WaitAsync(ct));
        p.Registry.StartAll();
        var chunks = Enumerable.Range(1, 6).Select(i => $"N={i}\r\n").ToArray();

        var sending = DeviceSimulator.SendAsync(p.Receiver.Input, chunks);
        // Parser: 1 件解析中 + キュー 1 件、Receiver: 出力待ちで 1 件処理中 + キュー 1 件、残りは機器の WriteAsync で待つ
        await UntilAsync(() => p.Parser.PendingCount == 1 && p.Receiver.PendingCount == 1);
        await Task.Delay(100);

        Assert.False(sending.IsCompleted);             // バックプレッシャーで機器の送信が止まっている
        Assert.Empty(p.Readings);

        gate.SetResult();
        await sending.WaitAsync(Timeout);
        await p.Registry.CompleteAllAsync().WaitAsync(Timeout);

        Assert.Equal(Enumerable.Range(1, 6), p.Readings.Select(r => (int)r.Value!.Value));   // 捨てずに順序どおり
    }

    [Fact]
    public async Task キャンセル停止では解析中のフレームが失われ正常終了では残りがすべて解析される()
    {
        var entered = new TaskCompletionSource();
        var p = new Pipeline(8, 8, async (frame, ct) =>
        {
            if (frame.Text == "C=2")
            {
                entered.TrySetResult();
                await Task.Delay(System.Threading.Timeout.Infinite, ct);   // C=2 の解析中に止める
            }
        });
        p.Registry.StartAll();
        await DeviceSimulator.SendAsync(p.Receiver.Input, ["C=1\r\nC=2\r\nC=3\r\nC=4\r\n"]);
        await entered.Task.WaitAsync(Timeout);

        await p.Registry.StopAllAsync().WaitAsync(Timeout);   // キャンセル停止

        Assert.Equal(TaskStatus.Canceled, p.Parser.Execution!.Status);
        Assert.Equal(["C=1"], p.Readings.Select(r => r.Raw));
        Assert.Equal(2, p.Parser.PendingCount);               // C=3 と C=4 はキューに残る

        p.Registry.StartAll();
        await p.Registry.CompleteAllAsync().WaitAsync(Timeout);   // 正常終了: 残りをすべて処理する

        Assert.Equal(TaskStatus.RanToCompletion, p.Parser.Execution!.Status);
        Assert.Equal(["C=1", "C=3", "C=4"], p.Readings.Select(r => r.Raw));   // C=2 だけが失われた
    }

    [Fact]
    public async Task 未確定データは受信側の停止と再起動をまたいで保持される()
    {
        var p = new Pipeline(8, 8);
        p.Registry.StartAll();
        await DeviceSimulator.SendAsync(p.Receiver.Input, ["A=1\r"]);
        await UntilAsync(() => p.Receiver.PendingCount == 0);

        await p.Receiver.RestartAsync().WaitAsync(Timeout);
        await DeviceSimulator.SendAsync(p.Receiver.Input, ["\n"]);
        await p.Registry.CompleteAllAsync().WaitAsync(Timeout);

        Assert.Equal([new Reading("A=1", "A", 1, null)], p.Readings);
    }
}
