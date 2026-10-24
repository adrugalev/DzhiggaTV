using L = DzhiggaTV.Localization.Texts;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DzhiggaTV.Installer;

public sealed record UpdateRequest(string Target, int WaitPid, long WaitStartTicks, string DataDirectory,
    string? Payload = null, string? PayloadSha256 = null, string? TargetVersion = null)
{
    public static UpdateRequest Parse(string[] args)
    {
        string Required(string name)
        {
            if (args.Count(a => a == name) != 1) throw new InstallException(L.T("Неверные параметры обновления."));
            int index = Array.IndexOf(args, name);
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new InstallException(L.T("Неверные параметры обновления."));
            return args[index + 1];
        }
        string target = Path.GetFullPath(Required("--update-target"));
        string data = Path.GetFullPath(Required("--data-dir"));
        if (!int.TryParse(Required("--wait-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            !long.TryParse(Required("--wait-start"), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0)
            throw new InstallException(L.T("Неверные параметры ожидания плеера."));
        string? payload = null, hash = null, version = null;
        if (args.Any(a => a is "--update-payload" or "--payload-sha256" or "--target-version"))
        {
            payload = Path.GetFullPath(Required("--update-payload")); hash = Required("--payload-sha256"); version = Required("--target-version");
            if (hash.Length != 64 || hash.Any(c => !Uri.IsHexDigit(c))) throw new InstallException(L.T("Неверная контрольная сумма обновления."));
            _ = DzhiggaTV.Updating.DeltaPackage.VersionOf(version);
        }
        return new(target, pid, ticks, data, payload, hash, version);
    }
    public async Task WaitForPlayerAsync(CancellationToken token)
    {
        Process process;
        try { process = Process.GetProcessById(WaitPid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            try { if (process.StartTime.ToUniversalTime().Ticks != WaitStartTicks) return; }
            catch (InvalidOperationException) { return; }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            try { await process.WaitForExitAsync(timeout.Token); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InstallException(L.T("Плеер ещё работает. Закройте DzhiggaTV и повторите обновление.")); }
        }
    }
    public void LaunchPlayer()
    {
        string application = Path.Combine(Target, "DzhiggaTV.exe");
        var start = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = Target };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(DataDirectory);
        start.ArgumentList.Add("--resume");
        using var process = Process.Start(start) ?? throw new InstallException(L.T("Не удалось открыть плеер. Запустите DzhiggaTV из меню «Пуск»."));
    }
    public async Task<Stream> OpenPayloadAsync(CancellationToken token)
    {
        if (Payload is null || PayloadSha256 is null) throw new InstallException(L.T("Не указан подготовленный пакет обновления."));
        var stream = new FileStream(Payload, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        try
        {
            if (stream.Length is <= 0 or > 2L * 1024 * 1024 * 1024 ||
                !Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(stream, token)).Equals(PayloadSha256, StringComparison.OrdinalIgnoreCase))
                throw new InstallException(L.T("Подготовленный пакет обновления повреждён. Откройте плеер и повторите загрузку."));
            stream.Position = 0; return stream;
        }
        catch { await stream.DisposeAsync(); throw; }
    }
}
