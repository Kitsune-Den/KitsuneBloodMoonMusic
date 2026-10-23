using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

// Unity plays every sound in the game on one output device, the Windows default
// playback device, and has no way to send a single AudioSource anywhere else. So
// for the Output setting the music is taken off the AudioSource (OutputTap.cs) and
// played here, through Windows' own winmm waveOut, on the device the player picked.
// Windows only; nothing here touches Unity, so it can be tested on its own.
public sealed class WaveOutPlayer : IDisposable
{
    /// <summary>Not a device: waveOut's stand-in for the default playback device.</summary>
    const uint WaveMapper = 0xFFFFFFFF;
    const uint DrvmMapperConsoleVoiceComGet = 0x2000 + 23;
    const uint DrvQueryFunctionInstanceId = 0x0800 + 17;
    const uint DrvQueryFunctionInstanceIdSize = 0x0800 + 18;
    const uint WhdrDone = 0x01;
    const uint WhdrInQueue = 0x10;

    // Four 40 ms buffers: 160 ms queued on the device, enough to ride out the
    // thread's sleeps without making the volume buttons feel slow.
    const int Buffers = 4;
    const int BufferMs = 40;
    // The game's audio clock and the device's drift apart a little. Past this much
    // waiting in the ring, drop down to KeepMs (one small skip, minutes apart).
    const int MaxFillMs = 300;
    const int KeepMs = 100;

    public readonly int SampleRate;
    public readonly string DeviceName;
    /// <summary>Set by the feeding thread when the device went away (unplugged,
    /// disabled); the player is no use after that.</summary>
    public volatile string Failed;
    /// <summary>Frames played as silence for want of music, and dropped to catch up:
    /// both should stay near zero while a track plays.</summary>
    public long SilentFrames, DroppedFrames;

    IntPtr _wave;
    readonly IntPtr[] _headers = new IntPtr[Buffers];
    readonly IntPtr[] _data = new IntPtr[Buffers];
    readonly int _frames;
    readonly short[] _pcm;
    readonly float[] _ring;
    int _ringStart;
    int _ringCount;
    readonly object _lock = new object();
    readonly Thread _thread;
    volatile bool _running = true;

