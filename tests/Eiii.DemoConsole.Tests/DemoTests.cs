using System.Collections.Concurrent;
using Eiii.DemoConsole;

namespace Eiii.DemoConsole.Tests;

public class ResponseTrackerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    [Fact]
    public async Task 番号が一致したときだけ待機を解除する()
    {
        var tracker = new ResponseTracker();
        var waiting = tracker.Begin(1, Now);

        Assert.False(tracker.Complete(2, Completion.Cr));      // 別の番号の確定では解除しない
        Assert.False(waiting.IsCompleted);
        Assert.True(tracker.Complete(1, Completion.Cr));

        Assert.Equal(Completion.Cr, await waiting);
        Assert.Null(tracker.Current);
    }

    [Fact]
    public async Task 次の待機を始めると前の待機は取り消され取り残されない()
    {
        var tracker = new ResponseTracker();
        var first = tracker.Begin(1, Now);

        _ = tracker.Begin(2, Now);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.Equal(2, tracker.Current!.Value.Id);
    }

    [Fact]
    public async Task Cancelは番号が一致する待機だけを取り消す()
    {
        var tracker = new ResponseTracker();
        var waiting = tracker.Begin(1, Now);

        tracker.Cancel(2);
        Assert.False(waiting.IsCompleted);
        tracker.Cancel(1);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.Null(tracker.Current);
    }
}

