using System;
using UnityEngine;

/// <summary>
/// Sits after the music's AudioSource. On the game's output it does nothing. With
/// another device in the Output setting, it hands each block of the music to a
/// WaveOutPlayer and silences what Unity would play, so the AudioSource still keeps
/// time, ends tracks and fades, but the sound comes out where the player chose.
/// </summary>
public class BloodMoonMusicTap : MonoBehaviour
{
    /// <summary>The music's volume, fades included. The AudioSource stays at 1 while
    /// the tap is on, so this is the only volume the samples get.</summary>
    public volatile float Gain = 1f;
    /// <summary>The game's master volume (AudioListener.volume), which doesn't reach
    /// a device of our own by itself.</summary>
    public volatile float Master = 1f;
    volatile WaveOutPlayer _player;

    // The left channel's last samples, for the radio's level bars.
    const int ScopeSize = 1024;
    readonly float[] _scope = new float[ScopeSize];
    int _scopeAt;
    readonly object _scopeLock = new object();
    float[] _re, _im, _window;

    public WaveOutPlayer Player => _player;
    public bool On => _player != null;

    /// <summary>Start playing on this device; the old one, if any, closes.</summary>
    public void Use(WaveOutPlayer player)
    {
        var old = _player;
        _player = player;
        old?.Dispose();
    }

    /// <summary>Back to the game's output.</summary>
    public void Close() => Use(null);

    private void OnDestroy() => Close();

    // On the audio thread, with the AudioSource's block of samples.
    private void OnAudioFilterRead(float[] data, int channels)
    {
        var player = _player;
        if (player == null) return;
        float gain = Gain;
        player.Write(data, channels, gain * Master);
        lock (_scopeLock)
        {
            for (int i = 0; i < data.Length; i += channels)
            {
                _scope[_scopeAt] = data[i] * gain;
                _scopeAt = (_scopeAt + 1) % ScopeSize;
            }
        }
        Array.Clear(data, 0, data.Length);
    }

    /// <summary>The music's spectrum, as AudioSource.GetSpectrumData gives it (left
    /// channel, Blackman-Harris, after the volume), from what the tap sent out.
    /// samples.Length up to half the scope (512).</summary>
    public void GetSpectrum(float[] samples)
    {
        int n = samples.Length * 2;
        if (n > ScopeSize) n = ScopeSize;
        if (_re == null || _re.Length != n)
        {
            _re = new float[n];
            _im = new float[n];
            _window = new float[n];
            for (int i = 0; i < n; i++)
            {
                double x = 2 * Math.PI * i / (n - 1);
                _window[i] = (float)(0.35875 - 0.48829 * Math.Cos(x) + 0.14128 * Math.Cos(2 * x) - 0.01168 * Math.Cos(3 * x));
            }
        }
        float windowSum = 0f;
        lock (_scopeLock)
        {
            int start = (_scopeAt - n + ScopeSize) % ScopeSize;
            for (int i = 0; i < n; i++)
            {
                _re[i] = _scope[(start + i) % ScopeSize] * _window[i];
                _im[i] = 0f;
                windowSum += _window[i];
            }
        }
        Fft(_re, _im);
        // A full-scale sine reads 1 in its bin.
        float scale = 2f / windowSum;
        for (int k = 0; k < samples.Length; k++)
            samples[k] = k < n / 2 ? Mathf.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]) * scale : 0f;
    }

    /// <summary>In-place radix-2 FFT; the length is a power of two.</summary>
    static void Fft(float[] re, float[] im)
    {
        int n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                float t = re[i]; re[i] = re[j]; re[j] = t;
                t = im[i]; im[i] = im[j]; im[j] = t;
            }
        }
        for (int len = 2; len <= n; len <<= 1)
        {
            double angle = -2 * Math.PI / len;
            float wr = (float)Math.Cos(angle), wi = (float)Math.Sin(angle);
            for (int i = 0; i < n; i += len)
            {
                float cr = 1f, ci = 0f;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    float tr = re[b] * cr - im[b] * ci;
                    float ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    float next = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = next;
                }
            }
        }
    }
}

/// <summary>The Output setting's choices: the game's output, the Windows default
/// communication device, or a device by its endpoint ID.</summary>
public static class BloodMoonMusicOutput
{
    public const string Game = "game";
    public const string Communication = "comm";

    /// <summary>waveOut is Windows'; elsewhere the music stays on the game's output.</summary>
    public static bool Supported =>
        Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

    static System.Collections.Generic.List<WaveOutPlayer.Device> _devices = new System.Collections.Generic.List<WaveOutPlayer.Device>();

    /// <summary>Look at what's plugged in now.</summary>
    public static void Refresh()
    {
        if (Supported) _devices = WaveOutPlayer.Devices();
    }

    /// <summary>Every choice, in the order the settings page steps through them.</summary>
    public static System.Collections.Generic.List<string> Choices()
    {
        var choices = new System.Collections.Generic.List<string> { Game };
        if (!Supported) return choices;
        choices.Add(Communication);
        foreach (var d in _devices)
            if (d.Id != "" && !choices.Contains(d.Id)) choices.Add(d.Id);
        return choices;
    }

    /// <summary>The waveOut number to open for a choice, or -1 (the game's output,
    /// or a device that isn't connected).</summary>
    public static int Number(string choice)
    {
        if (choice == Game || !Supported) return -1;
        if (choice == Communication) return WaveOutPlayer.CommunicationDevice();
        Refresh();
        foreach (var d in _devices) if (d.Id == choice) return d.Number;
        return -1;
    }

    /// <summary>What the settings page shows for a choice.</summary>
    public static string Name(string choice)
    {
        if (choice == Game) return BloodMoonMusicText.Get("bmmOutputGame", "Game's output");
        if (choice == Communication) return BloodMoonMusicText.Get("bmmOutputComm", "Communication device");
        foreach (var d in _devices) if (d.Id == choice) return d.Name;
        return choice == BloodMoonMusicSettings.Output && BloodMoonMusicSettings.OutputName != "" ? BloodMoonMusicSettings.OutputName : choice;
    }
}
