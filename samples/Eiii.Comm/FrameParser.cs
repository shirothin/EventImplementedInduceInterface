using System.Globalization;

namespace Eiii.CommSim;

/// <summary>解析結果。解析できなかったフレームも <see cref="Error"/> 付きで出力する（黙って捨てない）。</summary>
/// <param name="Raw">元のフレームの内容。</param>
/// <param name="Key">キー。解析できなければ null。</param>
/// <param name="Value">値。解析できなければ null。</param>
/// <param name="Error">解析できなかった理由。成功なら null。</param>
public sealed record Reading(string Raw, string? Key, double? Value, string? Error);

/// <summary>フレームを KEY=VALUE 形式として解析する。Worker に依存しない純粋な処理。</summary>
public static class FrameParser
{
    /// <summary>フレームを解析する。</summary>
    /// <param name="frame">確定したフレーム。</param>
    /// <returns>解析結果。</returns>
    public static Reading Parse(Frame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (!frame.IsComplete) return new Reading(frame.Text, null, null, "未完了フレーム（区切り文字なしで入力が終了）");
        var eq = frame.Text.IndexOf('=', StringComparison.Ordinal);
        if (eq > 0 && double.TryParse(frame.Text.AsSpan(eq + 1), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            return new Reading(frame.Text, frame.Text[..eq], value, null);
        return new Reading(frame.Text, null, null, "KEY=VALUE 形式ではない");
    }
}
