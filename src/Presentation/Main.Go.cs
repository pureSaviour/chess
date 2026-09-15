using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Go;
using Xiangqi.Core.Domain;
using Xiangqi.Core.Domain.Go;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Screens;

namespace Xiangqi.Presentation;

public partial class Main
{
    private void ShowGoGallery(ClassicGalleryEntry entry) => SwitchTo(new GoGalleryScreen(
        ShowGallery, StartGoLocal, StartGoLanHost, StartGoLanClient, StartGoSteamInvite, entry));
    private void StartGoLocal(GoRoomOptions options) => SwitchTo(new GoGameScreen(
        new LocalGoMatchController(options.TimeControl), "本地对决", ShowGallery, profiles: CreateOfflineProfiles("本地玩家 2")));
    private void StartGoLanHost(int port, GoRoomOptions options)
    {
        try
        {
            var transport = EnetMatchTransport.Host(port);
            SwitchTo(new GoGameScreen(new NetworkGoMatchController(transport, options),
                $"房主 · {GetPreferredLanAddress()}:{port}", ShowGallery, transport, CreateLanProfiles()));
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    private void StartGoLanClient(string address, int port)
    {
        try
        {
            var transport = EnetMatchTransport.Join(address, port);
            SwitchTo(new GoGameScreen(new NetworkGoMatchController(transport, null),
                $"客户端 · {address}:{port}", ShowGallery, transport, CreateLanProfiles()));
        }
        catch (Exception error) { ShowError(error.Message); }
    }
    private void StartGoSteamInvite(GoRoomOptions options) => StartSteamInvite(new NetworkMatchConfiguration(
        options.TimeControl, options.HostColor == GoStone.Black ? PieceSide.Red : PieceSide.Black,
        TwoPlayerGameMode.Go, GoGame.StandardBoardSize, 0));
    private void ShowGoRecords(bool saved) => SwitchTo(new GoRecordScreen(saved, ShowGallery,
        record => SwitchTo(new GoReplayScreen(record, () => ShowGoRecords(saved)))));
}
