# Rust feature audit

Checked on Windows, October 13, 2026. This is a development build, not a guarantee that every external-service scenario works.

| Feature | Check |
| --- | --- |
| Library, process detection, executable matching | Settings round trips, Unicode path matching, invalid paths, process scan, native list selection |
| Alphabetical / last played sorting | Sorting and selected-game preservation; editing retains play time |
| Add game / VNDB search | Native typing, Enter, successful Link, duplicate rejection, Cancel and Escape; stale search protection; live ID/title searches and local API checks |
| Details / Japanese text | Native Save, Cancel and Escape; long Japanese text, resizing; failed settings writes restore the previous game |
| Discord presence / buttons | Named-pipe protocol checks, activity construction, reconnect resend fix |
| VNDB token / profile | Account requests, token protection and native connection dialog; mock account only |
| Labels / votes | Exclusive progress labels, custom labels retained, rapid edits, decimal Enter submission, failed-save retry and vote removal |
| Notes | Save and close, edits during a save, failure and retry; mock account only |
| Default / custom / community covers | Live release-cover and gallery reads; separate gallery, custom clearing, deleted-cover references removed; native UI and mock HTTP |
| Fit / crop / zoom / blur | Image pixel checks, crop bounds, wheel, native arrow keys, Reset, Cancel and Escape; native previews and saved PNG |
| Upload / identical-image reuse / removal | Owner headers, content hash, duplicate URL reuse, unauthorized deletion and errors against local server |
| Hosting cleanup | All 16 backend tests passed: capacity cleanup, usage tracking, rollback, limits, ownership and gallery isolation |
| Guide / tray / settings | All guide pages, native Close/Escape and modal ownership checked; import/export serialization and protected tokens tested; test mode preserves saved settings |
| Discord preview | Dark native card, matching buttons, separate Playing/details text; native game/profile link clicks checked without opening browser tabs |
| Drawing resources | 40 repeated layouts: 34 GDI handles before and after with the three-column theme; no growth |

Five offline Rust tests, a separate live read-only service test, and the native UI audit passed. The live test reads VNDB titles/releases/images and the public cover gallery; it also checks saved-account authentication and entry reads when a saved Rust token exists. Real account writes, public uploads/deletes, actual Discord rendering, Windows sign-in startup and moving between monitors with different scaling still require live verification. This audit uses local servers for account and hosting mutations.

Follow-up: Adjust cover released mouse capture on every mouse-up, preventing normal button clicks. It now releases capture only while finishing a cover drag. Native Reset and Cancel click checks pass; Cancel hides Adjust cover and reenables the cover chooser. The earlier Reset check sent a command directly and missed this bug.

Parity follow-up: restored Escape across dialogs, Enter for Details/custom URL/token connection and vote choices, and keyboard crop movement without stealing arrow keys from other controls. Failed game updates and Add saves no longer leave an unsaved change applied. Button labels and guide instructions no longer have trailing dots.

UI redesign: dark native three-column layout, lavender library selection, larger cover and text, a wider VNDB panel and visible action buttons. Dialogs share the theme; dynamically created account checkboxes receive it too. Native UI checks pass after the redesign. Captures use WM_PRINT to inspect layout; they do not verify the physical display or Discord's rendering.

Run `build.ps1 -Test` for Rust checks, `node --test cover-service/worker.test.mjs` from the repository root for hosting checks, and the executable with `--ui-test <output-directory>` for native UI captures.

Run `cargo test --manifest-path rust-app/Cargo.toml public_service_reads -- --ignored` separately for live read-only service checks. It does not upload, delete, or edit your VNDB list.
