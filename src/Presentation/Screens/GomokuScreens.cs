using Godot;
using Xiangqi.Core.Application;
using Xiangqi.Core.Application.Gomoku;
using Xiangqi.Core.Domain.Gomoku;
using Xiangqi.Core.Domain.Grid;
using Xiangqi.Presentation.Boards;
using Xiangqi.Presentation.Networking;
using Xiangqi.Presentation.Records;
using Xiangqi.Presentation.UI;

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
        var color=new OptionButton{CustomMinimumSize=new(240,42)}; color.AddItem("房主执黑"); color.AddItem("房主执白"); color.AddItem("随机猜先"); if(entry!=ClassicGalleryEntry.Local) content.AddChild(color);
        GomokuRoomOptions Options(){var hostColor=color.Selected==1?StoneColor.White:color.Selected==2?(Random.Shared.Next(2)==0?StoneColor.Black:StoneColor.White):StoneColor.Black; return new((int)size.Value,forbidden.ButtonPressed?GomokuRuleSet.RenjuForbiddenMoves:GomokuRuleSet.Freestyle,hostColor);}
        if(entry==ClassicGalleryEntry.Local){var start=UiStyle.CreateButton("开始本地对决",true); start.Pressed+=()=>local(Options()); content.AddChild(start);}
        else if(entry==ClassicGalleryEntry.Steam){var start=UiStyle.CreateButton("创建房间并邀请好友",true); start.Pressed+=()=>steam(Options()); content.AddChild(start);}
        else {var form=new GridContainer{Columns=2}; content.AddChild(form); var address=new LineEdit{Text="127.0.0.1",PlaceholderText="房主局域网 IP",CustomMinimumSize=new(390,42)}; var port=new SpinBox{MinValue=1024,MaxValue=65535,Value=EnetMatchTransport.DefaultPort,Step=1,CustomMinimumSize=new(180,42)}; form.AddChild(address);form.AddChild(port);var create=UiStyle.CreateButton("创建房间",true);create.Pressed+=()=>host((int)port.Value,Options());form.AddChild(create);var connect=UiStyle.CreateButton("加入房间");connect.Pressed+=()=>join(address.Text.Trim(),(int)port.Value);form.AddChild(connect);}
    }
}

public partial class GomokuGameScreen : Control
{
    private readonly IGomokuMatchController _controller; private readonly GomokuBoardView _board=new(); private readonly Label _status=new(); private readonly Button _save; private bool _recent; private long _displayedPly;
    public GomokuGameScreen(IGomokuMatchController controller,string roomTitle,Action back,Node? driver=null)
    {
        _controller=controller; if(driver is not null) AddChild(driver); SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); var margin=UiStyle.CreatePageMargin(30,18);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",8);margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);var title=UiStyle.CreateTitle($"五子棋 · {roomTitle}",28);title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);header.AddChild(_status);_save=UiStyle.CreateButton("保存棋谱");_save.Disabled=true;_save.Pressed+=Save;header.AddChild(_save);var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;header.AddChild(leave);var center=new CenterContainer{SizeFlagsVertical=SizeFlags.ExpandFill};center.AddChild(_board);root.AddChild(center);var hint=new Label{Text="SGF FF[4] / GM[4] · 点击交叉点落子",HorizontalAlignment=HorizontalAlignment.Center};UiStyle.SetMuted(hint,13);root.AddChild(hint);
        _board.PointPressed+=OnPoint;_controller.Changed+=OnChanged;_controller.Message+=OnMessage;Render(false);
    }
    private async void OnPoint(GridPoint point){if(!_controller.CanPlace)return;var result=await _controller.SubmitAsync(point);if(!result.Accepted)OnMessage(RejectText(result.Rejection,result.Error));}
    private void OnChanged(){var animate=_controller.Snapshot.PlyNumber==_displayedPly+1;Render(animate);}
    private void Render(bool animate){var snapshot=_controller.Snapshot;_board.SetPosition(snapshot,animate);_displayedPly=snapshot.PlyNumber;_status.Text=!_controller.IsConnected?"等待联机连接":!_controller.IsReady?"等待房主配置":snapshot.IsFinished?snapshot.Winner is { } winner?$"{StoneName(winner)}胜":"和棋":$"{StoneName(snapshot.SideToMove)}行棋"+(_controller.LocalColor is { } local?$" · 你是{StoneName(local)}":"");if(!snapshot.IsFinished)return;_save.Disabled=false;if(_recent)return;_recent=true;try{GomokuRecordStore.SaveRecent(_controller.CreateRecord());}catch(Exception error){GD.PushWarning(error.Message);}}
    private void OnMessage(string message)=>_status.Text=message;
    private void Save(){try{GomokuRecordStore.SavePermanent(_controller.CreateRecord());_save.Disabled=true;_save.Text="已保存";}catch(Exception error){OnMessage(error.Message);}}
    private static string StoneName(StoneColor color)=>color==StoneColor.Black?"黑方":"白方";
    private static string RejectText(GomokuMoveRejection rejection,string? fallback)=>rejection switch{GomokuMoveRejection.Overline=>"黑方长连禁手。",GomokuMoveRejection.DoubleFour=>"黑方双四禁手。",GomokuMoveRejection.DoubleThree=>"黑方双三禁手。",GomokuMoveRejection.Occupied=>"该交叉点已有棋子。",_=>fallback??"当前不能落子。"};
    public override void _ExitTree(){_board.PointPressed-=OnPoint;_controller.Changed-=OnChanged;_controller.Message-=OnMessage;_= _controller.DisposeAsync();}
}

