namespace Flowline.Utils;

static class FlowlineStoragePaths
{
    public static string GetStorageRoot()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(root))
            root = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");

        if (string.IsNullOrWhiteSpace(root))
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            root = string.IsNullOrWhiteSpace(home)
                ? Path.GetTempPath()
                : Path.Combine(home, ".cache");
        }

        return Path.Combine(root, "Flowline");
    }

    public static string GetRunsPath(DateOnly date) =>
        Path.Combine(GetStorageRoot(), "runs", $"{date:yyyy-MM-dd}.jsonl");

    public static string GetLogsPath(DateOnly date) =>
        Path.Combine(GetStorageRoot(), "logs", $"{date:yyyy-MM-dd}.log");
}
