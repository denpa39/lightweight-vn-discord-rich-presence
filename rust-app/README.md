# VN Presence — Rust build

The Rust port uses native Windows controls, WinHTTP for requests, and Windows image codecs. It runs as one EXE with no .NET installation or browser engine.

The UI now follows the Spotifast reference: a dark library sidebar, a larger cover and game view in the center, and a separate VNDB panel. Larger text, lavender highlights and visible action buttons carry through the dialogs. Builds replace the same `dist\VN Presence.exe`.

The previous C# source and releases remain available. Rust reads the existing library on its first launch and saves changes to `%LOCALAPPDATA%\VnPresence\settings-rust.json`. It never overwrites the C# settings file. Windows-protected tokens and cover ownership work on the same Windows account.

## Try it

Quit the C# app from the tray, then open `dist\VN Presence.exe`. Labels, votes, notes, and uploads use your real VNDB account and cover service when you use those controls.

For a preview with account editing, uploads, deletion, and Discord sharing disabled:

```powershell
& '.\dist\VN Presence.exe' --preview
```

## Ported features

- Game detection, automatic Discord reconnect, elapsed time, pause, tray, and Windows startup.
- VNDB search, game linking, sorting, details cleanup, Japanese text, and import/export.
- Account connection, profile button, labels, decimal votes, and notes with automatic saving.
- Default, release, custom, and community covers; fit, crop, blur, PNG saving, uploads, and removal rights.
- The built-in guide.

The hosted image service stays the same. Original uploader ownership and duplicate-image reuse are preserved.

## Build and checks

Use the `x86_64-pc-windows-gnu` Rust toolchain and MinGW (`gcc` and `windres` on PATH). The build script also finds the project-local Rust toolchain if present.

```powershell
.\rust-app\build.ps1 -Test
& '.\dist\VN Presence.exe' --ui-test '.\rust-app\target\ui-check'
```

Checks cover settings and token protection, title matching, Discord framing and ping responses, VNDB updates against a local fake server, notes saving and failed-save recovery, and fit/crop transparency. The app's UI check captures its own windows without changing real settings or accounts.

The first idle preview measurement with the existing library was about **24 MiB working memory**, **6 MiB private memory**, and **0.01% CPU**. This predates the UI redesign and is not a guarantee for connected sessions or open image editors. The EXE is about **1.5 MiB**.
