using DzhiggaTV.Services;

namespace DzhiggaTV.App;

public sealed class UpdateCoordinator : Observable, IDisposable
{
    public GitHubUpdateService Service { get; }
    private readonly SemaphoreSlim _checking = new(1, 1);
    private readonly Version _current;
    public UpdateCoordinator(GitHubUpdateService? service = null, Version? currentVersion = null)
    {
        Service = service ?? new();
        _current = GitHubUpdateService.Normalize(currentVersion ?? typeof(App).Assembly.GetName().Version!);
    }
    private AppRelease? _available;
    public AppRelease? Available { get => _available; private set { if (Set(ref _available, value)) Raise(nameof(HasUpdate)); } }
    public bool HasUpdate => Available is not null;
    public string? LastError { get; private set; }
    public DateTimeOffset LastChecked { get; private set; }

    public async Task CheckAsync(bool force, CancellationToken token)
    {
        await _checking.WaitAsync(token);
        try
        {
            if (!force && DateTimeOffset.UtcNow - LastChecked < TimeSpan.FromMinutes(5)) return;
            LastChecked = DateTimeOffset.UtcNow;
            try
            {
                var latest = await Service.CheckAsync(token);
                Available = latest is not null && latest.Version > _current ? latest : null;
                LastError = null;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
            catch (UpdateException e) { LastError = e.Message; }
            catch { LastError = "Не удалось связаться с GitHub. Проверьте интернет и попробуйте ещё раз."; }
        }
        finally { _checking.Release(); }
    }
    public void Dispose() => Service.Dispose();
}
