using DenpaHub;
using DenpaHub.Features.Niconico;

namespace DenpaHub.Tests;

public class ReturnTargetTests
{
    static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase) { "denpa.example.jp" };
    const string P = ReturnTarget.Path;

    [Test]
    [Arguments("http://192.168.1.20:3000" + P, "private")]
    [Arguments("http://10.0.0.5" + P, "private")]
    [Arguments("https://172.20.1.1:8443" + P, "private")]
    [Arguments("http://100.101.102.103:3000" + P, "private")]
    [Arguments("http://127.0.0.1:3000" + P, "loopback")]
    [Arguments("http://localhost:3000" + P, "loopback")]
    [Arguments("http://[::1]:3000" + P, "loopback")]
    [Arguments("http://[fd12:3456::1]:3000" + P, "private")]
    [Arguments("http://[::ffff:192.168.0.1]" + P, "private")]
    [Arguments("http://denpa.local:3000" + P, "local-name")]
    [Arguments("http://denpa.home.arpa" + P, "local-name")]
    [Arguments("https://denpa.example.jp" + P, "allowed-host")]
    [Arguments("https://DENPA.example.jp" + P, "allowed-host")]
    public async Task 通す(string url, string kind)
    {
        var result = ReturnTarget.Check(url, Allowed);
        await Assert.That(result.Error).IsNull();
        await Assert.That(result.Kind).IsEqualTo(kind);
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("/settings/integrations/niconico/return")]
    [Arguments("https://evil.example" + P)]
    [Arguments("http://denpa.example.jp" + P)] // 名前で届くホストは https だけ
    [Arguments("http://8.8.8.8" + P)]
    [Arguments("http://172.32.0.1" + P)]
    [Arguments("http://169.254.169.254" + P)]
    [Arguments("http://[fe80::1%25eth0]" + P)]
    [Arguments("http://[2001:db8::1]" + P)]
    [Arguments("http://192.168.1.1.nip.io" + P)]
    [Arguments("http://local" + P)]
    [Arguments("http://.local" + P)]
    [Arguments("javascript://192.168.1.1" + P)]
    [Arguments("ftp://192.168.1.1" + P)]
    [Arguments("http://192.168.1.1/")]
    [Arguments("http://192.168.1.1/other")]
    [Arguments("http://192.168.1.1" + P + "/")]
    [Arguments("http://192.168.1.1" + P + "?next=https://evil.example")]
    [Arguments("http://192.168.1.1" + P + "#x")]
    [Arguments("http://user@192.168.1.1" + P)]
    [Arguments("http://evil.example\\@192.168.1.1" + P)]
    [Arguments("http://192.168.1.1 " + P)]
    public async Task 断る(string? url)
    {
        var result = ReturnTarget.Check(url, Allowed);
        await Assert.That(result.Url).IsNull();
        await Assert.That(result.Error).IsNotNull();
    }

    [Test]
    public async Task 組み直した形を使う()
    {
        var result = ReturnTarget.Check("HTTP://0x7f.0.0.1:80/settings/integrations/niconico/./return", Allowed);
        await Assert.That(result.Url).IsEqualTo("http://127.0.0.1" + P);
    }
}

public class SettingsTests
{
    [Test]
    public async Task シークレットはファイルからも読める()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "from-file\n");
            var env = Harness.Env(new() { ["NICONICO_CLIENT_SECRET"] = null, ["NICONICO_CLIENT_SECRET_FILE"] = file });
            var (_, settings) = Harness.Load(env);
            await Assert.That(settings.ClientSecret).IsEqualTo("from-file");
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Test]
    public async Task リダイレクト_URI_は公開の_origin_から決まる()
    {
        var env = Harness.Env(new() { ["PUBLIC_ORIGIN"] = "https://hub.example.jp/" });
        var (_, settings) = Harness.Load(env);
        await Assert.That(settings.RedirectUri).IsEqualTo("https://hub.example.jp/niconico/callback");
    }

    [Test]
    [Arguments("PUBLIC_ORIGIN", "http://auth.example.jp")]
    [Arguments("PUBLIC_ORIGIN", "https://auth.example.jp/base")]
    [Arguments("NICONICO_STATE_KEY", "c2hvcnQ=")]
    [Arguments("NICONICO_TOKEN_URL", "http://provider.example/token")]
    [Arguments("NICONICO_TOKEN_AUTH", "private_key_jwt")]
    [Arguments("NICONICO_CLIENT_SECRET", null)]
    [Arguments("TRUSTED_PROXIES", "10.0.0.1/99")]
    public async Task 誤った設定は名前だけを言って止める(string name, string? value)
    {
        var env = Harness.Env(new() { [name] = value });
        var error = await Assert.That(() => Harness.Load(env)).Throws<SettingsException>();
        await Assert.That(error!.Message).Contains(name);
        await Assert.That(error.Message).DoesNotContain(FakeProvider.ClientSecret);
        if (value is not null) await Assert.That(error.Message).DoesNotContain(value);
    }
}
