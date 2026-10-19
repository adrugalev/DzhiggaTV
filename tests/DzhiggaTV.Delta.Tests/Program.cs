using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DzhiggaTV.Installer;
using DzhiggaTV.Updating;

internal static class Program
{
    private static int _passed;
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "DzhiggaTV-delta-tests-" + Guid.NewGuid().ToString("N"));
    private static void Assert(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); ++_passed; }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); } catch (Exception e) when (e is InstallException or InvalidDataException or FileNotFoundException) { Assert(true, name); return; }
        throw new Exception(name);
    }
    private static string At(string name) => Path.Combine(Work, name);
    private static async Task<int> Main(string[] args)
    {
        Directory.CreateDirectory(Work);
        try
        {
            if (args.Length == 2 && args[0] == "--published") await PublishedAsync(Path.GetFullPath(args[1]));
            else await UnitsAsync();
            Console.WriteLine($"{_passed} delta checks passed."); return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL " + error); return 1; }
        finally
        {
            // Only this process's newly created, randomly named fixture directory.
            if (Path.GetDirectoryName(Work) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(Work).StartsWith("DzhiggaTV-delta-tests-", StringComparison.Ordinal))
                try { Directory.Delete(Work, true); } catch { }
        }
    }
    private static async Task UnitsAsync()
    {
        string old = At("old"), next = At("new"); Directory.CreateDirectory(old); Directory.CreateDirectory(next);
        foreach (string directory in new[] { old, next })
        {
            Directory.CreateDirectory(Path.Combine(directory, "native"));
            byte[] stable = new byte[1024 * 1024]; new Random(42).NextBytes(stable);
            await File.WriteAllBytesAsync(Path.Combine(directory, "native", "libmpv-2.dll"), stable);
            await File.WriteAllTextAsync(Path.Combine(directory, "coreclr.dll"), "stable runtime");
        }
        await File.WriteAllTextAsync(Path.Combine(old, "DzhiggaTV.exe"), "old app");
        await File.WriteAllTextAsync(Path.Combine(old, "removed.txt"), "obsolete");
        await File.WriteAllTextAsync(Path.Combine(next, "DzhiggaTV.exe"), "new app");
        await File.WriteAllTextAsync(Path.Combine(next, "added.txt"), "added file");
        var baseline = await DeltaPackage.DescribeAsync(old, "1.0.16");
        var target = await DeltaPackage.DescribeAsync(next, "1.0.17");
        string delta = At("delta.zip"); await DeltaPackage.CreateAsync(baseline, target, next, delta);
        using (var zip = ZipFile.OpenRead(delta))
            Assert(zip.GetEntry("files/native/libmpv-2.dll") is null && zip.GetEntry("files/coreclr.dll") is null && zip.GetEntry("files/added.txt") is not null, "unchanged video engine and runtime never enter the delta");
        Assert(new FileInfo(delta).Length < 4096, "small app change produces a small delta despite a large dependency");
        string installed = At("install/DzhiggaTV");
        ZipFile.CreateFromDirectory(old, At("old.zip"));
        using (var stream = File.OpenRead(At("old.zip"))) await InstallEngine.InstallAsync(stream, installed, null, default);
        var prepared = await DeltaPackage.ReconstructAsync(delta, installed, new(1, 0, 17, 0), At("prepared.zip"), null, default);
        Assert(prepared.Sha256 == Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(prepared.Path))), "prepared archive has a verified whole-file digest");
        using (var stream = File.OpenRead(prepared.Path)) await InstallEngine.InstallAsync(stream, installed, null, default);
        foreach (var file in target.Files)
            Assert(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(installed, file.Path)))) == file.Sha256, "reconstructed installed bytes: " + file.Path);
        Assert(!File.Exists(Path.Combine(installed, "removed.txt")), "removed application files disappear in the atomic update");
        await File.WriteAllTextAsync(Path.Combine(old, "coreclr.dll"), "modified runtime");
        await Reject(() => DeltaPackage.ReconstructAsync(delta, old, new(1, 0, 17, 0), At("bad-base.zip"), null, default), "reject modified reused dependency before replacing the application");
        Assert(!File.Exists(At("bad-base.zip")) && !File.Exists(At("bad-base.zip.part")), "failed reconstruction leaves no complete or partial payload");
        await File.WriteAllTextAsync(Path.Combine(old, "coreclr.dll"), "stable runtime");
        await Reject(() => DeltaPackage.ReconstructAsync(delta, old, new(1, 0, 18, 0), At("wrong-version.zip"), null, default), "reject a delta for another target version");
        // Skipping versions works when all files omitted from the package still match.
        await File.WriteAllTextAsync(Path.Combine(old, "DzhiggaTV.exe"), "much older app");
        Assert(File.Exists((await DeltaPackage.ReconstructAsync(delta, old, new(1, 0, 17, 0), At("skipped.zip"), null, default)).Path), "allow skipped releases when reused dependencies are byte-identical");
        using (var cts = new CancellationTokenSource())
        {
            cts.Cancel(); bool cancelled = false;
            try { await DeltaPackage.ReconstructAsync(delta, old, new(1, 0, 17, 0), At("cancel.zip"), null, cts.Token); } catch (OperationCanceledException) { cancelled = true; }
            Assert(cancelled && !File.Exists(At("cancel.zip.part")), "cancellation removes the partial payload");
        }
        using var source = ZipFile.OpenRead(delta);
        string originalMetadata;
        using (var reader = new StreamReader(source.GetEntry("manifest.json")!.Open())) originalMetadata = await reader.ReadToEndAsync();
        foreach (string bad in new[] { "../escape.txt", "/absolute.txt", "C:/absolute.txt", "native/../escape.txt", "NUL.txt", "file.txt:ads", "native/trailing. " })
        {
            var files = target.Files.Select((f, i) => i == 0 ? f with { Path = bad } : f).ToList();
            string path = At("unsafe-" + Guid.NewGuid().ToString("N") + ".zip");
            await MakePackageAsync(path, JsonSerializer.Serialize(new DeltaManifest(target with { Files = files }, [])), []);
            await Reject(() => DeltaPackage.ReconstructAsync(path, old, new(1, 0, 17, 0), path + ".result", null, default), "reject unsafe delta path " + bad);
        }
        foreach (string mode in new[] { "corrupt", "extra", "duplicate", "missing", "symlink", "format", "collision" })
        {
            string path = At(mode + ".zip");
            using (var output = ZipFile.Open(path, ZipArchiveMode.Create))
            {
                foreach (var entry in source.Entries)
                {
                    if (mode == "missing" && entry.FullName == "files/added.txt") continue;
                    var copy = output.CreateEntry(entry.FullName);
                    if (mode == "symlink" && entry.FullName == "files/added.txt") copy.ExternalAttributes = 0xa000 << 16;
                    using var destination = copy.Open();
                    if (entry.FullName == "manifest.json" && mode is "format" or "collision")
                    {
                        string metadata = mode == "format" ? originalMetadata.Replace("\"FormatVersion\": 1", "\"FormatVersion\": 99") :
                            JsonSerializer.Serialize(new DeltaManifest(target with { Files = target.Files.Concat([new ReleaseFile("native", 0, new string('0', 64))]).ToList() }, ["DzhiggaTV.exe", "added.txt"]));
                        await destination.WriteAsync(Encoding.UTF8.GetBytes(metadata));
                    }
                    else if (mode == "corrupt" && entry.FullName == "files/added.txt") await destination.WriteAsync(Encoding.UTF8.GetBytes("wrong file"));
                    else { using var input = entry.Open(); await input.CopyToAsync(destination); }
                }
                if (mode == "extra") output.CreateEntry("unlisted.txt");
                if (mode == "duplicate") output.CreateEntry("files/ADDED.txt");
            }
            await Reject(() => DeltaPackage.ReconstructAsync(path, old, new(1, 0, 17, 0), path + ".result", null, default), "reject " + mode + " package");
        }
        var request = new UpdateRequest(installed, Environment.ProcessId, DateTime.UtcNow.Ticks, At("profile"), prepared.Path, prepared.Sha256, "1.0.17");
        using (var stream = await request.OpenPayloadAsync(default)) Assert(stream.Position == 0, "isolated runner verifies and rewinds the prepared payload");
        await Reject(async () => { using var ignored = await (request with { PayloadSha256 = new string('0', 64) }).OpenPayloadAsync(default); }, "runner rejects a prepared payload changed after preflight");
        await Reject(() => Task.Run(() => UpdateRequest.Parse(["--update-target", installed, "--data-dir", At("profile"), "--wait-pid", "1", "--wait-start", "1", "--update-payload", prepared.Path])), "reject incomplete delta handoff");
        using (var stream = File.OpenRead(prepared.Path))
            await Reject(() => InstallEngine.InstallAsync(stream, installed, null, default, new(1, 0, 17, 0)), "version mismatch is rejected before the directory swap");
        Assert(File.ReadAllText(Path.Combine(installed, "DzhiggaTV.exe")) == "new app", "failed installation leaves the prior installation usable");
    }
    private static async Task MakePackageAsync(string path, string metadata, List<(string Name, byte[] Bytes)> files)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        using (var output = zip.CreateEntry("manifest.json").Open()) await output.WriteAsync(Encoding.UTF8.GetBytes(metadata));
        foreach (var file in files) { using var output = zip.CreateEntry(file.Name).Open(); await output.WriteAsync(file.Bytes); }
    }
    private static async Task PublishedAsync(string published)
    {
        string old = At("baseline"); Directory.CreateDirectory(old);
        foreach (string file in Directory.EnumerateFiles(published, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(old, Path.GetRelativePath(published, file)); Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(file, target);
        }
        // Installer-created files are not part of a portable payload.
        foreach (string service in new[] { ".dzhiggatv-install.json", "Удаление DzhiggaTV.txt" })
            if (File.Exists(Path.Combine(old, service))) File.Delete(Path.Combine(old, service));
        var targetManifest = await DeltaPackage.DescribeAsync(old, FileVersionInfo.GetVersionInfo(Path.Combine(old, "DzhiggaTV.exe")).FileVersion!);
        await File.WriteAllTextAsync(Path.Combine(old, "DzhiggaTV.dll"), "previous application bytes");
        await File.WriteAllTextAsync(Path.Combine(old, "obsolete.txt"), "obsolete tracked file");
        var baseline = await DeltaPackage.DescribeAsync(old, "0.0.1");
        string delta = At("real-delta.zip"); await DeltaPackage.CreateAsync(baseline, targetManifest, published, delta);
        Assert(new FileInfo(delta).Length < 5 * 1024 * 1024, "a real application-only update stays below 5 MiB");
        Console.WriteLine($"MEASURED delta={new FileInfo(delta).Length} bytes; portable={targetManifest.Files.Sum(f => f.Length)} bytes unpacked");
        string installed = At("actual/DzhiggaTV"); ZipFile.CreateFromDirectory(old, At("baseline.zip"));
        using (var input = File.OpenRead(At("baseline.zip"))) await InstallEngine.InstallAsync(input, installed, null, default);
        File.Delete(At("baseline.zip"));
        Directory.Delete(old, true);
        var payload = await DeltaPackage.ReconstructAsync(delta, installed, DeltaPackage.VersionOf(targetManifest.Version), At("real-payload.zip"), null, default);
        string runner = At("runner"); Directory.CreateDirectory(runner);
        foreach (string file in Directory.EnumerateFiles(installed))
            if (Path.GetExtension(file).ToLowerInvariant() is ".dll" or ".exe" or ".json") File.Copy(file, Path.Combine(runner, Path.GetFileName(file)));
        using var current = Process.GetCurrentProcess();
        string report = At("runner-report.json");
        var start = new ProcessStartInfo(Path.Combine(runner, "DzhiggaTV.Setup.exe")) { UseShellExecute = false, WorkingDirectory = runner, RedirectStandardError = true, RedirectStandardOutput = true, CreateNoWindow = true };
        foreach (string arg in new[] { "--extract-to", installed, "--update-target", installed, "--wait-pid", current.Id.ToString(), "--wait-start", current.StartTime.ToUniversalTime().Ticks.ToString(), "--data-dir", At("untouched-profile"), "--update-payload", payload.Path, "--payload-sha256", payload.Sha256, "--target-version", targetManifest.Version, "--report", report }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try { await process.WaitForExitAsync(timeout.Token); } catch { process.Kill(); throw; }
        Assert(process.ExitCode == 0 && File.Exists(report) && JsonDocument.Parse(File.ReadAllText(report)).RootElement.GetProperty("success").GetBoolean(), "isolated installed updater runs with its local runtime and applies the prepared delta");
        foreach (var file in targetManifest.Files)
            if (Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(installed, file.Path)))) != file.Sha256) throw new Exception("Published byte mismatch: " + file.Path);
        Assert(!File.Exists(Path.Combine(installed, "obsolete.txt")), "real updater removes obsolete tracked files");
        Assert(!Directory.Exists(At("untouched-profile")), "real updater never touches the player profile");
        Assert(FileVersionInfo.GetVersionInfo(Path.Combine(installed, "DzhiggaTV.exe")).FileVersion == targetManifest.Version, "installed application version matches the target");
    }
}
