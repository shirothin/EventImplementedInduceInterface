using System.Text;
using Eiii.CommSim;

namespace Eiii.CommSim.Tests;

/// <summary>custom モード: エスケープ表記の解釈。</summary>
public class EscapesTests
{
    [Theory]
    [InlineData(@"A\rB", "A\rB")]
    [InlineData(@"A\nB", "A\nB")]
    [InlineData(@"A\r\nB", "A\r\nB")]
    [InlineData(@"\t", "\t")]
    [InlineData(@"C:\\dir", @"C:\dir")]
    [InlineData(@"\x0D\x0a", "\r\n")]
    [InlineData(@"\x41\x1B", "A\u001B")]
    [InlineData("plain text", "plain text")]
    [InlineData("", "")]
    public void エスケープ表記を解釈する(string input, string expected)
    {
        Assert.Equal(expected, Escapes.Unescape(input));
    }

    [Theory]
    [InlineData(@"A\")]       // 末尾の \
    [InlineData(@"\q")]       // 不明なエスケープ
    [InlineData(@"\x4")]      // 16 進 1 桁
    [InlineData(@"\xZZ")]     // 16 進でない
    [InlineData(@"\x-1")]     // 符号は不可
    public void 不正なエスケープはFormatException(string input)
    {
        Assert.Throws<FormatException>(() => Escapes.Unescape(input));
    }

    [Fact]
    public void Showは解釈の逆になる()
    {
        var text = "A\rB\nC\tD\\E";

        Assert.Equal(text, Escapes.Unescape(Escapes.Show(text)));
    }
}

/// <summary>custom モード: 引数の解析。</summary>
public class CustomArgumentsTests
{
    [Fact]
    public void 既定値()
    {
        var o = CustomArguments.Parse(["--text", "A"]);

        Assert.Equal("A", o.Text);
        Assert.Equal(CompareMode.All, o.Mode);
        Assert.Equal("\r", o.Delimiter);
        Assert.Equal(new IntRange(8, 40), o.ChunkRange);
        Assert.Equal(new IntRange(10, 100), o.GapRange);
        Assert.Null(o.SplitPositions);
        Assert.Null(o.Gaps);
        Assert.Equal(1, o.Seed);
        Assert.Equal(250, o.IdleMilliseconds);
        Assert.Equal(2000, o.CollectionMilliseconds);
        Assert.Equal(1024, o.MaxFrameLength);
        Assert.Equal(25, o.TickMilliseconds);
        Assert.False(o.Verbose);
    }

    [Fact]
    public void すべてのオプションを解析する()
    {
        var o = CustomArguments.Parse(
        [
            "--text", @"A\rB", "--mode", "idle", "--delimiter", @"\r\n", "--chunk-range", "2-5", "--gap-range", "0-300",
            "--split", "1,2", "--gaps", "50,400", "--seed", "-7", "--idle", "300", "--collection", "1500",
            "--max", "512", "--tick", "10", "--verbose",
        ]);

        Assert.Equal("A\rB", o.Text);
        Assert.Equal(CompareMode.Idle, o.Mode);
        Assert.Equal("\r\n", o.Delimiter);
        Assert.Equal(new IntRange(2, 5), o.ChunkRange);
        Assert.Equal(new IntRange(0, 300), o.GapRange);
        Assert.Equal([1, 2], o.SplitPositions);
        Assert.Equal([50, 400], o.Gaps);
        Assert.Equal(-7, o.Seed);
        Assert.Equal(300, o.IdleMilliseconds);
        Assert.Equal(1500, o.CollectionMilliseconds);
        Assert.Equal(512, o.MaxFrameLength);
        Assert.Equal(10, o.TickMilliseconds);
        Assert.True(o.Verbose);
    }

    [Fact]
    public void ファイル指定とヘルプ()
    {
        Assert.Equal("osd.txt", CustomArguments.Parse(["--file", "osd.txt"]).FilePath);
        Assert.True(CustomArguments.Parse(["--help"]).ShowHelp);
        Assert.True(CustomArguments.Parse(["--text", "A", "--help"]).ShowHelp);
    }

    // InlineData の引数は params object[] のため、string[] を直接渡すと引数の配列そのものとみなされる（CS0182）。TheoryData で渡す。
    public static TheoryData<string[]> InvalidArguments => new()
    {
        Array.Empty<string>(),                                  // 入力なし
        new[] { "--text", "A", "--file", "f" },                 // 両方
        new[] { "--text" },                                     // 値なし
        new[] { "--text", "A", "--text", "B" },                 // 重複
        new[] { "--text", "A", "--unknown", "1" },              // 不明なオプション
        new[] { "A" },                                          // オプションでない引数
        new[] { "--text", @"\q" },                              // 不正なエスケープ
        new[] { "--text", "A", "--mode", "fast" },              // 不正な方式
        new[] { "--text", "A", "--delimiter", "" },             // 空の区切り文字
        new[] { "--text", "A", "--chunk-range", "0-5" },        // チャンク長 0
        new[] { "--text", "A", "--chunk-range", "9-3" },        // 最小 > 最大
        new[] { "--text", "A", "--gap-range", "10" },           // 範囲の形式でない
        new[] { "--text", "A", "--split", "1,x" },              // 整数でない
        new[] { "--text", "A", "--gaps", "10" },                // --split なしの --gaps
        new[] { "--text", "A", "--idle", "0" },                 // 0 以下
        new[] { "--text", "A", "--max", "-1" },                 // 負数
    };

    [Theory]
    [MemberData(nameof(InvalidArguments))]
    public void 誤った引数はCustomUsageException(string[] args)
    {
        Assert.Throws<CustomUsageException>(() => CustomArguments.Parse(args));
    }
}

/// <summary>custom モード: 分割と間欠。</summary>
public class CustomFragmentsTests
{
    private static CustomOptions Options(string text) => new() { Text = text };

    [Fact]
    public void 分割位置と間欠を明示する()
    {
        var o = Options("ABCDEFG") with { SplitPositions = [2, 5], Gaps = [50, 400] };

        var fragments = CustomFragments.Create("ABCDEFG", o);

        Assert.Equal(["AB", "CDE", "FG"], fragments.Select(f => f.Text));
        Assert.Equal([0, 50, 400], fragments.Select(f => f.GapBefore.TotalMilliseconds));
    }

    [Fact]
    public void 分割位置だけ明示すると間欠は範囲内の乱数でseedにより再現できる()
    {
        var o = Options("ABCDEFG") with { SplitPositions = [2, 5], GapRange = new IntRange(10, 100), Seed = 9 };

        var first = CustomFragments.Create("ABCDEFG", o);
        var second = CustomFragments.Create("ABCDEFG", o);

        Assert.Equal(["AB", "CDE", "FG"], first.Select(f => f.Text));
        Assert.Equal(TimeSpan.Zero, first[0].GapBefore);
        Assert.All(first.Skip(1), f => Assert.InRange(f.GapBefore.TotalMilliseconds, 10, 100));
        Assert.Equal(first, second);
    }

    [Fact]
    public void 乱数で分割してもseedが同じなら同じ結果で連結すると元に戻る()
    {
        var text = OsdSimulator.CreateResponse(300);
        var o = Options(text) with { ChunkRange = new IntRange(5, 30), Seed = 4 };

        var first = CustomFragments.Create(text, o);
        var second = CustomFragments.Create(text, o);

        Assert.Equal(first, second);
        Assert.Equal(text, string.Concat(first.Select(f => f.Text)));
        Assert.All(first.Take(first.Count - 1), f => Assert.InRange(f.Text.Length, 5, 30));
    }

    [Theory]
    [InlineData(new[] { 0 })]          // 先頭
    [InlineData(new[] { 7 })]          // 末尾（電文は 7 文字）
    [InlineData(new[] { 3, 3 })]       // 昇順でない（同じ位置）
    [InlineData(new[] { 5, 2 })]       // 昇順でない
    public void 不正な分割位置はCustomUsageException(int[] split)
    {
        Assert.Throws<CustomUsageException>(() => CustomFragments.Create("ABCDEFG", Options("ABCDEFG") with { SplitPositions = split }));
    }

    [Fact]
    public void 間欠の個数がチャンク数と合わなければCustomUsageException()
    {
        var o = Options("ABCDEFG") with { SplitPositions = [2, 5], Gaps = [50] };

        var ex = Assert.Throws<CustomUsageException>(() => CustomFragments.Create("ABCDEFG", o));
        Assert.Contains("2 個が必要", ex.Message, StringComparison.Ordinal);
    }
}

/// <summary>custom モード: ファイルの読み込み（文字コードと CR / LF の保持）。</summary>
public sealed class CustomInputFileTests : IDisposable
{
    private readonly string path = Path.Combine(Path.GetTempPath(), $"eiii-custom-{Guid.NewGuid():N}.txt");

    public void Dispose() => File.Delete(path);

    [Fact]
    public void CRとLFとCRLFを変換せずそのまま読む()
    {
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("A\rB\nC\r\nD\r"));

        Assert.Equal("A\rB\nC\r\nD\r", CustomInput.Load(new CustomOptions { FilePath = path }));
    }

    [Fact]
    public void UTF8のBOMは取り除き日本語も読める()
    {
        File.WriteAllBytes(path, [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes("設定\r")]);

        Assert.Equal("設定\r", CustomInput.Load(new CustomOptions { FilePath = path }));
    }

    [Fact]
    public void UTF8として不正なバイトはCustomUsageException()
    {
        File.WriteAllBytes(path, [0x41, 0x82, 0xA0, 0x0D]);   // Shift_JIS の「あ」を含む

        Assert.Throws<CustomUsageException>(() => CustomInput.Load(new CustomOptions { FilePath = path }));
    }

    [Fact]
    public void 存在しないファイルと空の電文はCustomUsageException()
    {
        Assert.Throws<CustomUsageException>(() => CustomInput.Load(new CustomOptions { FilePath = path + ".missing" }));
        File.WriteAllBytes(path, []);
        Assert.Throws<CustomUsageException>(() => CustomInput.Load(new CustomOptions { FilePath = path }));
    }
}

/// <summary>custom モード: 実行（実時間）。</summary>
public class CustomRunnerTests
{
    [Fact]
    public async Task 同じ分割と間欠で3方式を比較しIdleは閾値を超える間欠で分割する()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        // "L1\rL2\r" | 80ms | "L3\r" | 400ms | "L4\r"
        var code = await CustomRunner.RunAsync(
            ["--text", @"L1\rL2\rL3\rL4\r", "--split", "6,9", "--gaps", "80,400", "--collection", "1000"], output, error);

