using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

/// <summary>The Blood Moon Music button on the Esc menu: opens the settings window.</summary>
public class XUiC_BloodMoonMusicOpener : XUiController
{
    public override void Init()
    {
        base.Init();
        if (GetChildByType<XUiC_SimpleButton>() is XUiC_SimpleButton button)
        {
            button.OnPressed += (_sender, _mouseButton) => xui.playerUI.windowManager.Open("bmMusic", true);
        }
    }
}

/// <summary>
/// The settings window: volume, fade length, muting the game's music, a preview,
/// reloading and opening the Music folder, and the tracks with any playlist songs
/// that have no file. Every change applies and saves straight away.
/// </summary>
public class XUiC_BloodMoonMusicWindow : XUiController
{
    // Lines the track list has room for, missing songs included.
    const int ListLines = 11;
    const string Gold = "[ffc35a]";
    const string Dim = "[9a9a9a]";
    const string Red = "[ff7b6b]";

    XUiV_Label volume;
    XUiV_Label fade;
    XUiV_Label status;
    XUiV_Label tracks;
    XUiC_ToggleButton mute;
    XUiC_SimpleButton preview;
    float refresh;

    public override void Init()
    {
        base.Init();
        volume = Label("lblVolume");
        fade = Label("lblFade");
        status = Label("lblStatus");
        tracks = Label("lblTracks");
        mute = GetChildById("tglMute") as XUiC_ToggleButton;
        if (mute != null)
        {
            mute.OnValueChanged += (_sender, _value) =>
            {
                BloodMoonMusicSettings.MuteGameMusic = _value;
                Changed();
            };
        }
        WhenPressed("btnVolDown", () => StepVolume(-1));
        WhenPressed("btnVolUp", () => StepVolume(1));
        WhenPressed("btnFadeDown", () => StepFade(-1));
        WhenPressed("btnFadeUp", () => StepFade(1));
        preview = Button("btnPreview");
        WhenPressed("btnPreview", () => Music?.TogglePreview());
        WhenPressed("btnReload", () => Music?.Reload());
        WhenPressed("btnFolder", OpenFolder);
        WhenPressed("btnClose", () => xui.playerUI.windowManager.Close("bmMusic"));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (mute != null) mute.Value = BloodMoonMusicSettings.MuteGameMusic;
        Show();
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        // What's playing and the reload count change underneath the window.
        refresh -= _dt;
        if (refresh <= 0f) Show();
    }

    static BloodMoonMusicComponent Music => BloodMoonMusicComponent.Instance;

    void StepVolume(int step)
    {
        int percent = Mathf.RoundToInt(BloodMoonMusicSettings.Volume * 100f);
        percent = Mathf.Clamp((percent + 5) / 10 * 10 + step * 10, 0, 100);
        BloodMoonMusicSettings.Volume = percent / 100f;
        Changed();
    }

    void StepFade(int step)
    {
        var steps = BloodMoonMusicSettings.FadeSteps;
        // The step nearest the current value, so a hand-edited file still steps cleanly.
        int i = Array.IndexOf(steps, steps.OrderBy(s => Mathf.Abs(s - BloodMoonMusicSettings.FadeSeconds)).First());
        BloodMoonMusicSettings.FadeSeconds = steps[Mathf.Clamp(i + step, 0, steps.Length - 1)];
        Changed();
    }

    void Changed()
    {
        if (Music != null) Music.SettingsChanged();
        else BloodMoonMusicSettings.Save();
        Show();
    }

    void OpenFolder()
    {
        if (Music == null) return;
        try
        {
            System.IO.Directory.CreateDirectory(Music.MusicDir);
            Application.OpenURL(new Uri(Music.MusicDir).AbsoluteUri);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BloodMoonMusic] couldn't open " + Music.MusicDir + ": " + e.Message);
        }
    }

    void Show()
    {
        refresh = 0.5f;
        if (volume != null) volume.Text = Mathf.RoundToInt(BloodMoonMusicSettings.Volume * 100f) + "%";
        if (fade != null)
        {
            float s = BloodMoonMusicSettings.FadeSeconds;
            fade.Text = s <= 0f ? "Off" : s.ToString("0.#") + " s";
        }
        var music = Music;
        if (preview != null) preview.Text = music != null && music.Preview ? "Stop preview" : "Preview";
        if (status != null) status.Text = Status(music);
        if (tracks != null) tracks.Text = TrackList(music);
    }

    static string Status(BloodMoonMusicComponent music)
    {
        if (music == null) return Red + "Blood Moon Music isn't running[-]";
        if (!music.Loaded) return Dim + "Loading tracks...[-]";
        string playing = music.Playing;
        if (playing != null) return Gold + "Playing:[-] " + playing;
        return music.BloodMoonNow ? "Blood Moon is up" : Dim + "Waiting for the Blood Moon[-]";
    }

    static string TrackList(BloodMoonMusicComponent music)
    {
        if (music == null || !music.Loaded) return "";
        var names = music.Tracks.ToList();
        var lines = new List<string>();
        lines.Add(music.PlaylistName != null
            ? Gold + "Playlist " + music.PlaylistName + ":[-] " + names.Count + " of " + (names.Count + music.Missing.Count) + " songs found"
            : Gold + names.Count + " track(s)[-], in file-name order");
        if (names.Count == 0)
        {
            lines.Add(Red + "No .ogg, .mp3 or .wav files in the Music folder.[-]");
            lines.Add(Dim + "Open it, drop some in, then Reload.[-]");
        }
        string playing = music.Playing;
        var missing = music.Missing;
        // Room for the missing songs' heading and at least two of them.
        int room = ListLines - lines.Count - (missing.Count > 0 ? Math.Min(missing.Count, 2) + 1 : 0);
        for (int i = 0; i < names.Count; i++)
        {
            if (i == room - 1 && names.Count > room) { lines.Add(Dim + "...and " + (names.Count - i) + " more[-]"); break; }
            string line = (i + 1) + ". " + names[i];
            lines.Add(names[i] == playing ? Gold + line + "[-]" : line);
        }
        if (missing.Count > 0)
        {
            lines.Add(Red + "Not in the Music folder (" + missing.Count + "):[-]");
            int left = ListLines - lines.Count;
            for (int i = 0; i < missing.Count && left > 0; i++, left--)
            {
                if (left == 1 && missing.Count - i > 1) { lines.Add(Dim + "...and " + (missing.Count - i) + " more[-]"); break; }
                lines.Add(Dim + missing[i] + "[-]");
            }
        }
        return string.Join("\n", lines.ToArray());
    }

    XUiV_Label Label(string id) => GetChildById(id)?.ViewComponent as XUiV_Label;

    XUiC_SimpleButton Button(string id) => GetChildById(id) as XUiC_SimpleButton;

    void WhenPressed(string id, Action action)
    {
        if (Button(id) is XUiC_SimpleButton b)
        {
            b.OnPressed += (_sender, _mouseButton) =>
            {
                action();
                Show();
            };
        }
    }
}
