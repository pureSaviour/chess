using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Gomoku;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.AI;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.Settings;
using Xiangqi.Presentation.UI;
using Xiangqi.Presentation.Profiles;

namespace Xiangqi.Presentation.Screens;

public partial class GomokuGalleryScreen : Control
{
    public GomokuGalleryScreen(Action back, Action<GomokuRoomOptions> local, Action<int,GomokuRoomOptions> host,
        Action<string,int> join, Action<GomokuRoomOptions> steam, ClassicGalleryEntry entry)
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); var margin=UiStyle.CreatePageMargin(54,34); AddChild(margin); var root=new VBoxContainer(); root.AddThemeConstantOverride("separation",16); margin.AddChild(root);
        var header=new HBoxContainer(); root.AddChild(header); var title=UiStyle.CreateTitle(entry==ClassicGalleryEntry.Local?"五子棋 · 本地对决":entry==ClassicGalleryEntry.Lan?"五子棋 · 局域网":"五子棋 · Steam",34); title.SizeFlagsHorizontal=SizeFlags.ExpandFill; header.AddChild(title); var leave=UiStyle.CreateButton("返回画廊"); leave.Pressed+=back; header.AddChild(leave);
        var center=new CenterContainer{SizeFlagsVertical=SizeFlags.ExpandFill}; root.AddChild(center); var panel=UiStyle.CreatePanel(UiStyle.PanelLight,14); panel.CustomMinimumSize=new(680,entry==ClassicGalleryEntry.Lan?530:430); center.AddChild(panel); var inset=new MarginContainer(); foreach(var key in new[]{"margin_left","margin_right","margin_top","margin_bottom"}) inset.AddThemeConstantOverride(key,28); panel.AddChild(inset); var content=new VBoxContainer(); content.AddThemeConstantOverride("separation",16); inset.AddChild(content);
        content.AddChild(new Label{Text="黑子先行；禁手开启时仅黑方受长连、双四、双三限制。",AutowrapMode=TextServer.AutowrapMode.WordSmart});
        var boardRow=new HBoxContainer(); content.AddChild(boardRow); boardRow.AddChild(new Label{Text="棋盘路数",CustomMinimumSize=new(140,0)}); var size=new SpinBox{MinValue=9,MaxValue=25,Value=15,Step=1,CustomMinimumSize=new(180,42)}; boardRow.AddChild(size);
        var forbidden=new CheckButton{Text="启用连珠禁手规则",ButtonPressed=false}; content.AddChild(forbidden);
        var timeEnabled=new CheckButton{Text="启用计时（局时 + 步时）",ButtonPressed=true};content.AddChild(timeEnabled);var timeRow=new HBoxContainer();content.AddChild(timeRow);timeRow.AddChild(new Label{Text="局时（分钟）"});var gameMinutes=new SpinBox{MinValue=1,MaxValue=180,Value=20,Step=1};timeRow.AddChild(gameMinutes);timeRow.AddChild(new Label{Text="步时（秒）"});var moveSeconds=new SpinBox{MinValue=5,MaxValue=300,Value=60,Step=5};timeRow.AddChild(moveSeconds);timeEnabled.Toggled+=value=>{gameMinutes.Editable=value;moveSeconds.Editable=value;};
        var color=new OptionButton{CustomMinimumSize=new(240,42)}; color.AddItem("房主执黑"); color.AddItem("房主执白"); color.AddItem("随机猜先"); if(entry!=ClassicGalleryEntry.Local) content.AddChild(color);
        GomokuRoomOptions Options(){var hostColor=color.Selected==1?StoneColor.White:color.Selected==2?(Random.Shared.Next(2)==0?StoneColor.Black:StoneColor.White):StoneColor.Black;var time=timeEnabled.ButtonPressed?new TimeControl((int)gameMinutes.Value*60,(int)moveSeconds.Value):TimeControl.Unlimited; return new((int)size.Value,forbidden.ButtonPressed?GomokuRuleSet.RenjuForbiddenMoves:GomokuRuleSet.Freestyle,hostColor,time);}
        if(entry==ClassicGalleryEntry.Local){var start=UiStyle.CreateButton("开始本地对决",true); start.Pressed+=()=>local(Options()); content.AddChild(start);}
        else if(entry==ClassicGalleryEntry.Steam){var start=UiStyle.CreateButton("创建房间并邀请好友",true); start.Pressed+=()=>steam(Options()); content.AddChild(start);}
        else {var form=new GridContainer{Columns=2}; content.AddChild(form); var address=new LineEdit{Text="127.0.0.1",PlaceholderText="房主局域网 IP",CustomMinimumSize=new(390,42)}; var port=new SpinBox{MinValue=1024,MaxValue=65535,Value=EnetMatchTransport.DefaultPort,Step=1,CustomMinimumSize=new(180,42)}; form.AddChild(address);form.AddChild(port);var create=UiStyle.CreateButton("创建房间",true);create.Pressed+=()=>host((int)port.Value,Options());form.AddChild(create);var connect=UiStyle.CreateButton("加入房间");connect.Pressed+=()=>join(address.Text.Trim(),(int)port.Value);form.AddChild(connect);}
    }
}

