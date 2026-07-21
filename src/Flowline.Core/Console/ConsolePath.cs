namespace Flowline.Core.Console;

using System;
using System.IO;
using Spectre.Console;

public static class ConsolePath
{
    /// <param name="markup">
    /// <c>true</c> (default) wraps the last path segment in Spectre markup for direct console rendering.
    /// Pass <c>false</c> when the result feeds a <see cref="Flowline.Diagnostics.FlowlineException"/>
    /// message or other text that gets markup-escaped before display — otherwise the escaped tags show up
    /// as literal <c>[bold]...[/]</c> in the user-facing text instead of being rendered or hidden.
    /// </param>
    public static string FormatRelativePath(string path, string? rootFolder = null, bool markup = true)
    {
        string cwd = rootFolder ?? Environment.CurrentDirectory;

        string relativePath = Path.GetRelativePath(cwd, path)
                                  .Replace("\\", "/");

        bool isDirectory =
            Directory.Exists(path) ||
            path.EndsWith("/") ||
            path.EndsWith("\\");

        if (isDirectory)
        {
            relativePath = relativePath.TrimEnd('/');

            int lastSlash = relativePath.LastIndexOf('/');

            if (lastSlash < 0)
            {
                return markup ? $"[bold]{Markup.Escape(relativePath)}/[/]" : $"{relativePath}/";
            }

            string parent = relativePath[..(lastSlash + 1)];
            string folder = relativePath[(lastSlash + 1)..];

            return markup
                ? $"{Markup.Escape(parent)}[bold]{Markup.Escape(folder)}/[/]"
                : $"{parent}{folder}/";
        }
        else
        {
            string? directory = Path.GetDirectoryName(relativePath)
                                    ?.Replace("\\", "/");

            string fileName = Path.GetFileName(relativePath);

            directory = string.IsNullOrWhiteSpace(directory)
                ? string.Empty
                : directory + "/";

            return markup
                ? $"{Markup.Escape(directory)}[bold]{Markup.Escape(fileName)}[/]"
                : $"{directory}{fileName}";
        }
    }

    public static string ShortenPath(string path, int maxLength = 40)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path;

        bool isDirectory =
            Directory.Exists(path) ||
            path.EndsWith('\\') ||
            path.EndsWith('/');

        string normalized = path
                            .TrimEnd('\\', '/')
                            .Replace('\\', '/');

        string[] parts = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
            return path;

        string lastPart = parts[^1];

        string shortened;

        if (normalized.Length <= maxLength || parts.Length <= 2)
        {
            shortened = normalized;
        }
        else
        {
            string first = parts[0];

            var middleParts = parts
                              .Skip(1)
                              .Take(parts.Length - 2)
                              .ToArray();

            shortened = $"{first}/.../{lastPart}";

            for (int i = middleParts.Length - 1; i >= 0; i--)
            {
                string candidate =
                    $"{first}/.../{string.Join('/', middleParts.Skip(i))}/{lastPart}";

                if (candidate.Length <= maxLength)
                {
                    shortened = candidate;
                }
                else
                {
                    break;
                }
            }
        }

        int lastSlash = shortened.LastIndexOf('/');

        if (lastSlash < 0)
        {
            return $"[bold]{Markup.Escape(shortened)}[/]" +
                   (isDirectory ? "/" : "");
        }

        string parent = shortened[..(lastSlash + 1)];
        string last = shortened[(lastSlash + 1)..];

        return
            $"{Markup.Escape(parent)}" +
            $"[bold]{Markup.Escape(last)}[/]" +
            (isDirectory ? "/" : "");
    }
}
