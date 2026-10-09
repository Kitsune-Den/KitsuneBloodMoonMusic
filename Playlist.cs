using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

// A playlist file in Music/ picks which of your tracks play, and in what
// order. The songs are always your own files: a playlist only names them.
//
//   .csv   Exportify (or TuneMyMusic, Soundiiz): a "Track Name" / "Artist"
//          column each
//   .json  Spotify's "Download your data" (Playlist1.json): the playlist
//          named like "Blood Moon" or "Horde", else the first one
//   .m3u   file names or paths, one a line (#EXTINF titles help too)
//
// Each entry is matched to a file by name: "Iron Nine - Remainder.mp3",
// "3_Remainder.ogg" and "remainder.wav" all match Remainder by Iron Nine.
public static class Playlist
{
    public static readonly string[] Extensions = { ".csv", ".json", ".m3u", ".m3u8" };

    /// <summary>One song the playlist asks for.</summary>
    public class Entry
    {
        public string Artist = "";
        public string Title = "";
        /// <summary>A file name, from an M3U line.</summary>
        public string File = "";

        public override string ToString() =>
            File != "" ? File : (Artist != "" ? Artist + " - " + Title : Title);
    }

    /// <summary>The playlist file to use: one named "playlist", else the first
    /// by name; null for none.</summary>
    public static string Find(string dir)
    {
        var files = Directory.GetFiles(dir)
            .Where(f => Extensions.Contains(Path.GetExtension(f).ToLowerInvariant()))
            .OrderBy(f => Path.GetFileName(f), NaturalOrder.Instance)
            .ToList();
        return files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals("playlist", StringComparison.OrdinalIgnoreCase))
            ?? files.FirstOrDefault();
    }

    public static List<Entry> Read(string file)
    {
        var text = File.ReadAllText(file, Encoding.UTF8);
        switch (Path.GetExtension(file).ToLowerInvariant())
        {
            case ".csv": return ReadCsv(text);
            case ".json": return ReadSpotifyJson(text);
            default: return ReadM3u(text);
        }
    }

    // -- formats ---------------------------------------------------------------

    public static List<Entry> ReadCsv(string text)
    {
        var rows = CsvRows(text);
        var entries = new List<Entry>();
        if (rows.Count == 0) return entries;
        var head = rows[0].Select(h => h.Trim().ToLowerInvariant()).ToList();
        int title = head.FindIndex(h => h == "track name" || h == "title" || h == "track" || h == "name" || h == "song");
        if (title < 0) title = head.FindIndex(h => h.Contains("track") && h.Contains("name"));
        int artist = head.FindIndex(h => h.StartsWith("artist"));
        if (title < 0) return entries;
        foreach (var row in rows.Skip(1))
        {
            if (title >= row.Count || row[title].Trim() == "") continue;
            // Exportify lists several artists comma-separated: the first is enough.
            var a = artist >= 0 && artist < row.Count ? row[artist].Split(',')[0].Trim() : "";
            entries.Add(new Entry { Artist = a, Title = row[title].Trim() });
        }
        return entries;
    }

    public static List<Entry> ReadSpotifyJson(string text)
    {
        var entries = new List<Entry>();
        var root = JToken.Parse(text);
        var playlists = (root["playlists"] as JArray)?.OfType<JObject>().ToList() ?? new List<JObject>();
        if (playlists.Count == 0) return entries;
        var chosen = playlists.FirstOrDefault(p =>
        {
            var name = ((string)p["name"] ?? "").ToLowerInvariant();
            return name.Contains("blood moon") || name.Contains("bloodmoon") || name.Contains("horde");
        }) ?? playlists[0];
        foreach (var item in (chosen["items"] as JArray)?.OfType<JObject>() ?? Enumerable.Empty<JObject>())
        {
            var track = item["track"] as JObject;
            var t = (string)track?["trackName"];
            if (string.IsNullOrWhiteSpace(t)) continue;
            entries.Add(new Entry { Artist = ((string)track["artistName"] ?? "").Trim(), Title = t.Trim() });
        }
        return entries;
    }

    public static List<Entry> ReadM3u(string text)
    {
        var entries = new List<Entry>();
        string extinf = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimStart('\uFEFF');
            if (line == "") continue;
            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',');
                extinf = comma >= 0 ? line.Substring(comma + 1).Trim() : null;
                continue;
            }
            if (line.StartsWith("#")) continue;
            // Control characters can't be in a file name; a messy line still matches.
            var clean = new string(line.Where(c => !char.IsControl(c)).ToArray());
            var entry = new Entry { File = clean.Replace('\\', '/').Split('/').Last() };
            if (!string.IsNullOrEmpty(extinf)) SplitArtistTitle(extinf, entry);
            entries.Add(entry);
            extinf = null;
        }
        return entries;
    }

    // -- matching --------------------------------------------------------------

    /// <summary>The audio files in playlist order. Entries with no file are
    /// listed in `missing`; files the playlist doesn't name don't play.</summary>
    public static List<string> Order(List<Entry> entries, IList<string> files, List<string> missing)
    {
        var left = new List<string>(files);
        var ordered = new List<string>();
        foreach (var e in entries)
        {
            var match = Match(e, left);
            if (match == null)
            {
                missing.Add(e.ToString());
                continue;
            }
            ordered.Add(match);
            left.Remove(match);
        }
        return ordered;
    }

    private static string Match(Entry e, List<string> files)
    {
        if (e.File != "")
        {
            var byName = files.FirstOrDefault(f => Path.GetFileName(f).Equals(e.File, StringComparison.OrdinalIgnoreCase));
            if (byName != null) return byName;
            var stem = Key(Stem(e.File));
            var byStem = files.FirstOrDefault(f => Key(Path.GetFileNameWithoutExtension(f)) == stem);
            if (byStem != null) return byStem;
            if (e.Title == "") return null;
        }
        var keys = files.Select(f => new { File = f, Key = Key(StripNumber(Path.GetFileNameWithoutExtension(f))) }).ToList();
        // The title as given, then without what Spotify adds after it:
        // "Remainder - Radio Edit", "Song (feat. X)", "Song - Remastered 2011".
        foreach (var t in new[] { e.Title, BaseTitle(e.Title) }.Distinct())
        {
            var title = Key(t);
            if (title == "") continue;
            var both = Key(e.Artist + " " + t);
            var found = keys.FirstOrDefault(k => k.Key == both)?.File
                ?? keys.FirstOrDefault(k => k.Key == title)?.File
                // The title inside a longer name: "Iron Nine - Remainder (live)".
                ?? (title.Length >= 4 ? keys.FirstOrDefault(k => (" " + k.Key + " ").Contains(" " + title + " "))?.File : null);
            if (found != null) return found;
        }
        return null;
    }

    /// <summary>A file name without its extension, without Path's checks
    /// (a playlist line may hold anything).</summary>
    private static string Stem(string name)
    {
        var dot = name.LastIndexOf('.');
        return dot > 0 ? name.Substring(0, dot) : name;
    }

    /// <summary>A title without its " - Radio Edit" or "(feat. ...)" part.</summary>
    private static string BaseTitle(string title)
    {
        var cut = title.Length;
        foreach (var mark in new[] { " - ", " (", " [" })
        {
            var i = title.IndexOf(mark, StringComparison.Ordinal);
            if (i > 0 && i < cut) cut = i;
        }
        return title.Substring(0, cut).Trim();
    }

    private static void SplitArtistTitle(string s, Entry e)
    {
        var dash = s.IndexOf(" - ", StringComparison.Ordinal);
        if (dash > 0)
        {
            e.Artist = s.Substring(0, dash).Trim();
            e.Title = s.Substring(dash + 3).Trim();
        }
        else e.Title = s.Trim();
    }

    /// <summary>"3_Song" and "03 - Song": the track number in front, gone.</summary>
    private static string StripNumber(string name)
    {
        int i = 0;
        while (i < name.Length && char.IsDigit(name[i])) i++;
        if (i == 0 || i == name.Length) return name;
        return name.Substring(i).TrimStart(' ', '_', '-', '.');
    }

    /// <summary>Lowercase letters and digits, words split by single spaces:
    /// "Iron_Nine - Remainder!" is "iron nine remainder".</summary>
    public static string Key(string s)
    {
        var sb = new StringBuilder();
        foreach (var c in s.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c)) sb.Append(c);
            else if (c == '&') sb.Append(" and ");
            else if (c != '\'' && c != '\u2019') sb.Append(' ');
        }
        return string.Join(" ", sb.ToString().Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Rows of a CSV file, quoted fields and all.</summary>
    private static List<List<string>> CsvRows(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;
        text = text.TrimStart('\uFEFF');
        for (int i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else field.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { row.Add(field.ToString()); field.Clear(); }
            else if (c == '\n' || c == '\r')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                row.Add(field.ToString());
                field.Clear();
                if (row.Any(f => f != "")) rows.Add(row);
                row = new List<string>();
            }
            else field.Append(c);
        }
        row.Add(field.ToString());
        if (row.Any(f => f != "")) rows.Add(row);
        return rows;
    }
}
