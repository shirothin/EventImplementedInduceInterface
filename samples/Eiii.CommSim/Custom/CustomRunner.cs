using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace Eiii.CommSim;

/// <summary>1 つの方式で確定したフレーム。</summary>
/// <param name="Text">フレームの内容（デリミタ方式は区切り文字を除く）。</param>
/// <param name="End">確定した理由。</param>
/// <param name="EndsWithDelimiter">デリミタ方式で区切り文字により確定したら true（入力との一致確認に使う）。</param>
/// <param name="SinceFirstChunk">最初のチャンクを送ってから確定するまでの時間。</param>
public sealed record MethodFrame(string Text, string End, bool EndsWithDelimiter, TimeSpan SinceFirstChunk);

/// <summary>1 つの方式の結果。</summary>
/// <param name="Name">方式の名前。</param>
/// <param name="Condition">条件の説明。</param>
/// <param name="Frames">確定したフレーム。</param>
/// <param name="Lossless">フレームを連結すると入力に一致するか（データが失われていないか）。</param>
public sealed record MethodResult(string Name, string Condition, IReadOnlyList<MethodFrame> Frames, bool Lossless);

/// <summary>
/// 任意の電文を、同じ分割・同じ間欠で各方式に流し、確定したフレームを比較表示する（<c>custom</c> モード）。
/// 既存の部品（ReceiverTask / DelimiterFramer、TimedReceiverTask / TimeoutFramer / TickSource、OsdSimulator.SendAsync）を組み合わせるだけで、
/// 新しい通信機能は持たない。
/// </summary>
public static class CustomRunner
{
    /// <summary>引数を解析して実行する。</summary>
    /// <param name="args"><c>custom</c> より後の引数。</param>
    /// <param name="output">結果の出力先。</param>
    /// <param name="error">誤りの出力先。</param>
    /// <returns>終了コード（0: 正常、2: 引数・入力の誤り）。</returns>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        CustomOptions options;
        string text;
        IReadOnlyList<(string Text, TimeSpan GapBefore)> fragments;
        try
        {
            options = CustomArguments.Parse(args);
            if (options.ShowHelp)
            {
                await output.WriteLineAsync(CustomArguments.Usage);
                return 0;
            }
            text = CustomInput.Load(options);
            fragments = CustomFragments.Create(text, options);
        }
        catch (CustomUsageException ex)
        {
            await error.WriteLineAsync($"エラー: {ex.Message}");
            await error.WriteLineAsync();
            await error.WriteLineAsync(CustomArguments.Usage);
            return 2;
        }

        await WriteInputAsync(text, fragments, options, output);
        var results = new List<MethodResult>();
        if (options.Mode is CompareMode.All or CompareMode.Delimiter) results.Add(await RunDelimiterAsync(text, fragments, options));
        if (options.Mode is CompareMode.All or CompareMode.Idle) results.Add(await RunTimedAsync(text, fragments, options, idle: true));
        if (options.Mode is CompareMode.All or CompareMode.Fixed) results.Add(await RunTimedAsync(text, fragments, options, idle: false));

