using System.Text;

namespace Eiii.DemoConsole;

/// <summary>
/// 画面の描画とキー入力。コンソールへの書き込みはこのクラスの描画ループ 1 か所に集約する
/// （Worker からの出来事は DemoSystem.Events の Channel で受け取る）。
/// </summary>
/// <remarks>
/// 100 桁 × 30 行以上の対話的なコンソールでは全画面表示、狭い場合や出力がリダイレクトされている場合は行単位のログ表示にする。
/// 標準入力がリダイレクトされている場合は、文字をキー操作の代わりに読む（'.' は 0.5 秒待つ）。
/// </remarks>
internal sealed class ConsoleUi(DemoSystem system)
{
    private const int Width = 100;
    private const int Height = 30;
    private const int LogTop = 14;
    private static readonly int[] ParserDelays = [0, 300, 1000];
    private static readonly string Rule = new('-', Width - 1);

    private readonly List<string> log = [];
    private bool fullScreen;
    private int parserDelayIndex;
    private string lastStatus = "";

    public async Task RunAsync()
    {
        fullScreen = !Console.IsOutputRedirected && TryGetWindow(out var width, out var height) && width >= Width && height >= Height;
        if (fullScreen)
        {
            Console.OutputEncoding = Encoding.UTF8;
            Console.CursorVisible = false;
            Console.Clear();
        }
        else
        {
            foreach (var line in Help()) Console.WriteLine(line);
        }

        var rendering = RenderLoopAsync();
        try
        {
            await InputLoopAsync();
        }
        finally
        {
            await system.CompleteAsync();   // 正常終了: 残りを処理してから終わる
            await rendering;
            if (fullScreen)
            {
                Console.SetCursorPosition(0, Height - 1);
                Console.CursorVisible = true;
                Console.WriteLine();
            }
            Console.WriteLine($"終了しました（受理 {system.Accepted} / 拒否 {system.Rejected} / Callback {system.Callbacks}）。");
        }
    }

    private async Task InputLoopAsync()
    {
        while (true)
        {
            var key = await ReadKeyAsync();
            switch (key)
            {
                case '1':
                    system.Send(CommandKind.Normal);
                    break;
                case '2':
                    system.Send(CommandKind.Osd);
                    break;
                case '3':
                    for (int i = 0; i < 10; i++) system.Send(CommandKind.Normal);
                    break;
                case 's':
                    parserDelayIndex = (parserDelayIndex + 1) % ParserDelays.Length;
                    system.Parser.DelayMilliseconds = ParserDelays[parserDelayIndex];
                    system.Note($"S: Parser の処理時間を {ParserDelays[parserDelayIndex]}ms にした");
                    break;
                case 'g':
                    system.Device.LongGap = !system.Device.LongGap;
                    system.Note($"G: OSD 応答の途中の長い間欠 = {OnOff(system.Device.LongGap)}" +
                                (system.Device.LongGap ? "（閾値を超えるので誤分割し、次のコマンドが早く送られ得る）" : ""));
                    break;
                case 'n':
                    system.Device.Silent = !system.Device.Silent;
                    system.Note($"N: 機器の無応答 = {OnOff(system.Device.Silent)}");
                    break;
                case 'r':
                    await system.RestartAsync();
                    break;
                case '.':
                    await Task.Delay(500);
                    break;
                case 'q':
                case '\u001b':
                case '\0':
                    return;
            }
        }
    }

    private static async Task<char> ReadKeyAsync()
    {
        if (Console.IsInputRedirected)
        {
            var c = await Task.Run(Console.In.Read);
            return c < 0 ? '\0' : char.ToLowerInvariant((char)c);
        }
        while (!Console.KeyAvailable) await Task.Delay(30);
        var info = Console.ReadKey(intercept: true);
        return info.Key == ConsoleKey.Escape ? '\u001b' : char.ToLowerInvariant(info.KeyChar);
    }

    private async Task RenderLoopAsync()
    {
        var reader = system.Events;
        var nextStatus = DateTime.UtcNow;
        while (true)
        {
            while (reader.TryRead(out var e)) Append(e);
            if (fullScreen) Draw();
            else if (DateTime.UtcNow >= nextStatus)
            {
                var status = StatusLine();
                if (status != lastStatus) Console.WriteLine(status);
                lastStatus = status;
                nextStatus = DateTime.UtcNow.AddSeconds(1);
            }
            if (reader.Completion.IsCompleted) break;
            await Task.WhenAny(reader.WaitToReadAsync().AsTask(), Task.Delay(200));
        }
        if (fullScreen) Draw();
        else Console.WriteLine(StatusLine());
    }

    private void Append(DemoEvent e)
    {
        var line = $"{e.At.ToLocalTime():HH:mm:ss.fff} {(e.Id is { } id ? $"#{id,-3}" : "    ")} [{Label(e.Stage)}] {e.Message}";
        if (!fullScreen)
        {
            Console.WriteLine(line);
            return;
        }
        log.Add(line);
        if (log.Count > Height - LogTop) log.RemoveAt(0);
    }

