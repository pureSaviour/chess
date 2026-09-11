using Godot;

namespace Xiangqi.Presentation.Audio;

/// <summary>
/// Maps gameplay semantics to replaceable audio resources. It deliberately owns separate
/// players so move and capture impacts remain independent presentation resources.
/// </summary>
public partial class GameplayAudioPlayer : Node
{
    private readonly AudioStreamPlayer _move = CreatePlayer();
    private readonly AudioStreamPlayer _capture = CreatePlayer();

    public GameplayAudioPlayer()
    {
        AddChild(_move);
        AddChild(_capture);
    }

    public override void _Ready()
    {
        _move.Stream = GD.Load<AudioStream>("res://assets/audio/piece_move.ogg");
        _capture.Stream = GD.Load<AudioStream>("res://assets/audio/piece_capture.ogg");
    }

    public void PlayLanding(bool captured) => Restart(captured ? _capture : _move);

    private static AudioStreamPlayer CreatePlayer() => new() { Bus = "SFX" };

    private static void Restart(AudioStreamPlayer player, float pitch = 1.0f)
    {
        if (player.Stream is null) return;
        player.Stop();
        player.PitchScale = pitch;
        player.Play();
    }
}
