using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace DzhiggaTV.Installer;

public sealed record UpdateRequest(string Target, int WaitPid, long WaitStartTicks, string DataDirectory)
{
    public static UpdateRequest Parse(string[] args)
    {
        string Required(string name)
        {
            if (args.Count(a => a == name) != 1) throw new InstallException("Неверные параметры обновления.");
            int index = Array.IndexOf(args, name);
            if (index + 1 >= args.Length || args[index + 1].StartsWith("--")) throw new InstallException("Неверные параметры обновления.");
            return args[index + 1];
        }
        string target = Path.GetFullPath(Required("--update-target"));
        string data = Path.GetFullPath(Required("--data-dir"));
        if (!int.TryParse(Required("--wait-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out int pid) || pid <= 0 ||
            !long.TryParse(Required("--wait-start"), NumberStyles.None, CultureInfo.InvariantCulture, out long ticks) || ticks <= 0)
            throw new InstallException("Неверные параметры ожидания плеера.");
        return new(target, pid, ticks, data);
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
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new InstallException("Плеер ещё работает. Закройте DzhiggaTV и повторите обновление."); }
        }
    }
    public void LaunchPlayer()
    {
        string application = Path.Combine(Target, "DzhiggaTV.exe");
        var start = new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = Target };
        start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(DataDirectory);
        start.ArgumentList.Add("--resume");
        using var process = Process.Start(start) ?? throw new InstallException("Не удалось открыть плеер. Запустите DzhiggaTV из меню «Пуск».");
    }
}
