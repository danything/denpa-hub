using System.Text.Encodings.Web;
using System.Text.Json;

namespace DenpaHub.Features.Jikkyo;

/// <summary>コメントの出どころ。配るときの <c>src</c></summary>
public enum Source
{
    /// <summary>hub の独自コメント</summary>
    Hub,
    /// <summary>NX-Jikkyo に書かれたもの</summary>
    Nx,
    /// <summary>NX-Jikkyo が公式のニコニコ実況から取り込んだもの (<c>user_id</c> が <c>nicolive:</c> で始まる)</summary>
    Nicolive,
}

/// <summary>
/// 配るコメント 1 件。hub → denpa → 端末で同じ形 (docs/jikkyo.md の「コメントの形」)。
/// NX-Jikkyo や過去ログの <c>user_id</c> などは読んだその場で捨て、持つのはこれだけ
/// </summary>
/// <param name="At">投稿時刻 (unix ms)</param>
/// <param name="Mail">コマンド。無ければ null</param>
/// <param name="Id">独自コメントの ID。ほかは null</param>
public sealed record Comment(long At, string Text, string? Mail, Source Src, string? Id)
{
    /// <summary>同じ時刻・同じ本文のものを 1 つにするキー (繋ぎ直しで同じものが 2 度来る)</summary>
    public (long, string) Key => (At, Text);

    static readonly JsonWriterOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Write(Utf8JsonWriter json)
    {
        json.WriteStartObject();
        json.WriteNumber("at", At);
        json.WriteString("text", Text);
        if (!string.IsNullOrEmpty(Mail)) json.WriteString("mail", Mail);
        json.WriteString("src", Src switch { Source.Hub => "hub", Source.Nicolive => "nicolive", _ => "nx" });
        if (Id is not null) json.WriteString("id", Id);
        json.WriteEndObject();
    }

    /// <summary>コメントの並びを JSON の配列に</summary>
    public static byte[] ToJson(IEnumerable<Comment> comments)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, Options))
        {
            json.WriteStartArray();
            foreach (var comment in comments) comment.Write(json);
            json.WriteEndArray();
        }
        return stream.ToArray();
    }

    /// <summary>
    /// NX-Jikkyo の WebSocket と過去ログ API の <c>chat</c> を読む (値は数のことも文字列のこともある)。
    /// 流さないもの (本文が空・<c>/nicoad</c> などの運営のコメント) と、形が違って読めないものは null。
    /// <paramref name="userId"/> は二重にしないために返すだけで、持たない
    /// </summary>
    public static Comment? FromChat(JsonElement chat, out string? userId, out bool odd)
    {
        userId = null;
        odd = false;
        if (chat.ValueKind != JsonValueKind.Object) { odd = true; return null; }
        if (!TryNumber(chat, "date", out var date) || !chat.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String)
        {
            odd = true;
            return null;
        }
        long usec = 0;
        if (chat.TryGetProperty("date_usec", out _) && !TryNumber(chat, "date_usec", out usec)) { odd = true; return null; }
        if (chat.TryGetProperty("user_id", out var user) && user.ValueKind == JsonValueKind.String) userId = user.GetString();
        var text = content.GetString()!.Trim();
        if (text.Length == 0 || IsOperator(text)) return null;
        string? mail = chat.TryGetProperty("mail", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString()!.Trim() : null;
        var src = userId?.StartsWith("nicolive:", StringComparison.Ordinal) == true ? Source.Nicolive : Source.Nx;
        return new Comment(date * 1000 + usec / 1000, text, string.IsNullOrEmpty(mail) ? null : mail, src, null);
    }

    /// <summary><c>/</c> と英小文字で始まるもの (運営のコメントの形)</summary>
    public static bool IsOperator(string text) => text.Length >= 2 && text[0] == '/' && text[1] is >= 'a' and <= 'z';

    static bool TryNumber(JsonElement e, string name, out long value)
    {
        value = 0;
        if (!e.TryGetProperty(name, out var v)) return false;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out value);
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), out value);
    }
}
