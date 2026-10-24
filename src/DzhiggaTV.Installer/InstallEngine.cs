using L = DzhiggaTV.Localization.Texts;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DzhiggaTV.Installer;

public sealed class InstallException(string message) : Exception(message);
public sealed record InstallProgress(double Percent, string Message);
public sealed record InstallResult(string Directory, int FileCount, string? PreviousVersionBackup);

/// <summary>Installs only an embedded ZIP payload. Never reads or writes the separate user data directory.</summary>
public static class InstallEngine
{
    private const string Marker = ".dzhiggatv-install.json";
    private const string Product = "DzhiggaTV.Windows.x64";
    private const string RemovalGuide = "Удаление DzhiggaTV.txt";
    private const long MaximumUnpackedBytes = 2L * 1024 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 16 };
    private sealed record InstalledFile(string RelativePath, long Length, string Sha256);
    private sealed record Manifest(int FormatVersion, string ProductId, string InstallRoot, string ApplicationHash, DateTimeOffset InstalledAt, List<InstalledFile> Files);
    private sealed record PayloadEntry(ZipArchiveEntry Entry, string RelativePath);

    public static Task<InstallResult> InstallAsync(Stream zip, string destination, IProgress<InstallProgress>? progress, CancellationToken cancellationToken, Version? expectedVersion = null)
        => Task.Run(() => InstallCoreAsync(zip, destination, progress, cancellationToken, expectedVersion), cancellationToken);

    public static async Task ValidateUpdateTargetAsync(string destination, CancellationToken token)
    {
        string root = ValidateDestination(destination);
        _ = await ValidateExistingAsync(root, token);
    }

    private static async Task<InstallResult> InstallCoreAsync(Stream source, string destination, IProgress<InstallProgress>? progress, CancellationToken token, Version? expectedVersion)
    {
        string root = ValidateDestination(destination);
        string parent = Path.GetDirectoryName(root)!;
        EnsureNoReparseAncestors(parent);
        Directory.CreateDirectory(parent);
        EnsureNoReparseAncestors(parent);
        progress?.Report(new(0, L.T("Проверка установочного архива…")));
        using var archive = new ZipArchive(source, ZipArchiveMode.Read, true);
        List<PayloadEntry> payload = ValidatePayload(archive);
        Manifest? previous = await ValidateExistingAsync(root, token);
        string stage = Path.Combine(parent, ".DzhiggaTV.stage-" + Guid.NewGuid().ToString("N"));
        string backup = Path.Combine(parent, ".DzhiggaTV.previous-" + Guid.NewGuid().ToString("N"));
        EnsureDirectChild(stage, parent); EnsureDirectChild(backup, parent);
        if (Directory.Exists(stage) || Directory.Exists(backup) || File.Exists(stage) || File.Exists(backup)) throw new InstallException(L.T("Временная папка занята. Повторите установку."));
        Directory.CreateDirectory(stage);
        var written = new List<string>();
        var files = new List<InstalledFile>();
        long total = payload.Sum(item => item.Entry.Length), completed = 0, lastReported = -4 * 1024 * 1024;
        bool switched = false, backedUp = false;
        try
        {
            foreach (var item in payload)
            {
                token.ThrowIfCancellationRequested();
                string path = SafePath(stage, item.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                EnsureNoReparseAncestors(Path.GetDirectoryName(path)!);
                written.Add(item.RelativePath);
                using var input = item.Entry.Open();
                using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[65536];
                long bytes = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, token)) != 0)
                {
                    bytes += read;
                    if (bytes > item.Entry.Length || bytes > MaximumUnpackedBytes) throw new InstallException(L.T("Неверный размер файла в архиве."));
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), token);
                    completed += read;
                    if (completed - lastReported >= 4 * 1024 * 1024)
                    {
                        lastReported = completed;
                        progress?.Report(new(total == 0 ? 0 : completed * 92d / total, L.T("Распаковка DzhiggaTV…")));
                    }
                }
                if (bytes != item.Entry.Length) throw new InstallException(L.T("Установочный архив повреждён."));
                files.Add(new(item.RelativePath, bytes, Convert.ToHexStringLower(hash.GetHashAndReset())));
            }
            string guide = L.T("DzhiggaTV — удаление\r\n\r\n1. Закройте DzhiggaTV.\r\n2. Удалите эту папку приложения: ") + root + L.T("\r\n3. Удалите созданные ярлыки DzhiggaTV из меню «Пуск» и с рабочего стола.\r\n\r\nНастройки, избранное, плейлисты и EPG хранятся отдельно в %LOCALAPPDATA%\\DzhiggaTV и остаются у пользователя.\r\nУстановщик не удаляет личные данные.\r\n");
            string guidePath = SafePath(stage, RemovalGuide);
            written.Add(RemovalGuide);
            await File.WriteAllTextAsync(guidePath, guide, token);
            files.Add(await DescribeAsync(stage, RemovalGuide, token));
            string applicationHash = files.Single(file => file.RelativePath.Equals("DzhiggaTV.exe", StringComparison.OrdinalIgnoreCase)).Sha256;
            if (expectedVersion is not null)
            {
                var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(SafePath(stage, "DzhiggaTV.exe"));
                if (info.ProductName != "DzhiggaTV" || !Version.TryParse(info.FileVersion, out var actualVersion) || actualVersion != expectedVersion)
                    throw new InstallException(L.T("Версия подготовленного приложения не совпадает с выпуском."));
            }
            var manifest = new Manifest(1, Product, root, applicationHash, DateTimeOffset.UtcNow, files);
            written.Add(Marker);
            await File.WriteAllTextAsync(SafePath(stage, Marker), JsonSerializer.Serialize(manifest, Json), token);
            token.ThrowIfCancellationRequested();
            progress?.Report(new(95, L.T("Завершение установки…")));
            // Revalidate immediately before the directory swap; an unrelated directory is never replaced.
            await ValidateExistingAsync(root, token);
            EnsureNoReparseAncestors(root);
            if (Directory.Exists(root)) { Directory.Move(root, backup); backedUp = true; }
            try { Directory.Move(stage, root); switched = true; }
            catch
            {
                if (backedUp && !Directory.Exists(root)) { Directory.Move(backup, root); backedUp = false; }
                throw;
            }
            string? leftover = null;
            if (backedUp)
            {
                try
                {
                    if (previous != null) await RemoveOwnedBackupAsync(backup, parent, previous);
                    else if (!Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup);
                    if (Directory.Exists(backup)) leftover = backup;
                }
                catch { leftover = backup; }
            }
            progress?.Report(new(100, L.T("DzhiggaTV установлен")));
            return new(root, files.Count, leftover);
        }
        finally
        {
            if (!switched && Directory.Exists(stage)) CleanupStaging(stage, parent, written);
        }
    }

    private static string ValidateDestination(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination) || !Path.IsPathFullyQualified(destination)) throw new InstallException(L.T("Укажите полный путь к папке DzhiggaTV."));
        string root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Path.GetFileName(root).Equals("DzhiggaTV", StringComparison.OrdinalIgnoreCase)) throw new InstallException(L.T("Название конечной папки должно быть DzhiggaTV. Выберите отдельную папку приложения."));
        if (root.StartsWith(@"\\", StringComparison.Ordinal)) throw new InstallException(L.T("Для установки выберите локальный диск."));
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd(Path.DirectorySeparatorChar);
        string userData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DzhiggaTV");
        if (root.Equals(userData, StringComparison.OrdinalIgnoreCase) || IsInside(root, userData) || root.Equals(windows, StringComparison.OrdinalIgnoreCase) || IsInside(root, windows)) throw new InstallException(L.T("Выберите отдельную папку приложения, например Programs\\DzhiggaTV."));
        EnsureNoReparseAncestors(root);
        if (File.Exists(root)) throw new InstallException(L.T("Вместо папки существует файл. Выберите другую папку."));
        return root;
    }

    private static List<PayloadEntry> ValidatePayload(ZipArchive archive)
    {
        if (archive.Entries.Count is 0 or > 5000) throw new InstallException(L.T("Неверное число файлов в установочном архиве."));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<PayloadEntry>();
        long length = 0;
        foreach (var entry in archive.Entries)
        {
            string normalized = entry.FullName.Replace('\\', '/');
            bool directory = normalized.EndsWith('/');
            string relative = ValidateRelative(normalized.TrimEnd('/'));
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000) throw new InstallException(L.T("Архив содержит недопустимую ссылку."));
            if (directory) continue;
            if (!names.Add(relative) || relative.Equals(Marker, StringComparison.OrdinalIgnoreCase) || relative.Equals(RemovalGuide, StringComparison.OrdinalIgnoreCase)) throw new InstallException(L.T("Архив содержит повторяющиеся или служебные имена."));
            if (entry.Length < 0 || entry.Length > 512L * 1024 * 1024) throw new InstallException(L.T("Недопустимый размер файла в архиве."));
            length = checked(length + entry.Length);
            if (length > MaximumUnpackedBytes) throw new InstallException(L.T("Установочный архив превышает допустимый размер."));
            result.Add(new(entry, relative));
        }
        foreach (string name in names)
        {
            string? parent = Path.GetDirectoryName(name.Replace('/', Path.DirectorySeparatorChar));
            while (!string.IsNullOrEmpty(parent))
            {
                if (names.Contains(parent.Replace(Path.DirectorySeparatorChar, '/'))) throw new InstallException(L.T("Файл и папка имеют одинаковый путь в архиве."));
                parent = Path.GetDirectoryName(parent);
            }
        }
        if (!result.Any(item => item.RelativePath.Equals("DzhiggaTV.exe", StringComparison.OrdinalIgnoreCase) && item.Entry.Length > 0)) throw new InstallException(L.T("В архиве отсутствует DzhiggaTV.exe."));
        return result;
    }

    internal static string ValidateRelative(string relative)
    {
        relative = relative.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Length > 220) throw new InstallException(L.T("Недопустимый путь в установочном архиве."));
        foreach (var part in relative.Split('/'))
        {
            string stem = part.Split('.')[0];
            if (part.Length == 0 || part is "." or ".." || part.TrimEnd(' ', '.') != part || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':') || part.Any(char.IsControl)
                || stem.Equals("CON", StringComparison.OrdinalIgnoreCase) || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && stem[3] is >= '1' and <= '9'))
                throw new InstallException(L.T("Недопустимое имя файла в установочном архиве."));
        }
        return relative;
    }
    internal static string SafePath(string root, string relative)
    {
        string path = Path.GetFullPath(Path.Combine(root, ValidateRelative(relative).Replace('/', Path.DirectorySeparatorChar)));
        if (!IsInside(path, root)) throw new InstallException(L.T("Путь выходит за пределы папки установки."));
        return path;
    }
    private static bool IsInside(string path, string root) => path.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static void EnsureDirectChild(string path, string parent)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase)) throw new InstallException(L.T("Неверная временная папка установки."));
        EnsureNoReparseAncestors(parent);
    }
    internal static void EnsureNoReparseAncestors(string path)
    {
        for (string? current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InstallException(L.T("Папка установки не должна проходить через символические ссылки или junction."));
    }

    private static async Task<Manifest?> ValidateExistingAsync(string root, CancellationToken token)
    {
        if (!Directory.Exists(root)) return null;
        EnsureNoReparseAncestors(root);
        if (!Directory.EnumerateFileSystemEntries(root).Any()) return null;
        string marker = SafePath(root, Marker);
        if (!File.Exists(marker) || new FileInfo(marker).Length > 2 * 1024 * 1024) throw new InstallException(L.T("Выбранная папка не пуста и не принадлежит установщику DzhiggaTV. Выберите отдельную папку."));
        EnsureNoReparseAncestors(marker);
        Manifest? manifest;
        try { manifest = JsonSerializer.Deserialize<Manifest>(await File.ReadAllTextAsync(marker, token), Json); }
        catch (JsonException) { throw new InstallException(L.T("Метка предыдущей установки повреждена. Выберите другую папку.")); }
        if (manifest == null || manifest.FormatVersion != 1 || manifest.ProductId != Product || !root.Equals(manifest.InstallRoot, StringComparison.OrdinalIgnoreCase) || manifest.Files is null || manifest.Files.Count is 0 or > 5000) throw new InstallException(L.T("Папка не подтверждена как установка DzhiggaTV."));
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Marker };
        foreach (var file in manifest.Files)
        {
            string relative = ValidateRelative(file.RelativePath);
            if (!known.Add(relative) || file.Length < 0 || file.Sha256 is not { Length: 64 } || file.Sha256.Any(c => !Uri.IsHexDigit(c))) throw new InstallException(L.T("Манифест предыдущей установки повреждён."));
            string path = SafePath(root, relative);
            if (File.Exists(path))
            {
                EnsureNoReparseAncestors(path);
                var actual = await DescribeAsync(root, relative, token);
                if (actual.Length != file.Length || actual.Sha256 != file.Sha256) throw new InstallException(L.T("Файлы предыдущей установки изменены. Выберите новую папку, чтобы сохранить изменения."));
            }
        }
        var app = manifest.Files.SingleOrDefault(file => file.RelativePath.Equals("DzhiggaTV.exe", StringComparison.OrdinalIgnoreCase));
        if (app == null || app.Sha256 != manifest.ApplicationHash || !File.Exists(SafePath(root, app.RelativePath))) throw new InstallException(L.T("Не удалось подтвердить предыдущую версию DzhiggaTV."));
        foreach (string path in EnumerateSafe(root))
            if (File.Exists(path) && !known.Contains(Path.GetRelativePath(root, path).Replace('\\', '/'))) throw new InstallException(L.T("В папке приложения найдены посторонние файлы. Выберите новую папку, чтобы сохранить их."));
        return manifest;
    }
    private static IEnumerable<string> EnumerateSafe(string root)
    {
        var pending = new Stack<string>(); pending.Push(root);
        while (pending.Count > 0)
        {
            string parent = pending.Pop();
            foreach (string path in Directory.EnumerateFileSystemEntries(parent))
            {
                if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InstallException(L.T("В папке приложения обнаружена символическая ссылка."));
                yield return path;
                if (Directory.Exists(path)) pending.Push(path);
            }
        }
    }
    private static async Task<InstalledFile> DescribeAsync(string root, string relative, CancellationToken token)
    {
        string path = SafePath(root, relative);
        EnsureNoReparseAncestors(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new(relative, input.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token)));
    }
    private static async Task RemoveOwnedBackupAsync(string backup, string parent, Manifest manifest)
    {
        EnsureDirectChild(backup, parent); EnsureNoReparseAncestors(backup);
        foreach (var file in manifest.Files)
        {
            string path = SafePath(backup, file.RelativePath);
            if (!File.Exists(path)) continue;
            var actual = await DescribeAsync(backup, file.RelativePath, CancellationToken.None);
            if (actual.Length == file.Length && actual.Sha256 == file.Sha256) File.Delete(path);
        }
        DeleteEmptyDirectories(backup);
        if (Directory.EnumerateFileSystemEntries(backup).All(path => Path.GetFileName(path).Equals(Marker, StringComparison.OrdinalIgnoreCase)))
        {
            string marker = SafePath(backup, Marker);
            EnsureNoReparseAncestors(marker);
            if (File.Exists(marker)) File.Delete(marker);
            if (!Directory.EnumerateFileSystemEntries(backup).Any()) Directory.Delete(backup);
        }
    }
    private static void CleanupStaging(string stage, string parent, IEnumerable<string> written)
    {
        try
        {
            EnsureDirectChild(stage, parent); EnsureNoReparseAncestors(stage);
            foreach (string relative in written)
            {
                string path = SafePath(stage, relative); EnsureNoReparseAncestors(path);
                if (File.Exists(path)) File.Delete(path);
            }
            DeleteEmptyDirectories(stage);
            if (!Directory.EnumerateFileSystemEntries(stage).Any()) Directory.Delete(stage);
        }
        catch { /* Keep staging rather than risking an unsafe cleanup. */ }
    }
    private static void DeleteEmptyDirectories(string root)
    {
        foreach (string directory in EnumerateSafe(root).Where(Directory.Exists).OrderByDescending(path => path.Length).ToArray())
            if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
    }
}