public partial class GomokuGameScreen : Control
{
    private readonly IGomokuMatchController _controller; private readonly GomokuBoardView _board=new(); private readonly Label _status=new(); private readonly Button _save; private readonly Button _rematch; private readonly MatchPlayerProfiles? _profiles; private readonly RawMatchProfileExchange? _profileExchange; private readonly RawTwoPlayerRoomChat? _roomChat; private readonly TwoPlayerProfileRow _topPlayer=new(false); private readonly TwoPlayerProfileRow _bottomPlayer=new(true); private bool _profilesDirty=true; private StoneColor? _profileColor; private bool _recent; private long _displayedPly;
    public bool AreMoveNumbersVisible => _board.MoveNumbersVisible;
    public bool HasUnifiedPlayerCards => _topPlayer.GetParent() is not null && _bottomPlayer.GetParent() is not null;
    public Vector2 BoardAreaSize => _board.Size;
    public void SetMoveNumbersForSmoke(bool visible) => _board.SetMoveNumbersVisible(visible);
    public GomokuGameScreen(IGomokuMatchController controller,string roomTitle,Action back,Node? driver=null,MatchPlayerProfiles? profiles=null)
    {
        _controller=controller;_profiles=profiles;if(driver is ITwoPlayerPacketTransport packetTransport){if(profiles is not null){_profileExchange=new RawMatchProfileExchange(packetTransport,profiles);_profileExchange.OpponentChanged+=OnProfileChanged;}_roomChat=new RawTwoPlayerRoomChat(packetTransport,()=>_controller.LocalColor is { } color?(int)color:null);}if(driver is not null) AddChild(driver); SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); var margin=UiStyle.CreatePageMargin(24,14);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",7);margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);var title=UiStyle.CreateTitle("五子棋",28);title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);header.AddChild(_status);_rematch=UiStyle.CreateButton("再来一局");_rematch.Visible=_controller is IRematchableMatchController;_rematch.Pressed+=RequestRematch;header.AddChild(_rematch);_save=UiStyle.CreateButton("保存棋谱");_save.Disabled=true;_save.Pressed+=Save;header.AddChild(_save);
        var settings=UiStyle.CreateButton("局内设置");header.AddChild(settings);var settingsDialog=new AcceptDialog{Title="五子棋 · 局内设置",OkButtonText="完成",MinSize=new(500,210)};AddChild(settingsDialog);var settingsMargin=new MarginContainer();foreach(var key in new[]{"margin_left","margin_right","margin_top","margin_bottom"})settingsMargin.AddThemeConstantOverride(key,20);settingsDialog.AddChild(settingsMargin);var settingsColumn=new VBoxContainer();settingsColumn.AddThemeConstantOverride("separation",12);settingsMargin.AddChild(settingsColumn);var roomInfo=new Label{Text=$"房间信息：{roomTitle}",AutowrapMode=TextServer.AutowrapMode.WordSmart};UiStyle.SetMuted(roomInfo,13);settingsColumn.AddChild(roomInfo);var moveNumbers=new CheckButton{Text="在棋子上显示手数",ButtonPressed=false};moveNumbers.Toggled+=_board.SetMoveNumbersVisible;settingsColumn.AddChild(moveNumbers);settings.Pressed+=()=>settingsDialog.PopupCentered(new(500,210));
        var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;header.AddChild(leave);var body=new HBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};body.AddThemeConstantOverride("separation",10);root.AddChild(body);body.AddChild(_board);var rail=new VBoxContainer{CustomMinimumSize=new(300,0),SizeFlagsVertical=SizeFlags.ExpandFill};rail.AddThemeConstantOverride("separation",6);body.AddChild(rail);rail.AddChild(_topPlayer);if(_roomChat is not null)rail.AddChild(new TwoPlayerChatPanel(_roomChat,()=>_controller.LocalColor is { } color?(int)color:null));else rail.AddChild(new Control{SizeFlagsVertical=SizeFlags.ExpandFill});rail.AddChild(_bottomPlayer);var hint=new Label{Text="SGF FF[4] / GM[4] · 点击交叉点落子",HorizontalAlignment=HorizontalAlignment.Center};UiStyle.SetMuted(hint,13);root.AddChild(hint);
        _board.PointPressed+=OnPoint;_controller.Changed+=OnChanged;_controller.Message+=OnMessage;ApplyProfiles();Render(false);
    }
    public override async void _Ready(){try{await _controller.InitializeAsync();Render(false);}catch(Exception error){OnMessage(error.Message);}}
    public override void _Process(double delta){_controller.Poll();_controller.Tick(delta);UpdateClocks();}
    private async void OnPoint(GridPoint point){if(!_controller.CanPlace)return;var result=await _controller.SubmitAsync(point);if(!result.Accepted)OnMessage(RejectText(result.Rejection,result.Error));}
    private void OnChanged(){var animate=_controller.Snapshot.PlyNumber==_displayedPly+1;ApplyProfiles();Render(animate);}
    private void OnProfileChanged(){_profilesDirty=true;ApplyProfiles();}
    private void ApplyProfiles(){if(!_profilesDirty&&_profileColor==_controller.LocalColor)return;_profilesDirty=false;_profileColor=_controller.LocalColor;var bottom=_controller.LocalColor??StoneColor.Black;var top=bottom==StoneColor.Black?StoneColor.White:StoneColor.Black;_bottomPlayer.Apply(_profiles?.Local,StoneName(bottom),_controller.LocalColor is null?"本地双人":$"你是{StoneName(bottom)}",string.Empty);_topPlayer.Apply(_profiles?.Opponent,StoneName(top),_controller.LocalColor is null?"本地双人":$"对方是{StoneName(top)}",string.Empty);}
    private void Render(bool animate){var snapshot=_controller.Snapshot;if(!snapshot.IsFinished&&_recent){_recent=false;_save.Disabled=true;_save.Text="保存棋谱";} _board.SetPosition(snapshot,animate);_displayedPly=snapshot.PlyNumber;_status.Text=!_controller.IsConnected?"等待联机连接":!_controller.IsReady?"等待房主配置":snapshot.IsFinished?snapshot.Winner is { } winner?$"{StoneName(winner)}胜":"和棋":$"{StoneName(snapshot.SideToMove)}行棋"+(_controller.LocalColor is { } local?$" · 你是{StoneName(local)}":"");if(_controller is IRematchableMatchController rematch){_rematch.Disabled=!rematch.CanRequestRematch;_rematch.Text=rematch.Rematch.LocalReady?"等待对手同意":"再来一局";}UpdateClocks();if(!snapshot.IsFinished)return;_save.Disabled=false;if(_recent)return;_recent=true;try{GomokuRecordStore.SaveRecent(_controller.CreateRecord(),BuildPresentation(snapshot));}catch(Exception error){GD.PushWarning(error.Message);}}
    private async void RequestRematch(){if(_controller is not IRematchableMatchController value)return;try{await value.RequestRematchAsync();Render(false);}catch(Exception error){OnMessage(error.Message);}}
    private void UpdateClocks(){var local=_controller.LocalColor??StoneColor.Black;var other=local==StoneColor.Black?StoneColor.White:StoneColor.Black;var c=_controller.Clock;string Text(StoneColor s){var value=s==StoneColor.Black?c.FirstGameSeconds:c.SecondGameSeconds;return _controller.TimeControl.GameSeconds==0?"局时 ∞":$"局时 {TimeSpan.FromSeconds(value):mm\\:ss}";}_bottomPlayer.SetTrailing(Text(local));_topPlayer.SetTrailing(Text(other));var active=!_controller.Snapshot.IsFinished;_bottomPlayer.SetMoveClock(MoveClockIndicator.From(c.MoveSeconds,_controller.TimeControl.MoveSeconds,active&&c.ActiveSeat==(int)local));_topPlayer.SetMoveClock(MoveClockIndicator.From(c.MoveSeconds,_controller.TimeControl.MoveSeconds,active&&c.ActiveSeat==(int)other));}
    private void OnMessage(string message)=>_status.Text=message;
    private void Save(){try{GomokuRecordStore.SavePermanent(_controller.CreateRecord(),BuildPresentation(_controller.Snapshot));_save.Disabled=true;_save.Text="已保存";}catch(Exception error){OnMessage(error.Message);}}
    private MatchRecordPresentation BuildPresentation(GomokuSnapshot snapshot){var localColor=_controller.LocalColor??StoneColor.Black;var opponentColor=localColor==StoneColor.Black?StoneColor.White:StoneColor.Black;RecordPlayer Player(PlayerProfileSnapshot p,bool local)=>new(p.DisplayName,local,p.Avatar?.Width??0,p.Avatar?.Height??0,p.Avatar?.CopyRgba());var local=_profiles is null?new RecordPlayer(StoneName(localColor),true):Player(_profiles.Local,true);var opponent=_profiles is null?new RecordPlayer(StoneName(opponentColor),false):Player(_profiles.Opponent,false);var result=snapshot.Winner is null?"和":snapshot.Winner==localColor?"胜":"败";return new(DateTimeOffset.Now,"五子棋",local,[opponent],result);}
    private static string StoneName(StoneColor color)=>color==StoneColor.Black?"黑方":"白方";
    private static string RejectText(GomokuMoveRejection rejection,string? fallback)=>rejection switch{GomokuMoveRejection.Overline=>"黑方长连禁手。",GomokuMoveRejection.DoubleFour=>"黑方双四禁手。",GomokuMoveRejection.DoubleThree=>"黑方双三禁手。",GomokuMoveRejection.Occupied=>"该交叉点已有棋子。",_=>fallback??"当前不能落子。"};
    public override void _ExitTree(){_board.PointPressed-=OnPoint;_controller.Changed-=OnChanged;_controller.Message-=OnMessage;if(_profileExchange is not null)_profileExchange.OpponentChanged-=OnProfileChanged;_profileExchange?.Dispose();_roomChat?.Dispose();_profiles?.Dispose();_= _controller.DisposeAsync();}
}

