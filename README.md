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

Tracks live in `KitsuneBloodMoonMusic/Music/` as **Ogg Vorbis** (`.ogg`) files and play in filename order — prefix with numbers (`1_`, `2_`, …) to set the order.

Convert any audio to `.ogg` with ffmpeg:

```
ffmpeg -i "your track.mp3" -vn -c:a libvorbis -q:a 5 "1_your_track.ogg"
```

Swap in whatever you like — remove the bundled tracks and drop in your own.

## How it works

An `IModApi` mod that polls `GameManager.Instance.World.aiDirector.BloodMoonComponent.BloodMoonActive` once per second, driving a `DontDestroyOnLoad` `AudioSource`. Clips are loaded at startup via `UnityWebRequestMultimedia.GetAudioClip`.

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
