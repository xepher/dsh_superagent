using System.Security.Cryptography;
using DshSuperagent.Providers.WeCom;

namespace DshSuperagent.Tests;

public sealed class WeComCryptoTests
{
    private static string NewAesKey() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=');

    [Fact]
    public void Encrypt_Decrypt_Roundtrip()
    {
        var key = NewAesKey();
        var plaintext = """{"msgid":"1","msgtype":"text","text":{"content":"你好 @机器人"}}""";

        var encrypted = WeComCrypto.Encrypt(key, plaintext, receiveId: "");
        var decrypted = WeComCrypto.Decrypt(key, encrypted, receiveId: "");

        Assert.Equal(plaintext, decrypted);
    }

    [Fact]
    public void Signature_Is_Order_Independent_And_Verifiable()
    {
        var timestamp = "13500001234";
        var nonce = "123412323";
        var encrypt = "ENCRYPTED";
        var signature = WeComCrypto.ComputeSignature("token", timestamp, nonce, encrypt);

        Assert.True(WeComCrypto.VerifySignature("token", timestamp, nonce, encrypt, signature));
        var replacement = signature[^1] == '0' ? '1' : '0';
        Assert.False(WeComCrypto.VerifySignature("token", timestamp, nonce, encrypt, signature[..^1] + replacement));
    }

    [Fact]
    public void Tampered_Ciphertext_Is_Rejected()
    {
        var key = NewAesKey();
        var encrypted = WeComCrypto.Encrypt(key, "hello", "");
        var bytes = Convert.FromBase64String(encrypted);
        bytes[^1] ^= 0x7F;

        Assert.ThrowsAny<CryptographicException>(() => WeComCrypto.Decrypt(key, Convert.ToBase64String(bytes), ""));
    }

    [Fact]
    public void Wrong_ReceiveId_Is_Rejected()
    {
        var key = NewAesKey();
        var encrypted = WeComCrypto.Encrypt(key, "hello", "receive-a");

        Assert.ThrowsAny<CryptographicException>(() => WeComCrypto.Decrypt(key, encrypted, "receive-b"));
    }
}
