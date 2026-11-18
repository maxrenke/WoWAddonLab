using NLayer;
using NVorbis;
using Silk.NET.OpenAL;
using WoWAddonLab.Assets;
using WoWAddonLab.Emulator.Lua;

namespace WoWAddonLab.Audio;

// Plays the sounds addons request (PlaySound / PlaySoundFile) through OpenAL Soft.
// Addon files are read from the product folder, game sounds from TACT (SoundKitEntry -> FileDataID).
internal sealed class SoundPlayer : IDisposable
{
    private const int MaxVoices = 32;

    private readonly AL? _al;
    private readonly ALContext? _alc;
    private readonly unsafe Device* _device;
    private readonly unsafe Context* _context;
    private readonly Dictionary<int, (uint Source, uint Buffer)> _voices = [];
    private readonly Dictionary<string, Pcm?> _decoded = new(StringComparer.OrdinalIgnoreCase);
    private Task<Dictionary<int, uint[]>>? _soundKits;
    private TactAssetSource? _soundKitSource;

    public unsafe SoundPlayer()
    {
        try
        {
            _alc = ALContext.GetApi(true);
            _al = AL.GetApi(true);
            _device = _alc.OpenDevice(null);
            if (_device == null)
                return;
            _context = _alc.CreateContext(_device, null);
            _alc.MakeContextCurrent(_context);
        }
        catch (Exception exception)
        {
            Error = exception.Message;
            _al = null;
        }
    }

    public string? Error { get; set; }

    public unsafe void Service(WowSoundState sound, string? productPath, TactAssetSource? tact, string? build)
    {
        var requests = sound.PlaybackRequests.ToArray();
        var stops = sound.StopRequests.ToArray();
        sound.PlaybackRequests.Clear();
        sound.StopRequests.Clear();
        if (_al is null || _context == null)
            return;
        if (tact is not null && build is not null)
            SoundKitFiles(tact, build); // start loading before the first kit is played

        foreach (var stop in stops)
            Release(stop.Handle);

        foreach (var request in requests)
        {
            if (_voices.Count >= MaxVoices)
                break;
            var pcm = Resolve(request, productPath, tact, build);
            if (pcm is null)
            {
                sound.ActivePlaybacks.Remove(request.Handle);
                continue;
            }
            var buffer = _al.GenBuffer();
            fixed (short* samples = pcm.Samples)
                _al.BufferData(buffer, pcm.Channels == 1 ? BufferFormat.Mono16 : BufferFormat.Stereo16,
                    samples, pcm.Samples.Length * sizeof(short), pcm.SampleRate);
            var source = _al.GenSource();
            _al.SetSourceProperty(source, SourceInteger.Buffer, (int)buffer);
            _al.SetSourceProperty(source, SourceFloat.Gain, Math.Clamp(request.ScaledVolume, 0f, 1f));
            _al.SourcePlay(source);
            _voices[request.Handle] = (source, buffer);
        }

        foreach (var (handle, voice) in _voices.ToArray())
        {
            _al.GetSourceProperty(voice.Source, GetSourceInteger.SourceState, out var state);
            if (state == (int)SourceState.Playing)
                continue;
            Release(handle);
            sound.ActivePlaybacks.Remove(handle);
        }
    }

    private void Release(int handle)
    {
        if (_al is null || !_voices.Remove(handle, out var voice))
            return;
        _al.SourceStop(voice.Source);
        _al.DeleteSource(voice.Source);
        _al.DeleteBuffer(voice.Buffer);
    }

