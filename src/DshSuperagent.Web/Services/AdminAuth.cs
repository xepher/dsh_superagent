using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;

namespace DshSuperagent.Web.Services;

public sealed class AdminCredentials
{
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly byte[] _passwordHash;

    public AdminCredentials(IConfiguration configuration, IHostEnvironment environment)
    {
        var password = configuration["SUPERAGENT_ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(password))
        {
            password = configuration["Superagent:AdminPassword"];
        }

        if (string.IsNullOrWhiteSpace(password))
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            {
                throw new InvalidOperationException(
                    "生产环境必须通过 SUPERAGENT_ADMIN_PASSWORD 配置管理后台密码。");
            }

            password = "admin123";
        }

        _passwordHash = Derive(password);
        IsDefaultPassword = password == "admin123";
    }

    public bool IsDefaultPassword { get; }

    public bool Verify(string candidate)
    {
        var candidateHash = Derive(candidate);
        return CryptographicOperations.FixedTimeEquals(candidateHash, _passwordHash);
    }

    private byte[] Derive(string password) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            _salt,
            120_000,
            HashAlgorithmName.SHA256,
            32);
}

public sealed class LoginThrottle
{
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.OrdinalIgnoreCase);

    public bool IsBlocked(string key, out TimeSpan retryAfter)
    {
        if (_attempts.TryGetValue(key, out var attempt) && attempt.BlockedUntil > DateTimeOffset.UtcNow)
        {
            retryAfter = attempt.BlockedUntil - DateTimeOffset.UtcNow;
            return true;
        }

        retryAfter = TimeSpan.Zero;
        return false;
    }

    public void Fail(string key)
    {
        _attempts.AddOrUpdate(
            key,
            _ => new Attempt(1, DateTimeOffset.UtcNow.AddMinutes(5)),
            (_, existing) =>
            {
                var count = existing.BlockedUntil > DateTimeOffset.UtcNow ? existing.Count : existing.Count + 1;
                return new Attempt(count, count >= 5 ? DateTimeOffset.UtcNow.AddMinutes(5) : DateTimeOffset.UtcNow);
            });
    }

    public void Success(string key) => _attempts.TryRemove(key, out _);

    private sealed record Attempt(int Count, DateTimeOffset BlockedUntil);
}