        foreach (var result in results) await WriteResultAsync(result, output);
        if (results.Count > 1) await WriteComparisonAsync(results, output);
        await output.WriteLineAsync("注意: 時間方式による確定は、電文を最後まで受信したことの証明ではない（終端マーカーがないため）。");
        return 0;
    }

    /// <summary>デリミタ方式で実行する。</summary>
    public static async Task<MethodResult> RunDelimiterAsync(string text, IReadOnlyList<(string Text, TimeSpan GapBefore)> fragments, CustomOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(options);
        var frames = new ConcurrentQueue<MethodFrame>();
        var clock = new Stopwatch();
        var receiver = new ReceiverTask(fragments.Count + 16, new DelimiterFramer(options.Delimiter),
            new TaskToCallback<Frame>(f => frames.Enqueue(new MethodFrame(f.Text, f.IsComplete ? "Delimiter" : "残り(入力終了)", f.IsComplete, clock.Elapsed))));
        receiver.Start();

        clock.Start();
        await SendStringsAsync(receiver.Input, fragments);
        receiver.CompleteInput();                                   // 区切り文字のない末尾は「残り」として出る
        await receiver.Execution!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        var rebuilt = string.Concat(frames.Select(f => f.EndsWithDelimiter ? f.Text + options.Delimiter : f.Text));
        return new MethodResult("Delimiter", $"区切り {Escapes.Show(options.Delimiter)}", [.. frames], rebuilt == text);
    }

    /// <summary>時間方式（Idle または Fixed）で実行する。</summary>
    public static async Task<MethodResult> RunTimedAsync(string text, IReadOnlyList<(string Text, TimeSpan GapBefore)> fragments, CustomOptions options, bool idle)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(fragments);
        ArgumentNullException.ThrowIfNull(options);
        var time = TimeProvider.System;
        var threshold = TimeSpan.FromMilliseconds(idle ? options.IdleMilliseconds : options.CollectionMilliseconds);
        var tick = TimeSpan.FromMilliseconds(options.TickMilliseconds);
        var timeout = new TimeoutOptions
        {
            IdleTimeout = idle ? threshold : null,
            CollectionTimeout = idle ? null : threshold,
            MaxFrameLength = options.MaxFrameLength,
        };
        var frames = new ConcurrentQueue<MethodFrame>();
        var clock = new Stopwatch();
        var receiver = new TimedReceiverTask(fragments.Count + 1024, new TimeoutFramer(timeout),
            new TaskToCallback<TimedFrame>(f => frames.Enqueue(new MethodFrame(f.Text, f.End.ToString(), false, clock.Elapsed))));
        receiver.Start();
        using var ticking = new CancellationTokenSource();
        var ticks = TickSource.RunAsync(receiver.Input, tick, time, ticking.Token);

        clock.Start();
        await OsdSimulator.SendAsync(receiver.Input, fragments, time);
        await Task.Delay(threshold + tick + tick + TimeSpan.FromMilliseconds(100), time);   // 最後のフレームが時間条件で確定するのを待つ

        await ticking.CancelAsync();
        await ticks;
        receiver.CompleteInput();                                   // まだ確定していないデータがあれば Flush として出る
        await receiver.Execution!.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        return new MethodResult(idle ? "Idle" : "Fixed",
                                idle ? $"無通信 {options.IdleMilliseconds}ms" : $"収集 {options.CollectionMilliseconds}ms",
                                [.. frames], string.Concat(frames.Select(f => f.Text)) == text);
    }

    private static async Task SendStringsAsync(ChannelWriter<string> port, IEnumerable<(string Text, TimeSpan GapBefore)> fragments)
    {
        foreach (var (chunk, gap) in fragments)
        {
            if (gap > TimeSpan.Zero) await Task.Delay(gap);
            await port.WriteAsync(chunk);
        }
    }

    private static async Task WriteInputAsync(string text, IReadOnlyList<(string Text, TimeSpan GapBefore)> fragments, CustomOptions options, TextWriter output)
    {
        var gaps = fragments.Skip(1).Select(f => f.GapBefore.TotalMilliseconds).DefaultIfEmpty(0).ToArray();
        var how = options.SplitPositions is null
            ? $"乱数で分割（seed {options.Seed}、チャンク長 {options.ChunkRange.Min}-{options.ChunkRange.Max}、間欠 {options.GapRange.Min}-{options.GapRange.Max}ms）"
            : options.Gaps is null ? $"分割位置を明示、間欠は乱数（seed {options.Seed}、{options.GapRange.Min}-{options.GapRange.Max}ms）" : "分割位置と間欠を明示";
        await output.WriteLineAsync("=== 入力 ===");
        await output.WriteLineAsync($"長さ {text.Length} 文字 / CR {Count(text, '\r')} 個 / LF {Count(text, '\n')} 個 / 先頭 \"{Preview(text, 40)}\"");
        await output.WriteLineAsync($"チャンク {fragments.Count} 個、間欠 {gaps.Min():0}～{gaps.Max():0}ms、送信にかかる時間 約 {gaps.Sum():0}ms（{how}）");
        if (options.Verbose)
        {
            for (int i = 0; i < fragments.Count; i++)
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"  chunk {i + 1,3}  間欠 {fragments[i].GapBefore.TotalMilliseconds,5:0}ms  {fragments[i].Text.Length,4} 文字  \"{Escapes.Show(fragments[i].Text)}\""));
        }
        await output.WriteLineAsync();
    }

    private static async Task WriteResultAsync(MethodResult result, TextWriter output)
    {
        await output.WriteLineAsync($"=== {result.Name}（{result.Condition}） ===");
        for (int i = 0; i < result.Frames.Count; i++)
        {
            var f = result.Frames[i];
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"  {i + 1,3}  [{f.End}]  {f.Text.Length,4} 文字  CR {Count(f.Text, '\r'),3}  確定 +{f.SinceFirstChunk.TotalMilliseconds,5:0}ms  \"{Preview(f.Text, 36)}\""));
        }
        await output.WriteLineAsync($"  フレーム {result.Frames.Count} 個 / 連結すると入力と一致: {(result.Lossless ? "はい" : "いいえ")}");
        await output.WriteLineAsync();
    }

    private static async Task WriteComparisonAsync(IReadOnlyList<MethodResult> results, TextWriter output)
    {
        await output.WriteLineAsync("=== 比較（同じ分割・同じ間欠） ===");
        foreach (var r in results)
        {
            var ends = string.Join(" / ", r.Frames.GroupBy(f => f.End).Select(g => $"{g.Key}×{g.Count()}"));
            var last = r.Frames.Count > 0 ? $"最後の確定 +{r.Frames[^1].SinceFirstChunk.TotalMilliseconds:0}ms" : "確定なし";
            await output.WriteLineAsync($"  {r.Name,-9} {r.Condition} | フレーム {r.Frames.Count} 個 | {ends} | {last} | 入力と一致: {(r.Lossless ? "はい" : "いいえ")}");
        }
        await output.WriteLineAsync();
    }

    private static int Count(string text, char c) => text.Count(x => x == c);

    private static string Preview(string text, int length)
    {
        var shown = Escapes.Show(text);
        return shown.Length <= length ? shown : shown[..length] + "…";
    }
}
