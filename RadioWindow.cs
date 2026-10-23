using System;
using UnityEngine;
using T = BloodMoonMusicText;

/// <summary>
/// The radio: power (play or stop now), a red display with the status, track and
/// station over level bars that follow the music, VOL + and -, tracks 1 to 6,
/// seek, stations (Kitsune's tracks or yours), settings and close.
/// </summary>
public class XUiC_BloodMoonMusicRadio : XUiController
{
    const int Bars = 24;
    const int TrackButtons = 6;
    // Spectrum bins the bars cover, log-spaced: at 48 kHz, 512 bins of ~47 Hz,
    // so bin 1 to 255 is roughly 50 Hz to 12 kHz.
    const int Samples = 512;
    const int LowBin = 1;
    const int HighBin = 255;
    // dB range shown, from silent to a full bar.
    const float FloorDb = -55f;
    const float RangeDb = 45f;
    // How fast a bar falls, in full heights per second.
    const float Fall = 1.8f;
    // Music has far less energy up high, so without this only the left bars move:
    // each octave up gets this much more gain (the top bars about +27 dB).
    const float TiltDbPerOctave = 3.5f;

    static readonly Color PowerOn = new Color32(255, 70, 58, 255);
    static readonly Color PowerOff = new Color32(120, 120, 124, 255);
    const string Hot = "[ff4a3a]";

    readonly XUiV_Sprite[] bars = new XUiV_Sprite[Bars];
    readonly float[] levels = new float[Bars];
    readonly float[] spectrum = new float[Samples];
    readonly XUiC_SimpleButton[] trackButtons = new XUiC_SimpleButton[TrackButtons];
    XUiV_Label status;
    XUiV_Label track;
    XUiV_Label station;
    XUiV_Label volume;
    XUiV_Sprite powerIcon;
    XUiC_SimpleButton stations;
    float refresh;

    static BloodMoonMusicComponent Music => BloodMoonMusicComponent.Instance;

    public override void Init()
    {
        base.Init();
        for (int i = 0; i < Bars; i++) bars[i] = GetChildById("bar" + i)?.ViewComponent as XUiV_Sprite;
        status = Label("lblStatus");
        track = Label("lblTrack");
        station = Label("lblStation");
        volume = Label("lblVolume");
        powerIcon = GetChildById("powerIcon")?.ViewComponent as XUiV_Sprite;
        stations = Button("btnStations");

        WhenPressed("btnPower", () => Music?.Power());
        WhenPressed("btnVolUp", () => StepVolume(1));
        WhenPressed("btnVolDown", () => StepVolume(-1));
        WhenPressed("btnSeekBack", () => Music?.Seek(-1));
        WhenPressed("btnSeekFwd", () => Music?.Seek(1));
        WhenPressed("btnStations", () => Music?.SwitchStation());
        WhenPressed("btnSettings", () =>
        {
            xui.playerUI.windowManager.Close("bmMusic");
            xui.playerUI.windowManager.Open("bmMusicSettings", true);
        });
        WhenPressed("btnClose", () => xui.playerUI.windowManager.Close("bmMusic"));
        for (int i = 0; i < TrackButtons; i++)
        {
            int n = i;
            trackButtons[i] = Button("btnTrack" + (i + 1));
            WhenPressed("btnTrack" + (i + 1), () => Music?.PlayTrack(n));
        }
    }

    public override void OnOpen()
    {
        base.OnOpen();
        Array.Clear(levels, 0, levels.Length);
        Show();
    }

    public override void Update(float _dt)
    {
        base.Update(_dt);
        UpdateBars(_dt);
        refresh -= _dt;
        if (refresh <= 0f) Show();
    }

