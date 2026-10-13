using System.Collections.Concurrent;
using System.Diagnostics;
using static Eiii.CommSim.DeviceSimulator;

namespace Eiii.CommSim;

/// <summary>
/// 通信シミュレーター。
/// 通常のコマンド応答（CR 終端）はデリミタ方式（A～C）: Device → Receiver（string → Frame）→ Parser（Frame → Reading）。
/// OSD 取得などの終端マーカーのない応答は時間方式（D～G）: Device → TimedReceiver（RxInput → TimedFrame）→ OsdParser。
/// コマンドの種類に応じた方式の選択、Worker 間の接続（LinkTo）、実行順序はアプリケーション（このクラス）の責務で、
/// 起動・停止・完了の順序も、このクラスが Worker ごとに明示する（受信 → 解析の直列なので、起動は下流から、停止と完了は上流から 1 つずつ）。
/// ライブラリ本体は通信の知識を持たない。
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        // custom: 任意の電文を投入して Delimiter / Idle / Fixed を比較する（引数の詳細は custom --help）
        if (args.Length > 0 && args[0] == "custom") return await CustomRunner.RunAsync(args[1..], Console.Out, Console.Error);

        var only = args.Length > 0 ? args[0] : "all";
        if (only is "all" or "delimiter")
        {
            await NormalCompletionAsync();
            await BackpressureAsync();
            await CancellationAsync();
        }
        if (only is "all" or "timeout")
        {
            await OsdIdleAsync();
            await OsdLongGapAsync();
            await OsdFixedAsync();
            await OsdNoResponseAsync();
        }
        return 0;
    }

    // ---- 時間方式（OSD 取得など終端マーカーのない応答）。値は記憶に基づく概算で、確定仕様ではない ----

    private static readonly TimeSpan RelayLatency = TimeSpan.FromMilliseconds(1500);   // 中継による応答開始までの時間（参考: 最大約 5 秒）
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(6);        // 応答待ちの上限（フレーム確定の閾値ではない）

    /// <summary>D. Idle 方式: 100ms 以下の間欠を含む約 300 文字の応答を、閾値 250ms で 1 つに確定する。</summary>
    private static async Task OsdIdleAsync()
    {
        Log.Header("D. OSD 応答 × Idle 方式（無通信 250ms で確定。断片間の間欠は 10～100ms）");
        var response = OsdSimulator.CreateResponse(300, screenId: 1);
        var fragments = OsdSimulator.Fragment(response, seed: 1, minChunk: 8, maxChunk: 40, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100));
        var results = await RunOsdAsync(new TimeoutOptions { IdleTimeout = TimeSpan.FromMilliseconds(250), ResponseTimeout = ResponseTimeout },
                                        fragments, expectedResults: 1);
        Log.Write("main", $"結果: 応答 {results.Count} 件、内容が送信と一致（CR を保持）={results.Count == 1 && string.Join('\r', results[0].Lines) == response}");
    }

    /// <summary>E. Idle 方式の誤分割: 応答の途中に閾値を超える 400ms の間欠があると、1 つの応答が 2 つに分かれる。</summary>
    private static async Task OsdLongGapAsync()
    {
        Log.Header("E. OSD 応答 × Idle 方式（途中に 400ms の間欠 → 閾値 250ms を超えて誤分割）");
        var fragments = OsdSimulator.Fragment(OsdSimulator.CreateResponse(300, screenId: 2), seed: 2, 8, 40,
                                              TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)).ToList();
        fragments[fragments.Count / 2] = (fragments[fragments.Count / 2].Text, TimeSpan.FromMilliseconds(400));
        var results = await RunOsdAsync(new TimeoutOptions { IdleTimeout = TimeSpan.FromMilliseconds(250), ResponseTimeout = ResponseTimeout },
                                        fragments, expectedResults: 2);
        Log.Write("main", $"結果: 応答 {results.Count} 件（本来は 1 件）。時間条件による確定は完全性の証明にならない");
    }

    /// <summary>F. Fixed 方式: 最初の受信から 2 秒で収集を打ち切る。途中の 400ms の間欠では分割しない。</summary>
    private static async Task OsdFixedAsync()
    {
        Log.Header("F. OSD 応答 × Fixed 方式（最初の受信から 2 秒で収集を終了。途中に 400ms の間欠）");
        var fragments = OsdSimulator.Fragment(OsdSimulator.CreateResponse(300, screenId: 3), seed: 2, 8, 40,
                                              TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)).ToList();
        fragments[fragments.Count / 2] = (fragments[fragments.Count / 2].Text, TimeSpan.FromMilliseconds(400));
        var results = await RunOsdAsync(new TimeoutOptions { CollectionTimeout = TimeSpan.FromSeconds(2), ResponseTimeout = ResponseTimeout },
                                        fragments, expectedResults: 1);
        Log.Write("main", $"結果: 応答 {results.Count} 件。Idle 方式より確定が遅い（最後の受信から待つのではなく、最初の受信から 2 秒）");
    }

    /// <summary>G. 応答なし: 応答待ちの上限（6 秒）を過ぎても最初の受信がなければ NoResponse を通知する。</summary>
    private static async Task OsdNoResponseAsync()
    {
        Log.Header("G. 応答なし（応答待ち 6 秒。フレーム確定の閾値とは別の概念）");
        var results = await RunOsdAsync(new TimeoutOptions { IdleTimeout = TimeSpan.FromMilliseconds(250), ResponseTimeout = ResponseTimeout },
                                        fragments: null, expectedResults: 1);
        Log.Write("main", $"結果: {(results.Count == 1 ? results[0].End.ToString() : "なし")}");
    }

    private static async Task<List<OsdResponse>> RunOsdAsync(TimeoutOptions options, IReadOnlyList<(string Text, TimeSpan GapBefore)>? fragments, int expectedResults)
    {
        var time = TimeProvider.System;
        var results = new ConcurrentQueue<OsdResponse>();
        var receiver = new TimedReceiverTask(256, new TimeoutFramer(options),
            new TaskToCallback<TimedFrame>(frame => Log.Write("receiver",
                $"フレーム確定 [{frame.End}] 長さ={frame.Text.Length}" +
                (frame.FirstReceivedAt is { } first ? $" 最初→最後の受信={(frame.LastReceivedAt!.Value - first).TotalMilliseconds:0}ms" : ""))));
        var parser = new OsdParserTask(4, callback: new TaskToCallback<OsdResponse>(r =>
        {
            results.Enqueue(r);
            Log.Write("parser", $"OSD 応答 [{r.End}] 行数={r.Lines.Count} 長さ={r.Length}（終端マーカーがないため完全性は未保証）");
        }));
        receiver.LinkTo(parser.Input);
        parser.Start();   // 起動は下流から
        receiver.Start();

        using var ticking = new CancellationTokenSource();
        var ticks = TickSource.RunAsync(receiver.Input, TimeSpan.FromMilliseconds(25), time, ticking.Token);

        await receiver.Input.WriteAsync(new ResponseExpected(time.GetUtcNow()));
        Log.Write("main", "OSD 取得要求を送信（応答待ち開始）");
        if (fragments is not null)
        {
            await Task.Delay(RelayLatency, time);
            var gaps = fragments.Skip(1).Select(f => f.GapBefore.TotalMilliseconds).ToArray();
            Log.Write("device", $"応答開始: {fragments.Sum(f => f.Text.Length)} 文字を {fragments.Count} チャンクで送信 " +
                                $"（間欠 {gaps.Min():0}～{gaps.Max():0}ms、内部の CR={fragments.Sum(f => f.Text.Count(c => c == '\r'))} 個）");
            await OsdSimulator.SendAsync(receiver.Input, fragments, time);
        }

        var deadline = time.GetUtcNow() + ResponseTimeout + TimeSpan.FromSeconds(3);
        while (results.Count < expectedResults && time.GetUtcNow() < deadline) await Task.Delay(TimeSpan.FromMilliseconds(20), time);

        await ticking.CancelAsync();
        await ticks;
        await receiver.CompleteAsync();   // 完了は上流から（下流の入力も CompleteAsync が閉じる）
        await parser.CompleteAsync();
        return [.. results];
    }

    /// <summary>A. 正常終了: 分割されたチャンクからフレームを復元し、末尾の未確定データも結果として確認する。</summary>
    private static async Task NormalCompletionAsync()
    {
        Log.Header("A. 正常終了（チャンク分割・区切り文字の境界またぎ・末尾の未確定データ）");
        var (receiver, parser, readings) = Build(receiverCapacity: 8, parserCapacity: 4, parseTime: TimeSpan.FromMilliseconds(50));
        parser.Start();   // 起動は下流から
        receiver.Start();

        await SendAsync(receiver.Input, ["TEMP=2", "3.5\r", "\nHUM=4", "0\r\nPRES=10", "13\r\nTAIL=7"], TraceSend);
        Log.Write("main", "CompleteAsync を Receiver → Parser の順に呼ぶ（入力を閉じ、残りを処理して終了）");
        await receiver.CompleteAsync();   // 完了は上流から（下流の入力も CompleteAsync が閉じる）
        await parser.CompleteAsync();

        Log.Write("main", $"結果: receiver={receiver.Execution?.Status} parser={parser.Execution?.Status} " +
                          $"解析={readings.Count} 件 [{string.Join(", ", readings.Select(Describe))}]");
    }

    /// <summary>B. バックプレッシャー: Parser が遅いと、Parser のキュー → Receiver → 機器の WriteAsync の順に待ちが伝わる。</summary>
    private static async Task BackpressureAsync()
    {
        Log.Header("B. バックプレッシャー（Parser のキュー容量 1、解析 200ms、Receiver のキュー容量 2）");
        var (receiver, parser, readings) = Build(receiverCapacity: 2, parserCapacity: 1, parseTime: TimeSpan.FromMilliseconds(200));
        parser.Start();   // 起動は下流から
        receiver.Start();

        await SendAsync(receiver.Input, Enumerable.Range(1, 6).Select(i => $"N={i}\r\n"),
            (chunk, waited) => Log.Write("device", $"送信 \"{Show(chunk)}\" WriteAsync 待ち={waited}ms " +
                                                   $"(Receiver 待ち={receiver.PendingCount}, Parser 待ち={parser.PendingCount})"));
        await receiver.CompleteAsync();   // 完了は上流から（下流の入力も CompleteAsync が閉じる）
        await parser.CompleteAsync();

        var values = readings.Select(r => (int)r.Value!.Value).ToArray();
        Log.Write("main", $"結果: 解析={values.Length}/6 件、順序保持={values.SequenceEqual(Enumerable.Range(1, 6))}、捨てたデータ=0");
    }

    /// <summary>C. キャンセル停止: 処理中のフレームは失われ得る。正常終了（A）と違い、残りは次の起動まで処理されない。</summary>
    private static async Task CancellationAsync()
    {
        Log.Header("C. キャンセル停止（StopAsync）と、その後の正常終了");
        var (receiver, parser, readings) = Build(receiverCapacity: 8, parserCapacity: 8, parseTime: TimeSpan.FromMilliseconds(300));
        parser.Start();   // 起動は下流から
        receiver.Start();

        await SendAsync(receiver.Input, ["C=1\r\nC=2\r\nC=3\r\nC=4\r\n"], TraceSend);
        await Task.Delay(450);
        Log.Write("main", "StopAsync を Receiver → Parser の順に呼ぶ（キャンセルによる中断。入力は閉じない）");
        await receiver.StopAsync();   // 停止は上流から（入力は閉じないので、後で再開できる）
        await parser.StopAsync();
        Log.Write("main", $"停止後: receiver={receiver.Execution?.Status} parser={parser.Execution?.Status} " +
                          $"解析済み={readings.Count} 件、Parser に残った未処理={parser.PendingCount} 件");

        Log.Write("main", "Start（Parser → Receiver）→ CompleteAsync（Receiver → Parser）で残りを処理して正常終了");
        parser.Start();   // 起動は下流から
        receiver.Start();
        await receiver.CompleteAsync();   // 完了は上流から（下流の入力も CompleteAsync が閉じる）
        await parser.CompleteAsync();
        Log.Write("main", $"結果: parser={parser.Execution?.Status} 送信フレーム=4 件、解析={readings.Count} 件 " +
                          $"[{string.Join(", ", readings.Select(Describe))}]、停止で失われた={4 - readings.Count} 件");
    }

    private static (ReceiverTask Receiver, ParserTask Parser, ConcurrentQueue<Reading> Readings) Build(
        int receiverCapacity, int parserCapacity, TimeSpan parseTime)
    {
        var readings = new ConcurrentQueue<Reading>();

        var receiver = new ReceiverTask(receiverCapacity, new DelimiterFramer(),
            new TaskToCallback<Frame>(frame => Log.Write("receiver",
                $"フレーム確定 → Parser のキューが受理 \"{Show(frame.Text)}\"{(frame.IsComplete ? "" : "（未完了）")}")));

        var parser = new ParserTask(parserCapacity,
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
            }));

        receiver.LinkTo(parser.Input);   // 接続はサンプル側で指定する。Parser のキューが満杯なら Receiver は WriteAsync で待つ
        return (receiver, parser, readings);
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
