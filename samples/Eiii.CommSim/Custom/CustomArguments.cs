using System.Globalization;

namespace Eiii.CommSim;

/// <summary><c>custom</c> モードの引数を解析する（ファイルの読み込みなどの入出力はしない純粋な処理）。</summary>
public static class CustomArguments
{
    /// <summary>使い方。</summary>
    public const string Usage = """
        使い方: dotnet run --project samples/Eiii.CommSim -- custom (--text <電文> | --file <パス>) [オプション]

        入力（どちらか一方）
          --text <電文>         エスケープ表記を解釈する: \r \n \t \\ \xHH
          --file <パス>         ファイルの内容をそのまま使う（UTF-8。BOM があれば BOM に従う。CR/LF は変換しない）

        比較する方式
          --mode <方式>         all（既定）| delimiter | idle | fixed

        分割と間欠（指定しなければ乱数。--seed で再現できる）
          --chunk-range <a-b>   乱数で分割するときのチャンク長（既定 8-40）
          --gap-range <a-b>     乱数で決めるときの間欠 ms（既定 10-100）
          --split <p1,p2,...>   分割位置を明示する（文字の位置。昇順。0 と末尾は不可）
          --gaps <g1,g2,...>    2 番目以降の各チャンクの前の間欠 ms を明示する（--split が必要。個数 = チャンク数 - 1）
          --seed <n>            乱数の種（既定 1）

        方式の条件
          --delimiter <区切り>  デリミタ方式の区切り文字（エスケープ表記。既定 \r）
          --idle <ms>           Idle 方式の無通信時間（既定 250）
          --collection <ms>     Fixed 方式の収集時間（既定 2000）
          --max <文字数>        時間方式の MaxFrameLength（既定 1024）
          --tick <ms>           Tick の周期（既定 25）

        その他
          --verbose             チャンクの一覧も表示する
          --help                この使い方を表示する
        """;

    /// <summary>引数を解析する。</summary>
    /// <param name="args"><c>custom</c> より後の引数。</param>
    /// <returns>設定。</returns>
    /// <exception cref="CustomUsageException">引数の誤り。</exception>
    public static CustomOptions Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var options = new CustomOptions();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < args.Count; i++)
        {
            var name = args[i];
            if (name is "--help" or "-h") return options with { ShowHelp = true };
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new CustomUsageException($"オプションではない引数: {name}");
            if (!seen.Add(name)) throw new CustomUsageException($"{name} が重複している");
            if (name == "--verbose")
            {
                options = options with { Verbose = true };
                continue;
            }
            if (i + 1 >= args.Count) throw new CustomUsageException($"{name} の値がない");
            var value = args[++i];
            options = name switch
            {
                "--text" => options with { Text = Unescape(value, name) },
                "--file" => options with { FilePath = value },
                "--mode" => options with { Mode = ParseMode(value) },
                "--delimiter" => options with { Delimiter = NonEmpty(Unescape(value, name), name) },
                "--chunk-range" => options with { ChunkRange = ParseRange(value, name, minimum: 1) },
                "--gap-range" => options with { GapRange = ParseRange(value, name, minimum: 0) },
                "--split" => options with { SplitPositions = ParseList(value, name) },
                "--gaps" => options with { Gaps = ParseList(value, name) },
                "--seed" => options with { Seed = ParseInt(value, name, int.MinValue) },
                "--idle" => options with { IdleMilliseconds = ParseInt(value, name, 1) },
                "--collection" => options with { CollectionMilliseconds = ParseInt(value, name, 1) },
                "--max" => options with { MaxFrameLength = ParseInt(value, name, 1) },
                "--tick" => options with { TickMilliseconds = ParseInt(value, name, 1) },
                _ => throw new CustomUsageException($"不明なオプション: {name}"),
            };
        }

        if ((options.Text is null) == (options.FilePath is null))
            throw new CustomUsageException("--text と --file のどちらか一方を指定する");
        if (options.Gaps is not null && options.SplitPositions is null)
            throw new CustomUsageException("--gaps は --split と組み合わせて使う（チャンク数を確定させるため）");
        return options;
    }

    private static string Unescape(string value, string name)
    {
        try
        {
            return Escapes.Unescape(value);
        }
        catch (FormatException ex)
        {
            throw new CustomUsageException($"{name}: {ex.Message}", ex);
        }
    }

    private static string NonEmpty(string value, string name)
        => value.Length > 0 ? value : throw new CustomUsageException($"{name} は空にできない");

    private static CompareMode ParseMode(string value) => value switch
    {
        "all" => CompareMode.All,
        "delimiter" => CompareMode.Delimiter,
        "idle" => CompareMode.Idle,
        "fixed" => CompareMode.Fixed,
        _ => throw new CustomUsageException($"--mode の値が不正: {value}（all / delimiter / idle / fixed）"),
    };

    private static int ParseInt(string value, string name, int minimum)
    {
        if (!int.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number) || number < minimum)
            throw new CustomUsageException($"{name} の値が不正: {value}（{minimum} 以上の整数）");
        return number;
    }

    private static IntRange ParseRange(string value, string name, int minimum)
    {
        var parts = value.Split('-');
        if (parts.Length != 2) throw new CustomUsageException($"{name} の値が不正: {value}（例: 8-40）");
        var range = new IntRange(ParseInt(parts[0], name, minimum), ParseInt(parts[1], name, minimum));
        if (range.Min > range.Max) throw new CustomUsageException($"{name} の値が不正: {value}（最小 ≦ 最大）");
        return range;
    }

    private static List<int> ParseList(string value, string name)
        => [.. value.Split(',').Select(part => ParseInt(part.Trim(), name, 0))];
}
