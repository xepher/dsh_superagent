using DshSuperagent.Core.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace DshSuperagent.Web.Services;

public sealed class DataProtectionSecretProtector : ISecretProtector
{
    private const string Prefix = "dp:v1:";
    private readonly IDataProtector _protector;

    public DataProtectionSecretProtector(IDataProtectionProvider provider)
    {
        _protector = provider.CreateProtector("DshSuperagent.Secrets.v1");
    }

    public string Protect(string plaintext) => Prefix + _protector.Protect(plaintext);

    public string Unprotect(string protectedText)
    {
        if (!protectedText.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Allows an operator to migrate a hand-written config row; new writes are always protected.
            return protectedText;
        }

        return _protector.Unprotect(protectedText[Prefix.Length..]);
    }
}
