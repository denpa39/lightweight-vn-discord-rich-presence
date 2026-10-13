use crate::core::wide;
use native_windows_gui as nwg;
use winapi::{
    shared::windef::HBRUSH,
    um::{wingdi::*, winuser::*},
};

#[link(name = "uxtheme")]
extern "system" {
    fn SetWindowTheme(hwnd: winapi::shared::windef::HWND, app: *const u16, id: *const u16) -> i32;
}

pub fn style_control(handle: &nwg::ControlHandle) {
    unsafe {
        SetWindowTheme(handle.hwnd().unwrap(), wide("").as_ptr(), wide("").as_ptr());
    }
}

pub fn combo(handle: &nwg::ControlHandle) -> nwg::RawEventHandler {
    nwg::bind_raw_event_handler(handle, 0x10004, |hwnd, msg, w, _| unsafe {
        if !matches!(msg, WM_PAINT | WM_PRINT | WM_PRINTCLIENT) {
            return None;
        }
        let mut paint = std::mem::zeroed();
        let dc = if msg == WM_PAINT {
            BeginPaint(hwnd, &mut paint)
        } else {
            w as _
        };
        let mut rect = std::mem::zeroed();
        GetClientRect(hwnd, &mut rect);
        let brush = GetStockObject(DC_BRUSH as i32) as HBRUSH;
        SetDCBrushColor(dc, 0x2f1d20);
        FillRect(dc, &rect, brush);
        let old = SelectObject(dc, SendMessageW(hwnd, WM_GETFONT, 0, 0) as _);
        SetTextColor(dc, 0xf1dfdf);
        SetBkMode(dc, TRANSPARENT as i32);
        let index = SendMessageW(hwnd, CB_GETCURSEL, 0, 0);
        let len = SendMessageW(hwnd, CB_GETLBTEXTLEN, index as _, 0);
        let mut text = [0u16; 2048];
        if index >= 0 && len >= 0 && len < text.len() as isize {
            SendMessageW(hwnd, CB_GETLBTEXT, index as _, text.as_mut_ptr() as _);
            rect.left += 8;
            rect.right -= 24;
            DrawTextW(
                dc,
                text.as_ptr(),
                len as i32,
                &mut rect,
                DT_SINGLELINE | DT_VCENTER | DT_END_ELLIPSIS | DT_NOPREFIX,
            );
        }
        GetClientRect(hwnd, &mut rect);
        rect.left = rect.right - 24;
        let arrow = wide("▾");
        DrawTextW(
            dc,
            arrow.as_ptr(),
            1,
            &mut rect,
            DT_SINGLELINE | DT_VCENTER | DT_CENTER,
        );
        SelectObject(dc, old);
        if GetFocus() == hwnd {
            GetClientRect(hwnd, &mut rect);
            InflateRect(&mut rect, -2, -2);
            DrawFocusRect(dc, &rect);
        }
        if msg == WM_PAINT {
            EndPaint(hwnd, &paint);
        }
        Some(0)
    })
    .unwrap()
}

