using System.Collections.Concurrent;
using EventImplementedInduceInterface;
using EventImplementedInduceInterface.CommSim;

namespace Eiii.CommSim.Tests;

/// <summary>TimedReceiver → OsdParser を実時間で動かす結合テスト（Tick は PeriodicTimer）。</summary>
public class TimedPipelineTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly TimeProvider Time = TimeProvider.System;

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(10, cts.Token);
    }

    private sealed class Pipeline : IAsyncDisposable
    {
        private readonly CancellationTokenSource ticking = new();
        private readonly Task ticks;

        public Pipeline(TimeoutOptions options, int parserCapacity = 4, Func<TimedFrame, CancellationToken, Task>? work = null)
        {
            Receiver = Registry.Add("osd-receiver", new TimedReceiverTask(256, new TimeoutFramer(options)));
            Parser = Registry.Add("osd-parser", new OsdParserTask(parserCapacity, work, Results.Enqueue));
            Receiver.LinkTo(Parser.Input);
            Registry.StartAll();
            ticks = TickSource.RunAsync(Receiver.Input, TimeSpan.FromMilliseconds(25), Time, ticking.Token);
        }

        public TaskDbContext Registry { get; } = new();
        public TimedReceiverTask Receiver { get; }
        public OsdParserTask Parser { get; }
        public ConcurrentQueue<OsdResponse> Results { get; } = new();

        public async ValueTask DisposeAsync()
        {
            await ticking.CancelAsync();
            await ticks;
            await Registry.CompleteAllAsync();
            ticking.Dispose();
        }
    }

    private static IReadOnlyList<(string Text, TimeSpan GapBefore)> Fragments(string response, int seed)
        => OsdSimulator.Fragment(response, seed, 8, 40, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task 実時間でも100ms以下の間欠を含む約300文字の応答をCRを保持したまま1つに確定する()
    {
        var response = OsdSimulator.CreateResponse(300);
        await using var p = new Pipeline(new TimeoutOptions { IdleTimeout = TimeSpan.FromMilliseconds(250) });

        await OsdSimulator.SendAsync(p.Receiver.Input, Fragments(response, seed: 1), Time);
        await UntilAsync(() => p.Results.Count == 1);
        await Task.Delay(400);                                        // 余分なフレームが出ないことも確認する

        var result = Assert.Single(p.Results);
        Assert.Equal(FrameEnd.IdleTimeout, result.End);
        Assert.Equal(response, string.Join('\r', result.Lines));
    }

    [Fact]
    public async Task キューで待たされてから処理されても受信時刻で判定するため2つの応答を正しく分ける()
    {
        var release = new TaskCompletionSource();
        await using var p = new Pipeline(new TimeoutOptions { IdleTimeout = TimeSpan.FromMilliseconds(250) },
                                         parserCapacity: 1, work: (_, ct) => release.Task.WaitAsync(ct));
        // Parser を「解析中 1 件 + 待ち 1 件」で満杯にしておく。最初の応答 A を出力した時点で Receiver が待たされる
        await p.Parser.Input.WriteAsync(new TimedFrame("dummy1", FrameEnd.Flush, null, null));
        await p.Parser.Input.WriteAsync(new TimedFrame("dummy2", FrameEnd.Flush, null, null));
        await UntilAsync(() => p.Parser.PendingCount == 1);

        var responseA = OsdSimulator.CreateResponse(300, screenId: 1);
        var responseB = OsdSimulator.CreateResponse(300, screenId: 2);
        await OsdSimulator.SendAsync(p.Receiver.Input, Fragments(responseA, seed: 5), Time);
        await Task.Delay(400);                                        // A と B の間の無通信（閾値 250ms 超え）
        var maxPending = 0;
        var sendingB = OsdSimulator.SendAsync(p.Receiver.Input, Fragments(responseB, seed: 6), Time);
        while (!sendingB.IsCompleted)
        {
            maxPending = Math.Max(maxPending, p.Receiver.PendingCount);
            await Task.Delay(10);
        }
        await Task.Delay(400);
        maxPending = Math.Max(maxPending, p.Receiver.PendingCount);

        Assert.True(maxPending > 5, $"Receiver のキューに溜まった数={maxPending}");   // B のチャンクと Tick がキューで待たされた
        Assert.Empty(p.Results);

        release.SetResult();                                          // ここから溜まった分がまとめて処理される
        await UntilAsync(() => p.Results.Count == 4);

        var responses = p.Results.Skip(2).ToArray();
        Assert.Equal(responseA, string.Join('\r', responses[0].Lines));
        Assert.Equal(responseB, string.Join('\r', responses[1].Lines));
        Assert.All(responses, r => Assert.Equal(FrameEnd.IdleTimeout, r.End));
    }

    [Fact]
    public async Task 応答がなければ応答待ちの上限でNoResponseが届く()
    {
        await using var p = new Pipeline(new TimeoutOptions
        {
            IdleTimeout = TimeSpan.FromMilliseconds(250),
            ResponseTimeout = TimeSpan.FromMilliseconds(500),         // テストを短くするための値（実機の参考値は約 5 秒）
        });

        await p.Receiver.Input.WriteAsync(new ResponseExpected(Time.GetUtcNow()));
        await UntilAsync(() => p.Results.Count == 1);

        Assert.Equal(FrameEnd.NoResponse, Assert.Single(p.Results).End);
    }
}
