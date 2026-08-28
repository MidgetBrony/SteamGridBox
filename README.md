# SteamGridBox

Adds a native-looking **SteamGrid** artwork button below **Change Art** and **Add Screenshots** while inspecting a BOXROOM game box. The button opens a controller-friendly ModsPanel picker for SteamGridDB 600x900 covers. Choosing a result opens a full preview with **Apply Cover** and **Cancel**; BOXROOM's normal `steam_cache_v2` is changed only after confirmation.

## Install

1. Install ModsPanel 2.4.2 or newer.
2. Copy `SteamGridBox.dll` to BOXROOM's `Mods` folder.
3. Open **Settings > Mod Settings > SteamGridBox**.
4. Paste a SteamGridDB API key into **SteamGridDB API key**.
5. Inspect a game box and choose **SteamGrid**.

The API key is stored locally at `UserData/SteamGridBox/config.json`. It is sent only to SteamGridDB as the API authorization header. SteamGridDB keys are available from https://www.steamgriddb.com/profile/preferences/api.

Cover lookup first uses the positive Steam App ID. If SteamGridDB returns no usable grids for that ID, SteamGridBox searches by the BOXROOM game name, prefers an exact case-insensitive title match, falls back to SteamGridDB's first search result, and then loads that SteamGridDB game's covers. Name-fallback results are identified in the picker.

The Mod Settings section also has **Grab All Art**. It processes every currently loaded game with a positive Steam App ID in sequence and selects the first ranked 600x900 static result. **Skip games that already have cover art** is enabled by default and checks both the game's loaded state and its cached `boxart.jpg`; turn it off to overwrite covers. Progress and the final applied/skipped/failed counts appear as notifications. If SteamGridDB returns HTTP 429, the run honors `Retry-After` when supplied (or waits 60 seconds), then continues automatically.

## Build

Copy `Directory.Build.user.props.example` to `Directory.Build.user.props`, update the paths, then run `dotnet build -c Release`. The user props file is ignored so machine-specific paths are not committed.

Build validation does not confirm the button's final in-game position, controller focus, SteamGridDB network access, or cover refresh during live gameplay.
