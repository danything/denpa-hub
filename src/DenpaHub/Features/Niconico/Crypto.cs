using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DenpaHub.Features.Niconico;

/// <summary>
/// state の中身。中継だけが読める (AES-GCM で暗号化し、改ざんも見破る)。
/// 中継はサーバ側に何も持たないので、コールバックで要るものは全部ここに入れて往復させる。
/// </summary>
/// <param name="Return">組み直した戻り先</param>
/// <param name="Key">denpa の公開鍵 (base64url)。トークンはこの鍵に宛てて暗号化する</param>
/// <param name="Verifier">PKCE の code_verifier</param>
/// <param name="Expires">有効期限 (Unix 秒)</param>
/// <param name="Binding">ブラウザに置いた Cookie の値の SHA-256 (base64url)</param>
public sealed record StatePayload(string Return, string Key, string Verifier, long Expires, string Binding);

[JsonSerializable(typeof(StatePayload))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal sealed partial class NiconicoJson : JsonSerializerContext;

/// <summary>state を封じる・開く。形は <c>base64url(1 バイトのバージョン || nonce 12 || 暗号文 || tag 16)</c></summary>
public sealed class StateCodec(byte[] key)
{
    const byte Version = 1;
    const int NonceSize = 12;
    const int TagSize = 16;
    static readonly byte[] Aad = "denpa-hub niconico state"u8.ToArray();

    public string Seal(StatePayload payload)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(payload, NiconicoJson.Default.StatePayload);
        var sealedState = new byte[1 + NonceSize + plain.Length + TagSize];
        sealedState[0] = Version;
        var nonce = sealedState.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, sealedState.AsSpan(1 + NonceSize, plain.Length), sealedState.AsSpan(1 + NonceSize + plain.Length), Aad);
        return Base64Url.EncodeToString(sealedState);
    }

    /// <summary>読めない・改ざんされた・別の鍵のものは null</summary>
    public StatePayload? Open(string? state)
    {
        if (string.IsNullOrEmpty(state) || state.Length > 4096) return null;
        try
        {
            var data = Base64Url.DecodeFromChars(state);
            if (data.Length < 1 + NonceSize + TagSize || data[0] != Version) return null;
            var plainLength = data.Length - 1 - NonceSize - TagSize;
            var plain = new byte[plainLength];
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(data.AsSpan(1, NonceSize), data.AsSpan(1 + NonceSize, plainLength), data.AsSpan(1 + NonceSize + plainLength), plain, Aad);
            return JsonSerializer.Deserialize(plain, NiconicoJson.Default.StatePayload);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// トークンを denpa の公開鍵に宛てて暗号化する (ECDH P-256 → HKDF-SHA256 → AES-256-GCM)。
/// ブラウザの URL (フラグメント) を通って届くので、履歴や拡張機能に見えても中身が読めないようにする。
/// 開く手順は docs/niconico.md の「トークンの渡し方」。
/// </summary>
public static class TokenSeal
{
    /// <summary>非圧縮の P-256 公開鍵 (0x04 || X 32 || Y 32)。WebCrypto の exportKey("raw") と同じ形</summary>
    public const int PublicKeyLength = 65;
    const int NonceSize = 12;
    const int TagSize = 16;
    public static readonly byte[] Info = "denpa-hub niconico token v1"u8.ToArray();

    /// <summary>base64url の公開鍵を読み、曲線の上の点かまで確かめる</summary>
    public static bool TryParsePublicKey(string? value, out byte[] key)
    {
        key = [];
        if (string.IsNullOrEmpty(value) || value.Length > 128) return false;
        try
        {
            var raw = Base64Url.DecodeFromChars(value);
            if (raw.Length != PublicKeyLength || raw[0] != 0x04) return false;
            using var _ = ECDiffieHellman.Create(ToParameters(raw));
            key = raw;
            return true;
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return false;
        }
    }

    /// <summary>形は <c>base64url(使い捨ての公開鍵 65 || nonce 12 || 暗号文 || tag 16)</c>。AAD は戻り先の URL</summary>
    public static string Seal(byte[] recipient, ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> aad)
    {
        using var peer = ECDiffieHellman.Create(ToParameters(recipient));
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var ephemeralKey = ToRaw(ephemeral.ExportParameters(false));
        var key = DeriveKey(ephemeral.DeriveRawSecretAgreement(peer.PublicKey), ephemeralKey, recipient);

        var output = new byte[PublicKeyLength + NonceSize + plaintext.Length + TagSize];
        ephemeralKey.CopyTo(output, 0);
        var nonce = output.AsSpan(PublicKeyLength, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plaintext, output.AsSpan(PublicKeyLength + NonceSize, plaintext.Length), output.AsSpan(PublicKeyLength + NonceSize + plaintext.Length), aad);
        return Base64Url.EncodeToString(output);
    }

    /// <summary>鍵は HKDF-SHA256 (salt = 使い捨ての公開鍵 || denpa の公開鍵、info = "denpa-hub niconico token v1") で 32 バイト</summary>
    internal static byte[] DeriveKey(byte[] sharedSecret, byte[] ephemeralKey, byte[] recipient) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, [.. ephemeralKey, .. recipient], Info);

    internal static ECParameters ToParameters(byte[] raw) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = raw[1..33], Y = raw[33..65] },
    };

    internal static byte[] ToRaw(ECParameters parameters) => [0x04, .. parameters.Q.X!, .. parameters.Q.Y!];
}
