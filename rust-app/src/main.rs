#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]
mod core;
mod images;
mod net;
mod theme;
mod ui;
struct Instance(winapi::shared::ntdef::HANDLE);
impl Drop for Instance {
    fn drop(&mut self) {
        unsafe {
            winapi::um::handleapi::CloseHandle(self.0);
        }
    }
}
fn main() {
    let args: Vec<String> = std::env::args().collect();
    let readonly = args.iter().any(|s| s == "--preview");
    let override_path = args
        .windows(2)
        .find(|a| a[0] == "--settings")
        .map(|a| std::path::PathBuf::from(&a[1]));
    let path = override_path
        .clone()
        .unwrap_or_else(|| core::Settings::directory().join("settings-rust.json"));
    let source = if path.exists() || override_path.is_some() {
        path.clone()
    } else {
        core::Settings::directory().join("settings.json")
    };
    if let Some(test) = args.windows(2).find(|a| a[0] == "--ui-test") {
        let result = core::Settings::load(&source)
            .and_then(|s| ui::self_test(s, std::path::PathBuf::from(&test[1])));
        if let Err(e) = result {
            let _ = std::fs::write(
                std::path::PathBuf::from(&test[1]).join("result.txt"),
                format!("FAIL: {e}"),
            );
            std::process::exit(1);
        }
        return;
    }
    let mutex = unsafe {
        winapi::um::synchapi::CreateMutexW(
            std::ptr::null_mut(),
            0,
            core::wide("VnPresence.Desktop.Rust").as_ptr(),
        )
    };
    if mutex.is_null() {
        native_windows_gui::error_message("VN Presence", &core::last_error());
        return;
    }
    let exists = unsafe { winapi::um::errhandlingapi::GetLastError() } == 183;
    let _instance = Instance(mutex);
    if !readonly {
        let old = unsafe {
            winapi::um::synchapi::OpenMutexW(
                0x100000,
                0,
                core::wide("VnPresence.Desktop.Singleton").as_ptr(),
            )
        };
        if !old.is_null() {
            unsafe {
                winapi::um::handleapi::CloseHandle(old);
            }
            native_windows_gui::error_message(
                "VN Presence",
                "Quit the current VN Presence app from the tray before starting the Rust version.",
            );
            return;
        }
    }
    if exists {
        unsafe {
            let w = [
                "VN Presence",
                "VN Presence — Rust development",
                "VN Presence — Rust preview",
            ]
            .iter()
            .find_map(|title| {
                let w =
                    winapi::um::winuser::FindWindowW(std::ptr::null(), core::wide(title).as_ptr());
                (!w.is_null()).then_some(w)
            });
            if let Some(w) = w {
                winapi::um::winuser::ShowWindow(w, 9);
                winapi::um::winuser::SetForegroundWindow(w);
            }
        }
        return;
    }
    if let Err(e) = core::Settings::load(&source)
        .and_then(|s| ui::run(s, path, readonly, args.iter().any(|s| s == "--background")))
    {
        native_windows_gui::error_message("VN Presence", &e);
    }
}