public partial class GomokuRecordScreen : Control
{
    private readonly List<ImageTexture> _textures=[];
    private readonly RecordExportDialog _exportDialog;
    private readonly AcceptDialog _actionError=new(){Title="记录操作失败"};
    public GomokuRecordScreen(bool saved,Action back,Action<GomokuGameRecord> replay)
    {_exportDialog=new RecordExportDialog(".sgf","SGF FF[4] / GM[4]",GomokuRecordStore.Export);SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(60,36);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",12);margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);var title=UiStyle.CreateTitle(saved?"五子棋 · 保存对局":"五子棋 · 最近对局");title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;header.AddChild(leave);root.AddChild(RecordEntryRow.CreateHeadings(true));var scroll=new ScrollContainer{SizeFlagsVertical=SizeFlags.ExpandFill,CustomMinimumSize=new Vector2(900,360)};root.AddChild(scroll);var rows=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};rows.AddThemeConstantOverride("separation",10);scroll.AddChild(rows);AddChild(_exportDialog);AddChild(_actionError);foreach(var entry in GomokuRecordStore.ListEntries(saved)){bool SaveEntry(){try{GomokuRecordStore.SavePermanent(entry);return true;}catch(Exception error){_actionError.DialogText=error.Message;_actionError.PopupCentered(new(620,170));return false;}}rows.AddChild(RecordEntryRow.Create(entry.Presentation,()=>replay(GomokuRecordStore.Load(entry.RecordPath)),_textures,saved?null:SaveEntry,()=>_exportDialog.Open(entry.RecordPath)));}}
    public override void _ExitTree(){foreach(var texture in _textures)texture.Dispose();_textures.Clear();}
}

public partial class GomokuReplayScreen : Control
{
    private readonly List<GomokuSnapshot> _positions=[];
    private readonly GomokuBoardView _board=new();
    private readonly Label _ply=new();
    private readonly Label? _score;
    private readonly ReplayEvaluationGraph? _graph;
    private readonly GomokuReplayAnalysisCoordinator? _analysis;
    private readonly Godot.Timer? _analysisDebounce;
    private int _index;

