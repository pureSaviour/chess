using Godot;
using Xiangqi.Core.Application;

namespace Xiangqi.Presentation.Audio;

/// <summary>
/// Maps gameplay semantics to replaceable audio resources. It deliberately owns separate
/// players so a selection sound cannot cut off an impact sound already in progress.
/// </summary>
public partial class GameplayAudioPlayer : Node
{
    private readonly AudioStreamPlayer _selection = CreatePlayer();
    private readonly AudioStreamPlayer _move = CreatePlayer();
    private readonly AudioStreamPlayer _capture = CreatePlayer();

    public GameplayAudioPlayer()
    {
        AddChild(_selection);
        AddChild(_move);
        AddChild(_capture);
    }

    public override void _Ready()
    {
        _selection.Stream = GD.Load<AudioStream>("res://assets/audio/piece_select.ogg");
        _move.Stream = GD.Load<AudioStream>("res://assets/audio/piece_move.ogg");
        _capture.Stream = GD.Load<AudioStream>("res://assets/audio/piece_capture.ogg");
    }

    public void PlaySelection() => Restart(_selection);

    public void PlayLanding(bool captured) => Restart(captured ? _capture : _move);

    public void PlayCue(PresentationCue cue)
    {
        if (cue.Kind == PresentationCueKind.IllegalMove) Restart(_selection, 0.82f);
    }

    private static AudioStreamPlayer CreatePlayer() => new() { Bus = "SFX" };

    private static void Restart(AudioStreamPlayer player, float pitch = 1.0f)
    {
        if (player.Stream is null) return;
        player.Stop();
        player.PitchScale = pitch;
        player.Play();
    }
}
