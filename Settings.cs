using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using UnityEngine;

/// <summary>The player's Blood Moon Music settings, saved per player in the game's
/// user data folder (next to Saves and Mods) as plain key=value lines.</summary>
public static class BloodMoonMusicSettings
{
    public static readonly float[] FadeSteps = { 0f, 2f, 4f, 6f, 8f, 10f, 15f, 20f, 30f };

    /// <summary>0 to 1.</summary>
    public static float Volume = 0.6f;
    public static float FadeSeconds = 6f;
    /// <summary>Turn the game's own music down while ours plays.</summary>
    public static bool MuteGameMusic = true;

    public static string FilePath =>
        Path.Combine(GameIO.GetDefaultUserGameDataPath(), "KitsuneBloodMoonMusic", "settings.txt");

    public static void Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return;
            var values = new Dictionary<string, string>();
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int eq = line.IndexOf('=');
                if (eq > 0) values[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            if (values.TryGetValue("volume", out var v) && float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var vol))
                Volume = Mathf.Clamp01(vol);
            if (values.TryGetValue("fade", out var f) && float.TryParse(f, NumberStyles.Float, CultureInfo.InvariantCulture, out var fade))
                FadeSeconds = Mathf.Clamp(fade, 0f, 60f);
            if (values.TryGetValue("muteGameMusic", out var m) && bool.TryParse(m, out var mute))
                MuteGameMusic = mute;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BloodMoonMusic] couldn't read " + FilePath + ": " + e.Message);
        }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            File.WriteAllLines(FilePath, new[]
            {
                "volume=" + Volume.ToString("0.##", CultureInfo.InvariantCulture),
                "fade=" + FadeSeconds.ToString("0.##", CultureInfo.InvariantCulture),
                "muteGameMusic=" + (MuteGameMusic ? "true" : "false"),
            });
        }
        catch (Exception e)
        {
            Debug.LogWarning("[BloodMoonMusic] couldn't save " + FilePath + ": " + e.Message);
        }
    }
}
