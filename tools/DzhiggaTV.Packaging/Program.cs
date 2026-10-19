using System.Security.Cryptography;
using DzhiggaTV.Updating;

if (args.Length is 3 or 4 && args[0] == "--verify")
{
    var expected = await DeltaPackage.ReadManifestAsync(args[1]);
    await DeltaPackage.VerifyDirectoryAsync(expected, args[2]);
    if (args.Length == 4 && File.Exists(args[3]))
    {
        string reconstructed = Path.Combine(Path.GetTempPath(), "DzhiggaTV-verify-delta-" + Guid.NewGuid().ToString("N") + ".zip");
        try
        {
            await DeltaPackage.ReconstructAsync(args[3], args[2], DeltaPackage.VersionOf(expected.Version), reconstructed, null, default);
            using var archive = System.IO.Compression.ZipFile.OpenRead(reconstructed);
            if (archive.Entries.Count != expected.Files.Count) throw new Exception("Published delta file count mismatch.");
            foreach (var file in expected.Files)
            {
                var entry = archive.GetEntry(file.Path) ?? throw new Exception("Published delta is missing " + file.Path);
                await using var input = entry.Open();
                if (entry.Length != file.Length || Convert.ToHexStringLower(await SHA256.HashDataAsync(input)) != file.Sha256) throw new Exception("Published delta file hash mismatch.");
            }
        }
        finally { if (File.Exists(reconstructed)) File.Delete(reconstructed); }
    }
    Console.WriteLine("Published manifest and delta match the actual embedded application."); return;
}
if (args.Length is < 3 or > 4) throw new ArgumentException("target-directory version output-directory [baseline-manifest]");
string target = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[2]);
Directory.CreateDirectory(output);
var manifest = await DeltaPackage.DescribeAsync(target, args[1]);
string manifestPath = Path.Combine(output, DeltaPackage.ManifestName);
await DeltaPackage.WriteManifestAsync(manifest, manifestPath);
await ChecksumAsync(manifestPath);
if (args.Length == 4)
{
    var baseline = await DeltaPackage.ReadManifestAsync(args[3]);
    string package = Path.Combine(output, DeltaPackage.PackageName);
    await DeltaPackage.CreateAsync(baseline, manifest, target, package);
    if (new FileInfo(package).Length < new FileInfo(target + ".zip").Length)
    { await ChecksumAsync(package); Console.WriteLine($"Delta: {new FileInfo(package).Length / 1024d / 1024d:N2} MiB"); }
    else { File.Delete(package); Console.WriteLine("Delta would not save bandwidth; full installer only."); }
}
Console.WriteLine("Release manifest: " + manifestPath);

static async Task ChecksumAsync(string path)
{
    await using var stream = File.OpenRead(path);
    string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream));
    await File.WriteAllTextAsync(path + ".sha256", hash + "  " + Path.GetFileName(path) + "\n");
}