public partial class GomokuRecordScreen : Control
{
    public GomokuRecordScreen(bool saved,Action back,Action<GomokuGameRecord> replay)
    {SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(54,34);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",12);margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);var title=UiStyle.CreateTitle(saved?"五子棋 · 保存对局":"五子棋 · 最近对局");title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;header.AddChild(leave);var rows=new VBoxContainer{SizeFlagsVertical=SizeFlags.ExpandFill};root.AddChild(rows);foreach(var path in GomokuRecordStore.List(saved)){var record=GomokuRecordStore.Load(path);var button=UiStyle.CreateButton($"{File.GetLastWriteTime(path):yyyy-MM-dd HH:mm}    {record.BoardSize}路 · {(record.Rules==GomokuRuleSet.RenjuForbiddenMoves?"禁手":"无禁手")} · {record.Moves.Count}手");button.Pressed+=()=>replay(record);rows.AddChild(button);}}
}

public partial class GomokuReplayScreen : Control
{
    private readonly List<GomokuSnapshot> _positions=[]; private readonly GomokuBoardView _board=new(); private int _index; private readonly Label _ply=new();
    public GomokuReplayScreen(GomokuGameRecord record,Action back)
    {var game=new GomokuGame(record.BoardSize,record.Rules,record.Setup,record.SideToMove);_positions.Add(game.Snapshot);foreach(var move in record.Moves){game.TryPlace(move.Point,move.Color);_positions.Add(game.Snapshot);}SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(32,20);AddChild(margin);var root=new VBoxContainer();margin.AddChild(root);var header=new HBoxContainer();root.AddChild(header);var title=UiStyle.CreateTitle("五子棋复盘",30);title.SizeFlagsHorizontal=SizeFlags.ExpandFill;header.AddChild(title);header.AddChild(_ply);var leave=UiStyle.CreateButton("返回");leave.Pressed+=back;header.AddChild(leave);var center=new CenterContainer{SizeFlagsVertical=SizeFlags.ExpandFill};center.AddChild(_board);root.AddChild(center);var controls=new HBoxContainer{Alignment=BoxContainer.AlignmentMode.Center};root.AddChild(controls);var previous=UiStyle.CreateButton("◀ 上一步");var next=UiStyle.CreateButton("下一步 ▶");controls.AddChild(previous);controls.AddChild(next);void Render(){_board.SetPosition(_positions[_index]);_ply.Text=$"第 {_index}/{_positions.Count-1} 手";previous.Disabled=_index==0;next.Disabled=_index==_positions.Count-1;}previous.Pressed+=()=>{if(_index>0)_index--;Render();};next.Pressed+=()=>{if(_index+1<_positions.Count)_index++;Render();};Render();}
}

public partial class GomokuImportScreen : Control
{
    public GomokuImportScreen(bool position,Action back,Action<GomokuGameRecord> open)
    {SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var margin=UiStyle.CreatePageMargin(54,34);AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",12);margin.AddChild(root);var title=UiStyle.CreateTitle(position?"五子棋 · 局面导入":"五子棋 · SGF 导入");root.AddChild(title);var editor=new TextEdit{PlaceholderText=position?"粘贴含 AB/AW/PL 的 SGF 局面":"粘贴 SGF FF[4] / GM[4] 棋谱",SizeFlagsVertical=SizeFlags.ExpandFill};root.AddChild(editor);var status=new Label();root.AddChild(status);var actions=new HBoxContainer();root.AddChild(actions);var parse=UiStyle.CreateButton("验证并打开",true);parse.Pressed+=()=>{if(GomokuSgfCodec.TryParse(editor.Text,out var record,out var error))open(record!);else status.Text=error;};actions.AddChild(parse);var leave=UiStyle.CreateButton("返回画廊");leave.Pressed+=back;actions.AddChild(leave);}
}

public partial class GomokuAiNoticeScreen : Control
{
    public GomokuAiNoticeScreen(Action back){SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);var center=new CenterContainer();center.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);AddChild(center);var panel=UiStyle.CreatePanel(UiStyle.PanelLight,14);panel.CustomMinimumSize=new(680,300);center.AddChild(panel);var margin=new MarginContainer();foreach(var key in new[]{"margin_left","margin_right","margin_top","margin_bottom"})margin.AddThemeConstantOverride(key,28);panel.AddChild(margin);var root=new VBoxContainer();root.AddThemeConstantOverride("separation",16);margin.AddChild(root);root.AddChild(UiStyle.CreateTitle("五子棋 AI 尚未接入",30));root.AddChild(new Label{Text="按需求暂不选择、启动或打包任何 AI 引擎。\n待确认 Rapfi/Piskvork 或定制 C++ 方案后，再设计并实现独立 AI 端口。",AutowrapMode=TextServer.AutowrapMode.WordSmart});var leave=UiStyle.CreateButton("返回画廊",true);leave.Pressed+=back;root.AddChild(leave);}}
