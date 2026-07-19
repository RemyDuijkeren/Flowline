namespace Flowline.Core.Console;

using System;
using System.IO;
using Spectre.Console;

public static class ConsolePath
{
    public static string FormatRelativePath(string path, string? rootFolder = null)
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
                return $"[bold]{Markup.Escape(relativePath)}/[/]";
            }

            string parent = relativePath[..(lastSlash + 1)];
            string folder = relativePath[(lastSlash + 1)..];

            return
                $"{Markup.Escape(parent)}" +
                $"[bold]{Markup.Escape(folder)}/[/]";
        }
        else
        {
            string? directory = Path.GetDirectoryName(relativePath)
                                    ?.Replace("\\", "/");

            string fileName = Path.GetFileName(relativePath);

            directory = string.IsNullOrWhiteSpace(directory)
                ? string.Empty
                : directory + "/";

            return
                $"{Markup.Escape(directory)}" +
                $"[bold]{Markup.Escape(fileName)}[/]";
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
