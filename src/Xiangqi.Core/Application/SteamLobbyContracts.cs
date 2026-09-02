namespace Xiangqi.Core.Application;

public readonly record struct SteamLobbyId(ulong Value);

/// <summary>
/// Steam-specific matchmaking seam. Its implementation owns SteamAPI lifecycle,
/// lobby creation, the overlay invite dialog and GameLobbyJoinRequested callbacks. Gameplay
/// packets remain behind IMatchTransport so Steam Networking Messages can replace ENet without
/// changing GameSession or the match screens.
/// </summary>
public interface ISteamLobbyService : IAsyncDisposable
{
    event Action<SteamLobbyId>? LobbyJoinRequested;
    bool IsAvailable { get; }
    ValueTask<SteamLobbyId> CreateInviteLobbyAsync(
        NetworkMatchConfiguration configuration,
        CancellationToken cancellationToken = default);
    ValueTask OpenInviteOverlayAsync(SteamLobbyId lobby, CancellationToken cancellationToken = default);
    ValueTask JoinLobbyAsync(SteamLobbyId lobby, CancellationToken cancellationToken = default);
}
