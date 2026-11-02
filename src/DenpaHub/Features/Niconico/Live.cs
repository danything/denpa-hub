using System.Text.Json;

namespace DenpaHub.Features.Niconico;

/// <summary>
/// 実況の視聴の接続先を引く設定。名前と意味は docs/niconico.md の「設定」。
/// 上流のパス・応答の形はコードに書かず、ここから渡す。
/// </summary>
/// <param name="Channels">局 (jk の ID) → ニコニコのチャンネルの ID</param>
/// <param name="ProgramUrl">放送中の番組を引く URL の型 (<c>{channel}</c>)</param>
/// <param name="ProgramField">その応答の番組の ID の場所 (JSON Pointer)</param>
/// <param name="WatchUrl">視聴の接続先を引く URL の型 (<c>{program}</c>・<c>{user}</c>)</param>
/// <param name="WatchField">その応答の接続先の場所 (JSON Pointer)。無ければ応答から wss の URL を探す</param>
public sealed record LiveSettings(
    IReadOnlyDictionary<string, string> Channels,
    string ProgramUrl,
    JsonPointer ProgramField,
    string WatchUrl,
    JsonPointer? WatchField)
{
    public static LiveSettings Load(Env env)
    {
        var channels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in env.GetList("NICONICO_LIVE_CHANNELS"))
        {
            var pair = entry.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pair.Length != 2 || !LiveFeature.IsId(pair[0]) || !LiveFeature.IsId(pair[1]) || !channels.TryAdd(pair[0], pair[1]))
                throw new SettingsException("NICONICO_LIVE_CHANNELS は <局>=<チャンネル> をカンマ区切りで書いてください (同じ局は 1 度だけ)");
        }
        if (channels.Count == 0) throw new SettingsException("NICONICO_LIVE_CHANNELS が設定されていません");

        return new LiveSettings(
            Channels: channels,
            ProgramUrl: env.RequireUrlTemplate("NICONICO_LIVE_PROGRAM_URL", "channel"),
            ProgramField: JsonPointer.Parse(env.Require("NICONICO_LIVE_PROGRAM_FIELD"), "NICONICO_LIVE_PROGRAM_FIELD"),
            WatchUrl: env.RequireUrlTemplate("NICONICO_LIVE_WATCH_URL", "program", "user"),
            WatchField: env.Get("NICONICO_LIVE_WATCH_FIELD") is { } field ? JsonPointer.Parse(field, "NICONICO_LIVE_WATCH_FIELD") : null);
    }
}

/// <summary>JSON の中の場所 (RFC 6901)。応答の形を設定で渡すために使う</summary>
public sealed record JsonPointer(IReadOnlyList<string> Tokens)
{
    public static JsonPointer Parse(string value, string name)
    {
        if (!value.StartsWith('/') || value.Length == 1)
            throw new SettingsException($"{name} は /a/b のような JSON Pointer で書いてください");
        return new JsonPointer(value[1..].Split('/').Select(t => t.Replace("~1", "/").Replace("~0", "~")).ToArray());
    }

    /// <summary>場所の値を引く。無ければ (形がずれていれば) false</summary>
    public bool TryResolve(JsonElement root, out JsonElement value)
    {
        value = root;
        foreach (var token in Tokens)
        {
            if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(token, out var child)) value = child;
            else if (value.ValueKind == JsonValueKind.Array && int.TryParse(token, out var index) && index >= 0 && index < value.GetArrayLength()) value = value[index];
            else return false;
        }
        return true;
    }

    /// <summary>形がずれたときの次善。最後の名前と同じ名前のプロパティを、応答の中から深さ優先で探す</summary>
    public bool TryFindByName(JsonElement root, out JsonElement value) => LiveFeature.TryFind(root, e =>
    {
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(Tokens[^1], out var found)) return found;
        return null;
    }, out value);
}

/// <summary>
/// 実況の視聴の接続先の中継。denpa のサーバが利用者のアクセストークンで呼び、
/// 局 → ニコニコのチャンネル (設定の対応表) → 放送中の番組 → 視聴の WebSocket の接続先、の順に引いて返す。
/// 何も保存しない。契約は docs/niconico.md
/// </summary>
public static class LiveFeature
{
    public const string WatchPath = "/niconico/live/watch";

