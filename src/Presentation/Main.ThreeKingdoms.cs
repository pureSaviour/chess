using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.ThreeKingdoms;
using Xiangqi.Core.Domain.ThreeKingdoms;
using Xiangqi.Presentation.Profiles;
using Xiangqi.Presentation.Screens;
using Xiangqi.Transport;
#if GODOT_WINDOWS
using Xiangqi.Presentation.Networking;
#endif

namespace Xiangqi.Presentation;

public partial class Main
{
#if GODOT_WINDOWS
    private SteamThreeLobbyService? _threeSteam;
#endif
    private void ShowThreeGallery(ThreeGalleryEntry entry) => SwitchTo(new ThreeGalleryScreen(ShowGallery, StartThreeLocal,
        StartThreeLanHost, StartThreeLanJoin, StartThreeSteam, entry));
    private void ShowThreeRecords(bool saved) => SwitchTo(new ThreeRecordScreen(saved, ShowGallery,
        record => SwitchTo(new ThreeReplayScreen(_settings, _themes, record, () => ShowThreeRecords(saved))),
        record => SwitchTo(new ThreeGameScreen(_settings, ShowGallery,
            new LocalThreeMatchController(TimeControl.Unlimited,
                ThreeKingdomsGame.FromPosition(record.InitialPosition, record.AdvisorPolicy),
                CreateThreePlayerProfile()), themes: _themes))));
    private void StartThreeLocal(TimeControl time, AdvisorConquestPolicy policy)
    {
        CancelWaitingSteamLobby();
        SwitchTo(new ThreeGameScreen(_settings, ShowGallery,
            new LocalThreeMatchController(time, policy, CreateThreePlayerProfile()), themes: _themes));
    }
    private string ThreePlayerName => string.IsNullOrWhiteSpace(_settings.CustomPlayerId) ? $"棋手 {System.Environment.ProcessId}" : _settings.CustomPlayerId;
    private PlayerProfileCommand CreateThreePlayerProfile(PlayerProfileSnapshot? platformProfile = null)
    {
        var fallback = platformProfile ?? CreateDefaultLocalProfile(ThreePlayerName);
        var profile = _settings.HasCustomProfile
            ? CustomPlayerProfileFactory.ApplyOverrides(_settings, fallback)
            : fallback;
        try { return CustomPlayerProfileFactory.ToCommand(profile); }
        finally
        {
            if (!ReferenceEquals(profile.Avatar, fallback.Avatar)) profile.Avatar?.Dispose();
            fallback.Avatar?.Dispose();
        }
    }
    private void StartThreeLanHost(int port, TimeControl time, AdvisorConquestPolicy policy)
    {
        CancelWaitingSteamLobby();
        try
        {
            var transport = ThreeLanTransport.Host(port, ThreePlayerName, GetPreferredLanAddress());
            SwitchTo(new ThreeGameScreen(_settings, ShowGallery,
                new ThreeNetworkController(transport, time, policy, CreateThreePlayerProfile()), themes: _themes));
        }
        catch (Exception e) { ShowError(e.Message); }
    }
    private void StartThreeLanJoin(string address, int port)
    {
        CancelWaitingSteamLobby();
        try
        {
            var transport = ThreeLanTransport.Join(address, port, ThreePlayerName);
            SwitchTo(new ThreeGameScreen(_settings, ShowGallery,
                new ThreeNetworkController(transport, TimeControl.Unlimited, AdvisorConquestPolicy.SharedTerritory,
                    CreateThreePlayerProfile()),
                themes: _themes));
        }
        catch (Exception e) { ShowError(e.Message); }
    }
    private async void StartThreeSteam(TimeControl time, AdvisorConquestPolicy policy)
    {
#if GODOT_WINDOWS
        if (_threeSteam is null) { ShowError(_steamUnavailableReason); return; }
        CancelWaitingSteamLobby();
        try { await _threeSteam.Create(time, policy); }
        catch (OperationCanceledException) { }
        catch (Exception e) { ShowError(e.Message); }
#else
        await Task.CompletedTask; ShowError(_steamUnavailableReason);
#endif
    }
#if GODOT_WINDOWS
    private void InitializeThreeSteam()
    {
        _threeSteam = new();
        _threeSteam.Error += ShowError;
        _threeSteam.Ready += (transport, time, policy) =>
        {
            _steam!.ReleaseThreeLobby(transport.Lobby);
            _steam.TryGetLocalPlayerProfile(out var steamProfile);
            SwitchTo(new ThreeGameScreen(_settings, ShowGallery,
                new ThreeNetworkController(transport, time, policy, CreateThreePlayerProfile(steamProfile)), transport.InviteFriends, _themes));
        };
        _steam!.ThreeLobbyEntered += lobby => _threeSteam.Enter(lobby.Value);
    }
#endif
}