    private Pcm? Resolve(WowSoundPlayback request, string? productPath, TactAssetSource? tact, string? build)
    {
        if (request.FilePath is { Length: > 0 } path)
        {
            var normalized = path.Replace('/', '\\').TrimStart('\\');
            if (productPath is not null &&
                normalized.StartsWith(@"Interface\AddOns\", StringComparison.OrdinalIgnoreCase))
                return Decode("file:" + normalized, () =>
                {
                    var full = Path.Combine(productPath, normalized);
                    return File.Exists(full) ? File.ReadAllBytes(full) : null;
                });
            if (tact?.ResolveFileDataId(path) is > 0 and var id)
                return FromTact(tact, id);
            return null;
        }
        if (request.FileDataId is { } fileDataId && tact is not null)
            return FromTact(tact, fileDataId);
        if (request.SoundKitId is { } kit && tact is not null && build is not null &&
            SoundKitFiles(tact, build) is { } kits && kits.TryGetValue(kit, out var files))
            return FromTact(tact, files[Random.Shared.Next(files.Length)]);
        return null;
    }

    private Pcm? FromTact(TactAssetSource tact, uint fileDataId) =>
        Decode("fdid:" + fileDataId, () => tact.Read(fileDataId));

    // SoundKitEntry is large, so it loads in the background; kits played before it is ready stay silent.
    private Dictionary<int, uint[]>? SoundKitFiles(TactAssetSource tact, string build)
    {
        if (_soundKits is null || _soundKitSource != tact)
        {
            _soundKitSource = tact;
            _soundKits = Task.Run(() => tact.Database.Load("SoundKitEntry", build).Values
                .Select(row => (Kit: Convert.ToInt32(row["SoundKitID"]), File: Convert.ToUInt32(row["FileDataID"])))
                .Where(entry => entry.File != 0)
                .GroupBy(entry => entry.Kit)
                .ToDictionary(group => group.Key, group => group.Select(entry => entry.File).ToArray()));
        }
        if (_soundKits.IsFaulted && Error is null)
            Error = "SoundKitEntry: " + _soundKits.Exception?.GetBaseException().Message;
        return _soundKits.IsCompletedSuccessfully ? _soundKits.Result : null;
    }

    private Pcm? Decode(string key, Func<byte[]?> read)
    {
        if (_decoded.TryGetValue(key, out var cached))
            return cached;
        Pcm? pcm = null;
        try
        {
            if (read() is { Length: > 4 } bytes)
                pcm = DecodeBytes(bytes);
        }
        catch (Exception exception)
        {
            Error = $"{key}: {exception.Message}";
        }
        _decoded[key] = pcm;
        return pcm;
    }

    private static Pcm? DecodeBytes(byte[] bytes)
    {
        if (bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F')
            return DecodeWav(bytes);
        if (bytes[0] == 'O' && bytes[1] == 'g' && bytes[2] == 'g' && bytes[3] == 'S')
        {
            using var vorbis = new VorbisReader(new MemoryStream(bytes), true);
            return ToPcm(vorbis.SampleRate, vorbis.Channels, (buffer, count) => vorbis.ReadSamples(buffer, 0, count));
        }
        using var mpeg = new MpegFile(new MemoryStream(bytes));
        return ToPcm(mpeg.SampleRate, mpeg.Channels, (buffer, count) => mpeg.ReadSamples(buffer, 0, count));
    }

    private static Pcm? ToPcm(int sampleRate, int channels, Func<float[], int, int> read)
    {
        if (channels is < 1 or > 2)
            return null;
        var samples = new List<short>();
        var buffer = new float[4096 * channels];
        int count;
        while ((count = read(buffer, buffer.Length)) > 0)
            for (var i = 0; i < count; i++)
                samples.Add((short)(Math.Clamp(buffer[i], -1f, 1f) * short.MaxValue));
        return samples.Count == 0 ? null : new Pcm(samples.ToArray(), channels, sampleRate);
    }

    private static Pcm? DecodeWav(byte[] bytes)
    {
        int channels = 0, sampleRate = 0, bits = 0, offset = 12;
        while (offset + 8 <= bytes.Length)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, offset, 4);
            var size = BitConverter.ToInt32(bytes, offset + 4);
            var body = offset + 8;
            if (id == "fmt ")
            {
                if (BitConverter.ToInt16(bytes, body) != 1)
                    return null; // PCM only
                channels = BitConverter.ToInt16(bytes, body + 2);
                sampleRate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data" && bits is 8 or 16 && channels is 1 or 2)
            {
                var length = Math.Min(size, bytes.Length - body);
                var samples = bits == 16
                    ? new short[length / 2]
                    : new short[length];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = bits == 16
                        ? BitConverter.ToInt16(bytes, body + i * 2)
                        : (short)((bytes[body + i] - 128) << 8);
                return new Pcm(samples, channels, sampleRate);
            }
            offset = body + size + (size & 1);
        }
        return null;
    }

    public unsafe void Dispose()
    {
        foreach (var handle in _voices.Keys.ToArray())
            Release(handle);
        if (_alc is null)
            return;
        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
        }
        if (_device != null)
            _alc.CloseDevice(_device);
    }

    private sealed record Pcm(short[] Samples, int Channels, int SampleRate);
}