    /// <summary>局・チャンネル・利用者・番組の ID に通す文字 (URL に差し込むので、英数字と - _ . だけ)</summary>
    public static bool IsId(string value) =>
        value.Length is > 0 and <= 64 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static async Task Watch(HttpContext context)
    {
        var settings = context.RequestServices.GetRequiredService<NiconicoSettings>().Live;
        var log = NiconicoFeature.Log(context);

        var token = BearerToken(context.Request.Headers.Authorization.ToString());
        if (token is null)
        {
            log.LogInformation("live watch rejected: token");
            await NiconicoFeature.Json(context, StatusCodes.Status401Unauthorized, "invalid_token");
            return;
        }
        var form = await NiconicoFeature.ReadForm(context);
        var user = form?["user"].ToString() ?? "";
        if (form is null || !IsId(user))
        {
            log.LogInformation("live watch rejected: request");
            await NiconicoFeature.Json(context, StatusCodes.Status400BadRequest, "invalid_request");
            return;
        }
        if (!settings.Channels.TryGetValue(form["channel"].ToString(), out var channel))
        {
            log.LogInformation("live watch rejected: unknown channel");
            await NiconicoFeature.Json(context, StatusCodes.Status400BadRequest, "unknown_channel");
            return;
        }

        var upstream = context.RequestServices.GetRequiredService<Upstream>();
        try
        {
            // 1. 放送中の番組
            var program = await upstream.GetAsync(Fill(settings.ProgramUrl, ("channel", channel)), token, context.RequestAborted);
            if (!IsSuccess(program))
            {
                await Fail(context, "program", program.Status);
                return;
            }
            var programId = Read(program.Body, settings.ProgramField, null, log, "program", out var programElement)
                ? Scalar(programElement)
                : null;
            if (programId is null || !IsId(programId))
            {
                log.LogWarning("live watch: program response without a usable program id (version skew?)");
                await NiconicoFeature.Json(context, StatusCodes.Status502BadGateway, "temporarily_unavailable");
                return;
            }

            // 2. 視聴の WebSocket の接続先
            var watch = await upstream.GetAsync(Fill(settings.WatchUrl, ("program", programId), ("user", user)), token, context.RequestAborted);
            if (!IsSuccess(watch))
            {
                await Fail(context, "watch", watch.Status);
                return;
            }
            var url = Read(watch.Body, settings.WatchField, IsWebSocketUrl, log, "watch", out var urlElement) && IsWebSocketUrl(urlElement)
                ? urlElement.GetString()
                : null;
            if (url is null)
            {
                log.LogWarning("live watch: watch response without a wss url (version skew?)");
                await NiconicoFeature.Json(context, StatusCodes.Status502BadGateway, "temporarily_unavailable");
                return;
            }

            log.LogInformation("live watch: delivered");
            await Url(context, url);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            log.LogWarning("live watch: upstream unreachable ({Type})", e.GetType().Name);
            await NiconicoFeature.Json(context, StatusCodes.Status502BadGateway, "temporarily_unavailable");
        }
    }

    static bool IsSuccess(UpstreamResponse response) => (int)response.Status is >= 200 and < 300;

    /// <summary>上流の失敗を契約の名前に置き換える。状態コードだけで決め、本文は読まない</summary>
    static Task Fail(HttpContext context, string step, System.Net.HttpStatusCode status)
    {
        var (code, error) = (int)status switch
        {
            401 => (StatusCodes.Status401Unauthorized, "invalid_token"),
            403 => (StatusCodes.Status403Forbidden, "insufficient_scope"),
            404 => (StatusCodes.Status404NotFound, "not_on_air"),
            429 => (StatusCodes.Status429TooManyRequests, "temporarily_unavailable"),
            _ => (StatusCodes.Status502BadGateway, "temporarily_unavailable"),
        };
        var log = NiconicoFeature.Log(context);
        if (code == StatusCodes.Status502BadGateway) log.LogWarning("live watch: {Step} upstream {Status}", step, (int)status);
        else log.LogInformation("live watch: {Step} upstream {Status}", step, (int)status);
        return NiconicoFeature.Json(context, code, error);
    }

    /// <summary>
    /// 応答の JSON から値を読む。決めた場所に無ければ警告し、同じ名前のプロパティ、それも無ければ <paramref name="fallback"/> に合う値を探す
    /// (上流の形が少しずれても、読める所を読んで進む)
    /// </summary>
    static bool Read(byte[] body, JsonPointer? pointer, Func<JsonElement, bool>? fallback, ILogger log, string step, out JsonElement value)
    {
        value = default;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            JsonElement found = default;
            var ok = pointer is not null && pointer.TryResolve(root, out found);
            if (!ok)
            {
                if (pointer is not null) log.LogWarning("live watch: {Step} response is not in the configured shape (version skew?)", step);
                ok = (pointer is not null && pointer.TryFindByName(root, out found))
                    || (fallback is not null && TryFind(root, e => fallback(e) ? e : null, out found));
            }
            // JsonDocument を閉じたあとも使えるように写す
            if (ok) value = found.Clone();
            return ok;
        }
        catch (JsonException)
        {
            log.LogWarning("live watch: {Step} response is not JSON", step);
            return false;
        }
    }

    /// <summary>深さ優先で、<paramref name="match"/> が値を返す最初の所を探す</summary>
    internal static bool TryFind(JsonElement element, Func<JsonElement, JsonElement?> match, out JsonElement value)
    {
        if (match(element) is { } hit)
        {
            value = hit;
            return true;
        }
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
                if (TryFind(property.Value, match, out value)) return true;
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (TryFind(item, match, out value)) return true;
        }
        value = default;
        return false;
    }

    static string? Scalar(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.GetRawText(),
        _ => null,
    };

    static bool IsWebSocketUrl(JsonElement element) =>
        element.ValueKind == JsonValueKind.String
        && Uri.TryCreate(element.GetString(), UriKind.Absolute, out var url)
        && url.Scheme == "wss";

    static Uri Fill(string template, params (string Name, string Value)[] values)
    {
        foreach (var (name, value) in values)
            template = template.Replace($"{{{name}}}", Uri.EscapeDataString(value), StringComparison.Ordinal);
        return new Uri(template);
    }

    /// <summary>Authorization: Bearer の値。空白・制御文字を含むもの、長すぎるものは受けない</summary>
    static string? BearerToken(string header)
    {
        const string prefix = "Bearer ";
        if (!header.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = header[prefix.Length..].Trim();
        return token.Length is > 0 and <= 4096 && token.All(c => c is > ' ' and <= '~') ? token : null;
    }

    static async Task Url(HttpContext context, string url)
    {
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = "application/json";
        await using var writer = new Utf8JsonWriter(context.Response.Body);
        writer.WriteStartObject();
        writer.WriteString("url", url);
        writer.WriteEndObject();
        await writer.FlushAsync(context.RequestAborted);
    }
}
