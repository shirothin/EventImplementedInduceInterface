using System.Diagnostics;
using System.Threading.Channels;

namespace EventImplementedInduceInterface.Sample;

/// <summary>
/// (目的の)型 — 原型 MyItem。
/// 原型は制御命令 TSKCTRL（RESTART など）をデータに含めていたが、寿命制御は専用メソッドで行うため持たない。
/// </summary>
internal sealed record MyItem(int Number, string TextMessage);

/// <summary>
/// ワーカー — 原型 MyTask（EiiiTask&lt;MyItem&gt;）。要求 MyItem を処理し、結果の文字列を出力する。
/// 原型の eventFunc は呼び出し元のスレッドで動いたが、OnRequestAsync はワーカーのスレッドで 1 件ずつ動く。
/// </summary>
internal sealed class MyTask(int capacity, TimeSpan processingTime, TaskToCallback<string> callback)
    : EiiiTask<MyItem, string>(capacity, callback)
{
    protected override async ValueTask OnRequestAsync(MyItem request, CancellationToken cancellationToken)
    {
        Log.Write("worker", $"処理開始 Number={request.Number}");
        try
        {
            await Task.Delay(processingTime, cancellationToken);   // 時間のかかる処理の代わり
        }
        catch (OperationCanceledException)
        {
            Log.Write("worker", $"処理中に停止された Number={request.Number}（この要求は失われる）");
            throw;
        }
        await EmitAsync($"Number={request.Number},Text={request.TextMessage}", cancellationToken);   // 下流へ渡し、続いて callback
    }
}

/// <summary>
/// (目的の)メイン — 原型 Test。
/// Space: 要求を 1 件、B: 要求を 6 件（満杯の確認）、R: 再起動（キャンセル停止）、Esc / Q: 正常終了（CompleteInput）。
/// 標準入力がリダイレクトされているときは、文字（' ', 'b', 'r', 'q'）でキー操作の代わりにする。
/// </summary>
/// <remarks>
/// <para>同期と非同期の違い: イベントハンドラー（Raise の購読者）は Trigger を呼んだスレッドで、Trigger が戻る前に同期実行される。
/// ワーカー（OnRequestAsync）は Channel から要求を読み出して、呼び出し元とは独立に非同期で処理する。</para>
/// <para>非同期は「別スレッド」と同じ意味ではない: async/await は待機中にスレッドを占有しない仕組みであり、
/// 続きがどのスレッドで動くかは実行環境が決める。このサンプルでワーカーがスレッドプールで動くのは、
/// EiiiTask が Task.Run で起動しているためで、非同期処理が必ず別スレッドで動くという意味ではない。</para>
/// <para>Main を同期メソッドにした理由: 今回の実験で動作を観察しやすくするため（原型と同じ形）。
/// async Main では await の後の続きがスレッドプールで再開され、入力ループとワーカーのスレッド ID が混ざって見分けにくい。</para>
/// <para>GetAwaiter().GetResult() について: 同期 Main から非同期 API の完了を待つために、この実験に限って使う。
/// 一般的な非同期 API の推奨使用法ではない。通常は await を使うこと。SynchronizationContext を持つ UI アプリや、
/// ライブラリの中で使うとデッドロックの原因になる。</para>
/// </remarks>
internal static class Test
{
    private const string Caller = "Test.Main";   // Trigger の sender（要求の呼び出し元）