/// <summary>画面に依存しない DemoSystem の結合テスト（実時間。待ち時間は短い設定にする）。</summary>
public sealed class DemoSystemTests : IAsyncLifetime
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private readonly ConcurrentQueue<DemoEvent> events = new();
    private DemoSystem system = null!;
    private Task collecting = Task.CompletedTask;

    private static DemoOptions Fast(Func<DemoOptions, DemoOptions>? change = null)
    {
        var options = new DemoOptions
        {
            NormalLatency = TimeSpan.FromMilliseconds(20),
            OsdLatency = TimeSpan.FromMilliseconds(50),
            NormalResponseTimeout = TimeSpan.FromMilliseconds(300),
        };
        return change is null ? options : change(options);
    }

    private void Create(DemoOptions options)
    {
        system = new DemoSystem(options);
        collecting = Task.Run(async () =>
        {
            await foreach (var e in system.Events.ReadAllAsync()) events.Enqueue(e);
        });
        system.Start();
    }

    private async Task UntilAsync(Func<bool> condition)
    {
        using var cts = new CancellationTokenSource(Timeout);
        while (!condition()) await Task.Delay(10, cts.Token);
    }

    private List<DemoEvent> Of(int id) => [.. events.Where(e => e.Id == id)];

    private List<string> Callbacks(int id) => [.. Of(id).Where(e => e.Stage == Stage.Callback).Select(e => e.Message)];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (system is null) return;
        await system.CompleteAsync().WaitAsync(Timeout);
        await collecting.WaitAsync(Timeout);
        await system.DisposeAsync();
    }

    [Fact]
    public async Task 通常コマンドは要求から送信受信確定解析Callbackまで同じ番号で追える()
    {
        Create(Fast());

        Assert.True(system.Send(CommandKind.Normal));
        await UntilAsync(() => Callbacks(1).Count == 1);

        var stages = Of(1).Select(e => e.Stage).ToList();
        Assert.True(stages.IndexOf(Stage.Send) < stages.IndexOf(Stage.Frame));
        Assert.True(stages.IndexOf(Stage.Frame) < stages.IndexOf(Stage.Callback));
        Assert.Contains(Stage.Chunk, stages);
        Assert.Contains(Stage.Parse, stages);
        Assert.Contains("TEMP=21.5", Callbacks(1)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task OSD取得は内部にCRを含む約300文字の応答をIdleTimeoutで1つに確定する()
    {
        Create(Fast());

        system.Send(CommandKind.Osd);
        await UntilAsync(() => Callbacks(1).Count == 1);
        await Task.Delay(400);

        var callback = Assert.Single(Callbacks(1));
        Assert.Contains("13 行 / 300 文字", callback, StringComparison.Ordinal);
        Assert.Contains(Of(1), e => e.Stage == Stage.Frame && e.Message.Contains("IdleTimeout", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Commanderは前の応答が確定するまで次のコマンドを送らない()
    {
        Create(Fast());

        for (int i = 0; i < 3; i++) system.Send(CommandKind.Normal);
        await UntilAsync(() => Callbacks(3).Count == 1);

        var order = events.ToList();
        int Index(int id, Stage stage) => order.FindIndex(e => e.Id == id && e.Stage == stage);
        Assert.True(Index(1, Stage.Frame) < Index(2, Stage.Send));
        Assert.True(Index(2, Stage.Frame) < Index(3, Stage.Send));
    }

    [Fact]
    public async Task 無応答ならNoResponseで待機が解除され次のコマンドに進む()
    {
        Create(Fast());
        system.Device.Silent = true;

        system.Send(CommandKind.Normal);
        system.Send(CommandKind.Normal);
        await UntilAsync(() => Callbacks(2).Count == 1);

        Assert.Contains("応答なし", Callbacks(1)[0], StringComparison.Ordinal);
        Assert.Contains("応答なし", Callbacks(2)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task NoResponseの後に遅れて届いた応答は次のコマンドの応答として扱われる制約がある()
    {
        // 応答開始 500ms に対し、応答待ちの上限 300ms。#1 は約 300ms で NoResponse になり、
        // #1 の応答（約 500ms で到着）が #2 の待機中（#2 の上限は約 600ms）に届く
        Create(Fast(o => o with { NormalLatency = TimeSpan.FromMilliseconds(500), NormalResponseTimeout = TimeSpan.FromMilliseconds(300) }));

        system.Send(CommandKind.Normal);
        system.Send(CommandKind.Normal);
        await UntilAsync(() => Callbacks(2).Count >= 1);

        Assert.Contains("応答なし", Callbacks(1)[0], StringComparison.Ordinal);
        Assert.Contains(FakeXPortDevice.NormalResponse(1).TrimEnd('\r'), Callbacks(2)[0], StringComparison.Ordinal);   // #1 の応答が #2 の結果になる
    }

    [Fact]
    public async Task OSD応答が誤分割すると次のコマンドが早く送られ残りが次のコマンドの応答として扱われる制約がある()
    {
        Create(Fast());
        system.Device.LongGap = true;

        system.Send(CommandKind.Osd);
        system.Send(CommandKind.Normal);
        await UntilAsync(() => Callbacks(2).Any(c => c.Contains("TEMP=22.5", StringComparison.Ordinal)));

        Assert.Single(Callbacks(1));                                   // OSD は前半だけで確定した
        Assert.Contains(Callbacks(2), c => c.Contains("解析できない", StringComparison.Ordinal));   // OSD の残りが #2 として解析された
    }

    [Fact]
    public async Task Parserが遅いとReceiverのキューにデータが溜まり到着から処理までの時間が延びる()
    {
        Create(Fast());
        system.Parser.DelayMilliseconds = 300;

        var accepted = Enumerable.Range(0, 10).Count(_ => system.Send(CommandKind.Normal));
        await UntilAsync(() => system.Callbacks == accepted);

        Assert.True(system.Rejected >= 1);                              // Commander のキュー（8）を超えた分は拒否
        Assert.Contains(events, e => e.Stage == Stage.Chunk && !e.Message.EndsWith("到着→処理 0ms", StringComparison.Ordinal));
    }

    [Fact]
    public async Task 再起動すると応答待ちが取り消され再起動後に次のコマンドが処理される()
    {
        Create(Fast(o => o with { OsdLatency = TimeSpan.FromSeconds(2) }));
        system.Send(CommandKind.Osd);
        system.Send(CommandKind.Normal);
        await UntilAsync(() => system.Tracker.Current is { Id: 1 });

        await system.RestartAsync().WaitAsync(Timeout);
        await UntilAsync(() => Callbacks(2).Count == 1);

        Assert.Contains(Of(1), e => e.Stage == Stage.Wait && e.Message.Contains("取り消した", StringComparison.Ordinal));
        Assert.Empty(Callbacks(1));                                     // 停止で #1 の応答は失われた
        Assert.Contains("TEMP=22.5", Callbacks(2)[0], StringComparison.Ordinal);
    }

    [Fact]
    public async Task 正常終了は受理済みのコマンドをすべて処理してからすべてのWorkerを正常終了させる()
    {
        Create(Fast());
        for (int i = 0; i < 3; i++) system.Send(CommandKind.Normal);

        await system.CompleteAsync().WaitAsync(Timeout);

        Assert.Equal(3, system.Callbacks);
        Assert.All(system.Workers, w => Assert.Equal(TaskStatus.RanToCompletion, w.Worker.Execution!.Status));
    }
}
