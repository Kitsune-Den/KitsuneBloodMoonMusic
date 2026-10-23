/// <summary>Player-facing text from Config/Localization.csv (V3.x) or
/// Localization.txt (V2.x), with the English as a fallback if a key didn't load.</summary>
public static class BloodMoonMusicText
{
    public static string Get(string key, string english) =>
        Localization.Exists(key) ? Localization.Get(key, false) : english;

    public static string Format(string key, string english, params object[] args) =>
        string.Format(Get(key, english), args);
}