    private static void Main()
    {
        // Registry — 原型 TaskDbContext<MyItem>。入出力型の異なるワーカーも登録できる
        var registry = new TaskDbContext();

        // Callback — 原型 Cb[0]。型付きの delegate を明示的に生成する
        int callbacks = 0;
        var callback = new TaskToCallback<string>(output =>
        {
            Interlocked.Increment(ref callbacks);
            Log.Write("callback", $"出力が下流に受理された: {output}");
        });

        var task = registry.Add("aTsk", new MyTask(capacity: 3, processingTime: TimeSpan.FromMilliseconds(500), callback));

        // 下流 — callback（受理の通知）と下流での処理完了が別のタイミングであることを見せるための表示処理
        var display = Channel.CreateBounded<string>(2);
        task.LinkTo(display.Writer);
        int displayed = 0;
        var consumer = Task.Run(async () =>
        {
            await foreach (var output in display.Reader.ReadAllAsync())
            {
                await Task.Delay(200);
                Interlocked.Increment(ref displayed);
                Log.Write("display", $"下流で処理完了: {output}");
            }
            Log.Write("display", "下流の入力が閉じられたので終了");
        });

        // Launcher — 原型はキー入力のたびに new EiiiLauncher で SetSubscribe していた。ここでは 1 つを常駐させる
        var launcher = new EiiiLauncher<MyItem>();
        registry.Get<MyTask>("aTsk").Subscribe(launcher);   // 原型 GetTaskByName("aTsk")。ワーカーのハンドラーが TryWrite して Accepted を書き戻す

        // 観察用の購読者（Accepted は書き込まない）。マルチキャストなので登録順に、ワーカーのハンドラーの後で呼ばれる
        launcher.Raise += (sender, e) =>
            Log.Write("event", $"Raise を受信 sender={sender} Number={e.Value.Number} Accepted={e.Accepted}（呼び出し元スレッドで同期実行）");

        registry.StartAll();
        Log.Write("main", "Space: 要求1件 / B: 要求6件 / R: 再起動 / Esc: 正常終了");

        int number = 0, accepted = 0, rejected = 0;
        Command command;
        while ((command = ReadCommand()) != Command.Exit)
        {
            switch (command)
            {
                case Command.Request:
                    Send(1);
                    break;
                case Command.Burst:
                    Send(6);
                    break;
                case Command.Restart:
                    var before = task.Execution;
                    Log.Write("main", "R: RestartAsync（キャンセルで停止してから起動）");
                    task.RestartAsync().GetAwaiter().GetResult();
                    Log.Write("main", $"再起動完了: 旧実行={before?.Status} 未処理(チャネルに残存)={task.PendingCount} 稼働中={task.IsRunning}");
                    break;
            }
            Thread.Sleep(TimeSpan.FromSeconds(1));   // 原型 WaitForSeconds(1) の意図（原型は async void のため待てていなかった）
        }

        Log.Write("main", "Esc: CompleteAllAsync（入力を閉じ、残りを処理して正常終了）");
        registry.CompleteAllAsync().GetAwaiter().GetResult();
        consumer.GetAwaiter().GetResult();
        Log.Write("main", $"終了: 実行={task.Execution?.Status} 受理={accepted} 拒否={rejected} callback={callbacks} 下流で処理完了={displayed}"
                          + $" 停止で失われた={accepted - callbacks}");

        void Send(int count)
        {
            for (int i = 0; i < count; i++)
            {
                var item = new MyItem(++number, $"Item{number}");
                var ok = launcher.Trigger(Caller, item);   // 同期: Raise イベント → ハンドラーが Channel へ TryWrite
                if (ok) accepted++; else rejected++;
                Log.Write("main", $"Trigger(Number={item.Number}) → {(ok ? "受理" : "拒否（満杯）")} pending={task.PendingCount}");
            }
        }
    }

    private enum Command { Request, Burst, Restart, Exit }

    private static Command ReadCommand()
    {
        if (Console.IsInputRedirected)
        {
            int c;
            while ((c = Console.In.Read()) != -1)
            {
                switch (char.ToLowerInvariant((char)c))
                {
                    case ' ': return Command.Request;
                    case 'b': return Command.Burst;
                    case 'r': return Command.Restart;
                    case 'q' or '\u001b': return Command.Exit;
                }
            }
            return Command.Exit;
        }
        while (true)
        {
            switch (Console.ReadKey(intercept: true).Key)
            {
                case ConsoleKey.Spacebar: return Command.Request;
                case ConsoleKey.B: return Command.Burst;
                case ConsoleKey.R: return Command.Restart;
                case ConsoleKey.Escape or ConsoleKey.Q: return Command.Exit;
            }
        }
    }
}

/// <summary>経過時間とスレッド ID 付きのログ。複数スレッドから呼ばれるため出力を排他する。</summary>
internal static class Log
{
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lock Gate = new();

    public static void Write(string role, string message)
    {
        lock (Gate) Console.WriteLine($"{Clock.ElapsedMilliseconds,6}ms [T{Environment.CurrentManagedThreadId,2}] {role,-8} {message}");
    }
}
