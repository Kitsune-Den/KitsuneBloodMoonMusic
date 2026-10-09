# Kitsune Blood Moon Music

A client-side [7 Days to Die](https://7daystodie.com/) mod (V3.0 "Dead Hot Summer") that plays **your own music** during the Blood Moon horde night and stops at dawn.

When the Blood Moon begins, your tracks play in order at a comfortable volume. When one finishes, the next begins; the mod loops back to the top if the night runs long. As soon as the horde ends, the music stops and normal game audio returns.

## Install

1. Drop the `KitsuneBloodMoonMusic` folder into your game's `Mods` directory:
   - `…/steamapps/common/7 Days To Die/Mods/KitsuneBloodMoonMusic`
2. Make sure the Harmony loader (`0_TFP_Harmony`, ships with the game) is present in `Mods`.
3. Launch **without EasyAntiCheat** ("Play Not EAC") — DLL mods do not load with EAC enabled.

This is **client-side only**. It affects your own game audio, so each player installs it locally; it does not need to go on the dedicated server.

## Your own music

Tracks live in `KitsuneBloodMoonMusic/Music/` as **`.ogg`**, **`.mp3`** or **`.wav`** files and play in filename order — prefix with numbers (`1_`, `2_`, … `10_`) to set the order. Numbers sort as numbers, so `2_` comes before `10_`. Other files in the folder (playlists, cover art) are ignored.

### Your playlist, your files

To pick which tracks play and in what order, put a playlist file in `Music/` next to them. The songs still come from your own files: the playlist only names them.

- **Spotify:** export the playlist with Exportify and drop the `.csv` in. Or use Spotify's *Download your data* (Account → Privacy) and drop in `Playlist1.json`: the playlist named like "Blood Moon" or "Horde" plays, else the first one.
- **Other apps:** a CSV with a track name (or title) column and an artist column (TuneMyMusic, Soundiiz), or an `.m3u` / `.m3u8` list of file names.

Each song is matched to a file by name, so `Iron Nine - Remainder.mp3`, `3_Remainder.ogg` and `remainder.wav` all match "Remainder" by Iron Nine, as does "Remainder - Radio Edit". Songs in the playlist you don't have a file for are listed in the game's log and skipped; files the playlist doesn't name don't play. With several playlist files, the one named `playlist` wins, else the first by name. No playlist file: every track plays, in file-name order.

Anything else converts to `.ogg` with ffmpeg:

```
ffmpeg -i "your track.flac" -vn -c:a libvorbis -q:a 5 "1_your_track.ogg"
```

Swap in whatever you like — remove the bundled tracks and drop in your own.

## How it works

An `IModApi` mod that polls `GameManager.Instance.World.aiDirector.BloodMoonComponent.BloodMoonActive` once per second, driving a `DontDestroyOnLoad` `AudioSource`. Clips are loaded at startup, sorted by name, via `UnityWebRequestMultimedia.GetAudioClip`.

## Build

Requires the .NET SDK. The project references the game's own `Managed` assemblies (it targets the Unity/Mono **net48** profile — `Assembly-CSharp` has an indirect .NET 4.8 dependency, so `IModApi`/`Mod` only import cleanly at net48).

```
dotnet build -c Release
# or point at your install:
dotnet build -c Release -p:GameManaged="D:/path/to/7DaysToDie_Data/Managed"
```

The built `KitsuneBloodMoonMusic.dll` goes in the `KitsuneBloodMoonMusic/` mod folder next to `ModInfo.xml`.

## Credits

Bundled music is original work by **AdaInTheLab** (Iron Nine / Kitsune Den). Mod by AdaInTheLab. MIT licensed (see `LICENSE`); the music is the author's own and shared with the mod.
