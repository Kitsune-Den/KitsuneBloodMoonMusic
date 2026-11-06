using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Kitsune Blood Moon Music — client-side. Plays music during the Blood Moon
// horde and fades out at dawn: Kitsune's tracks (the mod's Music/ folder) or
// your own (.ogg, .mp3 or .wav in the user data folder's KitsuneBloodMoonMusic/
// Music), in file-name order (1_, 2_, ... 10_) or a playlist's.
public class BloodMoonMusicApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var go = new GameObject("KitsuneBloodMoonMusic");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var comp = go.AddComponent<BloodMoonMusicComponent>();
        comp.KitsuneDir = Path.Combine(_modInstance.Path, "Music");
        BloodMoonMusicSettings.Load();
        // There from the start, so Open folder has somewhere to go.
        try { Directory.CreateDirectory(BloodMoonMusicSettings.YourMusicDir); }
        catch (Exception e) { Debug.LogWarning("[BloodMoonMusic] couldn't create " + BloodMoonMusicSettings.YourMusicDir + ": " + e.Message); }
        Debug.Log("[BloodMoonMusic] init; station " + BloodMoonMusicSettings.Station + ", music dir = " + comp.MusicDir);
    }
}

public class BloodMoonMusicComponent : MonoBehaviour
{
    public static BloodMoonMusicComponent Instance;
    /// <summary>The tracks that come with the mod.</summary>
    public string KitsuneDir;
    /// <summary>The station's folder: Kitsune's tracks or the player's own.</summary>
    public string MusicDir => BloodMoonMusicSettings.YourStation ? BloodMoonMusicSettings.YourMusicDir : KitsuneDir;
    private AudioSource _source;
    private BloodMoonMusicTap _tap;
    /// <summary>The Output setting the tap's device is for.</summary>
    private string _tapFor;
    // The music's volume, fades included; SetVolume puts it where it applies.
    private float _volume;
    // The station's audio files, in play order. Only the playing track and the
    // one after it are read into memory: a read track is about 10 MB a minute,
    // so a long playlist read all at once would be gigabytes.
    private readonly List<string> _files = new List<string>();
    // The next track, read ahead so it starts without a wait, and its place.
    private AudioClip _next;
    private int _nextIndex = -1;
    // The track to start as soon as it's read, or -1.
    private int _want = -1;
    // Bumped by Reload, so a track still being read for the old list is dropped.
    private int _gen;
    // Tracks in a row that wouldn't load; all of them, and it stops trying until a Reload.
    private int _failed;
    private bool _wasActive;
    private bool _wasBloodMoon;
    private int _idx;
    private float _timer;
    private bool _loaded;
    private bool _preview;
    // Power pressed during a horde: no music until it's over.
    private bool _silenced;
    private int _current = -1;
    private Coroutine _fade;

    /// <summary>Playlist songs with no file in Music/, for the settings window.</summary>
    public readonly List<string> Missing = new List<string>();
    /// <summary>The playlist file in use, or null when every track plays.</summary>
    public string PlaylistName { get; private set; }
    public bool Loaded => _loaded;
    public bool Preview => _preview;
    public bool BloodMoonNow => IsBloodMoon();
    public IEnumerable<string> Tracks => _files.Select(f => Path.GetFileNameWithoutExtension(f));
    public int TrackCount => _files.Count;
    /// <summary>The playing track's place in the list (or the one about to play,
    /// while it's read), or -1.</summary>
    public int CurrentIndex => _want >= 0 ? _want : Playing != null ? _current : -1;
    public bool Fading => _fade != null;
    /// <summary>Horde night, with the music switched off with the power button.</summary>
    public bool Silenced => _silenced;
    /// <summary>The track that's playing, or null.</summary>
    public string Playing => _source != null && _source.isPlaying && _source.clip != null ? _source.clip.name : null;
    /// <summary>Why the Output setting's device isn't in use (and the music is on
    /// the game's output instead), or null.</summary>
    public string OutputError { get; private set; }