    void UpdateBars(float dt)
    {
        var music = Music;
        bool playing = music != null && music.GetSpectrum(spectrum);
        // The spectrum is after the volume; divide it back out so the bars move the
        // same at any volume.
        float gain = 1f / Mathf.Max(BloodMoonMusicSettings.Volume, 0.05f);
        for (int b = 0; b < Bars; b++)
        {
            float target = 0f;
            if (playing)
            {
                int lo = Bin(b), hi = Mathf.Max(Bin(b + 1), lo + 1);
                float peak = 0f;
                for (int i = lo; i < hi && i < Samples; i++) peak = Mathf.Max(peak, spectrum[i]);
                float db = 20f * Mathf.Log10(peak * gain + 1e-7f) + TiltDbPerOctave * Mathf.Log(lo, 2f);
                target = Mathf.Clamp01((db - FloorDb) / RangeDb);
            }
            levels[b] = Mathf.Max(target, levels[b] - Fall * dt);
            if (bars[b] != null) bars[b].Fill = levels[b];
        }
    }

    static int Bin(int band) =>
        Mathf.RoundToInt(LowBin * Mathf.Pow((float)HighBin / LowBin, (float)band / Bars));

    void StepVolume(int step)
    {
        int percent = Mathf.RoundToInt(BloodMoonMusicSettings.Volume * 100f);
        percent = Mathf.Clamp((percent + 5) / 10 * 10 + step * 10, 0, 100);
        BloodMoonMusicSettings.Volume = percent / 100f;
        if (Music != null) Music.SettingsChanged();
        else BloodMoonMusicSettings.Save();
    }

    void Show()
    {
        refresh = 0.25f;
        var music = Music;
        int current = music != null ? music.CurrentIndex : -1;
        int count = music != null ? music.TrackCount : 0;

        if (status != null) status.Text = T.Format("bmmRadioStatus", "STATUS: {0}", Hot + Status(music) + "[-]");
        if (track != null)
        {
            track.Text = current >= 0
                ? T.Format("bmmRadioTrack", "TRACK {0}  {1}", (current + 1).ToString("00"),
                    BloodMoonMusicComponent.DisplayName(music.Playing))
                : count == 1
                    ? T.Get("bmmRadioOneTrackReady", "TRACK --  1 TRACK READY")
                    : T.Format("bmmRadioTracksReady", "TRACK --  {0} TRACKS READY", count);
        }
        if (station != null)
        {
            station.Text = BloodMoonMusicSettings.YourStation
                ? T.Get("bmmRadioStationYours", "YOURS")
                : T.Get("bmmRadioStationKitsune", "KITSUNE'S");
        }
        if (volume != null)
        {
            volume.Text = T.Format("bmmRadioVolume", "VOL {0}%", Mathf.RoundToInt(BloodMoonMusicSettings.Volume * 100f));
        }
        if (powerIcon != null) powerIcon.Color = current >= 0 && !music.Fading ? PowerOn : PowerOff;
        if (stations?.Button != null) stations.Button.Selected = BloodMoonMusicSettings.YourStation;

        for (int i = 0; i < TrackButtons; i++)
        {
            var b = trackButtons[i];
            if (b == null) continue;
            b.Enabled = i < count;
            if (b.Button != null) b.Button.Selected = i == current;
        }
    }

    static string Status(BloodMoonMusicComponent music)
    {
        if (music == null) return T.Get("bmmStNotRunning", "NOT RUNNING");
        if (!music.Loaded) return T.Get("bmmStLoading", "LOADING TRACKS");
        if (music.TrackCount == 0) return T.Get("bmmStNoTracks", "NO TRACKS ON THIS STATION");
        if (music.Fading) return T.Get("bmmStFading", "FADING OUT");
        bool playing = music.Playing != null;
        if (music.BloodMoonNow)
        {
            if (music.Silenced) return T.Get("bmmStHordeMusicOff", "HORDE NIGHT, MUSIC OFF");
            return playing ? T.Get("bmmStHordeActive", "HORDE NIGHT ACTIVE") : T.Get("bmmStHorde", "HORDE NIGHT");
        }
        return playing ? T.Get("bmmStPlaying", "PLAYING") : T.Get("bmmStWaiting", "WAITING FOR THE BLOOD MOON");
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
