# VN Presence

Shows your VN's native title, brand, and cover on Discord. Cover and brand link to VNDB; multiple brands link to the game page. Extra text after the game name in its window title appears below the brand.

## Install

Download **VN-Presence-win-x64.zip** from [Releases](https://github.com/denpa39/vndb-discord-rich-precense/releases/latest), extract it, and run **VN Presence.exe**.

Windows x64. No installer, .NET download, token, or Developer Portal setup needed.

## Setup

1. Open Discord desktop and enable **Activity Privacy → Share your detected activities**.
2. Start your VN. In **Add game**, click its window, or **Browse...** to its executable.
3. Search its title or paste its VNDB URL. Select the result and click **Link**.

Select a saved game → **Details...** to use automatic window text, custom text, or hide it.
Automatic details can remove a fixed phrase using **Remove text**.
If the window name differs from VNDB, set **Window title prefix** to the game's name as shown in its title bar.
**Cover...** selects a VNDB release cover or **Custom image...** edits your own direct public image link. One custom image is saved per game; **Default** restores the main cover. Discord controls the square crop.

**Profile...** adds an optional **VNDB profile** activity button. Enter your VNDB profile URL or `u` followed by your user ID; leave it blank to hide the button. Settings stay in your Windows user's local app-data folder.

Link each game once. Minimize to the tray to keep sharing. Activity clears when the game closes. Closing the app stops it. There is no settings tab or Developer Portal setup.

Enable **Start with Windows** to launch in the tray when you sign in. Keep the executable in the same folder; uncheck it to disable.

## Build

Requires the .NET 8 SDK on Windows.

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --source https://api.nuget.org/v3/index.json -o dist
```
