using Eiii.CommSim;

namespace Eiii.CommSim.Tests;

/// <summary>デリミタ検出ロジックの単体テスト（Worker やスレッドに依存しない）。</summary>
public class DelimiterFramerTests
{
    [Fact]
    public void 一つのチャンクに含まれる複数のフレームを順に確定する()
    {
        var framer = new DelimiterFramer();

        var frames = framer.Feed("A=1\r\nB=2\r\n");

        Assert.Equal([new Frame("A=1", true), new Frame("B=2", true)], frames);
        Assert.Equal(0, framer.BufferedLength);
    }

    [Fact]
    public void 複数のチャンクに分かれたフレームを復元する()
    {
        var framer = new DelimiterFramer();

        Assert.Empty(framer.Feed("TE"));
        Assert.Empty(framer.Feed("MP=2"));
        var frames = framer.Feed("3.5\r\n");

        Assert.Equal([new Frame("TEMP=23.5", true)], frames);
    }

    [Fact]
    public void 区切り文字がチャンクの境界をまたいでも検出する()
    {
        var framer = new DelimiterFramer();

        Assert.Empty(framer.Feed("A=1\r"));            // CR だけではまだ確定しない
        var frames = framer.Feed("\nB=2\r\n");          // LF が次のチャンクの先頭に来る

        Assert.Equal([new Frame("A=1", true), new Frame("B=2", true)], frames);
    }

    [Fact]
    public void 複数文字の区切り文字が三つのチャンクにまたがっても検出する()
    {
        var framer = new DelimiterFramer("<EOT>");

        Assert.Empty(framer.Feed("X=9<E"));
        Assert.Empty(framer.Feed("O"));
        var frames = framer.Feed("T>");

        Assert.Equal([new Frame("X=9", true)], frames);
    }

    [Fact]
    public void 区切り文字が連続した空のフレームも捨てずに返す()
    {
        var framer = new DelimiterFramer();

        var frames = framer.Feed("\r\n\r\n");

        Assert.Equal([new Frame("", true), new Frame("", true)], frames);
    }

    [Fact]
    public void Flushは末尾の未確定データを未完了フレームとして返しバッファを空にする()
    {
        var framer = new DelimiterFramer();
        framer.Feed("A=1\r\nTAIL=7");

        var tail = framer.Flush();

        Assert.Equal(new Frame("TAIL=7", false), tail);
        Assert.Equal(0, framer.BufferedLength);
        Assert.Null(framer.Flush());                     // 2 回目は何もない
    }

    [Fact]
    public void 区切り文字の途中で終わった末尾もそのまま未完了フレームになる()
    {
        var framer = new DelimiterFramer();
        framer.Feed("A=1\r");

        Assert.Equal(new Frame("A=1\r", false), framer.Flush());
    }

    [Fact]
    public void 空の区切り文字は指定できない()
    {
        Assert.Throws<ArgumentException>(() => new DelimiterFramer(""));
    }
}

public class FrameParserTests
{
    [Fact]
    public void KEY_VALUE形式を解析する()
    {
        Assert.Equal(new Reading("TEMP=23.5", "TEMP", 23.5, null), FrameParser.Parse(new Frame("TEMP=23.5", true)));
    }

    [Fact]
    public void 形式が違うフレームはエラー付きの結果になる()
    {
        var reading = FrameParser.Parse(new Frame("garbage", true));

        Assert.Null(reading.Key);
        Assert.NotNull(reading.Error);
    }

    [Fact]
    public void 未完了フレームは値が正しく見えても解析せずエラー付きの結果になる()
    {
        var reading = FrameParser.Parse(new Frame("TAIL=7", false));

        Assert.Null(reading.Value);
        Assert.Contains("未完了", reading.Error, StringComparison.Ordinal);
    }
}
