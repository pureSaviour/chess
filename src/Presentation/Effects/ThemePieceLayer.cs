using Godot;
using Xiangqi.Presentation.Settings;

namespace Xiangqi.Presentation.Effects;

public readonly record struct ThemePieceDrawCommand(
    int Column, int Row, Vector2 Center, float Diameter, Color Modulate);

/// <summary>
/// Optional semantic-atlas renderer shared by both board types. The parent supplies only projected
/// draw commands; this layer owns its texture/material lease and knows nothing about rules or cells.
/// </summary>
public partial class ThemePieceLayer : Control
{
    private readonly Func<IReadOnlyList<ThemePieceDrawCommand>> _commands;
    private ThemeResourceLease? _lease;
    private Texture2D? _atlas;

    public bool IsActive => _atlas is not null;

    public ThemePieceLayer(Func<IReadOnlyList<ThemePieceDrawCommand>> commands)
    {
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
        MouseFilter = MouseFilterEnum.Ignore;
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
    }

    public bool ApplyTheme(IBoardTheme theme)
    {
        Material = null;
        _lease?.Dispose(); _lease = null; _atlas = null;
        if (theme.Pieces is null) { Visible = false; return false; }
        try
        {
            _lease = new ThemeResourceLease();
            _atlas = _lease.LoadTexture(theme.Pieces.Atlas);
            if (_atlas.GetWidth() % 7 != 0 || _atlas.GetHeight() % 5 != 0)
                throw new ThemePackageException("semantic-v1 atlas 的宽高必须分别可被 7 和 5 整除。");
            if (theme.Pieces.Shader is not null)
            {
                var material = _lease.CreateMaterial(theme.Pieces.Shader);
                material.SetShaderParameter("theme_texture", _atlas);
                Material = material;
            }
            Visible = true;
            QueueRedraw();
            return true;
        }
        catch (Exception error)
        {
            GD.PushWarning($"主题 {theme.Id} 的棋子 atlas 加载失败，已使用程序绘制棋子：{error.Message}");
            Material = null; _lease?.Dispose(); _lease = null; _atlas = null; Visible = false;
            return false;
        }
    }

    public override void _Draw()
    {
        if (_atlas is null) return;
        var sourceSize = new Vector2(_atlas.GetWidth() / 7f, _atlas.GetHeight() / 5f);
        foreach (var command in _commands())
        {
            if (command.Column is < 0 or > 6 || command.Row is < 0 or > 4 || command.Diameter <= 0) continue;
            var destination = new Rect2(command.Center - Vector2.One * command.Diameter * 0.5f,
                Vector2.One * command.Diameter);
            var source = new Rect2(new Vector2(command.Column, command.Row) * sourceSize, sourceSize);
            DrawTextureRectRegion(_atlas, destination, source, command.Modulate, false, true);
        }
    }

    public override void _ExitTree()
    {
        Material = null; _lease?.Dispose(); _lease = null; _atlas = null;
    }
}
