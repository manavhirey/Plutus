using Microsoft.AspNetCore.DataProtection;
using Plutus.Core.Abstractions;

namespace Plutus.Core.Security;

public sealed class DataProtectionConnectionProtector : IConnectionProtector
{
    private readonly IDataProtector _protector;

    public DataProtectionConnectionProtector(IDataProtectionProvider provider)
        => _protector = provider.CreateProtector("Plutus.SimpleFin.AccessUrl.v1");

    public string Protect(string plaintext) => _protector.Protect(plaintext);

    public string Unprotect(string ciphertext) => _protector.Unprotect(ciphertext);
}
