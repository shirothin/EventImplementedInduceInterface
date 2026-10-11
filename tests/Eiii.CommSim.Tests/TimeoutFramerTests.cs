using Eiii.CommSim;

namespace Eiii.CommSim.Tests;

/// <summary>時間方式のフレーム確定ロジックの単体テスト。時刻は合成値を渡すので、実時間に依存せず確定的に動く。</summary>
public class TimeoutFramerTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Idle250 = TimeSpan.FromMilliseconds(250);

    private static DateTimeOffset At(double milliseconds) => T0.AddMilliseconds(milliseconds);

    /// <summary>約 300 文字・内部に CR を含む応答を、不規則なチャンクと 10～100ms の間欠に分け、到着時刻を付ける。</summary>
    private static List<(string Text, DateTimeOffset At)> Arrivals(string response, int seed, double startMs, params (int Index, double GapMs)[] overrides)
    {
        var fragments = OsdSimulator.Fragment(response, seed, 8, 40, TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(100)).ToList();
        foreach (var (index, gap) in overrides) fragments[index] = (fragments[index].Text, TimeSpan.FromMilliseconds(gap));
        var result = new List<(string, DateTimeOffset)>();
        var at = At(startMs);
        foreach (var (text, gap) in fragments)
        {
            at += gap;
            result.Add((text, at));
        }
        return result;
    }

    /// <summary>到着を Feed しつつ、25ms ごとの Tick で Poll する（実際の Worker と同じ到着順）。</summary>
    private static List<TimedFrame> Run(TimeoutFramer framer, IEnumerable<(string Text, DateTimeOffset At)> arrivals, DateTimeOffset until)
    {
        var frames = new List<TimedFrame>();
        var events = arrivals.Select(a => (a.At, Chunk: (string?)a.Text)).ToList();
        for (var t = At(0); t <= until; t = t.AddMilliseconds(25)) events.Add((t, null));
        foreach (var (at, chunk) in events.OrderBy(e => e.At).ThenBy(e => e.Chunk is null ? 1 : 0))
            frames.AddRange(chunk is null ? framer.Poll(at) : framer.Feed(chunk, at));
        return frames;
    }

    [Fact]
    public void Idle方式は100ms以下の間欠では分割せず約300文字の応答をCRを保持したまま1つに確定する()
    {
        var response = OsdSimulator.CreateResponse(300);
        var arrivals = Arrivals(response, seed: 1, startMs: 1500);
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250 });

        var frames = Run(framer, arrivals, until: arrivals[^1].At.AddMilliseconds(400));

        var frame = Assert.Single(frames);
        Assert.Equal(FrameEnd.IdleTimeout, frame.End);
        Assert.Equal(response, frame.Text);                         // 内部の CR を保持して連結
        Assert.True(response.Count(c => c == '\r') > 1);
        Assert.Equal(arrivals[0].At, frame.FirstReceivedAt);
        Assert.Equal(arrivals[^1].At, frame.LastReceivedAt);
    }

    [Fact]
    public void Idle方式は最後の受信から閾値に達するまで確定しない()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250 });
        framer.Feed("A\r", At(0));
        framer.Feed("B\r", At(100));

        Assert.Empty(framer.Poll(At(349)));                          // 最後の受信（100ms）から 249ms
        Assert.Equal(FrameEnd.IdleTimeout, Assert.Single(framer.Poll(At(350))).End);   // ちょうど 250ms で確定
    }

    [Theory]
    [InlineData(249, 1)]   // 閾値未満の間欠: 分割しない
    [InlineData(250, 2)]   // 閾値ちょうど: 確定する（境界は「以上」）
    [InlineData(400, 2)]   // 閾値を超える間欠: 1 つの応答が 2 つに誤分割される
    public void Idle方式は応答途中の間欠が閾値以上なら誤分割する(double gapMs, int expectedFrames)
    {
        var response = OsdSimulator.CreateResponse(300);
        var arrivals = Arrivals(response, seed: 2, startMs: 0, (6, gapMs));
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250 });

        var frames = Run(framer, arrivals, until: arrivals[^1].At.AddMilliseconds(400));

        Assert.Equal(expectedFrames, frames.Count);
        Assert.Equal(response, string.Concat(frames.Select(f => f.Text)));   // 分割されてもデータは失われない
    }

    [Fact]
    public void Fixed方式は最初の受信から収集時間で確定し途中の長い間欠では分割しない()
    {
        var response = OsdSimulator.CreateResponse(300);
        var arrivals = Arrivals(response, seed: 2, startMs: 0, (6, 400));
        var framer = new TimeoutFramer(new TimeoutOptions { CollectionTimeout = TimeSpan.FromSeconds(2) });
        var first = arrivals[0].At;

        Assert.Empty(Run(framer, arrivals, until: first.AddMilliseconds(1990)));
        var frame = Assert.Single(framer.Poll(first.AddSeconds(2)));

        Assert.Equal(FrameEnd.CollectionTimeout, frame.End);
        Assert.Equal(response, frame.Text);
    }

    [Fact]
    public void Fixed方式は収集時間より長く続く応答を途中で打ち切る()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { CollectionTimeout = TimeSpan.FromSeconds(1) });
        framer.Feed("part1\r", At(0));
        framer.Feed("part2\r", At(900));

        var frames = framer.Feed("part3\r", At(1100)).ToList();   // 最初の受信から 1 秒を過ぎて届いた
        frames.AddRange(framer.Poll(At(2200)));

        Assert.Equal(["part1\rpart2\r", "part3\r"], frames.Select(f => f.Text));
        Assert.All(frames, f => Assert.Equal(FrameEnd.CollectionTimeout, f.End));   // 打ち切りは完全性の証明ではない
    }

    [Fact]
    public void Idle方式とFixed方式を併用すると先に達した条件で確定する()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250, CollectionTimeout = TimeSpan.FromSeconds(1) });
        var all = new List<TimedFrame>();

        for (int ms = 0; ms <= 1200; ms += 100) all.AddRange(framer.Feed("x", At(ms)));   // 間欠 100ms が続くので Idle では確定しない

        var frame = Assert.Single(all);                              // 1000ms の受信時に、収集時間 1 秒の上限で確定
        Assert.Equal(FrameEnd.CollectionTimeout, frame.End);
        Assert.Equal(10, frame.Text.Length);                         // 0～900ms の 10 回分（期限ちょうど 1000ms の受信は次のフレーム）
        Assert.Equal(3, framer.BufferedLength);                      // 1000・1100・1200ms の分は捨てずに次のフレームとして集めている
    }

    [Fact]
    public void 応答待ちの上限は無通信の閾値とは別に働き最初の受信がなければNoResponseを通知する()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250, ResponseTimeout = TimeSpan.FromSeconds(6) });
        framer.ExpectResponse(At(0));

        Assert.Empty(framer.Poll(At(250)));                          // 無通信の閾値は応答開始前には働かない
        Assert.Empty(framer.Poll(At(5999)));
        var frame = Assert.Single(framer.Poll(At(6000)));

        Assert.Equal(FrameEnd.NoResponse, frame.End);
        Assert.Equal("", frame.Text);
        Assert.Empty(framer.Poll(At(7000)));                         // 通知は 1 回だけ
    }

    [Fact]
    public void 応答待ちの上限内に応答が始まればNoResponseにならない()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250, ResponseTimeout = TimeSpan.FromSeconds(6) });
        framer.ExpectResponse(At(0));
        framer.Feed("OSD\r", At(4800));                              // 中継で 4.8 秒かかった応答

        var frames = framer.Poll(At(7000));

        Assert.Equal(FrameEnd.IdleTimeout, Assert.Single(frames).End);
    }

    [Fact]
    public void 応答待ちの上限を過ぎて届いたデータは受信時刻で判定しNoResponseを先に通知する()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250, ResponseTimeout = TimeSpan.FromSeconds(6) });
        framer.ExpectResponse(At(0));

        var frames = framer.Feed("late\r", At(6500));                // Poll されないまま上限を過ぎて届いた

        Assert.Equal(FrameEnd.NoResponse, Assert.Single(frames).End);
        Assert.Equal(5, framer.BufferedLength);                      // 遅れて届いたデータは捨てずに次のフレームとして集める
    }

    [Fact]
    public void 最大長を超えたらそこまでのデータ全体をOverflowとして出力し以降は新しいフレームとして集める()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250, MaxFrameLength = 1024 });
        var chunk = new string('d', 39) + "\r";                      // 40 文字
        var frames = new List<TimedFrame>();

        for (int i = 0; i < 30; i++) frames.AddRange(framer.Feed(chunk, At(i * 10)));   // 計 1,200 文字
        frames.AddRange(framer.Poll(At(1000)));

        Assert.Equal(FrameEnd.Overflow, frames[0].End);
        Assert.Equal(1040, frames[0].Text.Length);                   // 1,024 を超えた時点のチャンクまで含める（捨てない）
        Assert.Equal(FrameEnd.IdleTimeout, frames[1].End);
        Assert.Equal(1200, frames.Sum(f => f.Text.Length));          // 合計でデータは失われない
    }

    [Fact]
    public void 判定は処理した時刻ではなく受信時刻とTickの時刻で行うため後からまとめて処理しても結果は同じ()
    {
        var a = Arrivals(OsdSimulator.CreateResponse(300, 1), seed: 3, startMs: 0);
        var b = Arrivals(OsdSimulator.CreateResponse(300, 2), seed: 4, startMs: (a[^1].At - T0).TotalMilliseconds + 400);
        var arrivals = a.Concat(b).ToList();
        var until = arrivals[^1].At.AddMilliseconds(400);

        // 到着のたびにすぐ処理し、25ms ごとの Tick でも判定した場合
        var prompt = Run(new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250 }), arrivals, until);

        // バックプレッシャーでキューに溜まり、Tick も送られないまま、ずっと後でまとめて処理された場合
        var delayedFramer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250 });
        var delayed = arrivals.SelectMany(x => delayedFramer.Feed(x.Text, x.At)).ToList();
        delayed.AddRange(delayedFramer.Poll(until.AddSeconds(10)));   // 処理した時刻は大きく遅れている

        Assert.Equal(2, prompt.Count);
        Assert.Equal(prompt, delayed);                                // 処理の遅れは確定する境界に影響しない
        Assert.Equal(string.Concat(a.Select(x => x.Text)), prompt[0].Text);   // A と B を誤って結合も分割もしない
        Assert.Equal(string.Concat(b.Select(x => x.Text)), prompt[1].Text);
    }

    [Fact]
    public void 条件の検証()
    {
        Assert.Throws<ArgumentException>(() => new TimeoutFramer(new TimeoutOptions()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250, MaxFrameLength = 0 }));
    }

    [Fact]
    public void Flushは時間条件を満たす前に残ったデータをFlushとして返す()
    {
        var framer = new TimeoutFramer(new TimeoutOptions { IdleTimeout = Idle250 });
        framer.Feed("rest\r", At(0));

        Assert.Equal(new TimedFrame("rest\r", FrameEnd.Flush, At(0), At(0)), framer.Flush());
        Assert.Null(framer.Flush());
    }
}
