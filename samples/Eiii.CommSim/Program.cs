using System.Collections.Concurrent;
using System.Diagnostics;
using static EventImplementedInduceInterface.CommSim.DeviceSimulator;

namespace EventImplementedInduceInterface.CommSim;

/// <summary>
/// 通信シミュレーター（デリミタ方式）。Device → Receiver（string → Frame）→ Parser（Frame → Reading）。
/// Worker 間の接続（LinkTo）と実行順序はこのクラスで指定し、Registry は登録と一括の寿命管理だけに使う。
/// </summary>
internal static class Program
{
    private static async Task Main()
    {
        await NormalCompletionAsync();
        await BackpressureAsync();
        await CancellationAsync();
    }

    /// <summary>A. 正常終了: 分割されたチャンクからフレームを復元し、末尾の未確定データも結果として確認する。</summary>
    private static async Task NormalCompletionAsync()
    {
        Log.Header("A. 正常終了（チャンク分割・区切り文字の境界またぎ・末尾の未確定データ）");
        var (registry, receiver, parser, readings) = Build(receiverCapacity: 8, parserCapacity: 4, parseTime: TimeSpan.FromMilliseconds(50));
        registry.StartAll();

        await SendAsync(receiver.Input, ["TEMP=2", "3.5\r", "\nHUM=4", "0\r\nPRES=10", "13\r\nTAIL=7"], TraceSend);
        Log.Write("main", "CompleteAllAsync（Receiver → Parser の順に入力を閉じ、残りを処理して終了）");
        await registry.CompleteAllAsync();

        Log.Write("main", $"結果: receiver={receiver.Execution?.Status} parser={parser.Execution?.Status} " +
                          $"解析={readings.Count} 件 [{string.Join(", ", readings.Select(Describe))}]");
    }

    /// <summary>B. バックプレッシャー: Parser が遅いと、Parser のキュー → Receiver → 機器の WriteAsync の順に待ちが伝わる。</summary>
    private static async Task BackpressureAsync()
    {
        Log.Header("B. バックプレッシャー（Parser のキュー容量 1、解析 200ms、Receiver のキュー容量 2）");
        var (registry, receiver, parser, readings) = Build(receiverCapacity: 2, parserCapacity: 1, parseTime: TimeSpan.FromMilliseconds(200));
        registry.StartAll();

        await SendAsync(receiver.Input, Enumerable.Range(1, 6).Select(i => $"N={i}\r\n"),
            (chunk, waited) => Log.Write("device", $"送信 \"{Show(chunk)}\" WriteAsync 待ち={waited}ms " +
                                                   $"(Receiver 待ち={receiver.PendingCount}, Parser 待ち={parser.PendingCount})"));
        await registry.CompleteAllAsync();

        var values = readings.Select(r => (int)r.Value!.Value).ToArray();
        Log.Write("main", $"結果: 解析={values.Length}/6 件、順序保持={values.SequenceEqual(Enumerable.Range(1, 6))}、捨てたデータ=0");
    }

    /// <summary>C. キャンセル停止: 処理中のフレームは失われ得る。正常終了（A）と違い、残りは次の起動まで処理されない。</summary>
    private static async Task CancellationAsync()
    {
        Log.Header("C. キャンセル停止（StopAllAsync）と、その後の正常終了");
        var (registry, receiver, parser, readings) = Build(receiverCapacity: 8, parserCapacity: 8, parseTime: TimeSpan.FromMilliseconds(300));
        registry.StartAll();

        await SendAsync(receiver.Input, ["C=1\r\nC=2\r\nC=3\r\nC=4\r\n"], TraceSend);
        await Task.Delay(450);
        Log.Write("main", "StopAllAsync（キャンセルによる中断）");
        await registry.StopAllAsync();
        Log.Write("main", $"停止後: receiver={receiver.Execution?.Status} parser={parser.Execution?.Status} " +
                          $"解析済み={readings.Count} 件、Parser に残った未処理={parser.PendingCount} 件");

        Log.Write("main", "StartAll → CompleteAllAsync（残りを処理して正常終了）");
        registry.StartAll();
        await registry.CompleteAllAsync();
        Log.Write("main", $"結果: parser={parser.Execution?.Status} 送信フレーム=4 件、解析={readings.Count} 件 " +
                          $"[{string.Join(", ", readings.Select(Describe))}]、停止で失われた={4 - readings.Count} 件");
    }

    private static (TaskDbContext Registry, ReceiverTask Receiver, ParserTask Parser, ConcurrentQueue<Reading> Readings) Build(
        int receiverCapacity, int parserCapacity, TimeSpan parseTime)
    {
        var readings = new ConcurrentQueue<Reading>();
        var registry = new TaskDbContext();   // 登録順 = 上流から下流（CompleteAllAsync の適用条件）

        var receiver = registry.Add("receiver", new ReceiverTask(receiverCapacity, new DelimiterFramer(),
            new TaskToCallback<Frame>(frame => Log.Write("receiver",
                $"フレーム確定 → Parser のキューが受理 \"{Show(frame.Text)}\"{(frame.IsComplete ? "" : "（未完了）")}"))));

        var parser = registry.Add("parser", new ParserTask(parserCapacity,
            async (frame, ct) =>
            {
                Log.Write("parser", $"解析開始 \"{Show(frame.Text)}\"");
                try
                {
                    await Task.Delay(parseTime, ct);   // 遅い解析処理の模擬
                }
                catch (OperationCanceledException)
                {
                    Log.Write("parser", $"解析中に停止された \"{Show(frame.Text)}\"（このフレームは失われる）");
                    throw;
                }
            },
            new TaskToCallback<Reading>(reading =>
            {
                readings.Enqueue(reading);
                Log.Write("parser", $"解析結果 {Describe(reading)}");
            })));

        receiver.LinkTo(parser.Input);   // 接続はサンプル側で指定する。Parser のキューが満杯なら Receiver は WriteAsync で待つ
        return (registry, receiver, parser, readings);
    }

    private static void TraceSend(string chunk, long waited) => Log.Write("device", $"送信 \"{Show(chunk)}\" WriteAsync 待ち={waited}ms");

    private static string Describe(Reading reading)
        => reading.Error is null ? $"{reading.Key}={reading.Value}" : $"エラー「{reading.Error}」\"{Show(reading.Raw)}\"";
}

/// <summary>経過時間とスレッド ID 付きのログ。</summary>
internal static class Log
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lock Gate = new();

    public static void Header(string title)
    {
        lock (Gate) Console.WriteLine($"=== {title} ===");
    }

    public static void Write(string role, string message)
    {
        lock (Gate) Console.WriteLine($"{Clock.ElapsedMilliseconds,6}ms [T{Environment.CurrentManagedThreadId,2}] {role,-8} {message}");
    }
}
