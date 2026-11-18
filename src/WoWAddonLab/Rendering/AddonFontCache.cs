using System.Security.Cryptography;
using System.Numerics;
using WoWAddonLab.Assets;
using WoWAddonLab.Emulator.Diagnostics;
using ImGuiNET;
using Silk.NET.OpenGL;

namespace WoWAddonLab.Rendering;

public sealed class AddonFontCache : IDisposable
{
    private readonly GL _gl;
    private TextureCache _assets;
    private readonly FontSet _fallback;
    private readonly EmulatorLog _log;
    private readonly string _cacheDirectory;
    private readonly Dictionary<(string Path, float Size), RenderFont> _fonts = new();
    private readonly HashSet<(string Path, float Size)> _pending = [];
    private readonly HashSet<(string Path, float Size)> _missing = [];
    private uint _fontTexture;

    public AddonFontCache(
        GL gl,
        TextureCache assets,
        FontSet fallback,
        EmulatorLog log,
        string cacheDirectory)
    {
        _gl = gl;
        _assets = assets;
        _fallback = fallback;
        _log = log;
        _cacheDirectory = cacheDirectory;
    }

    public RenderFont Select(string fontPath, float pixelSize)
    {
        var size = Math.Clamp(MathF.Truncate(MathF.Max(2, pixelSize) + 0.5f), 2, 256);
        // Each new size rebuilds the whole font atlas, so sizes above 16px snap up to a ladder
        // (16-31 by 2, 32-63 by 8, then 64, 96, 128, ...); the renderer scales glyphs to the exact size.
        // Animated text otherwise asked for every size in between.
        var step = size switch { < 16 => 1, < 32 => 2, < 64 => 8, _ => 32 };
        var selectedPixelSize = MathF.Min(256, MathF.Ceiling(size / step) * step);
        var key = (Normalize(fontPath), selectedPixelSize);
        if (_fonts.TryGetValue(key, out var font))
            return font;
        if (!_missing.Contains(key))
            _pending.Add(key);
        // Until the atlas is rebuilt, draw with the closest size already cached for this font.
        RenderFont? nearest = null;
        foreach (var (cachedKey, cached) in _fonts)
        {
            if (cachedKey.Path == key.Item1 &&
                (nearest is null || MathF.Abs(cachedKey.Size - size) < MathF.Abs(nearest.PixelSize - size)))
                nearest = cached;
        }
        return nearest ?? _fallback.Select(pixelSize);
    }

    public unsafe void ProcessPending()
    {
        if (_pending.Count == 0)
            return;

        var added = false;
        foreach (var key in _pending.ToArray())
        {
            _pending.Remove(key);
            var bytes = _assets.ReadAsset(key.Path);
            if (bytes is null)
            {
                _missing.Add(key);
                _log.Warn("fonts", $"Font was not found: {key.Path}");
                continue;
            }

            Directory.CreateDirectory(_cacheDirectory);
            var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            var extension = Path.GetExtension(key.Path);
            if (string.IsNullOrWhiteSpace(extension))
                extension = ".ttf";
            var extractedPath = Path.Combine(_cacheDirectory, $"{hash}{extension}");
            if (!File.Exists(extractedPath))
                File.WriteAllBytes(extractedPath, bytes);

            var glyphSize = key.Size;
            var glyphOffset = Vector2.Zero;
            if (TrueTypeFontMetrics.TryRead(bytes, out var metrics))
            {
                glyphSize = metrics.ResolveImGuiGlyphSize(key.Size);
                glyphOffset.Y = metrics.ResolveImGuiGlyphOffsetY(key.Size);
            }

            var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
            ImFontPtr handle;
            try
            {
                config.GlyphOffset = glyphOffset;
                if (glyphSize >= 32)
                    config.OversampleH = 1; // large glyphs don't need it, and it halves the rebuild time
                handle = ImGui.GetIO().Fonts.AddFontFromFileTTF(
                    extractedPath,
                    glyphSize,
                    config);
            }
            finally
            {
                config.Destroy();
            }
            _fonts[key] = new RenderFont(key.Size, glyphSize, handle);
            added = true;
            _log.Info(
                "fonts",
                $"Cached {key.Path} at {key.Size:0}px (nominal glyph raster {glyphSize:0.##}px).");
        }

        if (!added)
            return;

        var fonts = ImGui.GetIO().Fonts;
        fonts.GetTexDataAsRGBA32(out nint pixels, out var width, out var height, out _);
        var texture = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, texture);
        _gl.TexImage2D(
            TextureTarget.Texture2D,
            0,
            InternalFormat.Rgba8,
            (uint)width,
            (uint)height,
            0,
            PixelFormat.Rgba,
            PixelType.UnsignedByte,
            (void*)pixels);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMinFilter,
            (int)TextureMinFilter.Linear);
        _gl.TexParameter(
            TextureTarget.Texture2D,
            TextureParameterName.TextureMagFilter,
            (int)TextureMagFilter.Linear);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
        fonts.SetTexID((nint)texture);
        if (_fontTexture != 0)
            _gl.DeleteTexture(_fontTexture);
        _fontTexture = texture;
    }

    public void RetryMissing()
    {
        foreach (var key in _missing)
            _pending.Add(key);
        _missing.Clear();
    }

    public void UpdateAssets(TextureCache assets)
    {
        _assets = assets;
        RetryMissing();
    }

    public void Dispose()
    {
        if (_fontTexture != 0)
            _gl.DeleteTexture(_fontTexture);
        _fontTexture = 0;
    }

    private static string Normalize(string path) =>
        path.Replace('/', '\\').TrimStart('\\');
}
