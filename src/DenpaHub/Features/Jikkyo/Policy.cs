using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DenpaHub.Features.Jikkyo;

/// <summary>方針のファイルの形が違う。メッセージには欄の名前だけを入れる</summary>
public sealed class PolicyException(string message) : Exception(message);

/// <summary>
/// **運営の方針** (<c>JIKKYO_POLICY</c> の JSON)。運営者が決めることはここに置き、コードには書かない。欄の意味は docs/jikkyo.md の「運営の方針」
/// </summary>
public sealed record Policy(
    int RetentionDays,
    int MaxLength,
    int PostGapSeconds,
    bool RelayToNx,
    int MaxAuthorsPerHome,
    IReadOnlySet<string> Channels,
    IReadOnlyList<string> NgWords,
    IReadOnlySet<string> BlockedHomes,
    IReadOnlySet<string> BlockedAuthors,
    IReadOnlySet<string> RemovedComments)
{
    /// <summary>語の正規化を済ませた NG ワード</summary>
    readonly string[] ng = [.. NgWords.Select(Fold).Where(w => w.Length > 0)];

    public static Policy Parse(ReadOnlySpan<byte> json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json.ToArray());
        }
        catch (JsonException)
        {
            throw new PolicyException("JSON として読めません");
        }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new PolicyException("中身はオブジェクトにしてください");
            var retention = Int(root, "retentionDays", 1, 3650);
            var maxLength = Int(root, "maxLength", 1, 1000);
            var gap = Int(root, "postGapSeconds", 1, 3600);
            if (!root.TryGetProperty("relayToNx", out var relay) || relay.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new PolicyException("relayToNx は true か false にしてください");
            return new Policy(
                retention,
                maxLength,
                gap,
                relay.GetBoolean(),
                Int(root, "maxAuthorsPerHome", 1, 1000),
                Strings(root, "channels").ToHashSet(StringComparer.Ordinal),
                Strings(root, "ngWords"),
                Strings(root, "blockedHomes").ToHashSet(StringComparer.Ordinal),
                Strings(root, "blockedAuthors").ToHashSet(StringComparer.Ordinal),
                Strings(root, "removedComments").ToHashSet(StringComparer.Ordinal));
        }
    }

    static int Int(JsonElement root, string name, int min, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var n) || n < min || n > max)
            throw new PolicyException($"{name} は {min}〜{max} の整数にしてください");
        return n;
    }

    static List<string> Strings(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new PolicyException($"{name} は文字列の配列にしてください");
        var list = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) throw new PolicyException($"{name} は文字列の配列にしてください");
            list.Add(item.GetString()!);
        }
        return list;
    }

    /// <summary>NG ワードを含むか</summary>
    public bool IsNg(string text)
    {
        if (ng.Length == 0) return false;
        var folded = Fold(text);
        foreach (var word in ng)
            if (folded.Contains(word, StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>
    /// 比べる前にそろえる。英字の大小・全角と半角の英数字と記号・ひらがなとカタカナを区別しない。
    /// ICU を使わない (本番は InvariantGlobalization) ので、文字の表で寄せる
    /// </summary>
    internal static string Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var ch = c;
            // 全角の英数字と記号 (！〜～) を半角に
            if (ch is >= '！' and <= '～') ch = (char)(ch - 0xFEE0);
            else if (ch == '　') ch = ' ';
            // カタカナをひらがなに (ァ〜ヶ)
            else if (ch is >= 'ァ' and <= 'ヶ') ch = (char)(ch - 0x60);
            builder.Append(char.ToLowerInvariant(ch));
        }
        return builder.ToString().Trim();
    }

    /// <summary>書記素の数 (本文の長さの上限は書記素で数える。denpa の post.ts と同じ)</summary>
    public static int Graphemes(string text) => new StringInfo(text).LengthInTextElements;
}

/// <summary>
/// いまの方針。ファイルの更新の時刻を 30 秒ごとに見て、変わっていたら読み直す (<see cref="JikkyoWorker"/>)。
/// 起動のときに読めなければ設定の誤りにする。動いている間に読めなくなったら、前のものを使い続ける
/// </summary>
public sealed class PolicyHolder
{
    readonly string path;
    DateTime stamp;

    public Policy Current { get; private set; }

    public PolicyHolder(JikkyoSettings settings)
    {
        path = settings.PolicyPath;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path);
            Current = Policy.Parse(File.ReadAllBytes(path));
        }
        catch (PolicyException e)
        {
            throw new SettingsException($"JIKKYO_POLICY の中身が違います ({e.Message})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"JIKKYO_POLICY のファイルを読めません ({e.GetType().Name})");
        }
    }

    /// <summary>変わっていたら読み直す。読み直したら新しい方針、変わっていなければ null。読めなければ例外 (前のものは残す)</summary>
    public Policy? Reload()
    {
        var now = File.GetLastWriteTimeUtc(path);
        if (now == stamp) return null;
        var next = Policy.Parse(File.ReadAllBytes(path));
        stamp = now;
        Current = next;
        return next;
    }
}
