using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using DzhiggaTV.Installer;

namespace DzhiggaTV.Updating;

public sealed record ReleaseFile(string Path, long Length, string Sha256);
public sealed record ReleaseManifest(int FormatVersion, string ProductId, string Version, List<ReleaseFile> Files);
public sealed record DeltaManifest(ReleaseManifest Target, List<string> IncludedFiles);
public sealed record PreparedPayload(string Path, string Sha256);

/// <summary>File deltas. Reconstructs a complete, verified payload without modifying the running installation.</summary>
public static class DeltaPackage
{
    public const string Product = "DzhiggaTV.Windows.x64";
    public const string ManifestName = "DzhiggaTV-win-x64.manifest.json";
    public const string PackageName = "DzhiggaTV-Update-x64.zip";
    private const long MaximumBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumManifest = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, MaxDepth = 16 };

    public static Version VersionOf(string value)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(value ?? "", @"^\d+\.\d+\.\d+(?:\.\d+)?$") || !Version.TryParse(value, out var version))
            throw new InstallException("Неверная версия пакета обновления.");
        return new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
    }

    public static async Task<ReleaseManifest> DescribeAsync(string directory, string version, CancellationToken token = default)
    {
        directory = Path.GetFullPath(directory);
        _ = VersionOf(version);
        var files = new List<ReleaseFile>();
        // Walk without following junctions, including directory junctions outside the application.
        var pending = new Stack<string>(); pending.Push(Path.GetFullPath(directory));
        while (pending.TryPop(out var parent))
        {
            InstallEngine.EnsureNoReparseAncestors(parent);
            foreach (string path in Directory.EnumerateFileSystemEntries(parent).Order(StringComparer.Ordinal))
            {
                token.ThrowIfCancellationRequested();
                InstallEngine.EnsureNoReparseAncestors(path);
                if (Directory.Exists(path)) { pending.Push(path); continue; }
                string relative = InstallEngine.ValidateRelative(Path.GetRelativePath(directory, path).Replace('\\', '/'));
                await using var input = File.OpenRead(path);
                files.Add(new(relative, input.Length, Convert.ToHexStringLower(await SHA256.HashDataAsync(input, token))));
            }
        }
        var manifest = new ReleaseManifest(1, Product, version, files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
        Validate(manifest); return manifest;
    }

    public static async Task WriteManifestAsync(ReleaseManifest manifest, string path, CancellationToken token = default)
    { Validate(manifest); await File.WriteAllTextAsync(path, JsonSerializer.Serialize(manifest, Json), token); }

    public static async Task<ReleaseManifest> ReadManifestAsync(string path, CancellationToken token = default)
    {
        if (new FileInfo(path).Length > MaximumManifest) throw new InstallException("Слишком большой манифест обновления.");
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(await File.ReadAllTextAsync(path, token), Json);
        Validate(manifest); return manifest!;
    }

    public static async Task VerifyDirectoryAsync(ReleaseManifest manifest, string directory, CancellationToken token = default)
    {
        Validate(manifest); directory = Path.GetFullPath(directory);
        foreach (var file in manifest.Files)
        {
            string path = InstallEngine.SafePath(directory, file.Path); InstallEngine.EnsureNoReparseAncestors(path);
            await using var input = File.OpenRead(path);
            await CopyVerifiedAsync(input, Stream.Null, file, token);
        }
    }

    public static async Task CreateAsync(ReleaseManifest baseline, ReleaseManifest target, string targetDirectory, string output, CancellationToken token = default)
    {
        targetDirectory = Path.GetFullPath(targetDirectory);
        Validate(baseline); Validate(target);
        if (VersionOf(baseline.Version) >= VersionOf(target.Version)) throw new InstallException("База дельта-пакета должна быть старше нового выпуска.");
        var old = baseline.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
        var included = target.Files.Where(f => !old.TryGetValue(f.Path, out var previous) || previous.Length != f.Length || previous.Sha256 != f.Sha256).ToList();
        string partial = output + ".part";
        try
        {
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                await using (var metadata = archive.CreateEntry("manifest.json", CompressionLevel.Optimal).Open())
                    await JsonSerializer.SerializeAsync(metadata, new DeltaManifest(target, included.Select(f => f.Path).ToList()), Json, token);
                foreach (var file in included)
                {
                    token.ThrowIfCancellationRequested();
                    string path = InstallEngine.SafePath(targetDirectory, file.Path); InstallEngine.EnsureNoReparseAncestors(path);
                    await using var input = File.OpenRead(path);
                    await using var entry = archive.CreateEntry("files/" + file.Path, CompressionLevel.Optimal).Open();
                    await CopyVerifiedAsync(input, entry, file, token);
                }
            }
            File.Move(partial, output);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    public static async Task<PreparedPayload> ReconstructAsync(string package, string installedDirectory, Version expectedVersion, string output,
        IProgress<InstallProgress>? progress, CancellationToken token)
    {
        installedDirectory = Path.GetFullPath(installedDirectory);
        string partial = output + ".part";
        try
        {
            using var archive = ZipFile.OpenRead(package);
            if (archive.Entries.Count is 0 or > 5001) throw new InstallException("Неверное число файлов в дельта-пакете.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.Contains('\\') || InstallEngine.ValidateRelative(entry.FullName) != entry.FullName ||
                    !entries.TryAdd(entry.FullName, entry) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000)
                    throw new InstallException("Дельта-пакет содержит недопустимый путь или ссылку.");
            }
            if (!entries.TryGetValue("manifest.json", out var metadata) || metadata.Length > MaximumManifest) throw new InstallException("В дельта-пакете отсутствует манифест.");
            DeltaManifest? delta;
            await using (var input = metadata.Open())
            {
                using var bounded = new MemoryStream();
                await CopyBoundedAsync(input, bounded, MaximumManifest, token);
                delta = JsonSerializer.Deserialize<DeltaManifest>(bounded.ToArray(), Json);
            }
            Validate(delta?.Target);
            if (delta!.IncludedFiles is null || VersionOf(delta.Target.Version) != expectedVersion) throw new InstallException("Дельта-пакет относится к другому выпуску.");
            var included = new HashSet<string>(delta.IncludedFiles, StringComparer.OrdinalIgnoreCase);
            var all = delta.Target.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            if (included.Count != delta.IncludedFiles.Count || included.Any(p => !all.ContainsKey(p)) || entries.Count != included.Count + 1)
                throw new InstallException("Состав дельта-пакета не совпадает с манифестом.");
            foreach (string path in included)
                if (!entries.TryGetValue("files/" + path, out var entry) || entry.Length != all[path].Length)
                    throw new InstallException("Неверный размер файла в дельта-пакете.");
            long total = delta.Target.Files.Sum(f => f.Length), completed = 0;
            await using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var result = new ZipArchive(stream, ZipArchiveMode.Create, true))
            {
                foreach (var file in delta.Target.Files)
                {
                    token.ThrowIfCancellationRequested();
                    Stream input;
                    if (included.Contains(file.Path)) input = entries["files/" + file.Path].Open();
                    else
                    {
                        string path = InstallEngine.SafePath(installedDirectory, file.Path); InstallEngine.EnsureNoReparseAncestors(path);
                        input = File.OpenRead(path);
                    }
                    await using (input)
                    await using (var entry = result.CreateEntry(file.Path, CompressionLevel.NoCompression).Open())
                        await CopyVerifiedAsync(input, entry, file, token);
                    completed += file.Length;
                    progress?.Report(new(total == 0 ? 100 : completed * 100d / total, "Подготовка обновления…"));
                }
            }
            await using var finished = File.OpenRead(partial);
            string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(finished, token));
            await finished.DisposeAsync();
            token.ThrowIfCancellationRequested(); File.Move(partial, output);
            return new(output, hash);
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static void Validate(ReleaseManifest? manifest)
    {
        if (manifest is null || manifest.FormatVersion != 1 || manifest.ProductId != Product || manifest.Files is null || manifest.Files.Count is 0 or > 5000)
            throw new InstallException("Неверный манифест обновления.");
        _ = VersionOf(manifest.Version);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total = 0;
        foreach (var file in manifest.Files)
        {
            if (file is null || InstallEngine.ValidateRelative(file.Path) != file.Path || file.Path.Contains('\\') ||
                file.Path.Equals(".dzhiggatv-install.json", StringComparison.OrdinalIgnoreCase) || file.Path.Equals("Удаление DzhiggaTV.txt", StringComparison.OrdinalIgnoreCase) ||
                !names.Add(file.Path) || file.Length is < 0 or > 512L * 1024 * 1024 ||
                file.Sha256 is not { Length: 64 } || file.Sha256.Any(c => !Uri.IsHexDigit(c)) || (total += file.Length) > MaximumBytes)
                throw new InstallException("Недопустимый файл в манифесте обновления.");
        }
        foreach (string name in names)
            for (int slash = name.LastIndexOf('/'); slash >= 0; slash = name.LastIndexOf('/', slash - 1))
                if (names.Contains(name[..slash])) throw new InstallException("Конфликт файла и папки в обновлении.");
        if (!manifest.Files.Any(f => f.Path == "DzhiggaTV.exe" && f.Length > 0)) throw new InstallException("В обновлении отсутствует DzhiggaTV.exe.");
    }

    private static async Task CopyVerifiedAsync(Stream input, Stream output, ReleaseFile file, CancellationToken token)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); long length = 0;
        byte[] buffer = new byte[65536]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            length += count;
            if (length > file.Length) throw new InstallException("Размер файла обновления не совпадает с манифестом.");
            hash.AppendData(buffer, 0, count); await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (length != file.Length || !Convert.ToHexStringLower(hash.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InstallException("Файл обновления или установленная копия не прошли проверку. Нужен полный установщик.");
    }
    private static async Task CopyBoundedAsync(Stream input, Stream output, int maximum, CancellationToken token)
    {
        byte[] buffer = new byte[8192]; int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        { if (output.Length + count > maximum) throw new InstallException("Слишком большой манифест обновления."); await output.WriteAsync(buffer.AsMemory(0, count), token); }
    }
}
