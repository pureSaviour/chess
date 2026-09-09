using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using Xiangqi.Core.Application.ThreeKingdoms;

namespace Xiangqi.Transport;

/// <summary>
/// 独立于引擎的 LAN 三人全连接适配器。每个参与者保有自己的监听端口，加入时交换名册与
/// 端点；高席位主动连接低席位，避免双重连接。满三席后身份固定，断线不释放席位。
/// 临时 ECDSA 公钥指纹标识本次房间玩家，每帧签名并绑定本连接 nonce，防止冒用席位和
/// 跨连接重放。私钥只保存在本实例；支持网络中断后的自动重连，不是关闭进程后的存档恢复。
/// 所有回调经 Poll 排队到主线程；接收长度、发送队列、连接数均有限制。
/// </summary>
public sealed class ThreeLanTransport : IThreeRoomTransport
{
    private const int MaxFrame = 256 * 1024;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentQueue<Action> _events = new();
    private readonly List<Link> _links = [];
    private readonly HashSet<string> _connecting = [];
    private readonly List<Contact> _contacts = [];
    private IReadOnlyList<ThreePeer> _rosterCache = Array.Empty<ThreePeer>();
    private readonly HashSet<string> _connected = [];
    private readonly ECDsa _identity = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _key;
    private readonly string _name;
    private readonly bool _bootstrap;
    private readonly string _address;
    private readonly int _bootstrapPort;
    private string? _root;
    private double _now, _maintenanceAt;
    private bool _disposed;
    public string LocalId { get; }
    public int ListeningPort => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public IReadOnlyList<ThreePeer> Roster
    {
        get
        {
            if (_rosterCache.Count != _contacts.Count)
                _rosterCache = Array.AsReadOnly(_contacts.Select(c => new ThreePeer(c.Id, c.Name)).ToArray());
            return _rosterCache;
        }
    }
    public IReadOnlySet<string> Connected => _connected;
    public string Status => _bootstrap ? $"LAN 房主 · {_address}:{ListeningPort}" : $"LAN {_address}:{_bootstrapPort} · 本机端口 {ListeningPort}";
    public event Action<string, byte[]>? Received;
    private sealed record Contact(string Id, string Name, string Address, int Port, string Key);
    private sealed record Wire(string Key, byte[] Body, byte[] Signature);
    private sealed record Frame(string Kind, string Nonce = "", string TargetNonce = "", Contact? Contact = null,
        Contact[]? Roster = null, byte[]? Data = null);
    private sealed class Link(TcpClient client, bool bootstrap)
    {
        public TcpClient Client { get; } = client;
        public bool Bootstrap { get; } = bootstrap;
        public string Nonce { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        public string RemoteNonce { get; set; } = "";
        public string? Id { get; set; }
        public double LastSeen { get; set; }
        public bool Closed { get; set; }
        public Channel<byte[]> Outbox { get; } = Channel.CreateBounded<byte[]>(128);
    }
    private ThreeLanTransport(bool host, string address, int port, string name)
    {
        if (port is < 1024 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        _bootstrap = host; _address = address; _bootstrapPort = port;
        _name = string.IsNullOrWhiteSpace(name) ? "局域网棋手" : new string(name.Where(c => !char.IsControl(c)).Take(30).ToArray());
        _key = Convert.ToBase64String(_identity.ExportSubjectPublicKeyInfo()); LocalId = Fingerprint(_key);
        _listener = new TcpListener(IPAddress.Any, host ? port : 0); _listener.Start(8);
        _connected.Add(LocalId);
        if (host) { _root = LocalId; _contacts.Add(Self("127.0.0.1")); }
        _ = AcceptLoop();
    }
    public static ThreeLanTransport Host(int port, string name, string displayAddress = "本机局域网IP") => new(true, displayAddress, port, name);
    public static ThreeLanTransport Join(string address, int port, string name)
    {
        if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != AddressFamily.InterNetwork || ip.Equals(IPAddress.Any))
            throw new ArgumentException("请填写有效的局域网 IPv4 地址。");
        return new(false, ip.ToString(), port, name);
    }
    private Contact Self(string address) => new(LocalId, _name, address, ListeningPort, _key);
    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                _events.Enqueue(() => Attach(client, false));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private void Attach(TcpClient client, bool bootstrap)
    {
        if (_disposed || _links.Count >= 8) { client.Dispose(); return; }
        client.NoDelay = true;
        var link = new Link(client, bootstrap) { LastSeen = _now }; _links.Add(link);
        _ = ReadLoop(link); _ = WriteLoop(link);
        SendFrame(link, new("hello", link.Nonce, Contact: Self("")));
    }
    private async Task Connect(string address, int port, string key, bool bootstrap)
    {
        var client = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token); timeout.CancelAfter(2500);
            await client.ConnectAsync(address, port, timeout.Token);
            _events.Enqueue(() => { _connecting.Remove(key); Attach(client, bootstrap); });
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        { client.Dispose(); _events.Enqueue(() => _connecting.Remove(key)); }
    }
    public void Poll(double delta)
    {
        if (_disposed) return;
        _now += delta;
        for (var i = 0; i < 256 && _events.TryDequeue(out var action); i++) action();
        if (_now < _maintenanceAt) return;
        _maintenanceAt = _now + 1;
        foreach (var link in _links.ToArray())
        {
            if (_now - link.LastSeen > 5) { Close(link); continue; }
            if (link.Id is not null) SendFrame(link, new("ping"));
        }
        var local = _contacts.FindIndex(c => c.Id == LocalId);
        if (!_bootstrap && local < 0)
        {
            if (!_links.Any(l => l.Bootstrap) && _connecting.Add("bootstrap"))
                _ = Connect(_address, _bootstrapPort, "bootstrap", true);
        }
        else
        {
            for (var i = 0; i < local; i++)
            {
                var c = _contacts[i];
                if (!_links.Any(l => l.Id == c.Id || l.Id is null) && _connecting.Add(c.Id))
                    _ = Connect(c.Address, c.Port, c.Id, false);
            }
        }
    }
    private async Task ReadLoop(Link link)
    {
        try
        {
            var stream = link.Client.GetStream(); var header = new byte[4];
            while (!_stop.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(header, _stop.Token);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is < 1 or > MaxFrame) break;
                var body = new byte[length]; await stream.ReadExactlyAsync(body, _stop.Token);
                if (_events.Count > 512) break;
                _events.Enqueue(() => ReceiveFrame(link, body));
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _events.Enqueue(() => Close(link)); }
    }
    private async Task WriteLoop(Link link)
    {
        try
        {
            var stream = link.Client.GetStream();
            await foreach (var body in link.Outbox.Reader.ReadAllAsync(_stop.Token))
            {
                var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, body.Length);
                await stream.WriteAsync(header, _stop.Token); await stream.WriteAsync(body, _stop.Token);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally { _events.Enqueue(() => Close(link)); }
    }
    private void ReceiveFrame(Link link, byte[] bytes)
    {
        if (link.Closed) return;
        try
        {
            var wire = JsonSerializer.Deserialize<Wire>(bytes);
            if (wire is null || wire.Key.Length > 256 || wire.Body.Length > MaxFrame || wire.Signature.Length > 128) { Close(link); return; }
            using var verifier = ECDsa.Create(); verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(wire.Key), out _);
            if (!verifier.VerifyData(wire.Body, wire.Signature, HashAlgorithmName.SHA256)) { Close(link); return; }
            var frame = JsonSerializer.Deserialize<Frame>(wire.Body); if (frame is null) return;
            var id = Fingerprint(wire.Key);
            if (frame.Kind == "hello")
            {
                if (link.Id is not null || frame.Contact is not { } contact || contact.Id != id || contact.Key != wire.Key ||
                    contact.Port is < 1024 or > 65535 || contact.Name.Length > 30 || contact.Name.Any(char.IsControl) ||
                    frame.Nonce.Length != 48 || id == LocalId) { Close(link); return; }
                if (_links.Any(l => l != link && l.Id == id)) { Close(link); return; }
                if (!_contacts.Any(c => c.Id == id))
                {
                    if (_bootstrap && _contacts.Count < 3)
                    {
                        var address = ((IPEndPoint)link.Client.Client.RemoteEndPoint!).Address.ToString();
                        _contacts.Add(contact with { Address = address });
                    }
                    else if (link.Bootstrap && _root is null) _root = id;
                    else { Close(link); return; }
                }
                link.Id = id; link.RemoteNonce = frame.Nonce; _connected.Add(id);
                // A newcomer obtains the complete root-signed roster, including the observed host address.
                if (_bootstrap) foreach (var known in _links.Where(l => l.Id is not null)) SendRoster(known);
            }
            else
            {
                if (id != link.Id || frame.TargetNonce != link.Nonce) { Close(link); return; }
                if (frame.Kind == "roster" && id == _root && !_bootstrap && frame.Roster is { Length: >= 1 and <= 3 } roster)
                {
                    if (roster[0].Id != _root || roster.Select(c => c.Id).Distinct().Count() != roster.Length ||
                        !roster.Any(c => c.Id == LocalId && c.Key == _key) || roster.Any(c => c.Port is < 1024 or > 65535 ||
                        c.Name.Length > 30 || c.Key.Length > 256 || c.Id != Fingerprint(c.Key) || !IPAddress.TryParse(c.Address, out _))) return;
                    if (_contacts.Count == 3 && !_contacts.Select(c => c.Id).SequenceEqual(roster.Select(c => c.Id))) return;
                    var remoteAddress = ((IPEndPoint)link.Client.Client.RemoteEndPoint!).Address.ToString();
                    _contacts.Clear(); _contacts.AddRange(roster);
                    _contacts[0] = _contacts[0] with { Address = remoteAddress };
                }
                else if (frame.Kind == "data" && frame.Data is { Length: > 0 and <= ThreeNetworkController.MaximumPacketBytes } data)
                    Received?.Invoke(id, data);
            }
            link.LastSeen = _now;
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException or NullReferenceException)
        { Close(link); }
    }
    private static string Fingerprint(string key) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(key)));
    private void SendRoster(Link link) => SendFrame(link, new("roster", Roster: _contacts.ToArray()));
    private void SendFrame(Link link, Frame frame)
    {
        if (link.Closed) return;
        var body = JsonSerializer.SerializeToUtf8Bytes(frame with { TargetNonce = link.RemoteNonce });
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new Wire(_key, body, _identity.SignData(body, HashAlgorithmName.SHA256)));
        if (bytes.Length > MaxFrame || !link.Outbox.Writer.TryWrite(bytes)) Close(link);
    }
    public void Send(string peerId, byte[] packet)
    {
        if (_disposed || packet.Length > ThreeNetworkController.MaximumPacketBytes) return;
        var link = _links.FirstOrDefault(l => l.Id == peerId);
        if (link is not null) SendFrame(link, new("data", Data: packet));
    }
    private void Close(Link link)
    {
        if (link.Closed) return;
        link.Closed = true; link.Outbox.Writer.TryComplete(); link.Client.Dispose(); _links.Remove(link);
        if (link.Id is { } id && !_links.Any(l => l.Id == id)) _connected.Remove(id);
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _stop.Cancel(); _listener.Stop();
        foreach (var link in _links.ToArray()) Close(link);
        _identity.Dispose(); _stop.Dispose();
        // Drain accepted sockets already queued before cancellation.
        while (_events.TryDequeue(out var action)) action();
    }
}
