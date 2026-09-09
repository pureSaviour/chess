using Godot;

namespace Xiangqi.Presentation.Settings;

/// <summary>
/// View-owned Godot resources created on the main thread. Definitions/catalogs own no engine
/// objects. Dispose is idempotent and detaching a material remains the caller's responsibility.
/// </summary>
public sealed class ThemeResourceLease : IDisposable
{
    private readonly List<GodotObject> _owned = [];
    private bool _disposed;

    public Texture2D LoadTexture(ThemeResourceReference reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var image = Image.LoadFromFile(reference.AbsolutePath);
        if (image.IsEmpty()) throw new ThemePackageException($"无法读取纹理：{reference.RelativePath}");
        if (image.GetWidth() is <= 0 or > 4096 || image.GetHeight() is <= 0 or > 4096)
            throw new ThemePackageException($"纹理尺寸必须在 1..4096：{reference.RelativePath}");
        var texture = ImageTexture.CreateFromImage(image);
        _owned.Add(texture);
        return texture;
    }

    public Shader LoadShader(ThemeResourceReference reference)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var code = File.ReadAllText(reference.AbsolutePath);
        if (code.Length == 0 || !code.Contains("shader_type canvas_item", StringComparison.Ordinal))
            throw new ThemePackageException($"shader 必须声明 shader_type canvas_item：{reference.RelativePath}");
        var shader = new Shader { Code = code };
        _owned.Add(shader);
        return shader;
    }

    public ShaderMaterial CreateMaterial(ThemeResourceReference shader)
    {
        var material = new ShaderMaterial { Shader = LoadShader(shader) };
        _owned.Add(material);
        return material;
    }

    public ShaderMaterial CreateTextureMaterial(Texture2D texture)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var shader = new Shader
        {
            Code = "shader_type canvas_item; render_mode unshaded; " +
                   "uniform sampler2D theme_texture; " +
                   "void fragment(){ COLOR = texture(theme_texture, UV); }"
        };
        var material = new ShaderMaterial { Shader = shader };
        material.SetShaderParameter("theme_texture", texture);
        _owned.Add(shader); _owned.Add(material);
        return material;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (var index = _owned.Count - 1; index >= 0; index--)
            if (GodotObject.IsInstanceValid(_owned[index])) _owned[index].Dispose();
        _owned.Clear();
    }
}
