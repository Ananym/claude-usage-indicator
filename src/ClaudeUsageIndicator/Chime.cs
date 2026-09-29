using System;
using System.IO;
using System.Media;

namespace ClaudeUsageIndicator;

/// <summary>
/// The "session finished" cue: a short, quiet motif synthesised in memory so the exe ships no
/// audio files. Three soft wooden-mallet taps rising sol-re-sol (G4, D5, G5), spread a little
/// left-to-right, over a barely-there low hum that gives them somewhere to sit. About a second
/// long including the tail. Nothing here is a stock Windows sound.
/// </summary>
internal sealed class Chime : IDisposable
{
    private const int Rate = 44100;
    private const double Duration = 1.05;

    // Mallet taps: frequency, onset (s), relative level, pan (-1 left .. +1 right).
    private static readonly (double Freq, double At, double Gain, double Pan)[] Taps =
    {
        (392.00, 0.00, 0.60, -0.35), // G4
        (587.33, 0.17, 0.50, +0.35), // D5
        (783.99, 0.34, 0.30,  0.00), // G5
    };

    // Partials of one tap: multiple of the fundamental, level, decay time constant (s).
    // A 4:1 second partial is what makes it read as marimba/kalimba rather than a bell.
    private static readonly (double Ratio, double Gain, double Tau)[] Partials =
    {
        (1.000, 1.00, 0.30),
        (1.004, 0.30, 0.30), // slightly detuned twin: a slow 1-2 Hz shimmer, warmth not chorus
        (2.000, 0.05, 0.12),
        (4.000, 0.15, 0.06),
    };

    private const double TapAttack = 0.004;

    // Low hum under the taps.
    private const double HumFreq = 196.00; // G3
    private const double HumGain = 0.16;
    private const double HumAttack = 0.06;
    private const double HumHoldUntil = 0.30;
    private const double HumReleaseTau = 0.22;

    private SoundPlayer? _player;
    private MemoryStream? _stream;
    private double _loadedVolume = -1;

    /// <summary>Play once, asynchronously. Volume 0..1; 0 is silent and returns immediately.</summary>
    public void Play(double volume)
    {
        var player = Prepare(volume);
        player?.Play();
    }

    /// <summary>Play once and block until it has finished (for --chime).</summary>
    public void PlayAndWait(double volume)
    {
        var player = Prepare(volume);
        player?.PlaySync();
    }

    /// <summary>The rendered WAV bytes at the given volume.</summary>
    public static byte[] RenderWav(double volume) => ToWav(Render(Math.Clamp(volume, 0, 1)));

    private SoundPlayer? Prepare(double volume)
    {
        volume = Math.Clamp(volume, 0, 1);
        if (volume <= 0) return null;
        if (_player is not null && Math.Abs(volume - _loadedVolume) < 0.001) return _player;

        _player?.Dispose();
        _stream?.Dispose();
        _stream = new MemoryStream(RenderWav(volume));
        _player = new SoundPlayer(_stream);
        _player.Load();
        _loadedVolume = volume;
        return _player;
    }

    /// <summary>Stereo interleaved samples, peak-normalised to <paramref name="volume"/> of full scale.</summary>
    private static float[] Render(double volume)
    {
        var frames = (int)(Rate * Duration);
        var buf = new float[frames * 2];

        foreach (var tap in Taps)
        {
            var start = (int)(tap.At * Rate);
            var (l, r) = PanGains(tap.Pan);
            for (var i = start; i < frames; i++)
            {
                var t = (i - start) / (double)Rate;
                var attack = 1 - Math.Exp(-t / TapAttack);
                double s = 0;
                foreach (var p in Partials)
                {
                    s += p.Gain * Math.Exp(-t / p.Tau) * Math.Sin(2 * Math.PI * tap.Freq * p.Ratio * t);
                }
                s *= attack * tap.Gain;
                buf[2 * i] += (float)(s * l);
                buf[2 * i + 1] += (float)(s * r);
            }
        }

        for (var i = 0; i < frames; i++)
        {
            var t = i / (double)Rate;
            var env = t < HumHoldUntil
                ? 1 - Math.Exp(-t / HumAttack)
                : Math.Exp(-(t - HumHoldUntil) / HumReleaseTau);
            var s = HumGain * env * (Math.Sin(2 * Math.PI * HumFreq * t) + 0.25 * Math.Sin(2 * Math.PI * HumFreq * 2 * t));
            buf[2 * i] += (float)s;
            buf[2 * i + 1] += (float)s;
        }

        // Fade the last 40 ms so the tail never ends on a click.
        var fade = (int)(0.04 * Rate);
        for (var i = frames - fade; i < frames; i++)
        {
            var g = (float)(frames - 1 - i) / fade;
            buf[2 * i] *= g;
            buf[2 * i + 1] *= g;
        }

        float peak = 0;
        foreach (var v in buf) peak = Math.Max(peak, Math.Abs(v));
        if (peak > 0)
        {
            // Even at volume 1.0 stay a little under full scale: this is a cue, not an alarm.
            var scale = (float)(volume * 0.85 / peak);
            for (var i = 0; i < buf.Length; i++) buf[i] *= scale;
        }
        return buf;
    }

    /// <summary>Equal-power pan.</summary>
    private static (double Left, double Right) PanGains(double pan)
    {
        var angle = (Math.Clamp(pan, -1, 1) + 1) * Math.PI / 4;
        return (Math.Cos(angle), Math.Sin(angle));
    }

    /// <summary>16-bit PCM stereo RIFF/WAVE.</summary>
    private static byte[] ToWav(float[] samples)
    {
        const short channels = 2;
        const short bits = 16;
        var dataBytes = samples.Length * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8);
        w.Write(36 + dataBytes);
        w.Write("WAVE"u8);
        w.Write("fmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write(channels);
        w.Write(Rate);
        w.Write(Rate * channels * bits / 8);
        w.Write((short)(channels * bits / 8));
        w.Write(bits);
        w.Write("data"u8);
        w.Write(dataBytes);
        foreach (var s in samples)
        {
            w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        }
        w.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _player?.Dispose();
        _stream?.Dispose();
        _player = null;
        _stream = null;
    }
}
