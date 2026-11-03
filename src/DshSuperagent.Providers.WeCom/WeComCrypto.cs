using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace DshSuperagent.Providers.WeCom;

/// <summary>
/// WeCom callback encryption. This is provider-specific and intentionally isolated
/// from the agent core; other messaging providers bring their own transport security.
/// </summary>
public static class WeComCrypto
{
    public static string ComputeSignature(string token, string timestamp, string nonce, string encrypt)
    {
        var values = new[] { token, timestamp, nonce, encrypt }.Order(StringComparer.Ordinal);
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(string.Concat(values)));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool VerifySignature(string token, string? timestamp, string? nonce, string? encrypt, string? signature)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(encrypt) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var expected = ComputeSignature(token, timestamp, nonce, encrypt);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(signature.Trim().ToLowerInvariant());
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    public static byte[] ParseKey(string encodingAesKey)
    {
        if (string.IsNullOrWhiteSpace(encodingAesKey) || encodingAesKey.Length != 43)
        {
            throw new ArgumentException("EncodingAESKey 必须是 43 个字符。", nameof(encodingAesKey));
        }

        var key = Convert.FromBase64String(encodingAesKey + "=");
        if (key.Length != 32)
        {
            throw new ArgumentException("EncodingAESKey 解码后必须是 32 字节。", nameof(encodingAesKey));
        }

        return key;
    }

    public static string Decrypt(string encodingAesKey, string encrypted, string receiveId = "")
    {
        var key = ParseKey(encodingAesKey);
        byte[] cipher;
        try
        {
            cipher = Convert.FromBase64String(encrypted);
        }
        catch (FormatException ex)
        {
            throw new CryptographicException("回调密文不是合法 Base64。", ex);
        }

        if (cipher.Length == 0 || cipher.Length % 16 != 0)
        {
            throw new CryptographicException("回调密文长度不合法。");
        }

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = key[..16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        byte[] plain;
        using (var decryptor = aes.CreateDecryptor())
        {
            plain = decryptor.TransformFinalBlock(cipher, 0, cipher.Length);
        }

        var pad = plain[^1];
        if (pad is < 1 or > 32 || plain.Length < pad)
        {
            throw new CryptographicException("回调密文填充不合法。");
        }

        for (var index = plain.Length - pad; index < plain.Length; index++)
        {
            if (plain[index] != pad)
            {
                throw new CryptographicException("回调密文填充不合法。");
            }
        }

        var content = plain.AsSpan(0, plain.Length - pad);
        if (content.Length < 20)
        {
            throw new CryptographicException("回调明文长度不合法。");
        }

        var messageLength = BinaryPrimitives.ReadInt32BigEndian(content.Slice(16, 4));
        if (messageLength < 0 || 20 + messageLength > content.Length)
        {
            throw new CryptographicException("回调明文消息长度不合法。");
        }

        var message = Encoding.UTF8.GetString(content.Slice(20, messageLength));
        var actualReceiveId = Encoding.UTF8.GetString(content[(20 + messageLength)..]);
        if (!string.Equals(actualReceiveId, receiveId, StringComparison.Ordinal))
        {
            throw new CryptographicException("回调 ReceiveId 不匹配。");
        }

        return message;
    }

    public static string Encrypt(string encodingAesKey, string plaintext, string receiveId = "")
    {
        var key = ParseKey(encodingAesKey);
        var message = Encoding.UTF8.GetBytes(plaintext);
        var receive = Encoding.UTF8.GetBytes(receiveId);
        var total = 16 + 4 + message.Length + receive.Length;
        var pad = 32 - total % 32;
        var buffer = new byte[total + pad];
        RandomNumberGenerator.Fill(buffer.AsSpan(0, 16));
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(16, 4), message.Length);
        message.CopyTo(buffer.AsSpan(20));
        receive.CopyTo(buffer.AsSpan(20 + message.Length));
        buffer.AsSpan(total).Fill((byte)pad);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = key[..16];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();
        var cipher = encryptor.TransformFinalBlock(buffer, 0, buffer.Length);
        return Convert.ToBase64String(cipher);
    }

    public static string NewTimestamp() =>
        DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);

    public static string NewNonce() => Guid.NewGuid().ToString("N");
}