    WaveOutPlayer(IntPtr wave, int sampleRate, string name)
    {
        _wave = wave;
        SampleRate = sampleRate;
        DeviceName = name;
        _frames = sampleRate * BufferMs / 1000;
        _pcm = new short[_frames * 2];
        _ring = new float[sampleRate * 2];
        int bytes = _frames * 4;
        for (int i = 0; i < Buffers; i++)
        {
            _data[i] = Marshal.AllocHGlobal(bytes);
            _headers[i] = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WaveHdr)));
            var hdr = new WaveHdr { lpData = _data[i], dwBufferLength = (uint)bytes };
            Marshal.StructureToPtr(hdr, _headers[i], false);
            waveOutPrepareHeader(_wave, _headers[i], (uint)Marshal.SizeOf(typeof(WaveHdr)));
        }
        _thread = new Thread(Feed) { IsBackground = true, Name = "BloodMoonMusic waveOut" };
        _thread.Start();
    }

    /// <summary>Open a device for 16-bit stereo at this rate; null, with the reason
    /// in error, when Windows won't.</summary>
    public static WaveOutPlayer Open(int device, int sampleRate, out string error)
    {
        error = null;
        var format = new WaveFormatEx
        {
            wFormatTag = 1, // PCM
            nChannels = 2,
            nSamplesPerSec = (uint)sampleRate,
            wBitsPerSample = 16,
            nBlockAlign = 4,
            nAvgBytesPerSec = (uint)sampleRate * 4,
        };
        uint result;
        IntPtr wave;
        try { result = waveOutOpen(out wave, (uint)device, ref format, IntPtr.Zero, IntPtr.Zero, 0); }
        catch (Exception e) { error = e.Message; return null; }
        if (result != 0) { error = "waveOutOpen error " + result; return null; }
        return new WaveOutPlayer(wave, sampleRate, FullName(EndpointId(device)) ?? Name(device));
    }

    /// <summary>An output device: its waveOut number (which changes as devices come
    /// and go), its Windows endpoint ID (which doesn't) and its name.</summary>
    public class Device
    {
        public int Number;
        public string Id;
        public string Name;
    }

    /// <summary>The output devices Windows has, in waveOut order.</summary>
    public static List<Device> Devices()
    {
        var devices = new List<Device>();
        try
        {
            uint n = waveOutGetNumDevs();
            for (int i = 0; i < n; i++)
            {
                string id = EndpointId(i);
                devices.Add(new Device { Number = i, Id = id ?? "", Name = FullName(id) ?? Name(i) ?? "" });
            }
        }
        catch (Exception) { }
        return devices;
    }

    /// <summary>The Windows endpoint ID behind a waveOut number, like
    /// {0.0.0.00000000}.{guid}; null if the driver won't say.</summary>
    static string EndpointId(int device)
    {
        if (waveOutMessage(new IntPtr(device), DrvQueryFunctionInstanceIdSize, out uint size, out uint _unused) != 0 || size == 0)
            return null;
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (waveOutMessage(new IntPtr(device), DrvQueryFunctionInstanceId, buffer, new IntPtr(size)) != 0) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>The name Windows shows for an endpoint, "Speakers (Realtek USB
    /// Audio)", from the registry: waveOut's own names stop at 31 characters.</summary>
    static string FullName(string id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        int dot = id.LastIndexOf('.');
        if (dot < 0) return null;
        try
        {
            using (var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render\" + id.Substring(dot + 1) + @"\Properties"))
            {
                // PKEY_Device_DeviceDesc ("Speakers") and the adapter's name ("Realtek USB Audio").
                var desc = key?.GetValue("{a45c254e-df1c-4efd-8020-67d146a850e0},2") as string;
                var adapter = key?.GetValue("{b3f8fa53-0004-438e-9003-51a46e139bfc},6") as string;
                if (string.IsNullOrEmpty(desc)) return null;
                return string.IsNullOrEmpty(adapter) ? desc : desc + " (" + adapter + ")";
            }
        }
        catch (Exception) { return null; }
    }

    /// <summary>The Windows default communication device's waveOut number, or -1.</summary>
    public static int CommunicationDevice()
    {
        try
        {
            if (waveOutMessage(new IntPtr(WaveMapper), DrvmMapperConsoleVoiceComGet, out uint device, out uint _flags) == 0
                && device != WaveMapper)
                return (int)device;
        }
        catch (Exception) { }
        return -1;
    }

    static string Name(int device)
    {
        var caps = new WaveOutCaps();
        return waveOutGetDevCapsW(new UIntPtr((uint)device), ref caps, (uint)Marshal.SizeOf(typeof(WaveOutCaps))) == 0
            ? caps.szPname
            : null;
    }

    /// <summary>From the audio thread: the next block of the music, interleaved in
    /// channels, with the first two (or the one) taken as left and right.</summary>
    public void Write(float[] data, int channels, float gain)
    {
        int frames = data.Length / channels;
        int cap = _ring.Length / 2;
        lock (_lock)
        {
            for (int f = 0; f < frames; f++)
            {
                float l = data[f * channels] * gain;
                float r = channels > 1 ? data[f * channels + 1] * gain : l;
                if (_ringCount == cap) { _ringStart = (_ringStart + 1) % cap; _ringCount--; }
                int at = (_ringStart + _ringCount) % cap * 2;
                _ring[at] = l;
                _ring[at + 1] = r;
                _ringCount++;
            }
            int max = SampleRate * MaxFillMs / 1000;
            if (_ringCount > max)
            {
                int drop = _ringCount - SampleRate * KeepMs / 1000;
                DroppedFrames += drop;
                _ringStart = (_ringStart + drop) % cap;
                _ringCount -= drop;
            }
        }
    }

    void Feed()
    {
        int flagsAt = (int)Marshal.OffsetOf(typeof(WaveHdr), "dwFlags");
        int cap = _ring.Length / 2;
        while (_running)
        {
            int queued = 0;
            for (int i = 0; i < Buffers; i++)
                if (((uint)Marshal.ReadInt32(_headers[i], flagsAt) & WhdrInQueue) != 0) queued++;
            for (int i = 0; i < Buffers && _running; i++)
            {
                uint flags = (uint)Marshal.ReadInt32(_headers[i], flagsAt);
                // Not written yet (no flags beyond prepared), or played out.
                if ((flags & WhdrInQueue) != 0 && (flags & WhdrDone) == 0) continue;
                lock (_lock)
                {
                    // The music comes in blocks: wait for a whole buffer's worth while
                    // the device has plenty queued, so it only plays silence when it
                    // would otherwise run dry.
                    if (_ringCount < _frames && queued >= 2) break;
                    queued++;
                    int take = Math.Min(_frames, _ringCount);
                    for (int f = 0; f < take; f++)
                    {
                        int at = (_ringStart + f) % cap * 2;
                        _pcm[f * 2] = ToShort(_ring[at]);
                        _pcm[f * 2 + 1] = ToShort(_ring[at + 1]);
                    }
                    _ringStart = (_ringStart + take) % cap;
                    _ringCount -= take;
                    // Short of music (starting up, a hitch): silence for the rest.
                    SilentFrames += _frames - take;
                    Array.Clear(_pcm, take * 2, (_frames - take) * 2);
                }
                Marshal.Copy(_pcm, 0, _data[i], _pcm.Length);
                Marshal.WriteInt32(_headers[i], flagsAt, (int)(flags & ~WhdrDone));
                uint result = waveOutWrite(_wave, _headers[i], (uint)Marshal.SizeOf(typeof(WaveHdr)));
                if (result != 0) { Failed = "waveOutWrite error " + result; _running = false; }
            }
            Thread.Sleep(5);
        }
    }

    static short ToShort(float s)
    {
        if (s >= 1f) return short.MaxValue;
        if (s <= -1f) return -short.MaxValue;
        return (short)(s * short.MaxValue);
    }

    public void Dispose()
    {
        if (_wave == IntPtr.Zero) return;
        _running = false;
        _thread.Join(500);
        waveOutReset(_wave);
        for (int i = 0; i < Buffers; i++)
        {
            waveOutUnprepareHeader(_wave, _headers[i], (uint)Marshal.SizeOf(typeof(WaveHdr)));
            Marshal.FreeHGlobal(_headers[i]);
            Marshal.FreeHGlobal(_data[i]);
        }
        waveOutClose(_wave);
        _wave = IntPtr.Zero;
    }

    // -- winmm ----------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    struct WaveFormatEx
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WaveHdr
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WaveOutCaps
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
        public uint dwSupport;
    }

    [DllImport("winmm.dll")] static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern uint waveOutGetDevCapsW(UIntPtr device, ref WaveOutCaps caps, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutOpen(out IntPtr wave, uint device, ref WaveFormatEx format, IntPtr callback, IntPtr instance, uint flags);
    [DllImport("winmm.dll")] static extern uint waveOutPrepareHeader(IntPtr wave, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutUnprepareHeader(IntPtr wave, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutWrite(IntPtr wave, IntPtr header, uint size);
    [DllImport("winmm.dll")] static extern uint waveOutReset(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveOutClose(IntPtr wave);
    [DllImport("winmm.dll")] static extern uint waveOutMessage(IntPtr wave, uint message, out uint param1, out uint param2);
    [DllImport("winmm.dll")] static extern uint waveOutMessage(IntPtr wave, uint message, IntPtr param1, IntPtr param2);
}
