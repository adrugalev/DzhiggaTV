using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DzhiggaTV.Installer;

internal static class Program
{
    private static readonly List<string> Passed = [];
    private static void Assert(bool value, string name) { if (!value) throw new Exception(name); Passed.Add(name); }
    private static MemoryStream Zip(params (string Name, string Text)[] entries)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, true))
            foreach (var pair in entries)
            {
                var entry = zip.CreateEntry(pair.Name);
                using var output = entry.Open();
                output.Write(Encoding.UTF8.GetBytes(pair.Text));
            }
        stream.Position = 0;
        return stream;
    }
    private static async Task Reject(Func<Task> action, string name)
    {
        try { await action(); }
        catch (InstallException) { Passed.Add(name); return; }
        throw new Exception(name);
    }
    private static async Task<int> Main(string[] args)
    {
        string scratch = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(scratch);
        try
        {
            string owned = Path.Combine(scratch, "owned", "DzhiggaTV");
            using (var initial = Zip(("DzhiggaTV.exe", "MZ synthetic app1"), ("native/libmpv-2.dll", "native fixture"), ("old.dll", "old payload")))
            {
                var result = await InstallEngine.InstallAsync(initial, owned, null, CancellationToken.None);
                Assert(result.FileCount == 4 && File.ReadAllText(Path.Combine(owned, "DzhiggaTV.exe")) == "MZ synthetic app1", "install valid archive to dedicated temporary directory");
                Assert(File.Exists(Path.Combine(owned, ".dzhiggatv-install.json")) && File.Exists(Path.Combine(owned, "Удаление DzhiggaTV.txt")), "write owned manifest and manual removal guide");
            }
            using (var updated = Zip(("DzhiggaTV.exe", "MZ synthetic app2"), ("native/libmpv-2.dll", "new native fixture"), ("new.dll", "new payload")))
            {
                var result = await InstallEngine.InstallAsync(updated, owned, null, CancellationToken.None);
                Assert(File.ReadAllText(Path.Combine(owned, "DzhiggaTV.exe")) == "MZ synthetic app2" && !File.Exists(Path.Combine(owned, "old.dll")) && result.PreviousVersionBackup == null, "verified owned update swaps payload and removes only tracked previous files");
            }
            File.WriteAllText(Path.Combine(owned, "personal.txt"), "keep me");
            using (var candidate = Zip(("DzhiggaTV.exe", "MZ should not install")))
                await Reject(() => InstallEngine.InstallAsync(candidate, owned, null, CancellationToken.None), "reject extra personal files inside existing install");
            Assert(File.ReadAllText(Path.Combine(owned, "personal.txt")) == "keep me", "preserve unknown personal file");
            File.Delete(Path.Combine(owned, "personal.txt"));
            File.WriteAllText(Path.Combine(owned, "DzhiggaTV.exe"), "user changed exe");
            using (var candidate = Zip(("DzhiggaTV.exe", "MZ should not install")))
                await Reject(() => InstallEngine.InstallAsync(candidate, owned, null, CancellationToken.None), "reject changed application hash");
            string unknown = Path.Combine(scratch, "unknown", "DzhiggaTV");
            Directory.CreateDirectory(unknown); File.WriteAllText(Path.Combine(unknown, "important.txt"), "keep unknown");
            using (var candidate = Zip(("DzhiggaTV.exe", "MZ should not install")))
                await Reject(() => InstallEngine.InstallAsync(candidate, unknown, null, CancellationToken.None), "reject unknown nonempty destination without marker");
            Assert(File.ReadAllText(Path.Combine(unknown, "important.txt")) == "keep unknown", "preserve unowned destination");
            foreach (string unsafeName in new[] { "../escape.txt", "/absolute.txt", "C:/absolute.txt", "native/../../escape.txt", "native/evil.txt:ads", "NUL.txt", "native/bad. ", "native/./bad.txt" })
            {
                using var candidate = Zip(("DzhiggaTV.exe", "MZ synthetic"), (unsafeName, "malicious"));
                await Reject(() => InstallEngine.InstallAsync(candidate, Path.Combine(scratch, "invalid", "DzhiggaTV"), null, CancellationToken.None), "reject unsafe archive path: " + unsafeName);
            }
            using (var duplicate = Zip(("DzhiggaTV.exe", "MZ1"), ("DZHiGGATV.exe", "MZ2")))
                await Reject(() => InstallEngine.InstallAsync(duplicate, Path.Combine(scratch, "duplicate", "DzhiggaTV"), null, CancellationToken.None), "reject case-insensitive duplicate payload names");
            using (var collision = Zip(("DzhiggaTV.exe", "MZ1"), ("native", "file"), ("native/mpv.dll", "file")))
                await Reject(() => InstallEngine.InstallAsync(collision, Path.Combine(scratch, "collision", "DzhiggaTV"), null, CancellationToken.None), "reject file-directory path collision");
            using (var candidate = Zip(("DzhiggaTV.exe", "MZ")))
                await Reject(() => InstallEngine.InstallAsync(candidate, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DzhiggaTV"), null, CancellationToken.None), "refuse separate DzhiggaTV user data directory");
            using (var candidate = Zip(("DzhiggaTV.exe", "MZ")))
                await Reject(() => InstallEngine.InstallAsync(candidate, Path.Combine(scratch, "not-an-app-folder"), null, CancellationToken.None), "require dedicated DzhiggaTV leaf directory");
            using (var candidate = Zip(("DzhiggaTV.exe", "MZ")))
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                bool cancelled = false;
                try { await InstallEngine.InstallAsync(candidate, Path.Combine(scratch, "cancelled", "DzhiggaTV"), null, cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Assert(cancelled && !Directory.Exists(Path.Combine(scratch, "cancelled", "DzhiggaTV")), "cancel before extraction without creating installation");
            }
            Assert(!File.Exists(Path.Combine(scratch, "escape.txt")), "no ZIP traversal writes outside destination");
            Console.WriteLine(JsonSerializer.Serialize(new { success = true, count = Passed.Count, passed = Passed, shortcutsCreated = false, registryModified = false, userDataTouched = false }));
            return 0;
        }
        catch (Exception exception) { Console.WriteLine(JsonSerializer.Serialize(new { success = false, passed = Passed, failure = exception.Message })); return 1; }
    }
}
