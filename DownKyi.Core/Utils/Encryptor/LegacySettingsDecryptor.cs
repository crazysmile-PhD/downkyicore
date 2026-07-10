using System.Security.Cryptography;
using System.Text;

namespace DownKyi.Core.Utils.Encryptor;

internal static class LegacySettingsDecryptor
{
    internal static string Decrypt(string encryptedSettings, string legacyKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(encryptedSettings);
        ArgumentException.ThrowIfNullOrEmpty(legacyKey);

        var key = Encoding.UTF8.GetBytes(legacyKey);
        if (key.Length != 8)
        {
            throw new ArgumentException("The legacy settings key must encode to exactly 8 bytes.", nameof(legacyKey));
        }

        var encryptedBytes = Convert.FromBase64String(encryptedSettings);

        // Read-only compatibility for settings written by DownKyi <= 1.0.20.
#pragma warning disable CA5351
        using var des = DES.Create();
#pragma warning restore CA5351
        using var decryptor = des.CreateDecryptor(key, key);
        using var input = new MemoryStream(encryptedBytes, writable: false);
        using var cryptoStream = new CryptoStream(input, decryptor, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
