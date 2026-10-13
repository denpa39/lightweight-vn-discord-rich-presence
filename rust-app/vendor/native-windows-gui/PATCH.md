# Local patch

Upstream native-windows-gui 1.0.13, with its MIT license retained.
Only `src/win32/high_dpi.rs` is changed: release the screen device context after reading DPI, and return 96 if acquiring it fails.
Repeated layout previously leaked Windows GDI handles. The app's `--ui-test` checks the handle count over 40 layouts.
