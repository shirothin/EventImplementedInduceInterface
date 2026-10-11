using System;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using EventImplementedInduceInterface;
using Xunit;

namespace Eiii.Legacy.Tests;

/// <summary>
/// 原型の現在の動作を記録する特性テスト。
/// Defect のテストは「現在の誤った動作」を表明しており、該当箇所を修正した段階で期待値を反転させる。
/// </summary>
public class CharacterizationTests
{
    [Fact, Trait(Kind.Name, Kind.Intended)]
    public async Task C1_Start後コールバックが1から連番で呼ばれる()
    {
        var rec = new CallbackRecorder();
        var task = new MyTask(rec.Callback, null, 100, 1);
        try
        {
            Assert.Equal("", await task.Start());
            Assert.True(await Legacy.WaitUntilAsync(() => rec.Count >= 3));
            Assert.Equal(new[] { 1, 2, 3 }, rec.Values.Take(3));
        }
        finally { Legacy.CancelLoop(task); }
    }

    [Fact, Trait(Kind.Name, Kind.Intended)]
    public async Task C2_Startを二重に呼んでも多重起動しない()
    {
        var rec = new CallbackRecorder();
        var task = new MyTask(rec.Callback, null, 100, 1);
        try
        {
            await task.Start();
            Assert.Null(await task.Start());   // starter が null でないため起動を拒否する
            Assert.True(await Legacy.WaitUntilAsync(() => rec.Count >= 5));
            var values = rec.Values;
            Assert.Equal(Enumerable.Range(1, values.Length), values);   // ループが 2 本なら値が重複する
        }
        finally { Legacy.CancelLoop(task); }
    }

    [Fact, Trait(Kind.Name, Kind.Intended)]
    public void C3_NEUTRAL要求でeventFuncが値を受け取る()
    {
        var db = new TaskDbContext<MyItem>();
        var probe = new EventProbe();
        db.Add("p", probe, null);
        var entity = db.GetTaskByName("p");
        entity.SetSubscribe(new EiiiLauncher<MyItem>());
        var item = new MyItem(TSKCTRL.NEUTRAL, 333, "Item333");

        entity.Trigger(item);

        Assert.Same(item, probe.Received);
    }

    [Fact, Trait(Kind.Name, Kind.Intended)]
    public async Task C4_RESTART要求でカウンタが1から再開する()
    {
        var rec = new CallbackRecorder();
        var db = new TaskDbContext<MyItem>();
        var task = new MyTask(rec.Callback, null, 100, 1);
        db.Add("a", task, null);
        try
        {
            await task.Start();
            Assert.True(await Legacy.WaitUntilAsync(() => rec.Count >= 3));
            var entity = db.GetTaskByName("a");
            entity.SetSubscribe(new EiiiLauncher<MyItem>());

            entity.Trigger(new MyItem(TSKCTRL.RESTART, 111, "Item111"));

            // 約 1.2 秒の待機を経て新しいループが始まり、2 回目の「1」が現れる
            Assert.True(await Legacy.WaitUntilAsync(() => rec.Values.Skip(1).Contains(1)));
        }
        finally { Legacy.CancelLoop(task); }
    }

    [Fact, Trait(Kind.Name, Kind.Unknown)]
    public void C5_eventFuncは呼び出し元スレッドで同期実行されsenderはLauncherである()
    {
        var db = new TaskDbContext<MyItem>();
        var probe = new EventProbe();
        db.Add("p", probe, null);
        var entity = db.GetTaskByName("p");
        entity.SetSubscribe(new EiiiLauncher<MyItem>());

        entity.Trigger(new MyItem(TSKCTRL.NEUTRAL, 1, "x"));

        Assert.Equal(Environment.CurrentManagedThreadId, probe.ThreadId);
        Assert.IsType<EiiiLauncher<MyItem>>(probe.Sender);
    }

    [Fact, Trait(Kind.Name, Kind.Defect)]
    public async Task C6_実行中にGetStatusがFaultedを返す()
    {
        var rec = new CallbackRecorder();
        var task = new MyTask(rec.Callback, null, 100, 1);
        try
        {
            await task.Start();
            Assert.True(await Legacy.WaitUntilAsync(() => rec.Count >= 2));
            Assert.Equal(TaskStatus.Faulted, task.GetStatus());   // ループ終了まで task フィールドが null のため
        }
        finally { Legacy.CancelLoop(task); }
    }

    [Fact, Trait(Kind.Name, Kind.Defect)]
    public async Task C9_コールバック例外でループが黙って停止しStartがnullを返す()
    {
        var rec = new CallbackRecorder();
        var task = new MyTask(rec.Callback, null, 100, 1);
        try
        {
            await task.Start();
            Assert.True(await Legacy.WaitUntilAsync(() => rec.Count >= 2));

            rec.ThrowOnNextCallback();
            Assert.True(await Legacy.WaitUntilAsync(() => !rec.ThrowPending));
            var countAfterFailure = rec.Count;
            await Task.Delay(500);

            Assert.Equal(countAfterFailure, rec.Count);   // 以後コールバックが来ない
            Assert.Null(await task.Start());              // starter が空文字のまま残り、再起動できない
        }
        finally { Legacy.CancelLoop(task); }
    }

    [Fact, Trait(Kind.Name, Kind.Unknown)]
    public async Task C10_Singleではループが0回で終わる()
    {
        var rec = new CallbackRecorder();
        var task = new MyTask(rec.Callback, null, 100, 0);

        await task.Start();
        await Task.Delay(400);

        Assert.Equal(0, rec.Count);
    }

    [Fact, Trait(Kind.Name, Kind.Defect)]
    public void C11_SetSubscribe前のTriggerでNullReferenceException()
    {
        var db = new TaskDbContext<MyItem>();
        db.Add("a", new EventProbe(), null);

        Assert.Throws<NullReferenceException>(() => db.GetTaskByName("a").Trigger(new MyItem(TSKCTRL.NEUTRAL, 1, "x")));
    }

    [Fact, Trait(Kind.Name, Kind.Defect)]
    public void C12_重複名のAddはnullを返すがリストには残る()
    {
        var db = new TaskDbContext<MyItem>();

        Assert.NotNull(db.Add("a", new EventProbe(), null));
        Assert.Null(db.Add("a", new EventProbe(), null));
        Assert.Equal(2, db.GetAll().Count());
    }

    [Fact, Trait(Kind.Name, Kind.Defect)]
    public void C13_WaitForSecondsは待機しない()
    {
        var method = typeof(Test).GetMethod("WaitForSeconds", BindingFlags.NonPublic | BindingFlags.Static);
        var sw = Stopwatch.StartNew();

        method.Invoke(null, new object[] { 1.0 });

        Assert.True(sw.ElapsedMilliseconds < 500, $"elapsed={sw.ElapsedMilliseconds}ms");   // async void のため即座に戻る
    }
}
