# VN Presence

A small Windows app that shares your visual novel on Discord, with its native title, brand, cover, and game details from the window title.

## Install

Download **VN.Presence.exe** from [Releases](https://github.com/denpa39/vndb-discord-rich-precense/releases/latest) and open it. No installer or extraction needed.

- Windows 10 or 11, 64-bit.
- [.NET 8 Desktop Runtime for Windows x64](https://dotnet.microsoft.com/en-us/download/dotnet/8.0). Older .NET Framework 4.x does not replace it. If the runtime is missing, Windows prompts you to download it; install it and reopen the app.
- Discord desktop running, with **Settings → Activity Privacy → Share your detected activities** enabled.

No Discord token, Developer Portal setup, or Cloudflare account is needed.

## Add and manage games

1. Start your game and click **Add...**.
2. Select its window, or use **Browse...** to choose its executable.
3. Check the search title, or paste the game's VNDB URL. Click **Search**.
4. Select the matching result and click **Link**.

Recognized version, age, and progress suffixes are removed from the search title automatically; you can edit it before searching. Already linked games and executables cannot be added twice.

The dropdown above the game list offers **Added**, **Alphabetical**, and **Last played**. Your choice is saved. Last played tracking starts with this version; games without a recorded session appear below played games.

**Remove** asks for confirmation. **More... → Change executable...** updates a moved game's path without losing its customizations.

## Details and Discord preview

Select a game → **Details...**:

- **Automatic** extracts details from its window title.
- **Custom** shows your text. Leave it blank to hide the details line.
- **Remove text** deletes phrases separated with `;`, such as `Ver1.0.0; R18`.
- **Trim from edges** removes surrounding characters only: `-` preserves internal hyphens, and `-()` also removes outer parentheses.
- **Window title prefix** handles a game name that differs from its VNDB title.

Copy from **Current window title**, check **Result**, then **Save**. Spaces and punctuation stay unless your rules remove them; outer whitespace is trimmed.

The preview on the right uses the same activity data as Discord. It shows the game, brand, cover, details, elapsed time, and buttons. Its height stays fixed when the profile button is toggled. Discord desktop and mobile may arrange the activity differently.

**View on VNDB** opens the game page. The cover also links to the game, and the brand links to its producer page; multiple producers link to the game page. Chapter changes keep the play timer running.

## Covers, crop, and blur

Open **Cover...** to choose the main VNDB cover, a release cover, or a **Community cover** uploaded for that game. Community covers reuse the existing image without another upload. **Custom image...** accepts a direct public image URL. **Default** restores the main VNDB cover.

Choose a cover → **Adjust...**:

- **Fit whole cover** keeps everything with transparent padding.
- **Crop** keeps the white square. Drag or use arrow keys to move it; scroll or use **Zoom** to resize it.
- **Blur cover** softens artwork before it is saved or uploaded. This is optional, not automatic NSFW detection.
- **Reset** recenters the crop and resets zoom.

**Upload & use**, then **Save** in the Cover window, applies the hosted PNG. **Save image...** saves a PNG on your PC instead. The checkerboard is only a transparency preview.

Uploads are public. The finished image and public VNDB game ID are sent to the cover service; game paths, account tokens, and profile details are not sent. Matching image bytes share one stored file.

The shared service uses Cloudflare Workers/D1 Free. Images stay while storage has room. At capacity, it removes only enough of the least recently used covers to fit the upload, using recorded usage days to break ties. There is **no fixed expiry or daily deletion**. Active cover use renews its usage date. Daily service limits can still stop requests or uploads; deleted covers need uploading again. [Hosting details](cover-service/README.md).

## VNDB account, labels, and votes

Click **Connect VNDB** below the preview. Create a token at [VNDB Applications](https://vndb.org/u/tokens) with both permissions:

- **Access private items on my list**
- **Add/remove/edit items on my list**

Paste the token and connect. Your password stays on VNDB; Windows encrypts the saved token for your Windows user.

Select a game to load its labels and vote. **Changes save automatically**. Choosing Playing, Finished, Stalled, or Dropped clears the other progress labels. Pick a rating or type a decimal from **1.0 to 10.0** and press Enter. **Remove vote** clears an existing score.

**Connected as [username]** stays on its own line. Save progress and errors appear beneath it. Wait for **Saved to VNDB** before quitting. If an update fails, the status says **Not saved**; change the value again to retry. **Refresh** reloads website changes; **Disconnect** removes the saved token.

The **Show VNDB profile button** checkbox sits beside the connected username. It adds **Visit [username]'s VNDB profile** to your activity using the account's URL automatically. Uncheck it to hide the button while staying connected. Discord hides your own activity buttons from you; other people can see them.

## Tray, guide, and backups

**Minimize** or **X** hides the app to the tray silently. Double-click its tray icon to reopen it; right-click → **Exit** to quit. **Pause** stops sharing until **Resume**. Closing the game clears its activity.

**Start with Windows** opens the app in the tray when you sign in. Keep the executable in the same folder; uncheck it to disable.

A skippable guide opens on first launch. **More... → Guide** reopens it anytime.

Settings use one file across versions: `%LOCALAPPDATA%\VnPresence\settings.json`. **More... → Export settings... / Import settings...** transfers games, customizations, profile preferences, sorting, and recorded play dates. Import replaces them after confirmation. Exports include game paths and your profile, but exclude the VNDB token; connect again on another PC.

## Build

Requires the .NET 8 SDK on Windows. The app uses the installed Desktop Runtime; it does not bundle one.

```powershell
dotnet publish -c Release --source https://api.nuget.org/v3/index.json -o dist
```

For uploads and community covers, deploy [cover-service](cover-service/README.md) and build with `-p:CoverUploadEndpoint=https://YOUR-WORKER.workers.dev/upload`.
