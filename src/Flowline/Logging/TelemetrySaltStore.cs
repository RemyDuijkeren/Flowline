using System.Security.Cryptography;
using Flowline.Utils;

namespace Flowline.Logging;

public sealed class TelemetrySaltStore(string path)
{
    const int SaltLength = 32;

    public TelemetrySaltStore() : this(GetDefaultSaltPath())
    {
    }

    public byte[] LoadOrCreate()
    {
        if (File.Exists(path))
        {
            try
            {
                return Convert.FromHexString(File.ReadAllText(path).Trim());
            }
            catch
            {
                // Corrupt salt file: fall through and regenerate.
            }
        }

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(Convert.ToHexString(salt));
        }
        catch (IOException)
        {
            // Another process created it first between our Exists check and CreateNew.
            return Convert.FromHexString(File.ReadAllText(path).Trim());
        }

        return salt;
    }

    static string GetDefaultSaltPath() =>
        Path.Combine(FlowlineStoragePaths.GetStorageRoot(), "telemetry-salt");
}