pub struct Theme {
    handler: nwg::RawEventHandler,
    background: HBRUSH,
    surface: HBRUSH,
    accent: HBRUSH,
    button: HBRUSH,
}
impl Theme {
    pub fn new(window: &nwg::Window, columns: bool) -> Self {
        unsafe {
            let background = CreateSolidBrush(0x241618);
            let surface = CreateSolidBrush(0x2f1d20);
            let accent = CreateSolidBrush(0xe6a4bf);
            let button = CreateSolidBrush(0x493635);
            unsafe extern "system" fn style(child: winapi::shared::windef::HWND, _: isize) -> i32 {
                let mut class = [0u16; 32];
                let count = GetClassNameW(child, class.as_mut_ptr(), 32);
                let class = String::from_utf16_lossy(&class[..count as usize]);
                SetWindowTheme(child, wide("").as_ptr(), wide("").as_ptr());
                if class == "Button" {
                    let flags = GetWindowLongW(child, GWL_STYLE) as u32;
                    if flags & BS_TYPEMASK == BS_PUSHBUTTON {
                        SetWindowLongW(
                            child,
                            GWL_STYLE,
                            ((flags & !BS_TYPEMASK) | BS_OWNERDRAW) as i32,
                        );
                    }
                }
                1
            }
            EnumChildWindows(window.handle.hwnd().unwrap(), Some(style), 0);
            let handler =
                nwg::bind_raw_event_handler(&window.handle, 0x10003, move |hwnd, msg, w, l| {
                    match msg {
                        WM_PAINT | WM_ERASEBKGND => {
                            let mut paint = std::mem::zeroed();
                            let dc = if msg == WM_PAINT {
                                BeginPaint(hwnd, &mut paint)
                            } else {
                                w as _
                            };
                            let mut rect = std::mem::zeroed();
                            GetClientRect(hwnd, &mut rect);
                            FillRect(dc, &rect, background);
                            if columns {
                                let scale = nwg::scale_factor();
                                let mut side = rect;
                                side.right = (300. * scale) as i32;
                                FillRect(dc, &side, surface);
                                side.left = rect.right - (360. * scale) as i32;
                                side.right = rect.right;
                                FillRect(dc, &side, surface);
                            }
                            if msg == WM_PAINT {
                                EndPaint(hwnd, &paint);
                            }
                            Some(1)
                        }
                        WM_CTLCOLORSTATIC | WM_CTLCOLOREDIT | WM_CTLCOLORLISTBOX
                        | WM_CTLCOLORBTN => {
                            SetTextColor(w as _, 0xf1dfdf);
                            SetBkColor(w as _, 0x2f1d20);
                            Some(surface as isize)
                        }
                        WM_DRAWITEM if l != 0 => {
                            let item = &*(l as *const DRAWITEMSTRUCT);
                            if item.CtlType != ODT_BUTTON
                                && item.CtlType != ODT_LISTBOX
                                && item.CtlType != ODT_COMBOBOX
                            {
                                return None;
                            }
                            let mut text = [0u16; 2048];
                            let len = if item.CtlType == ODT_LISTBOX || item.CtlType == ODT_COMBOBOX
                            {
                                let combo = item.CtlType == ODT_COMBOBOX;
                                let index = if combo && item.itemID == u32::MAX {
                                    SendMessageW(item.hwndItem, CB_GETCURSEL, 0, 0) as usize
                                } else {
                                    item.itemID as usize
                                };
                                if index == usize::MAX || index == u32::MAX as usize {
                                    return Some(1);
                                }
                                let len = SendMessageW(
                                    item.hwndItem,
                                    if combo {
                                        CB_GETLBTEXTLEN
                                    } else {
                                        LB_GETTEXTLEN
                                    },
                                    index,
                                    0,
                                );
                                if len < 0 || len >= text.len() as isize {
                                    return Some(1);
                                }
                                SendMessageW(
                                    item.hwndItem,
                                    if combo { CB_GETLBTEXT } else { LB_GETTEXT },
                                    index,
                                    text.as_mut_ptr() as _,
                                ) as i32
                            } else {
                                GetWindowTextW(item.hwndItem, text.as_mut_ptr(), text.len() as i32)
                            };
                            let caption = String::from_utf16_lossy(&text[..len as usize]);
                            let primary = matches!(
                                caption.as_str(),
                                "Add"
                                    | "Save"
                                    | "Save and close"
                                    | "Link"
                                    | "Connect"
                                    | "Upload and use"
                            );
                            let highlighted = primary || item.itemState & ODS_SELECTED != 0;
                            FillRect(
                                item.hDC,
                                &item.rcItem,
                                if highlighted {
                                    accent
                                } else if item.CtlType == ODT_BUTTON {
                                    button
                                } else {
                                    surface
                                },
                            );
                            let old = SelectObject(
                                item.hDC,
                                SendMessageW(item.hwndItem, WM_GETFONT, 0, 0) as _,
                            );
                            SetBkMode(item.hDC, TRANSPARENT as i32);
                            SetTextColor(
                                item.hDC,
                                if item.itemState & ODS_DISABLED != 0 {
                                    0x998b8e
                                } else if highlighted {
                                    0x241618
                                } else {
                                    0xf1dfdf
                                },
                            );
                            let mut rect = item.rcItem;
                            rect.left += 8;
                            rect.right -= 8;
                            DrawTextW(
                                item.hDC,
                                text.as_ptr(),
                                len,
                                &mut rect,
                                DT_SINGLELINE
                                    | if item.CtlType != ODT_BUTTON {
                                        DT_LEFT
                                    } else {
                                        DT_CENTER
                                    }
                                    | DT_VCENTER
                                    | DT_END_ELLIPSIS
                                    | DT_NOPREFIX,
                            );
                            if item.itemState & ODS_FOCUS != 0 {
                                InflateRect(&mut rect, -2, -4);
                                DrawFocusRect(item.hDC, &rect);
                            }
                            SelectObject(item.hDC, old);
                            Some(1)
                        }
                        _ => None,
                    }
                })
                .unwrap();
            Self {
                handler,
                background,
                surface,
                accent,
                button,
            }
        }
    }
}
impl Drop for Theme {
    fn drop(&mut self) {
        nwg::unbind_raw_event_handler(&self.handler).ok();
        unsafe {
            DeleteObject(self.background as _);
            DeleteObject(self.surface as _);
            DeleteObject(self.accent as _);
            DeleteObject(self.button as _);
        }
    }
}