    public GomokuReplayScreen(AppSettings settings,GomokuGameRecord record,Action back,string? enginePath=null)
    {
        var game=new GomokuGame(record.BoardSize,record.Rules,record.Setup,record.SideToMove);
        _positions.Add(game.Snapshot);
        foreach(var move in record.Moves){var result=game.TryPlace(move.Point,move.Color);if(!result.IsSuccess)break;_positions.Add(game.Snapshot);}
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(32,20);AddChild(margin);
        var root=new VBoxContainer();margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);
        var title=UiStyle.CreateTitle("五子棋复盘",30);title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);header.AddChild(_ply);
        var leave=UiStyle.CreateButton("返回");leave.Pressed+=back;header.AddChild(leave);
        var body=new HBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};body.AddThemeConstantOverride("separation",16);root.AddChild(body);
        var center=new CenterContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};center.AddChild(_board);body.AddChild(center);
        if(!string.IsNullOrWhiteSpace(enginePath)&&File.Exists(enginePath))
        {
            var panel=new VBoxContainer{CustomMinimumSize=new(350,0)};body.AddChild(panel);panel.AddChild(new Label{Text="Rapfi 胜率分析"});
            _score=new Label{Text="正在计算…",HorizontalAlignment=HorizontalAlignment.Right};panel.AddChild(_score);
            _graph=new ReplayEvaluationGraph();_graph.Configure(_positions.Count);_graph.SetLabels("黑优","白优");panel.AddChild(_graph);
            var hint=new Label{Text="曲线为 Rapfi 给出的黑方胜率 · 50% 为均衡",AutowrapMode=TextServer.AutowrapMode.WordSmart};UiStyle.SetMuted(hint,13);panel.AddChild(hint);
            var engine=new PiskvorkProcessGomokuAiEngine(enginePath);
            _analysis=new GomokuReplayAnalysisCoordinator(engine,_positions,settings.ReplayAnalysisMilliseconds);_analysis.AnalysisAvailable+=OnAnalysisAvailable;
            _analysisDebounce=new Godot.Timer{OneShot=true,WaitTime=.15};_analysisDebounce.Timeout+=()=>_ = AnalyzeAsync(_index);AddChild(_analysisDebounce);
        }
        var timeline=new HSlider{MinValue=0,MaxValue=Math.Max(0,_positions.Count-1),Step=1,SizeFlagsHorizontal=SizeFlags.ExpandFill};root.AddChild(timeline);
        var controls=new HBoxContainer{Alignment=BoxContainer.AlignmentMode.Center};root.AddChild(controls);var previous=UiStyle.CreateButton("◀ 上一步");var next=UiStyle.CreateButton("下一步 ▶");controls.AddChild(previous);controls.AddChild(next);
        void Render(bool analyze=true){_board.SetPosition(_positions[_index]);_ply.Text=$"第 {_index}/{_positions.Count-1} 手";timeline.SetValueNoSignal(_index);previous.Disabled=_index==0;next.Disabled=_index==_positions.Count-1;_graph?.SetCurrent(_index);if(_analysis?.Cache.TryGetValue(_index,out var point)==true)Apply(point);else{_board.SetAnalysisPoint(null);if(_score is not null)_score.Text=_positions[_index].IsFinished?"终局":"正在计算…";}if(analyze&&_analysisDebounce is not null)_analysisDebounce.Start();}
        previous.Pressed+=()=>{if(_index>0)_index--;Render();};next.Pressed+=()=>{if(_index+1<_positions.Count)_index++;Render();};timeline.ValueChanged+=value=>{_index=Math.Clamp((int)Math.Round(value),0,_positions.Count-1);Render();};Render();
    }
    private async Task AnalyzeAsync(int index){try{if(_analysis is not null)await _analysis.SelectAndSweepAsync(index);}catch(Exception error){if(_score is not null)_score.Text=$"引擎错误：{error.Message}";}}
    private void OnAnalysisAvailable(GomokuReplayAnalysisPoint point){_graph?.SetWinRatePoint(point);if(point.PositionIndex==_index)Apply(point);}
    private void Apply(GomokuReplayAnalysisPoint point){_board.SetAnalysisPoint(point.BestMove);if(_score is not null)_score.Text=point.BestMove is { } move?$"黑方胜率 {point.BlackWinRate:P1} · 最优解 ({move.Column+1},{move.Row+1})":$"黑方胜率 {point.BlackWinRate:P1}";}
    public override void _ExitTree(){if(_analysis is null)return;_analysis.AnalysisAvailable-=OnAnalysisAvailable;_= _analysis.DisposeAsync();}
}

