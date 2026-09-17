using Godot;

namespace AbyssScav.Gameplay;

/// <summary>
/// Procedural synth SFX: all cues are generated PCM (sine/noise envelopes),
/// no third-party assets. Ping chirp, survey tick, salvage clunk, alarm
/// two-tone, success chime, fault buzz. Visual warning banner is separate so
/// alerts are never audio-only (accessibility).
/// </summary>
public partial class ProceduralAudio : Node
{
    private AudioStreamPlayer? _ping;
    private AudioStreamPlayer? _tick;
    private AudioStreamPlayer? _alarm;
    private AudioStreamPlayer? _chime;
    private AudioStreamPlayer? _clunk;

    public override void _Ready()
    {
        _ping = MakePlayer("Ping", -6f);
        _tick = MakePlayer("Tick", -10f);
        _alarm = MakePlayer("Alarm", -8f);
        _chime = MakePlayer("Chime", -8f);
        _clunk = MakePlayer("Clunk", -8f);
        _ping.Stream = Chirp(880f, 420f, 0.7f, 0.8f);
        _tick.Stream = Chirp(1320f, 1180f, 0.12f, 0.5f);
        _alarm.Stream = TwoTone(620f, 470f, 0.5f);
        _chime.Stream = TwoTone(660f, 990f, 0.4f);
        _clunk.Stream = NoiseBurst(0.25f, 0.7f);
    }

    private AudioStreamPlayer MakePlayer(string name, float db)
    {
        var p = new AudioStreamPlayer { Name = name, VolumeDb = db, MaxPolyphony = 3 };
        AddChild(p);
        return p;
    }

    public void PlayPing() => _ping?.Play();
    public void PlayTick() => _tick?.Play();
    public void PlayAlarm() => _alarm?.Play();
    public void PlayChime() => _chime?.Play();
    public void PlayClunk() => _clunk?.Play();

    public void ApplyVolume(int masterPercent)
    {
        // Keep relative mix; mute handled by SettingsAppliance bus mute.
        var scale = masterPercent <= 0 ? -80f : 0f;
        foreach (var p in new[] { _ping, _tick, _alarm, _chime, _clunk })
        {
            if (p is not null && masterPercent > 0)
            {
                var pname = p.Name.ToString();
                p.VolumeDb = pname switch
                {
                    "Ping" => -6f,
                    "Tick" => -10f,
                    _ => -8f,
                };
            }
            else if (p is not null)
            {
                p.VolumeDb = scale;
            }
        }
    }

    private static AudioStreamWav Chirp(float fromHz, float toHz, float seconds, float gain)
    {
        const int rate = 22050;
        var n = (int)(rate * seconds);
        var data = new byte[n * 2];
        var phase = 0.0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / n;
            var freq = fromHz + (toHz - fromHz) * t;
            phase += 2.0 * Math.PI * freq / rate;
            var env = Math.Exp(-3.5 * t) * (1.0 - t * 0.3);
            var s = (short)(Math.Sin(phase) * env * gain * short.MaxValue);
            data[i * 2] = (byte)(s & 0xff);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data };
    }

    private static AudioStreamWav TwoTone(float f1, float f2, float seconds)
    {
        const int rate = 22050;
        var n = (int)(rate * seconds);
        var data = new byte[n * 2];
        for (var i = 0; i < n; i++)
        {
            var half = i < n / 2;
            var f = half ? f1 : f2;
            var t = (double)i / rate;
            // Soft edges: 10 ms ramps, no clicks or harsh flicker-equivalent audio.
            var edge = Math.Min(1.0, Math.Min(i, n - i) / (rate * 0.01));
            var s = (short)(Math.Sin(2.0 * Math.PI * f * t) * 0.5 * edge * short.MaxValue);
            data[i * 2] = (byte)(s & 0xff);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data };
    }

    private static AudioStreamWav NoiseBurst(float seconds, float gain)
    {
        const int rate = 22050;
        var n = (int)(rate * seconds);
        var data = new byte[n * 2];
        var rng = new Random(1234);
        double last = 0;
        for (var i = 0; i < n; i++)
        {
            var t = (double)i / n;
            var white = rng.NextDouble() * 2.0 - 1.0;
            last = last * 0.7 + white * 0.3; // lowpassed thud
            var s = (short)(last * Math.Exp(-6.0 * t) * gain * short.MaxValue);
            data[i * 2] = (byte)(s & 0xff);
            data[i * 2 + 1] = (byte)((s >> 8) & 0xff);
        }
        return new AudioStreamWav { Format = AudioStreamWav.FormatEnum.Format16Bits, MixRate = rate, Data = data };
    }
}
