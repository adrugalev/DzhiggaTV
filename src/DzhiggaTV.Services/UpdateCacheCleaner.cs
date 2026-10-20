using System.Diagnostics;
using System.Text.RegularExpressions;

namespace DzhiggaTV.Services;

public sealed record UpdateCacheCleanup(int Directories, long Bytes);

/// <summary>Remove completed obsolete update workspaces, never the profile or a pending/current release.</summary>
public static class UpdateCacheCleaner
{
    public static Task<UpdateCacheCleanup> CleanAsync(string cacheDirectory, Version currentVersion,
        CancellationToken cancellationToken = default, IReadOnlyCollection<string>? activeExecutables = null) => Task.Run(() =>
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(cacheDirectory));
        int count = 0; long bytes = 0;
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) return new UpdateCacheCleanup(0, 0);
        IReadOnlyCollection<string>? active = activeExecutables;
        var recent = DateTime.UtcNow.AddMinutes(-5);
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileName(directory);
            if (!Version.TryParse(name, out var version) || version.Build < 0 || version.Revision < 0 || version.ToString(4) != name || version >= GitHubUpdateService.Normalize(currentVersion)) continue;
            active ??= ActiveExecutables();
            string full = Path.GetFullPath(directory), prefix = full + Path.DirectorySeparatorChar;
            if (Path.GetDirectoryName(full) != root || active.Any(p => p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))) continue;
            try
            {
                var files = OwnedFiles(full, cancellationToken);
                // A recent file or partial download may still belong to another application instance.
                if (files is null || files.Any(f => f.Extension is ".part" or ".tmp" || f.LastWriteTimeUtc > recent)) continue;
                long size = files.Sum(f => f.Length);
                Directory.Delete(full, true); bytes += size; count++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Locked workspaces retry next time. */ }
        }
        return new UpdateCacheCleanup(count, bytes);
    }, cancellationToken);

    private static List<FileInfo>? OwnedFiles(string root, CancellationToken token)
    {
        var files = new List<FileInfo>(); var pending = new Stack<string>(); pending.Push(root);
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return null;
            foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                string relative = Path.GetRelativePath(root, entry.FullName);
                string[] parts = relative.Split(Path.DirectorySeparatorChar);
                if (entry is DirectoryInfo)
                {
                    if (parts.Length == 1 && Regex.IsMatch(parts[0], "^prepared-[0-9a-f]{32}$") ||
                        parts.Length == 2 && Regex.IsMatch(parts[0], "^prepared-[0-9a-f]{32}$") && parts[1] == "runner") pending.Push(entry.FullName);
                    else return null;
                }
                else if (parts.Length == 1 && (entry.Name is "DzhiggaTV-Setup-x64.exe" or "DzhiggaTV-Update-x64.zip" ||
                    Regex.IsMatch(entry.Name, @"^DzhiggaTV-\d+\.\d+\.\d+(?:\.\d+)?-Setup-x64\.exe$")) ||
                    parts.Length == 2 && parts[1] == "application.zip" ||
                    parts.Length == 3 && parts[1] == "runner" && entry.Extension.ToLowerInvariant() is ".dll" or ".exe" or ".json") files.Add((FileInfo)entry);
                else return null;
            }
        }
        return files;
    }

    private static IReadOnlyCollection<string> ActiveExecutables()
    {
        var result = new List<string>();
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try { if (process.MainModule?.FileName is { } path) result.Add(path); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return result;
    }
}
