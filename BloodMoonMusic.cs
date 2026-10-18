using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.Networking;

// Kitsune Blood Moon Music — client-side. Plays your own tracks during the
// Blood Moon horde and stops at dawn. Drop .ogg, .mp3 or .wav files in the
// mod's Music/ folder; they play in file-name order (1_, 2_, ... 10_).
public class BloodMoonMusicApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var go = new GameObject("KitsuneBloodMoonMusic");
        UnityEngine.Object.DontDestroyOnLoad(go);
        var comp = go.AddComponent<BloodMoonMusicComponent>();
        comp.MusicDir = Path.Combine(_modInstance.Path, "Music");
        BloodMoonMusicSettings.Load();
        Debug.Log("[BloodMoonMusic] init; music dir = " + comp.MusicDir);
    }
}

public class BloodMoonMusicComponent : MonoBehaviour
{
    public static BloodMoonMusicComponent Instance;
    public string MusicDir;
    private AudioSource _source;
    private readonly List<AudioClip> _clips = new List<AudioClip>();
    private bool _wasActive;
    private int _idx;
    private float _timer;
    private bool _loaded;
    private bool _preview;
    private Coroutine _fade;

    /// <summary>Playlist songs with no file in Music/, for the settings window.</summary>
    public readonly List<string> Missing = new List<string>();
    /// <summary>The playlist file in use, or null when every track plays.</summary>
    public string PlaylistName { get; private set; }
    public bool Loaded => _loaded;
    public bool Preview => _preview;
    public bool BloodMoonNow => IsBloodMoon();
    public IEnumerable<string> Tracks => _clips.Select(c => c.name);
    /// <summary>The track that's playing, or null.</summary>
    public string Playing => _source != null && _source.isPlaying && _source.clip != null ? _source.clip.name : null;

    private void Start()
    {
        Instance = this;
        _source = gameObject.AddComponent<AudioSource>();
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.volume = BloodMoonMusicSettings.Volume;
        StartCoroutine(LoadClips());
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
        _source.clip = null;
        foreach (var clip in _clips) Destroy(clip);
        _clips.Clear();
        Missing.Clear();
        PlaylistName = null;
        _idx = 0;
        // If it's horde night, Update turns the music (and the mute) back on
        // once the new tracks are in.
        _wasActive = false;
        _source.volume = BloodMoonMusicSettings.Volume;
        _loaded = false;
        Debug.Log("[BloodMoonMusic] reloading " + MusicDir);
        StartCoroutine(LoadClips());
    }

    /// <summary>After the settings window changes something: apply it now and save.</summary>
    public void SettingsChanged()
    {
        if (_fade == null) _source.volume = BloodMoonMusicSettings.Volume;
        if (_wasActive)
        {
            if (BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
            else RestoreGameMusic();
        }
        BloodMoonMusicSettings.Save();
    }

    private IEnumerator LoadClips()
    {
        if (!Directory.Exists(MusicDir))
        {
            Debug.LogWarning("[BloodMoonMusic] no Music folder at " + MusicDir);
            _loaded = true;
            yield break;
        }
        // The folder lists files in no set order (on Linux especially), so sort
        // them by name, numbers as numbers: 2_ before 10_.
        var files = Directory.GetFiles(MusicDir)
            .Where(f => TypeOf(f) != AudioType.UNKNOWN)
            .OrderBy(f => Path.GetFileName(f), NaturalOrder.Instance)
            .ToList();
        files = FromPlaylist(files);
        foreach (var f in files)
        {
            // A proper file URL, so names with spaces or # load too.
            var uri = new Uri(f).AbsoluteUri;
            using (var uwr = UnityWebRequestMultimedia.GetAudioClip(uri, TypeOf(f)))
            {
                yield return uwr.SendWebRequest();
                if (uwr.result == UnityWebRequest.Result.Success)
                {
                    var clip = DownloadHandlerAudioClip.GetContent(uwr);
                    clip.name = Path.GetFileNameWithoutExtension(f);
                    _clips.Add(clip);
                }
                else
                {
                    Debug.LogWarning("[BloodMoonMusic] load failed: " + f + " (" + uwr.error + ")");
                }
            }
        }
        _loaded = true;
        Debug.Log("[BloodMoonMusic] loaded " + _clips.Count + " track(s)");
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
        _timer += Time.deltaTime;
        if (_timer < 1f) return;
        _timer = 0f;
        if (!_loaded || _clips.Count == 0) return;

        bool active = _preview || IsBloodMoon();
        if (active && !_wasActive)
        {
            _wasActive = true;
            Debug.Log("[BloodMoonMusic] Blood Moon: music on");
            // Back on before a fade finished: carry on with the track that was fading.
            if (_fade != null) { StopCoroutine(_fade); _fade = null; _source.volume = BloodMoonMusicSettings.Volume; }
            if (!_source.isPlaying) PlayNext();
        }
        else if (!active && _wasActive)
        {
            _wasActive = false;
            Debug.Log("[BloodMoonMusic] dawn: fading out");
            _fade = StartCoroutine(FadeOut());
        }
        else if (active && !_source.isPlaying) { PlayNext(); }
        // Every tick, not once: the game sets the channel again when you change
        // the music volume or its audio snapshot changes.
        if (active && BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
    }

    /// <summary>Fade the track out, then stop it and give the game its music back.</summary>
    private IEnumerator FadeOut()
    {
        float start = _source.volume;
        float seconds = BloodMoonMusicSettings.FadeSeconds;
        for (float t = 0f; t < seconds && _source.isPlaying; t += Time.unscaledDeltaTime)
        {
            _source.volume = Mathf.Lerp(start, 0f, t / seconds);
            if (BloodMoonMusicSettings.MuteGameMusic) MuteGameMusic();
            yield return null;
        }
        _source.Stop();
        _source.volume = BloodMoonMusicSettings.Volume;
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

    private void PlayNext()
    {
        _source.clip = _clips[_idx % _clips.Count];
        _idx++;
        _source.Play();
        Debug.Log("[BloodMoonMusic] playing " + _source.clip.name);
    }

    /// <summary>For the bmmusic console command: play the tracks now, Blood Moon
    /// or not, until it is called again. Returns whether the preview is on.</summary>
    public bool TogglePreview()
    {
        _preview = !_preview;
        _timer = 1f;
        return _preview;
    }

    public string Status()
    {
        return (_loaded ? _clips.Count + " track(s) loaded" : "still loading tracks")
            + "; Blood Moon " + (IsBloodMoon() ? "on" : "off")
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

    public override string getDescription() { return "Kitsune Blood Moon Music: status, 'bmmusic play' to preview the tracks, 'bmmusic reload' to read the Music folder again"; }

    public override void Execute(List<string> _params, CommandSenderInfo _senderInfo)
    {
        var bmm = BloodMoonMusicComponent.Instance;
        if (bmm == null) { SdtdConsole.Instance.Output("Blood Moon Music isn't running"); return; }
        if (_params.Count > 0 && _params[0] == "play")
        {
            SdtdConsole.Instance.Output(bmm.TogglePreview() ? "preview on: 'bmmusic play' again to stop" : "preview off");
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
