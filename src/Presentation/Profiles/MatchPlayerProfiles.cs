namespace Xiangqi.Presentation.Profiles;

using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Presentation.Settings;

/// <summary>
/// Engine-neutral pixel payload returned by an identity provider. It deliberately contains no
/// Godot texture or Steam handle: providers may obtain it from Steam, a future account server, or
/// a LAN profile exchange, while the view decides how to render it. Dispose clears the temporary
/// network buffer as soon as the match UI releases the profile lease.
/// </summary>
public sealed class PlayerAvatarPixels : IDisposable
{
    private byte[]? _rgba;

    public PlayerAvatarPixels(int width, int height, byte[] rgba)
    {
        if (width is <= 0 or > 512 || height is <= 0 or > 512)
            throw new ArgumentOutOfRangeException(nameof(width), "头像尺寸必须介于 1 和 512 像素之间。");
        if (rgba.Length != checked(width * height * 4))
            throw new ArgumentException("头像必须是完整的 RGBA8 数据。", nameof(rgba));
        Width = width;
        Height = height;
        _rgba = rgba;
    }

    public int Width { get; }
    public int Height { get; }
    public bool IsDisposed => _rgba is null;
    public byte[] CopyRgba() => _rgba?.ToArray() ??
        throw new ObjectDisposedException(nameof(PlayerAvatarPixels));

    public void Dispose()
    {
        if (_rgba is null) return;
        Array.Clear(_rgba);
        _rgba = null;
    }
}

/// <summary>
/// Read-only identity shown by a match. PlayerId is platform-qualified display data (for example
/// a SteamID64), not an authentication token; secrets and mutable platform objects are forbidden.
/// </summary>
public sealed record PlayerProfileSnapshot(
    string Platform,
    string PlayerId,
    string DisplayName,
    PlayerAvatarPixels? Avatar = null);

/// <summary>
/// Match-scoped ownership boundary for both player profiles. The screen owns this lease after
/// navigation and must dispose it when the room closes, ensuring downloaded avatar buffers do not
/// become a process-wide cache. Native textures created from these buffers are owned separately by
/// the view and are disposed at the same boundary.
/// </summary>
public sealed class MatchPlayerProfiles : IDisposable
{
    private bool _disposed;

    public MatchPlayerProfiles(PlayerProfileSnapshot local, PlayerProfileSnapshot opponent)
    {
        Local = local;
        Opponent = opponent;
    }

    public PlayerProfileSnapshot Local { get; private set; }
    public PlayerProfileSnapshot Opponent { get; private set; }
    public bool IsDisposed => _disposed;

    public void ReplaceLocal(PlayerProfileSnapshot profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(Local.Avatar, profile.Avatar)) Local.Avatar?.Dispose();
        Local = profile;
    }

    public void ReplaceOpponent(PlayerProfileSnapshot profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!ReferenceEquals(Opponent.Avatar, profile.Avatar)) Opponent.Avatar?.Dispose();
        Opponent = profile;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Local.Avatar?.Dispose();
        if (!ReferenceEquals(Local.Avatar, Opponent.Avatar)) Opponent.Avatar?.Dispose();
    }
}

/// <summary>
/// Converts persisted preferences into a bounded, transport-safe snapshot. External image files
/// are decoded locally and reduced to 64×64 RGBA8 before entering the network layer; paths are
/// never exposed to the peer. Returning null means the platform/default identity should be used.
/// </summary>
public static class CustomPlayerProfileFactory
{
    /// <summary>
    /// Applies independently configured nickname/avatar overrides to a caller-owned fallback
    /// profile. Missing or unreadable override fields retain the fallback Steam/local value. The
    /// returned snapshot may share the fallback avatar; the caller must transfer it to one profile
    /// lease or dispose it. Image decoding is synchronous and the source path is never transmitted.
    /// </summary>
    public static PlayerProfileSnapshot ApplyOverrides(AppSettings settings, PlayerProfileSnapshot fallback)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(fallback);
        if (!settings.HasCustomProfile) return fallback;