    private void Draw()
    {
        var lines = new List<string>
        {
            $"Eiii DemoConsole  架空 XPort（模擬・実機接続なし）                        {DateTime.Now:HH:mm:ss}",
            "#番号はアプリ内の追跡用 ID で、実機プロトコルが返す識別子ではない",
            Rule,
            Fit("Worker", 11) + Fit("状態", 18) + Fit("キュー", 13) + Fit("完了", 8) + "補足",
        };
        foreach (var (name, worker, capacity) in system.Workers)
        {
            var pending = worker.PendingCount;
            var queue = $"{pending}/{capacity}" + (pending >= capacity && capacity > 0 ? " 満杯" : "");
            lines.Add(Fit(name, 11) + Fit(DemoSystem.Describe(worker), 18) + Fit(queue, 13) + Fit(Done(name).ToString(System.Globalization.CultureInfo.InvariantCulture), 8) + Note(name));
        }
        lines.Add($"要求: 受理 {system.Accepted} / 拒否 {system.Rejected}    受信チャンク {system.Receiver.Chunks}    確定 {system.Receiver.Frames}    解析 {system.Parser.Parsed}    Callback {system.Callbacks}");
        lines.Add(Rule);
        lines.AddRange(Help());
        lines.Add(Rule[..60] + " ログ（新しいものが下）");
        lines.AddRange(log);
        while (lines.Count < Height - 1) lines.Add("");

        for (int row = 0; row < Height - 1; row++)
        {
            Console.SetCursorPosition(0, row);
            Console.Write(Fit(lines[row], Width - 1));
        }
    }

    private int Done(string name) => name switch
    {
        "commander" => system.Commander.Completed,
        "device" => system.Device.Responded,
        "receiver" => system.Receiver.Frames,
        _ => system.Parser.Parsed,
    };

    private string Note(string name) => name switch
    {
        "commander" => system.Tracker.Current is { } c
            ? $"#{c.Id} 応答確定待ち {(system.Options.Time.GetUtcNow() - c.Since).TotalSeconds:0.0}s"
            : "応答待ちなし",
        "device" => $"無応答={OnOff(system.Device.Silent)} 長い間欠={OnOff(system.Device.LongGap)}",
        "receiver" => $"方式={system.Receiver.Mode}",
        _ => $"処理時間 {system.Parser.DelayMilliseconds}ms",
    };

    private string StatusLine()
        => "[状態] " + string.Join("  ", system.Workers.Select(w => $"{w.Name}={DemoSystem.Describe(w.Worker)} {w.Worker.PendingCount}/{w.Capacity}"))
           + $"  受理={system.Accepted} 拒否={system.Rejected} Callback={system.Callbacks}";

    private static IEnumerable<string> Help() =>
    [
        "[1]通常コマンド(CR終端)  [2]OSD取得(時間経過)  [3]通常x10(満杯で拒否)  [S]Parser処理時間 0/300/1000ms",
        "[G]OSD途中に長い間欠(誤分割)  [N]機器を無応答に  [R]キャンセル再起動(処理中は失われ得る)",
        "[Q]/[Esc]正常終了(受理済みをすべて処理してから終了)",
    ];

    private static string Label(Stage stage) => stage switch
    {
        Stage.Request => "要求",
        Stage.Send => "送信",
        Stage.Wait => "待機",
        Stage.Device => "機器",
        Stage.Chunk => "受信",
        Stage.Frame => "確定",
        Stage.Parse => "解析",
        Stage.Callback => "通知",
        _ => "操作",
    };

    private static string OnOff(bool value) => value ? "ON" : "OFF";

    private static bool TryGetWindow(out int width, out int height)
    {
        try
        {
            width = Console.WindowWidth;
            height = Console.WindowHeight;
            return true;
        }
        catch (IOException)
        {
            width = height = 0;
            return false;
        }
    }

    /// <summary>全角文字を 2 桁として、表示幅 <paramref name="width"/> に切り詰め・空白で埋める。</summary>
    internal static string Fit(string text, int width)
    {
        var sb = new StringBuilder();
        int used = 0;
        foreach (var c in text)
        {
            var w = DisplayWidth(c);
            if (used + w > width) break;
            sb.Append(c);
            used += w;
        }
        return sb.Append(' ', width - used).ToString();
    }

    private static int DisplayWidth(char c)
        => c is (>= 'ᄀ' and <= 'ᅟ') or (>= '⺀' and <= '꓏') or (>= '가' and <= '힣')
               or (>= '豈' and <= '﫿') or (>= '︰' and <= '﹏') or (>= '＀' and <= '｠') or (>= '￠' and <= '￦')
            ? 2 : 1;
}
