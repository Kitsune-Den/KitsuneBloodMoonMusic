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
        Debug.Log("[BloodMoonMusic] init; music dir = " + comp.MusicDir);
    }
}

public class BloodMoonMusicComponent : MonoBehaviour
{
    public string MusicDir;
    private AudioSource _source;
    private readonly List<AudioClip> _clips = new List<AudioClip>();
    private bool _wasActive;
    private int _idx;
    private float _timer;
    private bool _loaded;

    private void Start()
    {
        _source = gameObject.AddComponent<AudioSource>();
        _source.loop = false;
        _source.spatialBlend = 0f;
        _source.volume = 0.6f;
        StartCoroutine(LoadClips());
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

        bool active = IsBloodMoon();
        if (active && !_wasActive) { _wasActive = true; PlayNext(); }
        else if (!active && _wasActive) { _wasActive = false; _source.Stop(); }
        else if (active && !_source.isPlaying) { PlayNext(); }
    }

    private static bool IsBloodMoon()
    {
        var gm = GameManager.Instance;
        var world = gm != null ? gm.World : null;
        var ai = world != null ? world.aiDirector : null;
        var bm = ai != null ? ai.BloodMoonComponent : null;
        return bm != null && bm.BloodMoonActive;
    }

    private void PlayNext()
    {
        _source.clip = _clips[_idx % _clips.Count];
        _idx++;
        _source.Play();
    }
}

/// <summary>Compares names the way people number them: runs of digits as
/// numbers ("2_Song" before "10_Song"), the rest ignoring case.</summary>
public class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new NaturalOrder();

    public int Compare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                var na = a.Substring(si, i - si).TrimStart('0');
                var nb = b.Substring(sj, j - sj).TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLowerInvariant(a[i]).CompareTo(char.ToLowerInvariant(b[j]));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }
}