        var displayName = string.IsNullOrWhiteSpace(settings.CustomPlayerId)
            ? fallback.DisplayName
            : settings.CustomPlayerId.Trim();
        var avatar = TryLoadCustomAvatar(settings.CustomAvatarPath) ?? fallback.Avatar;
        return fallback with { Platform = $"自定义/{fallback.Platform}", DisplayName = displayName, Avatar = avatar };
    }

    public static PlayerProfileSnapshot? Create(AppSettings settings, string fallbackName)
    {
        if (!settings.HasCustomProfile) return null;
        return ApplyOverrides(settings,
            new PlayerProfileSnapshot("本地", "本地棋手", fallbackName));
    }

    private static PlayerAvatarPixels? TryLoadCustomAvatar(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        using var image = new Image();
        if (image.Load(path) != Error.Ok)
        {
            GD.PushWarning($"无法读取自定义头像：{path}");
            return null;
        }
        image.Convert(Image.Format.Rgba8);
        image.Resize(MatchWireProtocol.MaximumAvatarSide,
            MatchWireProtocol.MaximumAvatarSide, Image.Interpolation.Lanczos);
        return new PlayerAvatarPixels(image.GetWidth(), image.GetHeight(), image.GetData());
    }

    public static PlayerProfileCommand ToCommand(PlayerProfileSnapshot profile)
    {
        var avatar = profile.Avatar;
        return new PlayerProfileCommand(profile.PlayerId, profile.DisplayName,
            avatar?.Width ?? 0, avatar?.Height ?? 0, avatar?.CopyRgba() ?? []);
    }

    public static PlayerProfileSnapshot FromCommand(PlayerProfileCommand command) =>
        new("自定义", command.PlayerId, command.DisplayName,
            command.AvatarRgba.Length == 0 ? null :
                new PlayerAvatarPixels(command.AvatarWidth, command.AvatarHeight, command.AvatarRgba));
}

/// <summary>
/// Exchanges the already-bounded shared profile command over a raw two-player game channel.
/// It does not own the transport or profile lease; the match screen disposes both after unsubscribing.
/// </summary>
public sealed class RawMatchProfileExchange : IDisposable
{
    private readonly ITwoPlayerPacketTransport _transport;
    private readonly MatchPlayerProfiles _profiles;
    private bool _disposed;
    private bool _sent;

    public RawMatchProfileExchange(ITwoPlayerPacketTransport transport, MatchPlayerProfiles profiles)
    {
        _transport = transport;
        _profiles = profiles;
        _transport.PacketReceived += OnPacket;
        _transport.PacketStatusChanged += OnStatus;
        if (_transport.IsPacketConnected) SendLocal();
    }

    public event Action? OpponentChanged;

    private void OnStatus(TransportStatus status)
    {
        if (status.State == TransportConnectionState.Connected) SendLocal();
    }

    private async void SendLocal()
    {
        if (_sent || _disposed) return;
        _sent = true;
        try { await _transport.SendPacketAsync(MatchWireProtocol.Encode(CustomPlayerProfileFactory.ToCommand(_profiles.Local))); }
        catch (Exception error) { _sent = false; GD.PushWarning($"发送玩家资料失败：{error.Message}"); }
    }

    private void OnPacket(ReadOnlyMemory<byte> packet)
    {
        if (!MatchWireProtocol.TryDecode(packet.ToArray(), out var message) ||
            message.Type != MatchWireMessageType.PlayerProfile) return;
        _profiles.ReplaceOpponent(CustomPlayerProfileFactory.FromCommand((PlayerProfileCommand)message.Payload));
        OpponentChanged?.Invoke();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _transport.PacketReceived -= OnPacket;
        _transport.PacketStatusChanged -= OnStatus;
    }
}
