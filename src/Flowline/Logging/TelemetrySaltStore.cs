using System.Security.Cryptography;
using Flowline.Core.Diagnostics;

namespace Flowline.Logging;

public sealed class TelemetrySaltStore(string path)
{
    const int SaltLength = 32;

    public TelemetrySaltStore() : this(GetDefaultSaltPath())
    {
    }

    public byte[] LoadOrCreate()
    {
        if (TryRead(out var existing)) return existing;

        var salt = RandomNumberGenerator.GetBytes(SaltLength);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
            using var writer = new StreamWriter(stream);
            writer.Write(Convert.ToHexString(salt));
        }
        catch (IOException) when (TryRead(out var raced))
        {
            // Another process created it between the read above and this write.
            return raced;
        }
        catch
        {
            // The file exists but holds something that is not a salt, or the location cannot be
            // written at all. Neither may fail the launch: this store is built inside the same
            // swallowed block as the logger, so throwing here costs the run its log file too.
            // Overwriting is the repair for the first case and a no-op for the second, which leaves
            // this run with a salt that works but does not persist — its machine dimension then
            // differs from the next run's, which is the honest outcome rather than a shared constant.
            TryOverwrite(salt);
        }

        return salt;
    }

    /// <summary>Reads the persisted salt, treating anything that is not one as absent.</summary>
    /// <remarks>
    /// A short, empty or malformed file is not a salt. Returning one anyway is the dangerous failure:
    /// <c>Convert.FromHexString("")</c> succeeds and yields a zero-length key, and an HMAC under an
    /// empty key is a public function rather than a secret one.
    /// </remarks>
    bool TryRead(out byte[] salt)
    {
        salt = [];
        try
        {
            if (!File.Exists(path)) return false;

            var text = File.ReadAllText(path).Trim();
            if (text.Length != SaltLength * 2) return false;

            salt = Convert.FromHexString(text);
            return salt.Length == SaltLength;
        }
        catch
        {
            return false;
        }
    }

    void TryOverwrite(byte[] salt)
    {
        try { File.WriteAllText(path, Convert.ToHexString(salt)); }
        catch { } // Intentional: an unwritable location leaves this run with a non-persisted salt.
    }

    static string GetDefaultSaltPath() =>
        Path.Combine(FlowlineStoragePaths.GetStorageRoot(), "telemetry-salt");
}