public partial class GomokuImportScreen : Control
{
    public GomokuImportScreen(bool position,Action back,Action<GomokuGameRecord> open)
    {SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(54,34);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",12);margin.AddChild(root);var title=UiStyle.CreateTitle(position?"五子棋 · 局面导入":"五子棋 · SGF 导入");root.AddChild(title);var editor=new TextEdit{PlaceholderText=position?"粘贴含 AB/AW/PL 的 SGF 局面":"粘贴 SGF FF[4] / GM[4] 棋谱",SizeFlagsVertical=SizeFlags.ExpandFill};root.AddChild(editor);var status=new Label();root.AddChild(status);var actions=new HBoxContainer();root.AddChild(actions);var parse=UiStyle.CreateButton("验证并打开",true);parse.Pressed+=()=>{if(GomokuSgfCodec.TryParse(editor.Text,out var record,out var error))open(record!);else status.Text=error;};actions.AddChild(parse);var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;actions.AddChild(leave);}
}

public partial class GomokuAiSettingsScreen : Control
{
    private readonly VBoxContainer _advancedSettings;
    public bool AreAdvancedSettingsVisible => _advancedSettings.Visible;
    public bool HasManualEnginePicker => FindChildren("*","FileDialog",true,false).Count>0;
    public bool HasReplayAnalysisTimeControl => FindChildren("ReplayAnalysisThinkSeconds","SpinBox",true,false).Count==1;

