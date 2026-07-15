using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.Networking;

// Kitsune Blood Moon Music — client-side. Plays your own tracks during the
// Blood Moon horde and stops at dawn. Drop .ogg files in the mod's Music/ folder.
public class BloodMoonMusicApi : IModApi
{
    public void InitMod(Mod _modInstance)
    {
        var go = new GameObject("KitsuneBloodMoonMusic");
        Object.DontDestroyOnLoad(go);
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
        var files = Directory.GetFiles(MusicDir, "*.ogg");
        foreach (var f in files)
        {
            var uri = "file:///" + f.Replace("\\", "/");
            using (var uwr = UnityWebRequestMultimedia.GetAudioClip(uri, AudioType.OGGVORBIS))
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
