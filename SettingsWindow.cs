using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using T = BloodMoonMusicText;

/// <summary>The Blood Moon Music button on the Esc menu: opens the radio.</summary>
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
/// The radio's settings page: volume, fade length, the output device, muting the game's music,
/// reloading and opening the station's folder, and its tracks with any playlist
/// songs that have no file. Every change applies and saves straight away.
/// </summary>
public class XUiC_BloodMoonMusicWindow : XUiController
{
    // Lines the track list has room for, missing songs included.
    const int ListLines = 9;
    const string Gold = "[ffc35a]";
    const string Dim = "[9a9a9a]";
    const string Red = "[ff7b6b]";

    XUiV_Label volume;
    XUiV_Label fade;
    XUiV_Label output;
    XUiV_Label status;
    XUiV_Label tracks;
    XUiC_ToggleButton mute;
    float refresh;

    public override void Init()
    {
        base.Init();
        volume = Label("lblVolume");
        fade = Label("lblFade");
        output = Label("lblOutput");
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
        WhenPressed("btnOutputPrev", () => StepOutput(-1));
        WhenPressed("btnOutputNext", () => StepOutput(1));
        WhenPressed("btnReload", () => Music?.Reload());
        WhenPressed("btnFolder", OpenFolder);
        WhenPressed("btnBack", () =>
        {
            xui.playerUI.windowManager.Close("bmMusicSettings");
            xui.playerUI.windowManager.Open("bmMusic", true);
        });
        WhenPressed("btnClose", () => xui.playerUI.windowManager.Close("bmMusicSettings"));
    }

    public override void OnOpen()
    {
        base.OnOpen();
        if (mute != null) mute.Value = BloodMoonMusicSettings.MuteGameMusic;
        BloodMoonMusicOutput.Refresh();
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

    void StepOutput(int step)
    {
        BloodMoonMusicOutput.Refresh();
        var choices = BloodMoonMusicOutput.Choices();
        int i = choices.IndexOf(BloodMoonMusicSettings.Output);
        // An unplugged device isn't in the list: step from the game's output.
        i = i < 0 ? (step > 0 ? 0 : choices.Count) : i;
        string next = choices[((i + step) % choices.Count + choices.Count) % choices.Count];
        BloodMoonMusicSettings.Output = next;
        BloodMoonMusicSettings.OutputName = next == BloodMoonMusicOutput.Game || next == BloodMoonMusicOutput.Communication
            ? "" : BloodMoonMusicOutput.Name(next);
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
            fade.Text = s <= 0f ? T.Get("bmmOff", "Off") : T.Format("bmmSeconds", "{0} s", s.ToString("0.#"));
        }
        if (output != null) output.Text = BloodMoonMusicOutput.Name(BloodMoonMusicSettings.Output);
        var music = Music;
        if (status != null) status.Text = Status(music);
        if (tracks != null) tracks.Text = TrackList(music);
    }

    static string Status(BloodMoonMusicComponent music)
    {
        if (music == null) return Red + T.Get("bmmNotRunning", "Blood Moon Music isn't running") + "[-]";
        if (!music.Loaded) return Dim + T.Get("bmmLoadingTracks", "Loading tracks...") + "[-]";
        if (music.OutputError != null)
            return Red + T.Format("bmmOutputFailed", "Can't play on {0}: using the game's output", BloodMoonMusicOutput.Name(BloodMoonMusicSettings.Output)) + "[-]";
        string playing = music.Playing;
        if (playing != null) return Gold + T.Get("bmmPlaying", "Playing:") + "[-] " + BloodMoonMusicComponent.DisplayName(playing);
        return music.BloodMoonNow
            ? T.Get("bmmBloodMoonUp", "Blood Moon is up")
            : Dim + T.Get("bmmWaiting", "Waiting for the Blood Moon") + "[-]";
    }

    static string TrackList(BloodMoonMusicComponent music)
    {
        if (music == null || !music.Loaded) return "";
        var names = music.Tracks.ToList();
        var lines = new List<string>();
        string station = BloodMoonMusicSettings.YourStation
            ? T.Get("bmmYourMusic", "Your music")
            : T.Get("bmmKitsuneTracks", "Kitsune's tracks");
        lines.Add(music.PlaylistName != null
            ? Gold + T.Format("bmmPlaylistLabel", "{0}, playlist {1}:", station, music.PlaylistName) + "[-] "
                + T.Format("bmmSongsFound", "{0} of {1} songs found", names.Count, names.Count + music.Missing.Count)
            : Gold + station + ":[-] " + T.Format("bmmTrackCount", "{0} track(s), in file-name order", names.Count));
        if (names.Count == 0)
        {
            lines.Add(Red + T.Get("bmmNoAudioFiles", "No .ogg, .mp3 or .wav files in this station's folder.") + "[-]");
            lines.Add(Dim + T.Get("bmmDropSomeIn", "Open it, drop some in, then Reload.") + "[-]");
        }
        string playing = music.Playing;
        var missing = music.Missing;
        // Room for the missing songs' heading and at least two of them.
        int room = ListLines - lines.Count - (missing.Count > 0 ? Math.Min(missing.Count, 2) + 1 : 0);
        for (int i = 0; i < names.Count; i++)
        {
            if (i == room - 1 && names.Count > room) { lines.Add(Dim + T.Format("bmmAndMore", "...and {0} more", names.Count - i) + "[-]"); break; }
            string line = (i + 1) + ". " + BloodMoonMusicComponent.DisplayName(names[i]);
            lines.Add(names[i] == playing ? Gold + line + "[-]" : line);
        }
        if (missing.Count > 0)
        {
            lines.Add(Red + T.Format("bmmNotInFolder", "Not in the Music folder ({0}):", missing.Count) + "[-]");
            int left = ListLines - lines.Count;
            for (int i = 0; i < missing.Count && left > 0; i++, left--)
            {
                if (left == 1 && missing.Count - i > 1) { lines.Add(Dim + T.Format("bmmAndMore", "...and {0} more", missing.Count - i) + "[-]"); break; }
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