    public GomokuAiSettingsScreen(AppSettings current,EnginePackageInstaller installer,Action<AppSettings> save,Action back)
    {
        var draft=current.Copy();SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(60,34);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",14);margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);var title=UiStyle.CreateTitle("五子棋 · Rapfi 设置");title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;header.AddChild(leave);var panel=UiStyle.CreatePanel(UiStyle.PanelLight,14);panel.CustomMinimumSize=new(760,500);panel.SizeFlagsVertical=SizeFlags.ExpandFill;root.AddChild(panel);var scroll=new ScrollContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill,SizeFlagsVertical=SizeFlags.ExpandFill};panel.AddChild(scroll);var content=new VBoxContainer{SizeFlagsHorizontal=SizeFlags.ExpandFill};content.AddThemeConstantOverride("separation",16);scroll.AddChild(content);
        var basic=new GridContainer{Columns=2};basic.AddThemeConstantOverride("h_separation",20);basic.AddThemeConstantOverride("v_separation",14);content.AddChild(basic);
        basic.AddChild(new Label{Text="难度"});var difficulty=new OptionButton();foreach(var name in new[]{"入门","简单","适中","困难","大师","自定义"})difficulty.AddItem(name);difficulty.Select(draft.GomokuAiDifficulty);basic.AddChild(difficulty);
        basic.AddChild(new Label{Text="玩家执子"});var side=new OptionButton();side.AddItem("玩家执黑");side.AddItem("玩家执白");side.AddItem("随机猜先");side.Select(draft.GomokuAiSideChoice);basic.AddChild(side);
        basic.AddChild(new Label{Text="棋盘路数（Rapfi 9–22）"});var size=new SpinBox{MinValue=9,MaxValue=22,Value=Math.Min(draft.GomokuAiBoardSize,22),Step=1};basic.AddChild(size);
        basic.AddChild(new Label{Text="规则"});var rules=new CheckButton{Text="启用连珠禁手",ButtonPressed=draft.GomokuAiForbiddenMoves};basic.AddChild(rules);
        basic.AddChild(new Label{Text="局时（分钟，0 不限）"});var gameTime=new SpinBox{MinValue=0,MaxValue=180,Value=draft.AiGameSeconds/60.0,Step=1};basic.AddChild(gameTime);
        basic.AddChild(new Label{Text="步时（秒，0 不限）"});var moveTime=new SpinBox{MinValue=0,MaxValue=300,Value=draft.AiMoveSeconds,Step=5};basic.AddChild(moveTime);

        var advancedToggle=new CheckButton{Text="高级设置"};content.AddChild(advancedToggle);
        _advancedSettings=new VBoxContainer{Visible=false,SizeFlagsHorizontal=SizeFlags.ExpandFill};_advancedSettings.AddThemeConstantOverride("separation",12);content.AddChild(_advancedSettings);advancedToggle.Toggled+=visible=>_advancedSettings.Visible=visible;
        _advancedSettings.AddChild(new Label{Text="应用仅使用一键配置的固定 Rapfi 版本，不支持选择其他引擎程序。模型和配置文件由安装器统一校验。",AutowrapMode=TextServer.AutowrapMode.WordSmart});
        var installRow=new HBoxContainer();installRow.AddThemeConstantOverride("separation",12);_advancedSettings.AddChild(installRow);var install=UiStyle.CreateButton("一键下载并配置 Rapfi",true);installRow.AddChild(install);var installStatus=new Label{Text=installer.TryGetInstalled(EnginePackageKind.Rapfi,out var installed)?$"已配置 Rapfi {installed.Version}":"尚未配置",SizeFlagsHorizontal=SizeFlags.ExpandFill,AutowrapMode=TextServer.AutowrapMode.WordSmart};installRow.AddChild(installStatus);var consent=new ConfirmationDialog{Title="Rapfi 许可确认",DialogText=EnginePackageInstaller.LicenseNotice(EnginePackageKind.Rapfi),OkButtonText="同意并下载",CancelButtonText="取消"};AddChild(consent);var driver=new EngineInstallDriver(installer);AddChild(driver);
        var grid=new GridContainer{Columns=2};grid.AddThemeConstantOverride("h_separation",20);grid.AddThemeConstantOverride("v_separation",14);_advancedSettings.AddChild(grid);
        grid.AddChild(new Label{Text="自定义每步思考（秒）"});var think=new SpinBox{MinValue=.1,MaxValue=120,Value=draft.GomokuAiThinkMilliseconds/1000.0,Step=.1};grid.AddChild(think);
        grid.AddChild(new Label{Text="复盘每局面思考（秒）"});var replayThink=new SpinBox{Name="ReplayAnalysisThinkSeconds",MinValue=.1,MaxValue=120,Value=draft.ReplayAnalysisMilliseconds/1000.0,Step=.1};grid.AddChild(replayThink);
        grid.AddChild(new Label{Text="最大深度（0=不限）"});var depth=new SpinBox{MinValue=0,MaxValue=128,Value=draft.GomokuAiMaxDepth,Step=1};grid.AddChild(depth);
        grid.AddChild(new Label{Text="线程数"});var threads=new SpinBox{MinValue=1,MaxValue=256,Value=draft.GomokuAiThreads,Step=1};grid.AddChild(threads);
        grid.AddChild(new Label{Text="内存（MB）"});var memory=new SpinBox{MinValue=16,MaxValue=4096,Value=draft.GomokuAiMemoryMegabytes,Step=16};grid.AddChild(memory);
        var note=new Label{Text="深度与时间会同时限制搜索；0 表示不限制深度。外部进程仅支持 Windows 桌面版。",AutowrapMode=TextServer.AutowrapMode.WordSmart};UiStyle.SetMuted(note,13);_advancedSettings.AddChild(note);var saveButton=UiStyle.CreateButton("保存 Rapfi 设置",true);content.AddChild(saveButton);
        void Capture(){draft.GomokuAiSideChoice=side.Selected;draft.GomokuAiDifficulty=difficulty.Selected;draft.GomokuAiBoardSize=(int)size.Value;draft.GomokuAiForbiddenMoves=rules.ButtonPressed;draft.AiGameSeconds=(int)Math.Round(gameTime.Value*60);draft.AiMoveSeconds=(int)moveTime.Value;draft.GomokuAiThinkMilliseconds=(int)Math.Round(think.Value*1000);draft.ReplayAnalysisMilliseconds=(int)Math.Round(replayThink.Value*1000);draft.GomokuAiMaxDepth=(int)depth.Value;draft.GomokuAiThreads=(int)threads.Value;draft.GomokuAiMemoryMegabytes=(int)memory.Value;}saveButton.Pressed+=()=>{Capture();save(draft);};
        install.Pressed+=()=>consent.PopupCentered(new Vector2I(650,260));consent.Confirmed+=()=>{Capture();install.Disabled=saveButton.Disabled=true;installStatus.Text="准备下载…";driver.Start(EnginePackageKind.Rapfi);};driver.ProgressChanged+=value=>installStatus.Text=value.TotalBytes is>0?$"{value.Stage} {Math.Clamp(value.CompletedBytes*100/value.TotalBytes.Value,0,100)}%":value.Stage;driver.Completed+=result=>{installStatus.Text=$"已配置 Rapfi {result.Version}";save(draft);};driver.Failed+=error=>{install.Disabled=saveButton.Disabled=false;installStatus.Text=$"安装失败：{error}";};
#if GODOT_ANDROID
        install.Disabled=true;install.TooltipText="Android 当前不支持外部进程引擎。";
#endif
    }
}