        Assert.Equal(0, code);
        var text = output.ToString();
        Assert.Contains("=== Delimiter（区切り \\r） ===", text, StringComparison.Ordinal);
        Assert.Contains("区切り \\r | フレーム 4 個", text, StringComparison.Ordinal);   // Delimiter: CR ごとに 4 行
        Assert.Contains("無通信 250ms | フレーム 2 個", text, StringComparison.Ordinal);  // Idle: 400ms の間欠で分割
        Assert.Contains("収集 1000ms | フレーム 1 個", text, StringComparison.Ordinal);   // Fixed: 1 秒以内に収まる
        Assert.DoesNotContain("入力と一致: いいえ", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task 引数の誤りは終了コード2で使い方を表示する()
    {
        var output = new StringWriter();
        var error = new StringWriter();

        var code = await CustomRunner.RunAsync(["--text", "A", "--gaps", "10"], output, error);

        Assert.Equal(2, code);
        Assert.Contains("--gaps は --split と組み合わせて使う", error.ToString(), StringComparison.Ordinal);
        Assert.Contains("使い方:", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task helpは終了コード0で使い方を表示する()
    {
        var output = new StringWriter();

        Assert.Equal(0, await CustomRunner.RunAsync(["--help"], output, new StringWriter()));
        Assert.Contains("--split", output.ToString(), StringComparison.Ordinal);
    }
}
