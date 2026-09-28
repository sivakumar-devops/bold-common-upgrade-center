using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;

namespace Bold.UpgradeCenter.Services;

public interface IBoldConnectionStringDecryptor
{
    TokenCryptoServiceKeyMaterial GetTokenKeyMaterial(string machineKeyDecryptionKey, string encryptedPrivateKey);

    string DecryptConnectionString(string encryptedConnectionString, TokenCryptoServiceKeyMaterial keyMaterial);
}

public sealed record TokenCryptoServiceKeyMaterial(byte[] KeyBytes, byte[] InitVectorBytes);

public sealed class BoldConnectionStringDecryptor : IBoldConnectionStringDecryptor
{
    public TokenCryptoServiceKeyMaterial GetTokenKeyMaterial(string machineKeyDecryptionKey, string encryptedPrivateKey)
    {
        if (string.IsNullOrWhiteSpace(machineKeyDecryptionKey))
        {
            throw new InvalidOperationException("MachineKey decryption key is not available.");
        }

        if (string.IsNullOrWhiteSpace(encryptedPrivateKey))
        {
            throw new InvalidOperationException("Encrypted private key content is not available.");
        }

        var protectionKey = CreatePrivateKeyProtectionKey(machineKeyDecryptionKey);
        var encryptedPrivateKeyLine = ReadFirstNonEmptyLine(encryptedPrivateKey);
        var privateKeyXml = DecryptProtectedPrivateKey(encryptedPrivateKeyLine, protectionKey);
        var tokenKeys = ExtractTokenCryptoServiceKeys(privateKeyXml);

        return tokenKeys;
    }

    public string DecryptConnectionString(string encryptedConnectionString, TokenCryptoServiceKeyMaterial keyMaterial)
    {
        if (string.IsNullOrWhiteSpace(encryptedConnectionString))
        {
            return string.Empty;
        }

        if (keyMaterial.KeyBytes.Length is not (16 or 24 or 32))
        {
            throw new InvalidOperationException($"Unsupported token key length: {keyMaterial.KeyBytes.Length} bytes.");
        }

        if (keyMaterial.InitVectorBytes.Length != 16)
        {
            throw new InvalidOperationException("Token crypto service key material is not valid.");
        }

        var cipherBytes = Convert.FromBase64String(encryptedConnectionString);
        using var aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        aes.Key = keyMaterial.KeyBytes;
        aes.IV = keyMaterial.InitVectorBytes;

        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var memoryStream = new MemoryStream(cipherBytes);
        using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string CreatePrivateKeyProtectionKey(string machineKeyDecryptionKey)
    {
        var protectionKey = machineKeyDecryptionKey;
        var flag = 1;
        while (flag < 3)
        {
            protectionKey += EncryptWithDerivedAesKey(flag == 1 ? machineKeyDecryptionKey : protectionKey, machineKeyDecryptionKey);
            flag++;
        }

        return protectionKey;
    }

    private static string DecryptProtectedPrivateKey(string encryptedPrivateKey, string protectionKey)
    {
        var cipherBytes = Convert.FromBase64String(encryptedPrivateKey);
        using var aes = CreatePrivateKeyAes(protectionKey);
        using var decryptor = aes.CreateDecryptor(aes.Key, aes.IV);
        using var memoryStream = new MemoryStream(cipherBytes);
        using var cryptoStream = new CryptoStream(memoryStream, decryptor, CryptoStreamMode.Read);
        using var reader = new StreamReader(cryptoStream);
        return reader.ReadToEnd();
    }

    private static string ReadFirstNonEmptyLine(string value)
    {
        using var reader = new StringReader(value);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (!string.IsNullOrWhiteSpace(trimmed))
            {
                return trimmed;
            }
        }

        throw new InvalidOperationException("Encrypted private key content is empty.");
    }

    private static string EncryptWithDerivedAesKey(string key, string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        using var aes = CreatePrivateKeyAes(key);
        using var encryptor = aes.CreateEncryptor(aes.Key, aes.IV);
        using var memoryStream = new MemoryStream();
        using (var cryptoStream = new CryptoStream(memoryStream, encryptor, CryptoStreamMode.Write))
        {
            cryptoStream.Write(plainBytes, 0, plainBytes.Length);
            cryptoStream.FlushFinalBlock();
        }

        return Convert.ToBase64String(memoryStream.ToArray());
    }

    private static Aes CreatePrivateKeyAes(string key)
    {
        var aes = Aes.Create();
        using var md5 = MD5.Create();
        aes.Key = md5.ComputeHash(Encoding.Unicode.GetBytes(key));
        aes.IV = new byte[aes.BlockSize / 8];
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        return aes;
    }

    private static TokenCryptoServiceKeyMaterial ExtractTokenCryptoServiceKeys(string privateKeyXml)
    {
        var document = XDocument.Parse(NormalizeXmlDeclaration(privateKeyXml));
        var tokenKeysElement = document
            .Descendants()
            .FirstOrDefault(element => element.Name.LocalName.Equals("TokenCryptoServiceKeys", StringComparison.Ordinal));

        var keyBytesBase64 = tokenKeysElement?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals("KeyBytes", StringComparison.Ordinal))
            ?.Value
            .Trim();

        var initVectorBytesBase64 = tokenKeysElement?
            .Elements()
            .FirstOrDefault(element => element.Name.LocalName.Equals("InitVectorBytes", StringComparison.Ordinal))
            ?.Value
            .Trim();

        if (string.IsNullOrWhiteSpace(keyBytesBase64) || string.IsNullOrWhiteSpace(initVectorBytesBase64))
        {
            throw new InvalidOperationException("Token crypto service key material is not available.");
        }

        return new TokenCryptoServiceKeyMaterial(
            Convert.FromBase64String(keyBytesBase64),
            Convert.FromBase64String(initVectorBytesBase64));
    }

    private static string NormalizeXmlDeclaration(string xml)
    {
        return xml
            .Replace("encoding=\"utf-16\"", "encoding=\"utf-8\"", StringComparison.OrdinalIgnoreCase)
            .Replace("encoding='utf-16'", "encoding='utf-8'", StringComparison.OrdinalIgnoreCase);
    }
}
