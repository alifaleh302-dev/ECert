using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ECert.Services;

/// <summary>
/// Compresses and encrypts a database dump for off-site storage. The AES-256 key is derived from
/// the passphrase configured in <c>/SecuritySettings</c> with PBKDF2; the salt and IV are written
/// as a header in front of the ciphertext so the archive is self-contained and can be decrypted
/// later with the same passphrase. GZip runs first, so the ciphertext leaks nothing about the
/// plaintext structure.
/// </summary>
public sealed class BackupCryptoService
{
    private const int SaltSize = 16;
    private const int IvSize = 16;
    private const int KeySize = 32; // AES-256
    private const int Iterations = 200_000;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ECERTBK1");

    public byte[] Encrypt(byte[] plaintext, string passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
            throw new InvalidOperationException("مفتاح تشفير النسخ الاحتياطي غير مضبوط.");

        var compressed = Compress(plaintext);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var iv = RandomNumberGenerator.GetBytes(IvSize);
        var key = DeriveKey(passphrase, salt);

        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var encryptor = aes.CreateEncryptor();
        var ciphertext = encryptor.TransformFinalBlock(compressed, 0, compressed.Length);

        using var output = new MemoryStream();
        output.Write(Magic);
        output.Write(salt);
        output.Write(iv);
        output.Write(ciphertext);
        return output.ToArray();
    }

    public byte[] Decrypt(byte[] archive, string passphrase)
    {
        using var input = new MemoryStream(archive);
        var magic = new byte[Magic.Length];
        if (input.Read(magic, 0, magic.Length) != magic.Length || !magic.SequenceEqual(Magic))
            throw new InvalidDataException("ملف النسخة الاحتياطية غير صالح.");

        var salt = new byte[SaltSize];
        var iv = new byte[IvSize];
        input.ReadExactly(salt);
        input.ReadExactly(iv);
        var ciphertext = new byte[input.Length - input.Position];
        input.ReadExactly(ciphertext);

        var key = DeriveKey(passphrase, salt);
        using var aes = Aes.Create();
        aes.KeySize = 256;
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        var compressed = decryptor.TransformFinalBlock(ciphertext, 0, ciphertext.Length);
        return Decompress(compressed);
    }

    private static byte[] DeriveKey(string passphrase, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(passphrase), salt, Iterations, HashAlgorithmName.SHA256, KeySize);

    private static byte[] Compress(byte[] data)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.SmallestSize, leaveOpen: true))
            gzip.Write(data, 0, data.Length);
        return output.ToArray();
    }

    private static byte[] Decompress(byte[] data)
    {
        using var input = new MemoryStream(data);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
