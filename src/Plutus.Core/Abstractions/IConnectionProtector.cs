namespace Plutus.Core.Abstractions;

/// <summary>
/// Encrypts/decrypts the SimpleFIN access URL at rest. Backed by ASP.NET Data
/// Protection so the key ring (and thus the ability to decrypt) is owned by the host.
/// </summary>
public interface IConnectionProtector
{
    string Protect(string plaintext);

    string Unprotect(string ciphertext);
}
