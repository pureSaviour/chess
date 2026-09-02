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
        Local.Avatar?.Dispose();
        Local = profile;
    }

    public void ReplaceOpponent(PlayerProfileSnapshot profile)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Opponent.Avatar?.Dispose();
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
    public static PlayerProfileSnapshot? Create(AppSettings settings, string fallbackName)
    {
        if (!settings.HasCustomProfile) return null;
        PlayerAvatarPixels? avatar = null;
        if (!string.IsNullOrWhiteSpace(settings.CustomAvatarPath))
        {
            using var image = new Image();
            if (image.Load(settings.CustomAvatarPath) == Error.Ok)
            {
                image.Convert(Image.Format.Rgba8);
                image.Resize(MatchWireProtocol.MaximumAvatarSide,
                    MatchWireProtocol.MaximumAvatarSide, Image.Interpolation.Lanczos);
                avatar = new PlayerAvatarPixels(image.GetWidth(), image.GetHeight(), image.GetData());
            }
            else GD.PushWarning($"无法读取自定义头像：{settings.CustomAvatarPath}");
        }
        var id = string.IsNullOrWhiteSpace(settings.CustomPlayerId) ? "本地棋手" : settings.CustomPlayerId.Trim();
        return new PlayerProfileSnapshot("自定义", id, fallbackName, avatar);
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
