using CliWrap;
using CliWrap.Buffered;

namespace Flowline;

public static class GitUtils
{
    public static async Task<string> AssertGitInstalledAsync()
    {
        try
        {
            var result = await Cli.Wrap("git")
                                  .WithArguments("--version")
                                  .ExecuteBufferedAsync();

            // Extract the version from the output (format: "git version X.Y.Z")
            var output = result.StandardOutput.Trim();
            if (output.StartsWith("git version "))
            {
                return output.Substring("git version ".Length);
            }

            return "Unknown";
        }
        catch (Exception)
        {
            Console.Error.WriteLine("Git (git) is not installed or not in PATH. Please install: https://git-scm.com/");
            Environment.Exit(1);
            return string.Empty; // This line will never be reached due to Environment.Exit
        }
    }
}
