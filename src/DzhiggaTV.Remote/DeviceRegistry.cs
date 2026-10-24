using L = DzhiggaTV.Localization.Texts;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DzhiggaTV.Remote;

public sealed class DeviceRegistry
{
    public const string CookieName = "DzhiggaTV.Remote";
    private readonly object _gate = new();
    private readonly string _path;
    private readonly List<Device> _devices = [];
    private readonly Dictionary<string, Pending> _pending = [];
    private string _code = "";
    private DateTimeOffset _codeExpires;
    public event Action? Changed;
    public DeviceRegistry(string directory)
    {
        Directory.CreateDirectory(directory); _path = Path.Combine(directory, "remote-devices.json");
        try { _devices = JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(_path))?.Where(d => d.Expires > DateTimeOffset.UtcNow).Take(12).ToList() ?? []; }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
    }
    public string RotateCode()
    {
        lock (_gate) { _pending.Clear(); _code = Token(); _codeExpires = DateTimeOffset.UtcNow.AddMinutes(3); return _code; }
    }
    public PairRequest Begin(string code, string name)
    {
        PairRequest request;
        lock (_gate)
        {
            Prune();
            if (_code.Length == 0 || _codeExpires < DateTimeOffset.UtcNow || !Equal(code, _code))
                throw new RemoteException(L.T("Код истёк или уже использован. Обновите QR-код на компьютере."), 403);
            if (_pending.Count >= 4 || _devices.Count >= 12) throw new RemoteException(L.T("Слишком много устройств. Проверьте список подключений на ПК."), 429);
            _code = ""; // Consume before asking the desktop user; requests cannot replace a pending device.
            name = new string(name.Where(c => !char.IsControl(c)).Take(64).ToArray()).Trim();
            request = new(Token(), name.Length > 0 ? name : L.T("Телефон"), RandomNumberGenerator.GetInt32(100000, 1000000).ToString(), DateTimeOffset.UtcNow.AddMinutes(2));
            _pending.Add(request.Id, new(request));
        }
        Changed?.Invoke(); return request;
    }
    public IReadOnlyList<PairRequest> PendingRequests { get { lock (_gate) { Prune(); return _pending.Values.Where(p => !p.Decided).Select(p => p.Request).ToArray(); } } }
    public void Decide(string id, bool allow)
    {
        lock (_gate)
        {
            Prune();
            if (!_pending.TryGetValue(id, out var p) || p.Decided) return;
            p.Decided = true;
            if (allow)
            {
                p.Credential = Token();
                var now = DateTimeOffset.UtcNow;
                _devices.Add(new() { Id = Token(), Hash = Hash(p.Credential), Name = p.Request.Name, Csrf = Token(), Paired = now, LastSeen = now, Expires = now.AddDays(30) });
                Save();
            }
        }
        Changed?.Invoke();
    }
    public (string Status, string? Credential) Poll(string id)
    {
        lock (_gate)
        {
            Prune();
            if (!_pending.TryGetValue(id, out var p)) return ("expired", null);
            if (!p.Decided) return ("pending", null);
            _pending.Remove(id); return p.Credential is null ? ("denied", null) : ("approved", p.Credential);
        }
    }
    public Device? Authenticate(string? credential)
    {
        if (string.IsNullOrEmpty(credential) || credential.Length > 128) return null;
        lock (_gate)
        {
            var hash = Hash(credential);
            var d = _devices.Find(x => x.Expires > DateTimeOffset.UtcNow && Equal(x.Hash, hash));
            if (d is not null) d.LastSeen = DateTimeOffset.UtcNow;
            return d;
        }
    }
    public bool Exists(string id) { lock (_gate) return _devices.Any(d => d.Id == id && d.Expires > DateTimeOffset.UtcNow); }
    public IReadOnlyList<DeviceInfo> Devices { get { lock (_gate) return _devices.Where(d => d.Expires > DateTimeOffset.UtcNow).Select(d => new DeviceInfo(d.Id, d.Name, d.Paired, d.LastSeen, DateTimeOffset.UtcNow - d.LastSeen < TimeSpan.FromSeconds(20))).ToArray(); } }
    public void Revoke(string? id)
    {
        lock (_gate) { _devices.RemoveAll(d => id is null || d.Id == id); _pending.Clear(); _code = ""; Save(); }
        Changed?.Invoke();
    }
    public void ClearPending() { lock (_gate) { _pending.Clear(); _code = ""; } }
    private void Prune() { foreach (var p in _pending.Where(p => p.Value.Request.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) _pending.Remove(p); }
    private void Save() { File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(_devices)); File.Move(_path + ".tmp", _path, true); }
    public static string Token() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
    private static bool Equal(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    private sealed class Pending(PairRequest request) { public PairRequest Request { get; } = request; public bool Decided; public string? Credential; }
    public sealed class Device
    {
        public string Id { get; set; } = ""; public string Hash { get; set; } = ""; public string Csrf { get; set; } = ""; public string Name { get; set; } = "";
        public DateTimeOffset Paired { get; set; } public DateTimeOffset Expires { get; set; } public DateTimeOffset LastSeen { get; set; }
    }
}