    /// <summary>A track's file name as people would write it: the order number
    /// and underscores gone, so "1_Seven_Days_Stand" shows as "Seven Days Stand".</summary>
    public static string DisplayName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;
        string s = System.Text.RegularExpressions.Regex.Replace(name, @"^\d+\s*[-_.)\s]\s*", "");
        s = s.Replace('_', ' ').Trim();
        return s.Length > 0 ? s : name;
    }

    private void Start()
    {
        Instance = this;
        _source = gameObject.AddComponent<AudioSource>();
        _source.loop = false;
        _source.spatialBlend = 0f;
        // After the AudioSource, so its filter gets the source's samples.
        _tap = gameObject.AddComponent<BloodMoonMusicTap>();
        SetVolume(BloodMoonMusicSettings.Volume);
        ListTracks();
    }

    /// <summary>Read the Music folder again, for tracks or a playlist added
    /// while the game is running.</summary>
    public void Reload()
    {
        if (!_loaded) return;
        // Mid-horde or mid-fade, the game's music is turned down: give it back.
        if (_wasActive || _fade != null) RestoreGameMusic();
        if (_fade != null) { StopCoroutine(_fade); _fade = null; }
        _source.Stop();
        FreeTracks();
        _files.Clear();
        Missing.Clear();
        PlaylistName = null;
        _idx = 0;
        _current = -1;
        _want = -1;
        _failed = 0;
        _gen++;
        // If it's horde night, Update turns the music (and the mute) back on
        // with the new list's first track.
        _wasActive = false;
        SetVolume(BloodMoonMusicSettings.Volume);
        Debug.Log("[BloodMoonMusic] reloading " + MusicDir);
        ListTracks();
    }

    /// <summary>After the settings window changes something: apply it now and save.</summary>
    public void SettingsChanged()
    {
        if (_fade == null) SetVolume(BloodMoonMusicSettings.Volume);
        if (_source.isPlaying) UseOutput();
        else { OutputError = null; CloseOutput(); }
        if (_wasActive)
        {
            if (BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
            else RestoreGameMusic();
        }
        BloodMoonMusicSettings.Save();
    }

    /// <summary>The station's tracks, in play order: only their names. Each is
    /// read when it's about to play (Read).</summary>
    private void ListTracks()
    {
        if (Directory.Exists(MusicDir))
        {
            // The folder lists files in no set order (on Linux especially), so sort
            // them by name, numbers as numbers: 2_ before 10_.
            var files = Directory.GetFiles(MusicDir)
                .Where(f => TypeOf(f) != AudioType.UNKNOWN)
                .OrderBy(f => Path.GetFileName(f), NaturalOrder.Instance)
                .ToList();
            _files.AddRange(FromPlaylist(files));
        }
        else Debug.LogWarning("[BloodMoonMusic] no Music folder at " + MusicDir);
        _loaded = true;
        Debug.Log("[BloodMoonMusic] " + _files.Count + " track(s) on this station");
    }

    /// <summary>Read the track at this place in the list into memory, then start
    /// it if it's the one wanted (PlayIndex), or keep it as the next one.</summary>
    private IEnumerator Read(int index, int gen)
    {
        var f = _files[index];
        AudioClip clip = null;
        // A proper file URL, so names with spaces or # load too.
        using (var uwr = UnityWebRequestMultimedia.GetAudioClip(new Uri(f).AbsoluteUri, TypeOf(f)))
        {
            yield return uwr.SendWebRequest();
            if (uwr.result == UnityWebRequest.Result.Success)
            {
                clip = DownloadHandlerAudioClip.GetContent(uwr);
                clip.name = Path.GetFileNameWithoutExtension(f);
            }
            else Debug.LogWarning("[BloodMoonMusic] load failed: " + f + " (" + uwr.error + ")");
        }
        // Reloaded meanwhile: this list is gone.
        if (gen != _gen)
        {
            if (clip != null) Destroy(clip);
            yield break;
        }
        // The read ahead is done with: kept below, or there's nothing to keep.
        bool ahead = _nextIndex == index && _next == null;
        if (ahead && (clip == null || _want == index)) _nextIndex = -1;
        if (clip == null)
        {
            // On to the one after, unless none of them load.
            if (_want != index) yield break;
            _want = -1;
            if (++_failed < _files.Count) PlayIndex((index + 1) % _files.Count);
            else Debug.LogWarning("[BloodMoonMusic] none of this station's tracks would load");
            yield break;
        }
        if (_want == index) StartClip(clip, index);
        else if (ahead) _next = clip;
        else Destroy(clip);
    }

    /// <summary>Let go of the tracks in memory, the one read ahead too.</summary>
    private void FreeTracks()
    {
        var clip = _source.clip;
        _source.clip = null;
        if (clip != null) Destroy(clip);
        if (_next != null) Destroy(_next);
        _next = null;
        _nextIndex = -1;
    }

    /// <summary>A playlist file in Music/ (Playlist.cs) picks which tracks play
    /// and in what order; without one, every track plays in file-name order.</summary>
    private List<string> FromPlaylist(List<string> files)
    {
        var file = Playlist.Find(MusicDir);
        if (file == null) return files;
        try
        {
            var entries = Playlist.Read(file);
            var missing = new List<string>();
            var ordered = Playlist.Order(entries, files, missing);
            PlaylistName = Path.GetFileName(file);
            Missing.AddRange(missing);
            Debug.Log("[BloodMoonMusic] playlist " + Path.GetFileName(file) + ": " + ordered.Count + " of " + entries.Count + " found");
            if (missing.Count > 0)
            {
                Debug.Log("[BloodMoonMusic] not in Music/: " + string.Join("; ", missing.Take(10).ToArray())
                    + (missing.Count > 10 ? " (and " + (missing.Count - 10) + " more)" : ""));
            }
            if (ordered.Count > 0) return ordered;
            Debug.LogWarning("[BloodMoonMusic] none of the playlist's songs are in Music/; playing every track instead");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BloodMoonMusic] couldn't read " + Path.GetFileName(file) + ": " + e.Message + "; playing every track instead");
        }
        return files;
    }

    /// <summary>What Unity should decode a file as; UNKNOWN for anything we don't play.</summary>
    private static AudioType TypeOf(string file)
    {
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".ogg": return AudioType.OGGVORBIS;
            case ".mp3": return AudioType.MPEG;
            case ".wav": return AudioType.WAV;
            default: return AudioType.UNKNOWN;
        }
    }

    private void Update()
    {
        if (_tap != null) _tap.Master = AudioListener.volume;
        // Real time: single-player stops the game's clock in the Esc menu (where
        // the radio is), and the music plays on there.
        _timer += Time.unscaledDeltaTime;
        if (_timer < 1f) return;
        _timer = 0f;
        if (_tap.On)
        {
            // Nothing to play: let the device go (it opens again with the next track).
            if (!_source.isPlaying) CloseOutput();
            // Unplugged, or the game's audio restarted at another rate: open it again,
            // or carry on on the game's output.
            else if (_tap.Player.Failed != null || _tap.Player.SampleRate != AudioSettings.outputSampleRate) UseOutput();
        }
        if (!_loaded || _files.Count == 0) return;

        bool bloodMoon = IsBloodMoon();
        // Music switched on before the horde is the horde's now, so it fades at dawn.
        if (bloodMoon && !_wasBloodMoon) _preview = false;
        _wasBloodMoon = bloodMoon;
        if (!bloodMoon) _silenced = false;
        bool active = _preview || (bloodMoon && !_silenced);
        if (active && !_wasActive)
        {
            _wasActive = true;
            Debug.Log("[BloodMoonMusic] Blood Moon: music on");
            // Back on before a fade finished: carry on with the track that was fading.
            if (_fade != null) { StopCoroutine(_fade); _fade = null; SetVolume(BloodMoonMusicSettings.Volume); }
            if (!_source.isPlaying && _want < 0) PlayNext();
        }
        else if (!active && _wasActive)
        {
            _wasActive = false;
            _want = -1;
            Debug.Log("[BloodMoonMusic] dawn: fading out");
            _fade = StartCoroutine(FadeOut());
        }
        else if (active && !_source.isPlaying && _want < 0) { PlayNext(); }
        // Every tick, not once: the game sets the channel again when you change
        // the music volume or its audio snapshot changes.
        if (active && BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
    }

    /// <summary>Fade the track out, then stop it and give the game its music back.</summary>
    private IEnumerator FadeOut()
    {
        float start = _volume;
        float seconds = BloodMoonMusicSettings.FadeSeconds;
        for (float t = 0f; t < seconds && _source.isPlaying; t += Time.unscaledDeltaTime)
        {
            SetVolume(Mathf.Lerp(start, 0f, t / seconds));
            if (BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
            yield return null;
        }
        _source.Stop();
        // Out of memory until the next horde.
        FreeTracks();
        SetVolume(BloodMoonMusicSettings.Volume);
        RestoreGameMusic();
        _fade = null;
        Debug.Log("[BloodMoonMusic] music off");
    }

    /// <summary>The game's own music (horde, combat, exploration) all goes through
    /// the dmsVol channel of its mixer; ours doesn't, so turn that channel down
    /// while our tracks play.</summary>
    private static void MuteGameMusic()
    {
        var mixer = DynamicMusic.MixerController.Master;
        if (mixer != null) mixer.SetFloat("dmsVol", -80f);
    }

    /// <summary>Back to the music volume set in the game's options.</summary>
    private static void RestoreGameMusic()
    {
        if (DynamicMusic.MixerController.Master == null) return;
        DynamicMusic.MixerController.Instance.SetDynamicMusicVolume(GamePrefs.GetFloat(EnumGamePrefs.OptionsMusicVolumeLevel));
    }

    /// <summary>The world's own Blood Moon flag, worked out from the clock and the
    /// horde day. Unlike the AI director's, it is set on a client of a server too:
    /// the AI director only exists where the world is hosted.</summary>
    private static bool IsBloodMoon()
    {
        var gm = GameManager.Instance;
        var world = gm != null ? gm.World : null;
        return world != null && world.IsWorldEvent(World.WorldEvent.BloodMoon);
    }

    private void PlayNext() => PlayIndex(_idx % _files.Count);

    /// <summary>Play the track at this place in the list: now if it's in memory,
    /// else as soon as it's read.</summary>
    private void PlayIndex(int index)
    {
        if (_failed >= _files.Count) return;
        _idx = index + 1;
        if (_source.clip != null && _current == index)
        {
            // Still in memory (one track on the station, or the same one again).
            StartClip(_source.clip, index);
            return;
        }
        if (_next != null && _nextIndex == index)
        {
            var clip = _next;
            _next = null;
            _nextIndex = -1;
            StartClip(clip, index);
            return;
        }
        _source.Stop();
        _want = index;
        // Already being read ahead: it starts when that's done.
        if (_nextIndex != index) StartCoroutine(Read(index, _gen));
    }

    /// <summary>Start a track that's in memory, let the last one go, and read the
    /// one after it ahead.</summary>
    private void StartClip(AudioClip clip, int index)
    {
        _want = -1;
        _failed = 0;
        var old = _source.clip;
        _source.clip = clip;
        _current = index;
        if (old != null && old != clip) Destroy(old);
        UseOutput();
        _source.Play();
        Debug.Log("[BloodMoonMusic] playing " + clip.name);
        int next = (index + 1) % _files.Count;
        if (next == index || _nextIndex == next) return;
        if (_next != null) Destroy(_next);
        _next = null;
        _nextIndex = next;
        StartCoroutine(Read(next, _gen));
    }

    /// <summary>The power button: playing, it stops (with the fade; during a horde,
    /// until dawn); stopped, it plays the tracks now, Blood Moon or not.
    /// Returns whether music is on.</summary>
    public bool Power()
    {
        bool on = (Playing != null || _want >= 0) && _fade == null;
        if (on)
        {
            _preview = false;
            if (IsBloodMoon()) _silenced = true;
        }
        else
        {
            _silenced = false;
            // During a horde that's the horde's music back on, to fade at dawn.
            _preview = !IsBloodMoon();
        }
        _timer = 1f;
        return !on;
    }

    /// <summary>Play the track at this place in the list now, turning the music
    /// on if it was off.</summary>
    public void PlayTrack(int index)
    {
        if (!_loaded || index < 0 || index >= _files.Count) return;
        if (_fade != null) { StopCoroutine(_fade); _fade = null; }
        SetVolume(BloodMoonMusicSettings.Volume);
        _silenced = false;
        if (!_wasActive) { _preview = !IsBloodMoon(); _wasActive = true; }
        // A track picked by hand gets another go, even if none would load before.
        _failed = 0;
        PlayIndex(index);
        if (BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
    }

    /// <summary>The next (1) or previous (-1) track, wrapping round.</summary>
    public void Seek(int step)
    {
        int n = _files.Count;
        if (n == 0) return;
        int from = CurrentIndex;
        int to = from < 0 ? (step > 0 ? 0 : n - 1) : ((from + step) % n + n) % n;
        PlayTrack(to);
    }

    /// <summary>Kitsune's tracks or yours: switch, save, and load the other folder.
    /// Music that was on carries on, from the new station's first track.</summary>
    public void SwitchStation()
    {
        if (!_loaded) return;
        BloodMoonMusicSettings.Station = BloodMoonMusicSettings.YourStation ? "kitsune" : "yours";
        BloodMoonMusicSettings.Save();
        Reload();
    }

    /// <summary>The music's volume, fades included. On the game's output that's the
    /// AudioSource's own; on a device of our own the source stays at 1 and the tap
    /// scales the samples, whichever side of the filter Unity applies the volume.</summary>
    private void SetVolume(float volume)
    {
        _volume = volume;
        _tap.Gain = volume;
        _source.volume = _tap.On ? 1f : volume;
    }

    /// <summary>Put the music on the device in the Output setting, if it isn't there
    /// already: on the game's output for that setting, or if the device won't open.</summary>
    private void UseOutput()
    {
        string want = BloodMoonMusicSettings.Output;
        if (want == BloodMoonMusicOutput.Game || !BloodMoonMusicOutput.Supported)
        {
            OutputError = null;
            CloseOutput();
            return;
        }
        if (_tap.On && _tapFor == want && _tap.Player.Failed == null && _tap.Player.SampleRate == AudioSettings.outputSampleRate) return;
        if (_tap.On && _tap.Player.Failed != null) Debug.LogWarning("[BloodMoonMusic] lost " + _tap.Player.DeviceName + ": " + _tap.Player.Failed);
        int number = BloodMoonMusicOutput.Number(want);
        string error = null;
        var player = number < 0 ? null : WaveOutPlayer.Open(number, AudioSettings.outputSampleRate, out error);
        if (number < 0) error = "not connected";
        _tap.Use(player);
        _tapFor = player != null ? want : null;
        OutputError = player == null ? error : null;
        if (player != null) Debug.Log("[BloodMoonMusic] output: " + player.DeviceName + " at " + player.SampleRate + " Hz");
        else Debug.LogWarning("[BloodMoonMusic] couldn't play on " + BloodMoonMusicOutput.Name(want) + " (" + error + "); using the game's output");
        SetVolume(_volume);
    }

    /// <summary>Back to the game's output (until the next track, if the Output
    /// setting is another device).</summary>
    private void CloseOutput()
    {
        if (!_tap.On) return;
        _tap.Close();
        _tapFor = null;
        SetVolume(_volume);
    }

    /// <summary>The playing track's spectrum, for the radio's level bars; false when
    /// nothing is playing.</summary>
    public bool GetSpectrum(float[] samples)
    {
        if (_source == null || !_source.isPlaying) return false;
        // On a device of our own, Unity only hears the tap's silence.
        if (_tap.On) _tap.GetSpectrum(samples);
        else _source.GetSpectrumData(samples, 0, FFTWindow.BlackmanHarris);
        return true;
    }

    public string Status()
    {
        return (_loaded ? _files.Count + " track(s)" : "still loading tracks")
            + "; Blood Moon " + (IsBloodMoon() ? "on" : "off")
            + "; station " + BloodMoonMusicSettings.Station
            + "; output " + (_tap != null && _tap.On ? _tap.Player.DeviceName : "the game's")
            + (OutputError != null ? " (" + BloodMoonMusicOutput.Name(BloodMoonMusicSettings.Output) + ": " + OutputError + ")" : "")
            + (_preview ? "; preview on" : "")
            + (_source != null && _source.isPlaying ? "; playing " + _source.clip.name : "; not playing");
    }
}

/// <summary>bmmusic in the F1 console: what the mod sees, and a preview of the
/// music so you can hear it without waiting for horde night.</summary>
public class ConsoleCmdBloodMoonMusic : ConsoleCmdAbstract
{
    public override bool IsExecuteOnClient => true;
    public override bool AllowedInMainMenu => true;

    public override string[] getCommands() { return new[] { "bmmusic" }; }

    public override string getDescription() { return "Kitsune Blood Moon Music: status; play (on/off), next, station (Kitsune's or yours), reload, output [number]"; }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        var bmm = BloodMoonMusicComponent.Instance;
        if (bmm == null) { SdtdConsole.Instance.Output("Blood Moon Music isn't running"); return; }
        if (_params.Count > 0 && _params[0] == "play")
        {
            SdtdConsole.Instance.Output(bmm.Power() ? "music on: 'bmmusic play' again to stop" : "music off");
        }
        else if (_params.Count > 0 && _params[0] == "next")
        {
            bmm.Seek(1);
        }
        else if (_params.Count > 0 && _params[0] == "station")
        {
            bmm.SwitchStation();
            SdtdConsole.Instance.Output("station: " + BloodMoonMusicSettings.Station + "; loading " + bmm.MusicDir);
            return;
        }
        else if (_params.Count > 0 && _params[0] == "output")
        {
            BloodMoonMusicOutput.Refresh();
            var choices = BloodMoonMusicOutput.Choices();
            if (_params.Count > 1 && int.TryParse(_params[1], out int n) && n >= 1 && n <= choices.Count)
            {
                BloodMoonMusicSettings.Output = choices[n - 1];
                BloodMoonMusicSettings.OutputName = n > 2 ? BloodMoonMusicOutput.Name(choices[n - 1]) : "";
                bmm.SettingsChanged();
            }
            for (int i = 0; i < choices.Count; i++)
                SdtdConsole.Instance.Output((choices[i] == BloodMoonMusicSettings.Output ? "* " : "  ") + (i + 1) + ". " + BloodMoonMusicOutput.Name(choices[i]));
            if (choices.Count > 1) SdtdConsole.Instance.Output("'bmmusic output <number>' to choose");
        }
        else if (_params.Count > 0 && _params[0] == "reload")
        {
            bmm.Reload();
            SdtdConsole.Instance.Output("reading the Music folder again; 'bmmusic' in a moment for the count");
            return;
        }
        SdtdConsole.Instance.Output(bmm.Status());
    }
}
