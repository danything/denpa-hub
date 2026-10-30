using System.Net;
using System.Net.Sockets;

namespace DenpaHub.Features.Niconico;

/// <summary>戻り先の検査の結果。<see cref="Url"/> は組み直した形 (これだけを使い、受け取った文字列は使わない)</summary>
public sealed record ReturnCheck(string? Url, string? Kind, string? Error);

/// <summary>
/// 戻り先 (<c>return</c>) の検査。トークンを渡す先なので、任意の URL へ飛ばせると
/// 他人のトークンを外へ持ち出せてしまう (オープンリダイレクト)。
/// 通すのは「denpa の決まったパス」かつ「インターネットから受け取れないホスト」だけ
/// (docs/niconico.md の「戻り先の検査」)。
/// </summary>
public static class ReturnTarget
{
    /// <summary>denpa 側で連携の結果を受け取る画面のパス (denpa との契約)</summary>
    public const string Path = "/settings/integrations/niconico/return";

    static readonly IPNetwork[] PrivateNetworks =
    [
        IPNetwork.Parse("127.0.0.0/8"), // ループバック
        IPNetwork.Parse("10.0.0.0/8"), // RFC 1918
        IPNetwork.Parse("172.16.0.0/12"),
        IPNetwork.Parse("192.168.0.0/16"),
        IPNetwork.Parse("100.64.0.0/10"), // RFC 6598 (Tailscale などの VPN のアドレスもここ)
        IPNetwork.Parse("::1/128"),
        IPNetwork.Parse("fc00::/7"), // IPv6 のユニークローカル
    ];

    /// <summary>家の中でだけ名前が引ける接尾辞 (mDNS と RFC 8375)</summary>
    static readonly string[] LocalSuffixes = [".local", ".home.arpa"];

    public static ReturnCheck Check(string? raw, IReadOnlySet<string> allowedHosts)
    {
        if (string.IsNullOrEmpty(raw)) return Fail("return がありません");
        // Uri は \ を / と読み替えるなど寛容なので、紛らわしい文字は先に断る
        if (raw.Length > 512 || raw.Any(c => c <= ' ' || c == '\\' || c >= 0x7f))
            return Fail("return に使えない文字があります");
        if (!Uri.TryCreate(raw, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps))
            return Fail("return は http か https の URL にしてください");
        if (url.UserInfo.Length > 0 || url.Query.Length > 0 || url.Fragment.Length > 0)
            return Fail("return にユーザー名・クエリ・フラグメントは付けられません");
        if (url.AbsolutePath != Path)
            return Fail($"return のパスは {Path} にしてください");

        var kind = HostKind(url, allowedHosts);
        if (kind is null)
            return Fail("return のホストは LAN のアドレスか localhost にしてください");
        return new ReturnCheck(url.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped), kind, null);
    }

    static string? HostKind(Uri url, IReadOnlySet<string> allowedHosts)
    {
        if (url.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(url.DnsSafeHost, out var ip)) return null;
            if (ip.AddressFamily == AddressFamily.InterNetworkV6 && ip.ScopeId != 0) return null;
            if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
            var family = ip.AddressFamily;
            foreach (var network in PrivateNetworks)
                if (network.BaseAddress.AddressFamily == family && network.Contains(ip))
                    return IPAddress.IsLoopback(ip) ? "loopback" : "private";
            return null;
        }
        if (url.HostNameType != UriHostNameType.Dns) return null;
        var host = url.IdnHost.ToLowerInvariant();
        if (host == "localhost") return "loopback";
        foreach (var suffix in LocalSuffixes)
            if (host.EndsWith(suffix, StringComparison.Ordinal) && host.Length > suffix.Length)
                return "local-name";
        // 名前で届くホストは、置く人が設定に書いたものを https だけ通す
        if (url.Scheme == Uri.UriSchemeHttps && allowedHosts.Contains(host)) return "allowed-host";
        return null;
    }

    static ReturnCheck Fail(string error) => new(null, null, error);
}
