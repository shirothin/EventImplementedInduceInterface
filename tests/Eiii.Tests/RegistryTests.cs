using Eiii;

namespace Eiii.Tests;

/// <summary>Step 4: 入出力型の異なるワーカーの一元管理。</summary>
public class RegistryTests
{
    [Fact]
    public void 入出力型の異なるワーカーを登録し名前で具体的な型のまま取り出せる()
    {
        var registry = new EiiiWorkerRegistry();
        var length = new LengthTask(4);   // EiiiWorker<string, int>
        var times = new TestTask(4);      // EiiiWorker<int, int>

        Assert.Same(length, registry.Add("length", length));   // 戻り値は LengthTask 型
        Assert.Same(times, registry.Add("times", times));

        Assert.Same(length, registry.Get<LengthTask>("length"));
        Assert.Same(times, registry.Get<EiiiWorker<int, int>>("times"));
        Assert.Same(times, registry.Get<EiiiWorker>("times"));   // 基底型でも取り出せる
    }

    [Fact]
    public void 同じ名前の登録はArgumentExceptionで既存の登録は変わらない()
    {
        var registry = new EiiiWorkerRegistry();
        var first = registry.Add("a", new TestTask(4));

        Assert.Throws<ArgumentException>(() => registry.Add("a", new TestTask(4)));

        Assert.Same(first, registry.Get<TestTask>("a"));
    }

    [Fact]
    public void 未登録の名前はKeyNotFoundExceptionで型違いはInvalidCastException()
    {
        var registry = new EiiiWorkerRegistry();
        registry.Add("length", new LengthTask(4));

        Assert.Throws<KeyNotFoundException>(() => registry.Get<EiiiWorker>("missing"));
        Assert.Throws<InvalidCastException>(() => registry.Get<TestTask>("length"));
        Assert.Throws<ArgumentNullException>(() => registry.Add<TestTask>(null!, new TestTask(4)));
    }

    [Fact]
    public void 複数スレッドから異なる名前を同時に登録してもすべて登録される()
    {
        var registry = new EiiiWorkerRegistry();

        Parallel.For(0, 200, i => registry.Add($"w{i}", new TestTask(1)));

        for (int i = 0; i < 200; i++) Assert.NotNull(registry.Get<TestTask>($"w{i}"));
    }

    [Fact]
    public void GetAllは入出力型の異なるワーカーを登録順に返す()
    {
        var registry = new EiiiWorkerRegistry();
        var times = registry.Add("times", new TestTask(4));
        var length = registry.Add("length", new LengthTask(4));

        Assert.Equal([times, length], registry.GetAll());
    }

    [Fact]
    public void GetAllで取得した一覧は後からAddしても変わらない()
    {
        var registry = new EiiiWorkerRegistry();
        var first = registry.Add("first", new TestTask(4));
        var snapshot = registry.GetAll();

        var second = registry.Add("second", new TestTask(4));

        Assert.Equal([first], snapshot);                    // 取得済みの一覧は変わらない
        Assert.Equal([first, second], registry.GetAll());   // 新しく取得すれば含まれる
    }

    [Fact]
    public void 何も登録していなければGetAllは空の一覧を返す()
    {
        Assert.Empty(new EiiiWorkerRegistry().GetAll());
    }
}
