namespace Eiii.CommSim;

/// <summary>電文をチャンクに分け、各チャンクの前に置く間欠を割り当てる。</summary>
/// <remarks>
/// <list type="bullet">
/// <item>--split なし: 分割も間欠も乱数（既存の <see cref="OsdSimulator.Fragment"/> を使う。--seed で再現できる）。</item>
/// <item>--split あり・--gaps なし: 指定した位置で分割し、間欠は乱数。</item>
/// <item>--split あり・--gaps あり: 分割も間欠も明示。</item>
/// </list>
/// 最初のチャンクの前の間欠は常に 0。
/// </remarks>
public static class CustomFragments
{
    /// <summary>チャンクと間欠の列を作る。</summary>
    /// <param name="text">電文。</param>
    /// <param name="options">設定。</param>
    /// <returns>(チャンク, その前の間欠) の列。</returns>
    /// <exception cref="CustomUsageException">分割位置や間欠の個数が電文と合わない。</exception>
    public static IReadOnlyList<(string Text, TimeSpan GapBefore)> Create(string text, CustomOptions options)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(options);
        if (text.Length == 0) throw new CustomUsageException("投入する電文が空");

        if (options.SplitPositions is not { } split)
        {
            return OsdSimulator.Fragment(text, options.Seed, options.ChunkRange.Min, options.ChunkRange.Max,
                                         TimeSpan.FromMilliseconds(options.GapRange.Min), TimeSpan.FromMilliseconds(options.GapRange.Max));
        }

        var chunks = Split(text, split);
        IReadOnlyList<int> gaps = options.Gaps ?? RandomGaps(chunks.Count - 1, options);
        if (gaps.Count != chunks.Count - 1)
            throw new CustomUsageException($"--gaps の個数が合わない: {gaps.Count} 個（チャンク {chunks.Count} 個に対して {chunks.Count - 1} 個が必要）");
        return [.. chunks.Select((chunk, i) => (chunk, i == 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(gaps[i - 1])))];
    }

    private static List<string> Split(string text, IReadOnlyList<int> positions)
    {
        var chunks = new List<string>();
        int start = 0;
        foreach (var position in positions)
        {
            if (position <= start || position >= text.Length)
                throw new CustomUsageException($"--split の位置が不正: {position}（{start + 1}～{text.Length - 1} の範囲で昇順。電文は {text.Length} 文字）");
            chunks.Add(text[start..position]);
            start = position;
        }
        chunks.Add(text[start..]);
        return chunks;
    }

    private static List<int> RandomGaps(int count, CustomOptions options)
    {
#pragma warning disable CA5394 // 暗号用途ではない（模擬の間欠を再現可能に決める）
        var random = new Random(options.Seed);
        return [.. Enumerable.Range(0, count).Select(_ => random.Next(options.GapRange.Min, options.GapRange.Max + 1))];
#pragma warning restore CA5394
    }
}
