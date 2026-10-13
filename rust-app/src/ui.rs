use crate::{
    core::*,
    net::{self, Account, Entry},
};
use native_windows_gui as nwg;
use serde_json::Value;
#[path = "ui_cover.rs"]
mod ui_cover;
use std::{
    cell::{Cell, RefCell},
    path::PathBuf,
    rc::Rc,
    sync::mpsc::{self, Receiver, Sender},
    thread,
    time::{Duration, Instant},
};
use ui_cover::Covers;
use winapi::um::{shellapi::ShellExecuteW, winuser::*};
thread_local! {
    static TEST_URLS: RefCell<Option<Vec<String>>> = const { RefCell::new(None) };
}
fn keep_open(data: &nwg::EventData) {
    if let nwg::EventData::OnWindowClose(d) = data {
        d.close(false);
    }
}
fn modal(window: &nwg::Window, owner: &nwg::Window) {
    unsafe {
        SetWindowLongPtrW(
            window.handle.hwnd().unwrap(),
            GWLP_HWNDPARENT,
            owner.handle.hwnd().unwrap() as isize,
        );
    }
    owner.set_enabled(false);
}
fn hide(window: &nwg::Window) {
    window.set_visible(false);
    unsafe {
        let owner = GetWindow(window.handle.hwnd().unwrap(), GW_OWNER);
        if !owner.is_null() {
            EnableWindow(owner, 1);
            SetForegroundWindow(owner);
        }
    }
}

fn image_pixels(logical: u32) -> u32 {
    (logical as f64 * nwg::scale_factor()).round().max(1.) as u32
}
fn button(
    w: impl Into<nwg::ControlHandle>,
    text: &str,
    pos: (i32, i32),
    size: (i32, i32),
) -> nwg::Button {
    let mut v = nwg::Button::default();
    nwg::Button::builder()
        .parent(w)
        .text(text)
        .position(pos)
        .size(size)
        .build(&mut v)
        .unwrap();
    v
}
fn label(w: &nwg::Window, text: &str, pos: (i32, i32), size: (i32, i32)) -> nwg::Label {
    let mut v = nwg::Label::default();
    nwg::Label::builder()
        .parent(w)
        .text(text)
        .background_color(Some([32, 29, 47]))
        .position(pos)
        .size(size)
        .build(&mut v)
        .unwrap();
    v
}
fn input(w: &nwg::Window, text: &str, pos: (i32, i32), size: (i32, i32)) -> nwg::TextInput {
    let mut v = nwg::TextInput::default();
    nwg::TextInput::builder()
        .parent(w)
        .text(text)
        .position(pos)
        .size(size)
        .build(&mut v)
        .unwrap();
    v
}
fn check(w: &nwg::Window, text: &str, pos: (i32, i32), checked: bool) -> nwg::CheckBox {
    let mut v = nwg::CheckBox::default();
    nwg::CheckBox::builder()
        .parent(w)
        .text(text)
        .position(pos)
        .size((250, 24))
        .build(&mut v)
        .unwrap();
    v.set_check_state(if checked {
        nwg::CheckBoxState::Checked
    } else {
        nwg::CheckBoxState::Unchecked
    });
    crate::theme::style_control(&v.handle);
    v
}
fn is_checked(v: &nwg::CheckBox) -> bool {
    v.check_state() == nwg::CheckBoxState::Checked
}
fn open_url(url: &str) {
    if TEST_URLS.with(|urls| {
        if let Some(urls) = urls.borrow_mut().as_mut() {
            urls.push(url.into());
            true
        } else {
            false
        }
    }) {
        return;
    }
    let url = wide(url);
    let verb = wide("open");
    unsafe {
        ShellExecuteW(
            std::ptr::null_mut(),
            verb.as_ptr(),
            url.as_ptr(),
            std::ptr::null(),
            std::ptr::null(),
            SW_SHOWNORMAL,
        );
    }
}
fn confirm(w: &nwg::Window, title: &str, text: &str) -> bool {
    (unsafe {
        MessageBoxW(
            w.handle.hwnd().unwrap(),
            wide(text).as_ptr(),
            wide(title).as_ptr(),
            MB_YESNO | MB_ICONQUESTION | MB_DEFBUTTON2,
        )
    }) == IDYES
}
fn error(w: &nwg::Window, text: &str) {
    nwg::modal_error_message(w, "VN Presence", text);
}
fn file_dialog(w: &nwg::Window, title: &str, filter: &str, save: bool) -> Option<PathBuf> {
    let mut d = nwg::FileDialog::default();
    nwg::FileDialog::builder()
        .title(title)
        .filters(filter)
        .action(if save {
            nwg::FileDialogAction::Save
        } else {
            nwg::FileDialogAction::Open
        })
        .build(&mut d)
        .ok()?;
    if !d.run(Some(w)) {
        return None;
    }
    d.get_selected_item().ok().map(PathBuf::from)
}

fn fit_horizontal(list: &nwg::ListBox<String>) {
    use winapi::um::wingdi::*;
    unsafe {
        let hwnd = list.handle.hwnd().unwrap();
        let dc = GetDC(hwnd);
        let previous = SelectObject(dc, SendMessageW(hwnd, WM_GETFONT, 0, 0) as _);
        let mut width = 0;
        for s in list.collection().iter() {
            let text = wide(s);
            let mut size = std::mem::zeroed();
            if GetTextExtentPoint32W(dc, text.as_ptr(), text.len() as i32 - 1, &mut size) != 0 {
                width = width.max(size.cx);
            }
        }
        SelectObject(dc, previous);
        ReleaseDC(hwnd, dc);
        SendMessageW(hwnd, LB_SETHORIZONTALEXTENT, (width + 24) as usize, 0);
    }
}
enum Command {
    State(Settings, bool, Option<String>),
    Exit,
}
enum Update {
    Poll(Option<(Running, String)>, String),
    Image(String, Result<Vec<u8>>),
    Connected(Result<Account>, bool),
    Entry(String, u64, Result<Entry>),
    Saved(String, Entry, Result<()>),
    Search(Instant, Result<Vec<Novel>>),
    Exited,
    TrimCover,
}
struct State {
    settings: Settings,
    path: PathBuf,
    selected: Option<usize>,
    order: Vec<usize>,
    active: Option<(Running, usize)>,
    account: Option<Account>,
    entry: Option<(String, Entry)>,
    saving: bool,
    queued: Option<(String, Entry)>,
    draft: Option<Entry>,
    entry_generation: u64,
    readonly: bool,
}
pub struct App {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    icon: nwg::Icon,
    tray: nwg::TrayNotification,
    menu: nwg::Menu,
    show: nwg::MenuItem,
    exit: nwg::MenuItem,
    library: nwg::ListBox<String>,
    sort: nwg::ComboBox<String>,
    sort_style: nwg::RawEventHandler,
    _heading: nwg::Label,
    _app_heading: nwg::Label,
    account_heading: nwg::Label,
    _heading_font: nwg::Font,
    status: nwg::Label,
    add: nwg::Button,
    details: nwg::Button,
    cover: nwg::Button,
    remove: nwg::Button,
    more: nwg::Menu,
    more_button: nwg::Button,
    change_exe: nwg::MenuItem,
    export: nwg::MenuItem,
    import: nwg::MenuItem,
    guide: nwg::MenuItem,
    pause: nwg::Button,
    startup: nwg::CheckBox,
    preview_heading: nwg::Label,
    preview_card: nwg::Frame,
    preview_style: nwg::RawEventHandler,
    preview_brush: winapi::shared::windef::HBRUSH,
    details_preview: nwg::Label,
    art: nwg::ImageFrame,
    title: nwg::Label,
    brand: nwg::Label,
    progress: nwg::Label,
    elapsed: nwg::Label,
    game_button: nwg::Button,
    profile_button: nwg::Button,
    connect: nwg::Button,
    connection: nwg::Label,
    profile: nwg::CheckBox,
    labels_heading: nwg::Label,
    labels: RefCell<Vec<nwg::CheckBox>>,
    labels_scroll: nwg::ScrollBar,
    vote: nwg::Button,
    notes: nwg::Button,
    vote_label: nwg::Label,
    save_status: nwg::Label,
    state: RefCell<State>,
    handler: RefCell<Option<nwg::EventHandler>>,
    notice: nwg::Notice,
    tx: Sender<Update>,
    rx: Receiver<Update>,
    worker: Sender<Command>,
    paused: Cell<bool>,
    loading: Cell<bool>,
    closing: Cell<bool>,
    bitmap: RefCell<Option<nwg::Bitmap>>,
    image_url: RefCell<String>,
    cache: RefCell<Vec<(String, Vec<u8>)>>,
    details_form: RefCell<Option<Rc<Details>>>,
    add_form: RefCell<Option<Rc<AddGame>>>,
    token_form: RefCell<Option<Rc<Token>>>,
    vote_form: RefCell<Option<Rc<Vote>>>,
    notes_form: RefCell<Option<Rc<Notes>>>,
    cover_form: RefCell<Option<Rc<Covers>>>,
    guide_form: RefCell<Option<Rc<Guide>>>,
    _imaging: crate::images::Imaging,
}
impl Drop for App {
    fn drop(&mut self) {
        nwg::unbind_raw_event_handler(&self.sort_style).ok();
        let _ = nwg::unbind_raw_event_handler(&self.preview_style);
        unsafe {
            winapi::um::wingdi::DeleteObject(self.preview_brush as _);
        }
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
        let _ = self.worker.send(Command::Exit);
    }
}
impl App {
    pub fn build(
        settings: Settings,
        path: PathBuf,
        readonly: bool,
        background: bool,
    ) -> Result<Rc<Self>> {
        nwg::init().map_err(|e| e.to_string())?;
        let imaging = crate::images::Imaging::start()?;
        let mut font = nwg::Font::default();
        nwg::Font::builder()
            .family("Meiryo UI")
            .size_absolute(14)
            .build(&mut font)
            .map_err(|e| e.to_string())?;
        nwg::Font::set_global_default(Some(font));
        let resources = nwg::EmbedResource::load(None).map_err(|e| e.to_string())?;
        let mut icon = nwg::Icon::default();
        nwg::Icon::builder()
            .source_embed(Some(&resources))
            .source_embed_id(1)
            .size(Some((32, 32)))
            .build(&mut icon)
            .map_err(|e| e.to_string())?;
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title(if readonly {
                "VN Presence — Rust preview"
            } else {
                "VN Presence"
            })
            .size((1240, 760))
            .position((200, 150))
            .icon(Some(&icon))
            .build(&mut window)
            .map_err(|e| e.to_string())?;
        let mut tray = nwg::TrayNotification::default();
        nwg::TrayNotification::builder()
            .parent(&window)
            .icon(Some(&icon))
            .tip(Some("VN Presence — Rust"))
            .build(&mut tray)
            .map_err(|e| e.to_string())?;
        let mut menu = nwg::Menu::default();
        nwg::Menu::builder()
            .parent(&window)
            .popup(true)
            .build(&mut menu)
            .unwrap();
        let mut show = nwg::MenuItem::default();
        nwg::MenuItem::builder()
            .parent(&menu)
            .text("Open VN Presence")
            .build(&mut show)
            .unwrap();
        let mut exit = nwg::MenuItem::default();
        nwg::MenuItem::builder()
            .parent(&menu)
            .text("Exit")
            .build(&mut exit)
            .unwrap();
        let mut more = nwg::Menu::default();
        nwg::Menu::builder()
            .parent(&window)
            .popup(true)
            .build(&mut more)
            .unwrap();
        let mut change_exe = nwg::MenuItem::default();
        nwg::MenuItem::builder()
            .parent(&more)
            .text("Change executable")
            .build(&mut change_exe)
            .unwrap();
        let mut export = nwg::MenuItem::default();
        nwg::MenuItem::builder()
            .parent(&more)
            .text("Export settings")
            .build(&mut export)
            .unwrap();
        let mut import = nwg::MenuItem::default();
        nwg::MenuItem::builder()
            .parent(&more)
            .text("Import settings")
            .build(&mut import)
            .unwrap();
        let mut guide = nwg::MenuItem::default();
        nwg::MenuItem::builder()
            .parent(&more)
            .text("Guide")
            .build(&mut guide)
            .unwrap();
        let mut library = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .flags(unsafe {
                nwg::ListBoxFlags::from_bits_unchecked(
                    WS_VISIBLE | WS_TABSTOP | LBS_NOTIFY | LBS_OWNERDRAWFIXED | LBS_HASSTRINGS,
                )
            })
            .position((16, 80))
            .size((580, 542))
            .build(&mut library)
            .unwrap();
        unsafe {
            SendMessageW(
                library.handle.hwnd().unwrap(),
                LB_SETITEMHEIGHT,
                0,
                (40. * nwg::scale_factor()) as isize,
            );
        }
        let mut sort = nwg::ComboBox::default();
        nwg::ComboBox::builder()
            .parent(&window)
            .flags(unsafe {
                nwg::ComboBoxFlags::from_bits_unchecked(
                    WS_VISIBLE
                        | WS_TABSTOP
                        | CBS_DROPDOWNLIST
                        | CBS_OWNERDRAWFIXED
                        | CBS_HASSTRINGS,
                )
            })
            .position((440, 44))
            .size((155, 100))
            .collection(vec![
                "Added".into(),
                "Alphabetical".into(),
                "Last played".into(),
            ])
            .selected_index(Some(match settings.game_sort_order.as_str() {
                "Alphabetical" => 1,
                "Last played" => 2,
                _ => 0,
            }))
            .build(&mut sort)
            .unwrap();
        unsafe {
            SendMessageW(
                sort.handle.hwnd().unwrap(),
                CB_SETITEMHEIGHT,
                usize::MAX,
                (28. * nwg::scale_factor()) as isize,
            );
        }
        let sort_style = crate::theme::combo(&sort.handle);
        let heading = label(&window, "Your games", (16, 46), (200, 24));
        let app_heading = label(&window, "VN Presence", (20, 22), (260, 36));
        let account_heading = label(&window, "VNDB", (940, 24), (260, 36));
        let status = label(&window, "Connecting to Discord…", (16, 660), (960, 24));
        let add = button(&window, "Add", (16, 632), (75, 28));
        let details = button(&window, "Details", (100, 632), (85, 28));
        let cover = button(&window, "Cover", (194, 632), (85, 28));
        let remove = button(&window, "Remove", (288, 632), (80, 28));
        let more_button = button(&window, "More", (377, 632), (80, 28));
        let pause = button(&window, "Pause", (16, 10), (85, 28));
        let startup = check(&window, "Start with Windows", (114, 12), startup_enabled());
        let preview_heading = label(&window, "Discord preview", (620, 46), (364, 24));
        let mut preview_card = nwg::Frame::default();
        nwg::Frame::builder()
            .parent(&window)
            .position((620, 76))
            .size((364, 234))
            .flags(nwg::FrameFlags::VISIBLE)
            .build(&mut preview_card)
            .unwrap();
        let preview_brush = unsafe { winapi::um::wingdi::CreateSolidBrush(0x241618) };
        if preview_brush.is_null() {
            return Err(last_error());
        }
        let style = move |hwnd, message, w: usize, l: isize| unsafe {
            match message {
                WM_PAINT => {
                    let mut paint = std::mem::zeroed();
                    let dc = BeginPaint(hwnd, &mut paint);
                    let mut rect = std::mem::zeroed();
                    GetClientRect(hwnd, &mut rect);
                    FillRect(dc, &rect, preview_brush);
                    EndPaint(hwnd, &paint);
                    Some(0)
                }
                WM_ERASEBKGND => {
                    let mut rect = std::mem::zeroed();
                    GetClientRect(hwnd, &mut rect);
                    FillRect(w as _, &rect, preview_brush);
                    Some(1)
                }
                WM_CTLCOLORSTATIC => {
                    winapi::um::wingdi::SetTextColor(w as _, 0xf2f2f2);
                    winapi::um::wingdi::SetBkColor(w as _, 0x241618);
                    Some(preview_brush as isize)
                }
                WM_DRAWITEM if l != 0 => {
                    let item = &*(l as *const DRAWITEMSTRUCT);
                    if item.CtlType != ODT_BUTTON {
                        return None;
                    }
                    let selected = item.itemState & ODS_SELECTED != 0;
                    FillRect(
                        item.hDC,
                        &item.rcItem,
                        if selected {
                            GetSysColorBrush(COLOR_3DDKSHADOW)
                        } else {
                            preview_brush
                        },
                    );
                    FrameRect(item.hDC, &item.rcItem, GetSysColorBrush(COLOR_GRAYTEXT));
                    let font = SendMessageW(item.hwndItem, WM_GETFONT, 0, 0);
                    let previous = winapi::um::wingdi::SelectObject(item.hDC, font as _);
                    winapi::um::wingdi::SetBkMode(item.hDC, winapi::um::wingdi::TRANSPARENT as i32);
                    winapi::um::wingdi::SetTextColor(
                        item.hDC,
                        if item.itemState & ODS_DISABLED != 0 {
                            0x888888
                        } else {
                            0xf2f2f2
                        },
                    );
                    let mut text = [0u16; 256];
                    let len = GetWindowTextW(item.hwndItem, text.as_mut_ptr(), text.len() as i32);
                    let mut rect = item.rcItem;
                    rect.left += 8;
                    rect.right -= 8;
                    DrawTextW(
                        item.hDC,
                        text.as_ptr(),
                        len,
                        &mut rect,
                        DT_CENTER | DT_VCENTER | DT_SINGLELINE | DT_END_ELLIPSIS | DT_NOPREFIX,
                    );
                    winapi::um::wingdi::SelectObject(item.hDC, previous);
                    if item.itemState & ODS_FOCUS != 0 {
                        InflateRect(&mut rect, -2, -4);
                        DrawFocusRect(item.hDC, &rect);
                    }
                    Some(1)
                }
                _ => None,
            }
        };
        let card_label = |text: &str, pos, size| {
            let mut label = nwg::Label::default();
            nwg::Label::builder()
                .parent(&preview_card)
                .text(text)
                .flags(nwg::LabelFlags::VISIBLE | nwg::LabelFlags::ELIPSIS)
                .v_align(nwg::VTextAlign::Top)
                .position(pos)
                .size(size)
                .background_color(Some([24, 22, 36]))
                .build(&mut label)
                .unwrap();
            label
        };
        let mut art = nwg::ImageFrame::default();
        nwg::ImageFrame::builder()
            .parent(&preview_card)
            .position((16, 44))
            .size((100, 100))
            .build(&mut art)
            .unwrap();
        let title = card_label("Select a game", (132, 44), (216, 22));
        let brand = card_label("", (132, 66), (216, 22));
        let progress = card_label("Playing", (16, 12), (332, 24));
        let details_preview = card_label("", (132, 88), (216, 22));
        let elapsed = card_label("", (132, 110), (216, 22));
        let game_button = button(&preview_card, "View on VNDB", (16, 162), (332, 28));
        let profile_button = button(&preview_card, "VNDB profile", (16, 198), (332, 28));
        let mut heading_font = nwg::Font::default();
        nwg::Font::builder()
            .family("Meiryo UI")
            .size_absolute(20)
            .weight(700)
            .build(&mut heading_font)
            .unwrap();
        app_heading.set_font(Some(&heading_font));
        account_heading.set_font(Some(&heading_font));
        title.set_font(Some(&heading_font));
        for button in [&game_button, &profile_button] {
            unsafe {
                let hwnd = button.handle.hwnd().unwrap();
                let style = GetWindowLongW(hwnd, GWL_STYLE) as u32;
                SetWindowLongW(
                    hwnd,
                    GWL_STYLE,
                    ((style & !BS_TYPEMASK) | BS_OWNERDRAW) as i32,
                );
            }
        }
        let preview_style = nwg::bind_raw_event_handler(&preview_card.handle, 0x10002, style)
            .map_err(|e| e.to_string())?;
        let connect = button(&window, "Connect VNDB", (620, 316), (135, 28));
        let connection = label(&window, "Not connected to VNDB", (620, 354), (160, 24));
        let profile = check(
            &window,
            "Show VNDB profile button",
            (788, 351),
            settings.show_profile_button,
        );
        let labels_heading = label(&window, "My labels", (620, 390), (364, 24));
        let mut labels_scroll = nwg::ScrollBar::default();
        nwg::ScrollBar::builder()
            .parent(&window)
            .position((946, 418))
            .size((20, 144))
            .range(Some(0..1))
            .flags(nwg::ScrollBarFlags::VERTICAL | nwg::ScrollBarFlags::TAB_STOP)
            .build(&mut labels_scroll)
            .unwrap();
        let vote_label = label(&window, "My vote", (620, 588), (60, 24));
        let vote = button(&window, "Vote", (690, 584), (172, 28));
        let notes = button(&window, "Notes", (871, 584), (97, 28));
        let save_status = label(&window, "", (620, 620), (364, 34));
        let notice = nwg::Notice::create(&window).map_err(|e| e.to_string())?;
        let (tx, rx) = mpsc::channel();
        let (worker, commands) = mpsc::channel();
        let sender = tx.clone();
        let notice_sender = notice.sender();
        let initial = settings.clone();
        thread::spawn(move || {
            let mut rpc = net::Discord::default();
            let mut config = initial;
            let mut paused = readonly;
            let mut username = None;
            let mut previous = 0;
            let mut last = None::<Value>;
            let mut last_sent = Instant::now() - Duration::from_secs(60);
            let mut uses = Vec::<(String, Instant, Duration)>::new();
            loop {
                match commands.recv_timeout(Duration::from_secs(5)) {
                    Ok(Command::Exit) => {
                        if rpc.connected {
                            let _ = rpc.update(None);
                        }
                        let _ = sender.send(Update::Exited);
                        notice_sender.notice();
                        break;
                    }
                    Ok(Command::State(s, p, u)) => {
                        config = s;
                        paused = p;
                        username = u;
                    }
                    Err(mpsc::RecvTimeoutError::Disconnected) => break,
                    Err(_) => {}
                }
                let running = scan(Some(&config.games));
                let active = running
                    .as_ref()
                    .ok()
                    .and_then(|r| choose(r, &config.games, foreground_pid(), previous));
                previous = active.as_ref().map(|(p, _)| p.pid).unwrap_or(0);
                let activity = if paused {
                    None
                } else {
                    active.as_ref().map(|(p, i)| {
                        crate::core::activity(&config.games[*i], p, &config, username.as_deref())
                    })
                };
                let status = if readonly {
                    "Preview mode — Discord sharing is off".into()
                } else {
                    let reconnecting = !rpc.connected;
                    let result = rpc.connect(&config.client_id).and_then(|_| {
                        if reconnecting
                            || activity != last
                            || last_sent.elapsed() >= Duration::from_secs(20)
                        {
                            rpc.update(activity.clone())?;
                            last = activity;
                            last_sent = Instant::now();
                        }
                        Ok(())
                    });
                    match result {
                        Ok(()) => {
                            if !paused {
                                if let Some((_, i)) = &active {
                                    if let Some(im) =
                                        config.games[*i].image().filter(|im| net::hosted(&im.url))
                                    {
                                        if uses
                                            .iter()
                                            .find(|(url, _, _)| url == &im.url)
                                            .is_none_or(|(_, time, delay)| time.elapsed() >= *delay)
                                        {
                                            let url = im.url.clone();
                                            let id = config.games[*i].novel.id.clone();
                                            let delay = if net::touch(&url, &id).is_ok() {
                                                Duration::from_secs(86400)
                                            } else {
                                                Duration::from_secs(3600)
                                            };
                                            uses.retain(|(u, _, _)| u != &url);
                                            if uses.len() >= 100 {
                                                uses.remove(0);
                                            }
                                            uses.push((url, Instant::now(), delay));
                                        }
                                    }
                                }
                            }
                            if paused {
                                "Paused".into()
                            } else if active.is_some() {
                                "Connected to Discord".into()
                            } else {
                                "Connected to Discord · Waiting for a game".into()
                            }
                        }
                        Err(e) => {
                            rpc.disconnect();
                            e
                        }
                    }
                };
                let status = running.err().unwrap_or(status);
                let active = active.map(|(p, i)| (p, config.games[i].novel.id.clone()));
                let _ = sender.send(Update::Poll(active, status));
                notice_sender.notice();
            }
        });
        let theme = crate::theme::Theme::new(&window, true);
        let app = Rc::new(Self {
            window,
            _theme: theme,
            icon,
            tray,
            menu,
            show,
            exit,
            library,
            sort,
            sort_style,
            _heading: heading,
            _app_heading: app_heading,
            account_heading,
            _heading_font: heading_font,
            status,
            add,
            details,
            cover,
            remove,
            more,
            more_button,
            change_exe,
            export,
            import,
            guide,
            pause,
            startup,
            preview_heading,
            preview_card,
            preview_style,
            preview_brush,
            details_preview,
            art,
            title,
            brand,
            progress,
            elapsed,
            game_button,
            profile_button,
            connect,
            connection,
            profile,
            labels_heading,
            labels: RefCell::new(vec![]),
            labels_scroll,
            vote,
            notes,
            vote_label,
            save_status,
            state: RefCell::new(State {
                settings,
                path,
                selected: None,
                order: vec![],
                active: None,
                account: None,
                entry: None,
                saving: false,
                queued: None,
                draft: None,
                entry_generation: 0,
                readonly,
            }),
            handler: RefCell::new(None),
            notice,
            tx,
            rx,
            worker,
            paused: Cell::new(readonly),
            loading: Cell::new(false),
            closing: Cell::new(false),
            bitmap: RefCell::new(None),
            image_url: RefCell::new(String::new()),
            cache: RefCell::new(vec![]),
            details_form: RefCell::new(None),
            add_form: RefCell::new(None),
            token_form: RefCell::new(None),
            vote_form: RefCell::new(None),
            notes_form: RefCell::new(None),
            cover_form: RefCell::new(None),
            guide_form: RefCell::new(None),
            _imaging: imaging,
        });
        let weak = Rc::downgrade(&app);
        *app.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &app.window.handle,
            move |evt, data, handle| {
                if let Some(a) = weak.upgrade() {
                    a.event(evt, data, handle);
                }
            },
        ));
        app.refresh_library(None);
        if readonly {
            app.connect.set_enabled(false);
            app.startup.set_enabled(false);
            app.pause.set_enabled(false);
        }
        app.layout();
        app.send_state();
        if background {
            hide(&app.window);
        }
        if !readonly {
            let saved = app.state.borrow().settings.vndb_token_protected.clone();
            if let Some(s) = saved {
                match unprotect(&s) {
                    Ok(t) => app.connect_token(t, false),
                    Err(e) => app.save_status.set_text(&e),
                }
            }
        }
        Ok(app)
    }
    fn job(&self, f: impl FnOnce() -> Update + Send + 'static) {
        let tx = self.tx.clone();
        let notice = self.notice.sender();
        thread::spawn(move || {
            let result = f();
            let _ = tx.send(result);
            notice.notice();
        });
    }
    fn send_state(&self) {
        let s = self.state.borrow();
        let _ = self.worker.send(Command::State(
            s.settings.clone(),
            self.paused.get() || s.readonly,
            s.account.as_ref().map(|a| a.username.clone()),
        ));
    }
    fn persist(&self) -> bool {
        let s = self.state.borrow();
        if s.readonly {
            return true;
        }
        match s.settings.save(&s.path) {
            Ok(()) => true,
            Err(e) => {
                drop(s);
                error(&self.window, &e);
                false
            }
        }
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        let right = w as i32 - 340;
        let center_width = w.saturating_sub(700);
        self._heading.set_position(20, 80);
        self._heading.set_size(110, 24);
        self.library.set_position(20, 116);
        self.library.set_size(260, h.saturating_sub(264));
        self.sort.set_position(140, 76);
        self.sort.set_size(140, 100);
        self.add.set_position(20, h as i32 - 128);
        self.add.set_size(122, 34);
        self.more_button.set_position(158, h as i32 - 128);
        self.more_button.set_size(122, 34);
        for (b, x) in [(&self.details, 20), (&self.cover, 110), (&self.remove, 200)] {
            b.set_position(x, h as i32 - 82);
            b.set_size(80, 32);
        }
        self.pause.set_position(320, 22);
        self.startup.set_position(420, 24);
        self.status.set_position(16, h.saturating_sub(25) as i32);
        self.status.set_size(w.saturating_sub(32), 24);
        self.preview_heading.set_position(320, 80);
        self.preview_card.set_position(320, 116);
        self.preview_card.set_size(center_width, 340);
        self.art.set_position(20, 52);
        self.art.set_size(160, 160);
        self.progress.set_position(20, 16);
        self.game_button.set_position(20, 246);
        self.game_button
            .set_size(center_width.saturating_sub(40), 34);
        self.profile_button.set_position(20, 292);
        self.profile_button
            .set_size(center_width.saturating_sub(40), 34);
        self.account_heading.set_position(right, 24);
        self.connect.set_position(right, 80);
        self.connect.set_size(300, 38);
        self.connection.set_position(right, 132);
        self.connection.set_size(300, 24);
        self.profile.set_position(right, 161);
        self.profile.set_size(300, 24);
        self.sort.set_selection(Some(
            match self.state.borrow().settings.game_sort_order.as_str() {
                "Alphabetical" => 1,
                "Last played" => 2,
                _ => 0,
            },
        ));
        self.labels_heading.set_position(right, 198);
        self.labels_scroll.set_position(right + 284, 236);
        self.labels_scroll.set_size(16, 192);
        self.labels_heading.set_size(300, 24);
        let offset = self.labels_scroll.pos();
        for (i, c) in self.labels.borrow().iter().enumerate() {
            c.set_visible(i >= offset && i < offset + 6);
            c.set_position(right, 236 + (i as i32 - offset as i32) * 32);
            c.set_size(280, 28);
        }
        let count = 6;
        let y = (236 + count as i32 * 32).min(h as i32 - 108);
        self.vote_label.set_position(right, y + 6);
        self.vote.set_position(right + 80, y + 2);
        self.vote.set_size(220, 38);
        self.vote_label.set_size(80, 24);
        self.notes.set_position(right, y + 52);
        self.notes.set_size(300, 38);
        self.save_status.set_position(right, y + 104);
        self.save_status.set_size(300, 34);
        self.layout_preview_text();
    }
    fn refresh_library(&self, id: Option<String>) {
        self.loading.set(true);
        let (order, titles, selected) = {
            let s = self.state.borrow();
            let order = s.settings.sorted_indices();
            let titles = order
                .iter()
                .map(|&i| s.settings.games[i].novel.native_title().to_owned())
                .collect();
            let selected = id
                .as_deref()
                .and_then(|id| {
                    order
                        .iter()
                        .position(|&i| s.settings.games[i].novel.id == id)
                })
                .or_else(|| (!order.is_empty()).then_some(0));
            (order, titles, selected)
        };
        self.library.set_collection(titles);
        fit_horizontal(&self.library);
        self.library.set_selection(selected);
        self.state.borrow_mut().order = order;
        self.loading.set(false);
        self.select_game();
    }
    fn selected(&self) -> Option<Game> {
        let s = self.state.borrow();
        s.selected.and_then(|i| s.settings.games.get(i).cloned())
    }
    fn entry_unsaved(&self) -> bool {
        let s = self.state.borrow();
        s.entry
            .as_ref()
            .zip(s.draft.as_ref())
            .is_some_and(|((_, saved), draft)| {
                saved.vote != draft.vote
                    || saved
                        .labels
                        .iter()
                        .any(|id| *id != 0 && *id != 7 && !draft.labels.contains(id))
                    || draft
                        .labels
                        .iter()
                        .any(|id| *id != 0 && *id != 7 && !saved.labels.contains(id))
            })
    }
    fn can_leave_entry(&self) -> bool {
        let s = self.state.borrow();
        if s.saving || s.queued.is_some() {
            self.save_status
                .set_text("Wait for your VNDB changes to finish saving.");
            return false;
        }
        drop(s);
        if !self.entry_unsaved() {
            return true;
        }
        if !confirm(
            &self.window,
            "Unsaved VNDB changes",
            "Your label or vote changes weren't saved. Discard them?",
        ) {
            return false;
        }
        let saved = self.state.borrow().entry.as_ref().map(|(_, e)| e.clone());
        self.state.borrow_mut().draft = saved.clone();
        if let Some(e) = saved {
            self.show_entry(&e);
        }
        true
    }
    fn select_game(&self) {
        if self.loading.get() {
            return;
        }
        let index = {
            let s = self.state.borrow();
            self.library
                .selection()
                .and_then(|n| s.order.get(n).copied())
        };
        let old = self.state.borrow().selected;
        if old != index && !self.can_leave_entry() {
            let selection =
                old.and_then(|i| self.state.borrow().order.iter().position(|&n| n == i));
            self.loading.set(true);
            self.library.set_selection(selection);
            self.loading.set(false);
            return;
        }
        self.state.borrow_mut().selected = index;
        let enabled = index.is_some();
        for b in [&self.details, &self.cover, &self.remove] {
            b.set_enabled(enabled);
        }
        self.change_exe.set_enabled(enabled);
        self.refresh_preview();
        if old != index || (index.is_none() && self.state.borrow().entry.is_some()) {
            self.load_entry();
        }
    }
    fn layout_preview_text(&self) {
        let mut y = 52;
        let width = self.preview_card.size().0.saturating_sub(220);
        for label in [
            &self.title,
            &self.brand,
            &self.details_preview,
            &self.elapsed,
        ] {
            let visible = !label.text().is_empty();
            label.set_visible(visible);
            let height = if label.handle == self.title.handle {
                30
            } else {
                24
            };
            label.set_position(200, y);
            label.set_size(width, height);
            if visible {
                y += height as i32 + 8;
            }
        }
    }
    fn refresh_preview(&self) {
        let Some(game) = self.selected() else {
            self.title.set_text("Select a game");
            self.brand.set_text("");
            self.progress.set_text("Playing");
            self.details_preview.set_text("");
            self.game_button.set_enabled(false);
            self.profile_button.set_visible(false);
            self.art.set_bitmap(None);
            *self.bitmap.borrow_mut() = None;
            self.image_url.borrow_mut().clear();
            self.elapsed.set_text("");
            self.layout_preview_text();
            self.preview_heading
                .set_text("Discord preview · Game not running");
            return;
        };
        let (active, settings, username) = {
            let s = self.state.borrow();
            (
                s.active.clone(),
                s.settings.clone(),
                s.account.as_ref().map(|a| a.username.clone()),
            )
        };
        let running = active
            .as_ref()
            .filter(|(_, i)| Some(*i) == self.state.borrow().selected);
        self.preview_heading
            .set_text(if running.is_some() && !self.paused.get() {
                "Discord preview · Live"
            } else if running.is_some() {
                "Discord preview · Not shared"
            } else {
                "Discord preview · Game not running"
            });
        self.title.set_text(game.novel.native_title());
        self.elapsed.set_text(
            &running
                .map(|(p, _)| {
                    let sec = (now() - p.started).max(0);
                    format!(
                        "{}:{:02}:{:02} elapsed",
                        sec / 3600,
                        sec / 60 % 60,
                        sec % 60
                    )
                })
                .unwrap_or_default(),
        );
        self.brand.set_text(&game.novel.brand());
        self.details_preview.set_text(
            &running
                .and_then(|(p, _)| game.progress(&p.caption))
                .or_else(|| game.custom_progress.as_deref().and_then(nonblank))
                .unwrap_or_default(),
        );
        self.layout_preview_text();
        self.game_button.set_enabled(true);
        self.profile_button
            .set_text(&profile_label(username.as_deref()));
        self.profile_button.set_visible(
            settings.show_profile_button
                && settings
                    .profile_url
                    .as_deref()
                    .and_then(profile_url)
                    .is_some(),
        );
        let url = game
            .image()
            .map(|im| im.thumbnail.as_deref().unwrap_or(&im.url))
            .unwrap_or("")
            .to_owned();
        if *self.image_url.borrow() == url {
            return;
        }
        *self.image_url.borrow_mut() = url.clone();
        self.art.set_bitmap(None);
        *self.bitmap.borrow_mut() = None;
        if let Some((_, bytes)) = self.cache.borrow().iter().find(|(u, _)| u == &url) {
            self.show_image(bytes);
            return;
        }
        if !url.is_empty() {
            self.job(move || Update::Image(url.clone(), net::image(&url)));
        }
    }
    fn show_image(&self, bytes: &[u8]) {
        let prepared = crate::images::Picture::load(bytes)
            .and_then(|p| {
                p.output(
                    false,
                    1.,
                    (p.width as f32 / 2., p.height as f32 / 2.),
                    false,
                    image_pixels(160),
                )
            })
            .and_then(|p| p.png());
        let Ok(bytes) = prepared else {
            return;
        };
        let mut b = nwg::Bitmap::default();
        if nwg::Bitmap::builder()
            .source_bin(Some(&bytes))
            .build(&mut b)
            .is_ok()
        {
            self.art.set_bitmap(Some(&b));
            *self.bitmap.borrow_mut() = Some(b);
        }
    }
    fn connect_token(&self, token: String, remember: bool) {
        self.connect.set_enabled(false);
        self.connection.set_text("Connecting…");
        self.job(move || Update::Connected(Account::connect(&token), remember));
    }
    fn rebuild_labels(&self) {
        self.loading.set(true);
        self.labels.borrow_mut().clear();
        let account = self.state.borrow().account.clone();
        if let Some(a) = &account {
            for (i, l) in a.labels.iter().enumerate() {
                let name = format!("{}{}", l.name, if l.private { " (private)" } else { "" });
                self.labels.borrow_mut().push(check(
                    &self.window,
                    &name,
                    (0, 418 + i as i32 * 24),
                    false,
                ));
            }
        }
        self.loading.set(false);
        let count = self.labels.borrow().len();
        self.labels_scroll.set_range(0..count.saturating_sub(6));
        self.labels_scroll.set_pos(0);
        self.labels_scroll.set_visible(count > 6);
        self.layout();
        self.load_entry();
    }
    fn load_entry(&self) {
        self.state.borrow_mut().entry_generation += 1;
        self.state.borrow_mut().entry = None;
        self.state.borrow_mut().draft = None;
        self.vote.set_text("Vote");
        self.vote.set_enabled(false);
        self.notes.set_enabled(false);
        for c in self.labels.borrow().iter() {
            c.set_enabled(false);
            c.set_check_state(nwg::CheckBoxState::Unchecked);
        }
        if self.state.borrow().readonly {
            return;
        }
        let pair = {
            let s = self.state.borrow();
            s.account.clone().zip(
                s.selected
                    .and_then(|i| s.settings.games.get(i))
                    .map(|g| g.novel.id.clone()),
            )
        };
        if let Some((a, id)) = pair {
            let generation = self.state.borrow().entry_generation;
            self.save_status.set_text("Loading…");
            self.job(move || Update::Entry(id.clone(), generation, a.read(&id)));
        } else {
            self.save_status.set_text("");
        }
    }
    fn show_entry(&self, e: &Entry) {
        self.loading.set(true);
        let ids = self
            .state
            .borrow()
            .account
            .as_ref()
            .map(|a| a.labels.iter().map(|l| l.id).collect::<Vec<_>>())
            .unwrap_or_default();
        for (c, id) in self.labels.borrow().iter().zip(ids) {
            c.set_check_state(if e.labels.contains(&id) {
                nwg::CheckBoxState::Checked
            } else {
                nwg::CheckBoxState::Unchecked
            });
            c.set_enabled(true);
        }
        self.vote.set_text(
            &e.vote
                .map(|v| format!("{:.1}", v as f64 / 10.0))
                .unwrap_or_else(|| "Vote".into()),
        );
        self.vote.set_enabled(true);
        self.notes.set_enabled(true);
        self.loading.set(false);
    }
    fn queue_entry(&self, e: Entry) {
        if self.state.borrow().readonly {
            return;
        }
        self.state.borrow_mut().draft = Some(e.clone());
        let pair = self
            .state
            .borrow()
            .entry
            .as_ref()
            .map(|(id, _)| (id.clone(), e));
        if let Some(pair) = pair {
            self.state.borrow_mut().queued = Some(pair);
            self.start_save();
        }
    }
    fn start_save(&self) {
        let task = {
            let mut s = self.state.borrow_mut();
            if s.saving {
                return;
            }
            let Some((id, new)) = s.queued.take() else {
                return;
            };
            let Some(a) = s.account.clone() else {
                return;
            };
            let Some((entry_id, old)) = &s.entry else {
                return;
            };
            if entry_id != &id {
                return;
            }
            let old = old.clone();
            s.saving = true;
            (a, id, old, new)
        };
        self.save_status.set_text("Saving to VNDB…");
        let (a, id, old, new) = task;
        self.job(move || Update::Saved(id.clone(), new.clone(), a.save(&id, &old, &new)));
    }
    fn shutdown(&self) {
        if self
            .cover_form
            .borrow()
            .as_ref()
            .is_some_and(|c| c.pending())
        {
            self.status
                .set_text("Wait for the cover request to finish.");
            return;
        }
        if !self.can_leave_entry() {
            return;
        }
        if self.notes_form.borrow().as_ref().is_some_and(|n| n.dirty()) {
            if let Some(n) = self.notes_form.borrow().as_ref() {
                n.window.set_visible(true);
                n.close_pending.set(true);
                n.save();
            }
            return;
        }
        self.closing.set(true);
        self.window.set_enabled(false);
        self.status.set_text("Closing…");
        let _ = self.worker.send(Command::Exit);
    }
    fn event(self: &Rc<Self>, evt: nwg::Event, data: nwg::EventData, h: nwg::ControlHandle) {
        use nwg::Event as E;
        match evt {
            E::OnMinMaxInfo if h == self.window => data.on_min_max().set_min_size(1120, 720),
            E::OnResize if h == self.window => self.layout(),
            E::OnVerticalScroll if h == self.labels_scroll => self.layout(),
            E::OnWindowClose if h == self.window => {
                keep_open(&data);
                hide(&self.window);
            }
            E::OnWindowMinimize if h == self.window => hide(&self.window),
            E::OnNotice if h == self.notice => self.updates(),
            E::OnListBoxSelect if h == self.library => {
                if self.state.borrow().saving {
                    self.loading.set(true);
                    let s = self.state.borrow();
                    let n = s
                        .selected
                        .and_then(|i| s.order.iter().position(|&n| n == i));
                    drop(s);
                    self.library.set_selection(n);
                    self.loading.set(false);
                    self.save_status
                        .set_text("Wait for the current change to save.");
                } else {
                    self.select_game();
                }
            }
            E::OnComboxBoxSelection if h == self.sort && !self.loading.get() => {
                if let Some(sort) = self.sort.selection_string() {
                    self.state.borrow_mut().settings.game_sort_order = sort;
                    self.persist();
                    let id = self.selected().map(|g| g.novel.id);
                    self.refresh_library(id);
                }
            }
            E::OnMousePress(nwg::MousePressEvent::MousePressLeftUp) if h == self.art => {
                if let Some(game) = self.selected() {
                    open_url(&game.novel.url());
                }
            }
            E::OnMousePress(nwg::MousePressEvent::MousePressLeftUp) if h == self.brand => {
                if let Some(url) = self.selected().and_then(|g| g.novel.brand_url()) {
                    open_url(&url);
                }
            }
            E::OnMousePress(nwg::MousePressEvent::MousePressLeftUp) if h == self.tray => {
                self.window.set_visible(true);
                self.window.restore();
                self.window.set_focus();
            }
            E::OnContextMenu if h == self.tray => {
                let (x, y) = nwg::GlobalCursor::position();
                self.menu.popup(x, y);
            }
            E::OnMenuItemSelected if h == self.show => {
                self.window.set_visible(true);
                self.window.restore();
                self.window.set_focus();
            }
            E::OnMenuItemSelected if h == self.exit => self.shutdown(),
            E::OnMenuItemSelected if h == self.change_exe => {
                if let Some(path) = file_dialog(
                    &self.window,
                    "Choose the game's executable",
                    "Executable (*.exe)|*.exe",
                    false,
                ) {
                    if !valid_exe_path(&path.to_string_lossy()) {
                        error(&self.window, "Choose an executable (.exe) file.");
                        return;
                    }
                    let mut s = self.state.borrow_mut();
                    if let Some(i) = s.selected {
                        if s.settings
                            .games
                            .iter()
                            .enumerate()
                            .any(|(n, g)| n != i && same_exe(&g.exe, &path.to_string_lossy()))
                        {
                            drop(s);
                            error(&self.window, "This executable is already linked.");
                            return;
                        }
                        s.settings.games[i].exe = path.to_string_lossy().into_owned();
                    }
                    drop(s);
                    self.persist();
                    self.send_state();
                }
            }
            E::OnMenuItemSelected if h == self.export => {
                if let Some(path) = file_dialog(
                    &self.window,
                    "Export settings",
                    "JSON (*.json)|*.json",
                    true,
                ) {
                    if let Err(e) = std::fs::write(
                        path,
                        serde_json::to_vec_pretty(&self.state.borrow().settings.export()).unwrap(),
                    ) {
                        error(&self.window, &e.to_string());
                    }
                }
            }
            E::OnMenuItemSelected if h == self.import => {
                if !self.can_leave_entry() {
                    return;
                }
                if let Some(path) = file_dialog(
                    &self.window,
                    "Import settings",
                    "JSON (*.json)|*.json",
                    false,
                ) {
                    match Settings::read_backup(&path) {
                        Ok(b) => {
                            if confirm(
                                &self.window,
                                "Import settings",
                                "Replace your saved games and profile with this export?",
                            ) {
                                let mut s = self.state.borrow_mut();
                                s.settings.games = b.games;
                                s.settings.profile_url = b.profile_url;
                                s.settings.show_profile_button = b.show_profile_button;
                                s.settings.game_sort_order = b.game_sort_order;
                                s.selected = None;
                                drop(s);
                                self.persist();
                                self.refresh_library(None);
                                self.send_state();
                            }
                        }
                        Err(e) => error(&self.window, &e),
                    }
                }
            }
            E::OnMenuItemSelected if h == self.guide => {
                *self.guide_form.borrow_mut() = Some(Guide::build(self));
            }
            E::OnButtonClick => {
                if h == self.pause {
                    self.paused.set(!self.paused.get());
                    self.pause
                        .set_text(if self.paused.get() { "Resume" } else { "Pause" });
                    self.send_state();
                    self.refresh_preview();
                } else if h == self.startup {
                    if let Err(e) = set_startup(is_checked(&self.startup)) {
                        self.startup.set_check_state(if startup_enabled() {
                            nwg::CheckBoxState::Checked
                        } else {
                            nwg::CheckBoxState::Unchecked
                        });
                        error(&self.window, &e);
                    }
                } else if h == self.more_button {
                    let (x, y) = nwg::GlobalCursor::position();
                    self.more.popup(x, y);
                } else if h == self.game_button {
                    if let Some(g) = self.selected() {
                        open_url(&g.novel.url());
                    }
                } else if h == self.profile_button {
                    if let Some(url) = self
                        .state
                        .borrow()
                        .settings
                        .profile_url
                        .as_deref()
                        .and_then(profile_url)
                    {
                        open_url(&url);
                    }
                } else if h == self.profile {
                    self.state.borrow_mut().settings.show_profile_button =
                        is_checked(&self.profile);
                    self.persist();
                    self.send_state();
                    self.refresh_preview();
                } else if h == self.remove {
                    if !self.can_leave_entry() {
                        return;
                    }
                    if let Some(g) = self.selected() {
                        if confirm(
                            &self.window,
                            "Remove game",
                            &format!("Unlink {}?", g.novel.native_title()),
                        ) {
                            let mut s = self.state.borrow_mut();
                            if let Some(i) = s.selected {
                                s.settings.games.remove(i);
                            }
                            s.selected = None;
                            drop(s);
                            self.persist();
                            self.refresh_library(None);
                            self.send_state();
                        }
                    }
                } else if h == self.details {
                    if let Some(g) = self.selected() {
                        *self.details_form.borrow_mut() = Some(Details::build(self, g));
                    }
                } else if h == self.add {
                    if !self.can_leave_entry() {
                        return;
                    }
                    *self.add_form.borrow_mut() = Some(AddGame::build(self));
                } else if h == self.cover {
                    if let Some(c) = self
                        .cover_form
                        .borrow()
                        .as_ref()
                        .filter(|c| c.window.visible() || c.busy.get())
                    {
                        c.window.set_focus();
                        return;
                    }
                    if let Some(game) = self.selected() {
                        *self.cover_form.borrow_mut() = Some(Covers::build(self, game));
                    }
                } else if h == self.connect {
                    if !self.can_leave_entry() {
                        return;
                    }
                    if self.state.borrow().account.is_some() {
                        let mut s = self.state.borrow_mut();
                        s.account = None;
                        s.settings.vndb_token_protected = None;
                        drop(s);
                        self.persist();
                        self.connect.set_text("Connect VNDB");
                        self.connection.set_text("Not connected to VNDB");
                        self.rebuild_labels();
                        self.send_state();
                    } else {
                        *self.token_form.borrow_mut() = Some(Token::build(self));
                    }
                } else if h == self.vote {
                    *self.vote_form.borrow_mut() = Some(Vote::build(self));
                } else if h == self.notes {
                    if let Some(n) = self
                        .notes_form
                        .borrow()
                        .as_ref()
                        .filter(|n| n.window.visible() || n.dirty())
                    {
                        n.window.set_visible(true);
                        n.window.set_focus();
                        return;
                    }
                    if let (Some(a), Some((id, e))) = (
                        self.state.borrow().account.clone(),
                        self.state.borrow().entry.clone(),
                    ) {
                        *self.notes_form.borrow_mut() = Some(Notes::build(self, a, id, e.notes));
                    }
                } else if !self.loading.get() {
                    let ids = self
                        .state
                        .borrow()
                        .account
                        .as_ref()
                        .map(|a| a.labels.iter().map(|l| l.id).collect::<Vec<_>>())
                        .unwrap_or_default();
                    if let Some(i) = self.labels.borrow().iter().position(|c| c.handle == h) {
                        let id = ids[i];
                        if (1..=4).contains(&id) && is_checked(&self.labels.borrow()[i]) {
                            for (c, other) in self.labels.borrow().iter().zip(&ids) {
                                if *other != id && (1..=4).contains(other) {
                                    c.set_check_state(nwg::CheckBoxState::Unchecked);
                                }
                            }
                        }
                        let mut e = self.state.borrow().draft.clone().unwrap_or_default();
                        e.labels = self
                            .labels
                            .borrow()
                            .iter()
                            .zip(ids)
                            .filter(|(c, _)| is_checked(c))
                            .map(|(_, id)| id)
                            .collect();
                        self.queue_entry(e);
                    }
                }
            }
            _ => {}
        }
    }
    fn updates(self: &Rc<Self>) {
        while let Ok(update) = self.rx.try_recv() {
            match update {
                Update::Poll(active, status) => {
                    let active = active.and_then(|(p, id)| {
                        self.state
                            .borrow()
                            .settings
                            .games
                            .iter()
                            .position(|g| g.novel.id == id)
                            .map(|i| (p, i))
                    });
                    let changed = self.state.borrow().active.as_ref().map(|(p, _)| p.pid)
                        != active.as_ref().map(|(p, _)| p.pid);
                    if changed {
                        if let Some((_, i)) = &active {
                            self.state.borrow_mut().settings.games[*i].last_played = now();
                            self.persist();
                        }
                    }
                    self.state.borrow_mut().active = active;
                    if changed && self.state.borrow().settings.game_sort_order == "Last played" {
                        self.refresh_library(self.selected().map(|g| g.novel.id));
                    }
                    self.status.set_text(&status);
                    self.refresh_preview();
                    if let Some(d) = self.details_form.borrow().as_ref() {
                        d.preview();
                    }
                }
                Update::Image(url, result) => match result {
                    Ok(bytes) => {
                        if *self.image_url.borrow() == url {
                            self.show_image(&bytes);
                        }
                        let mut cache = self.cache.borrow_mut();
                        while cache.iter().map(|(_, b)| b.len()).sum::<usize>() + bytes.len()
                            > 8 * 1024 * 1024
                            && !cache.is_empty()
                        {
                            cache.remove(0);
                        }
                        cache.push((url, bytes));
                    }
                    Err(_) => {}
                },
                Update::Connected(result, remember) => {
                    self.connect.set_enabled(true);
                    match result {
                        Ok(a) => {
                            let mut s = self.state.borrow_mut();
                            if remember {
                                match protect(&a.token) {
                                    Ok(t) => s.settings.vndb_token_protected = Some(t),
                                    Err(e) => {
                                        drop(s);
                                        error(&self.window, &e);
                                        continue;
                                    }
                                }
                            }
                            s.settings.profile_url = Some(format!("https://vndb.org/{}", a.id));
                            let name = a.username.clone();
                            s.account = Some(a);
                            drop(s);
                            self.persist();
                            self.connect.set_text("Disconnect");
                            self.connection.set_text(&format!("Connected as {name}"));
                            self.rebuild_labels();
                            self.send_state();
                            self.refresh_preview();
                            if let Some(t) = self.token_form.borrow().as_ref() {
                                hide(&t.window);
                            }
                        }
                        Err(e) => {
                            self.connection.set_text("Not connected to VNDB");
                            self.save_status.set_text(&e);
                            if let Some(t) = self.token_form.borrow().as_ref() {
                                t.status.set_text(&e);
                                t.save.set_enabled(true);
                            }
                        }
                    }
                }
                Update::Entry(id, generation, result) => {
                    if self.state.borrow().entry_generation != generation
                        || self.selected().is_none_or(|g| g.novel.id != id)
                    {
                        continue;
                    }
                    match result {
                        Ok(e) => {
                            self.state.borrow_mut().draft = Some(e.clone());
                            self.state.borrow_mut().entry = Some((id, e.clone()));
                            self.show_entry(&e);
                            self.save_status.set_text("");
                        }
                        Err(e) => self.save_status.set_text(&e),
                    }
                }
                Update::Saved(id, mut new, result) => {
                    self.state.borrow_mut().saving = false;
                    match result {
                        Ok(()) => {
                            let mut s = self.state.borrow_mut();
                            if s.entry.as_ref().is_some_and(|(old, _)| old == &id) {
                                // Labels and votes never change notes; a concurrent notes save may be newer.
                                new.notes = s.entry.as_ref().unwrap().1.notes.clone();
                                s.entry = Some((id, new));
                            }
                            drop(s);
                            self.save_status.set_text("Saved to VNDB.");
                            self.start_save();
                        }
                        Err(e) => {
                            self.state.borrow_mut().queued = None;
                            self.save_status.set_text(&format!("Not saved: {e}"));
                        }
                    }
                }
                Update::Search(generation, result) => {
                    if let Some(a) = self
                        .add_form
                        .borrow()
                        .as_ref()
                        .filter(|a| a.last_search.get() == Some(generation))
                    {
                        a.search_done(result);
                    }
                }
                Update::Exited => nwg::stop_thread_dispatch(),
                Update::TrimCover => {
                    let closed = self
                        .cover_form
                        .borrow()
                        .as_ref()
                        .is_some_and(|c| !c.window.visible() && !c.pending());
                    if closed {
                        self.cover_form.borrow_mut().take();
                    }
                }
            }
        }
    }
    fn replace_game(&self, mut game: Game) -> bool {
        let mut s = self.state.borrow_mut();
        let Some(i) = s
            .settings
            .games
            .iter()
            .position(|g| g.novel.id == game.novel.id)
        else {
            return false;
        };
        game.last_played = s.settings.games[i].last_played;
        let previous = std::mem::replace(&mut s.settings.games[i], game);
        drop(s);
        let saved = self.persist();
        if saved {
            self.send_state();
            self.refresh_preview();
        } else {
            self.state.borrow_mut().settings.games[i] = previous;
        }
        saved
    }
}

fn startup_enabled() -> bool {
    use winapi::um::{winnt::KEY_READ, winreg::*};
    unsafe {
        let mut key = std::ptr::null_mut();
        let path = wide(r"Software\Microsoft\Windows\CurrentVersion\Run");
        if RegOpenKeyExW(HKEY_CURRENT_USER, path.as_ptr(), 0, KEY_READ, &mut key) != 0 {
            return false;
        }
        let result = RegQueryValueExW(
            key,
            wide("VN Presence Rust").as_ptr(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
            std::ptr::null_mut(),
        ) == 0;
        RegCloseKey(key);
        result
    }
}
fn set_startup(enabled: bool) -> Result<()> {
    use winapi::um::{
        winnt::{KEY_WRITE, REG_SZ},
        winreg::*,
    };
    let exe = std::env::current_exe().map_err(|e| e.to_string())?;
    unsafe {
        let mut key = std::ptr::null_mut();
        let code = RegCreateKeyExW(
            HKEY_CURRENT_USER,
            wide(r"Software\Microsoft\Windows\CurrentVersion\Run").as_ptr(),
            0,
            std::ptr::null_mut(),
            0,
            KEY_WRITE,
            std::ptr::null_mut(),
            &mut key,
            std::ptr::null_mut(),
        );
        if code != 0 {
            return Err(format!("Could not change startup setting ({code})."));
        }
        let name = wide("VN Presence Rust");
        let value = wide(&format!("\"{}\" --background", exe.display()));
        let code = if enabled {
            RegSetValueExW(
                key,
                name.as_ptr(),
                0,
                REG_SZ,
                value.as_ptr() as _,
                (value.len() * 2) as u32,
            )
        } else {
            RegDeleteValueW(key, name.as_ptr())
        };
        RegCloseKey(key);
        if code != 0 && code != 2 {
            Err(format!("Could not change startup setting ({code})."))
        } else {
            Ok(())
        }
    }
}

struct Details {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    mode: nwg::ComboBox<String>,
    custom: nwg::TextInput,
    remove: nwg::TextInput,
    prefix: nwg::TextInput,
    edges: nwg::TextInput,
    caption: nwg::TextBox,
    result: nwg::TextBox,
    save: nwg::Button,
    cancel: nwg::Button,
    _labels: Vec<nwg::Label>,
    game: RefCell<Game>,
    handler: RefCell<Option<nwg::EventHandler>>,
    app: std::rc::Weak<App>,
}
impl Drop for Details {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Details {
    fn build(app: &Rc<App>, game: Game) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Details")
            .size((700, 510))
            .position((300, 200))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let mut mode = nwg::ComboBox::default();
        nwg::ComboBox::builder()
            .parent(&window)
            .position((16, 16))
            .size((668, 90))
            .collection(vec!["Automatic".into(), "Custom".into()])
            .selected_index(Some(if game.custom_progress.is_some() { 1 } else { 0 }))
            .build(&mut mode)
            .unwrap();
        let custom = input(
            &window,
            game.custom_progress.as_deref().unwrap_or(""),
            (16, 52),
            (668, 28),
        );
        let remove = input(
            &window,
            game.remove_progress_text.as_deref().unwrap_or(""),
            (16, 115),
            (668, 28),
        );
        let prefix = input(
            &window,
            game.window_title_prefix.as_deref().unwrap_or(""),
            (16, 177),
            (668, 28),
        );
        let edges = input(
            &window,
            game.trim_edges.as_deref().unwrap_or(""),
            (16, 239),
            (668, 28),
        );
        let mut caption = nwg::TextBox::default();
        nwg::TextBox::builder()
            .parent(&window)
            .position((16, 306))
            .size((668, 58))
            .flags(
                nwg::TextBoxFlags::VISIBLE
                    | nwg::TextBoxFlags::VSCROLL
                    | nwg::TextBoxFlags::AUTOVSCROLL,
            )
            .readonly(true)
            .build(&mut caption)
            .unwrap();
        let mut result = nwg::TextBox::default();
        nwg::TextBox::builder()
            .parent(&window)
            .position((16, 397))
            .size((668, 58))
            .flags(
                nwg::TextBoxFlags::VISIBLE
                    | nwg::TextBoxFlags::VSCROLL
                    | nwg::TextBoxFlags::AUTOVSCROLL,
            )
            .readonly(true)
            .build(&mut result)
            .unwrap();
        let labels = vec![
            label(
                &window,
                "Remove text (separate phrases with ;)",
                (16, 90),
                (668, 24),
            ),
            label(
                &window,
                "Window title prefix (blank = VNDB titles)",
                (16, 152),
                (668, 24),
            ),
            label(
                &window,
                "Trim from edges (characters, e.g. - or ())",
                (16, 214),
                (668, 24),
            ),
            label(
                &window,
                "Current window title (select text to copy)",
                (16, 280),
                (668, 24),
            ),
            label(&window, "Result", (16, 372), (668, 24)),
        ];
        let save = button(&window, "Save", (510, 470), (80, 28));
        let cancel = button(&window, "Cancel", (602, 470), (80, 28));
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let d = Rc::new(Self {
            window,
            _theme: theme,
            mode,
            custom,
            remove,
            prefix,
            edges,
            caption,
            result,
            save,
            cancel,
            _labels: labels,
            game: RefCell::new(game),
            handler: RefCell::new(None),
            app: Rc::downgrade(app),
        });
        let weak = Rc::downgrade(&d);
        *d.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &d.window.handle,
            move |e, data, h| {
                if let Some(d) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            hide(&d.window);
                        }
                        nwg::Event::OnMinMaxInfo => data.on_min_max().set_min_size(620, 550),
                        nwg::Event::OnResize if h == d.window => d.layout(),
                        nwg::Event::OnTextInput | nwg::Event::OnComboxBoxSelection => {
                            if h == d.custom
                                || h == d.remove
                                || h == d.prefix
                                || h == d.edges
                                || h == d.mode
                            {
                                d.preview();
                            }
                        }
                        nwg::Event::OnKeyEsc => hide(&d.window),
                        nwg::Event::OnButtonClick if h == d.cancel => hide(&d.window),
                        nwg::Event::OnKeyEnter | nwg::Event::OnButtonClick
                            if e == nwg::Event::OnKeyEnter || h == d.save =>
                        {
                            d.read_fields();
                            if let Some(a) = d.app.upgrade() {
                                if a.replace_game(d.game.borrow().clone()) {
                                    hide(&d.window);
                                }
                            }
                        }
                        _ => {}
                    }
                }
            },
        ));
        d.preview();
        d
    }
    fn read_fields(&self) {
        let mut g = self.game.borrow_mut();
        g.custom_progress = (self.mode.selection() == Some(1)).then(|| self.custom.text());
        g.remove_progress_text = nonblank(&self.remove.text());
        g.window_title_prefix = nonblank(&self.prefix.text());
        g.trim_edges = nonblank(&self.edges.text());
    }
    fn preview(&self) {
        self.read_fields();
        let custom = self.mode.selection() == Some(1);
        self.custom.set_enabled(custom);
        for c in [&self.remove, &self.prefix, &self.edges] {
            c.set_enabled(!custom);
        }
        if let Some(a) = self.app.upgrade() {
            let s = a.state.borrow();
            let caption = s
                .active
                .as_ref()
                .filter(|(_, i)| s.settings.games[*i].novel.id == self.game.borrow().novel.id)
                .map(|(p, _)| p.caption.clone())
                .unwrap_or_default();
            drop(s);
            self.caption.set_text(if caption.is_empty() {
                "Game is not running"
            } else {
                &caption
            });
            let progress = self.game.borrow().progress(&caption).unwrap_or_default();
            self.result.set_text(&progress);
        }
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        let width = w.saturating_sub(32);
        for c in [&self.custom, &self.remove, &self.prefix, &self.edges] {
            c.set_size(width, 28);
        }
        self.mode.set_size(width, 90);
        self.caption.set_size(width, 58);
        self.result.set_size(width, h.saturating_sub(452));
        self.save.set_position(w as i32 - 190, h as i32 - 40);
        self.cancel.set_position(w as i32 - 98, h as i32 - 40);
    }
}

struct AddGame {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    processes: nwg::ListBox<String>,
    exe: nwg::TextInput,
    query: nwg::TextInput,
    results: nwg::ListBox<String>,
    browse: nwg::Button,
    refresh: nwg::Button,
    search: nwg::Button,
    link: nwg::Button,
    cancel: nwg::Button,
    status: nwg::Label,
    _labels: Vec<nwg::Label>,
    running: RefCell<Vec<Running>>,
    novels: RefCell<Vec<Novel>>,
    handler: RefCell<Option<nwg::EventHandler>>,
    app: std::rc::Weak<App>,
    last_search: Cell<Option<Instant>>,
}
impl Drop for AddGame {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl AddGame {
    fn build(app: &Rc<App>) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Add game")
            .size((840, 600))
            .position((260, 170))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let mut processes = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .position((16, 40))
            .size((808, 142))
            .build(&mut processes)
            .unwrap();
        let labels = vec![
            label(
                &window,
                "Running games — select yours or browse to its .exe",
                (16, 12),
                (650, 24),
            ),
            label(&window, "Executable", (16, 190), (110, 24)),
            label(&window, "VNDB title or URL", (16, 270), (300, 24)),
        ];
        let exe = input(&window, "", (16, 218), (628, 28));
        let browse = button(&window, "Browse", (658, 218), (80, 28));
        let refresh = button(&window, "Refresh", (750, 218), (74, 28));
        let query = input(&window, "", (16, 300), (710, 28));
        let search = button(&window, "Search", (740, 300), (84, 28));
        let mut results = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .position((16, 344))
            .size((808, 192))
            .build(&mut results)
            .unwrap();
        let status = label(&window, "", (16, 556), (600, 28));
        let link = button(&window, "Link", (652, 554), (80, 28));
        let cancel = button(&window, "Cancel", (744, 554), (80, 28));
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let a = Rc::new(Self {
            window,
            _theme: theme,
            processes,
            exe,
            query,
            results,
            browse,
            refresh,
            search,
            link,
            cancel,
            status,
            _labels: labels,
            running: RefCell::new(vec![]),
            novels: RefCell::new(vec![]),
            handler: RefCell::new(None),
            app: Rc::downgrade(app),
            last_search: Cell::new(None),
        });
        let weak = Rc::downgrade(&a);
        *a.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &a.window.handle,
            move |e, data, h| {
                if let Some(a) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            hide(&a.window);
                        }
                        nwg::Event::OnMinMaxInfo => data.on_min_max().set_min_size(750, 640),
                        nwg::Event::OnResize if h == a.window => a.layout(),
                        nwg::Event::OnListBoxSelect if h == a.processes => {
                            if let Some(p) = a
                                .processes
                                .selection()
                                .and_then(|i| a.running.borrow().get(i).cloned())
                            {
                                a.exe.set_text(&p.exe);
                                a.query.set_text(&net::search_title(&p.caption));
                            }
                        }
                        nwg::Event::OnKeyEsc => hide(&a.window),
                        nwg::Event::OnButtonClick if h == a.cancel => hide(&a.window),
                        nwg::Event::OnButtonClick if h == a.refresh => a.scan(),
                        nwg::Event::OnButtonClick if h == a.browse => {
                            if let Some(p) = file_dialog(
                                &a.window,
                                "Choose game executable",
                                "Executable (*.exe)|*.exe",
                                false,
                            ) {
                                a.exe.set_text(&p.to_string_lossy());
                            }
                        }
                        nwg::Event::OnButtonClick if h == a.search => a.search(),
                        nwg::Event::OnKeyEnter
                            if h == a.window
                                && unsafe { GetFocus() == a.query.handle.hwnd().unwrap() } =>
                        {
                            a.search()
                        }
                        nwg::Event::OnButtonClick if h == a.link => a.link(),
                        _ => {}
                    }
                }
            },
        ));
        a.scan();
        a
    }
    fn scan(&self) {
        match scan(None) {
            Ok(p) => {
                self.processes.set_collection(
                    p.iter()
                        .map(|p| {
                            format!(
                                "{}  [{}]",
                                p.caption,
                                PathBuf::from(&p.exe)
                                    .file_name()
                                    .unwrap_or_default()
                                    .to_string_lossy()
                            )
                        })
                        .collect(),
                );
                *self.running.borrow_mut() = p;
            }
            Err(e) => self.status.set_text(&e),
        }
    }
    fn search(&self) {
        if self
            .last_search
            .get()
            .is_some_and(|t| t.elapsed() < Duration::from_secs(3))
        {
            self.status
                .set_text("Wait a few seconds before searching again.");
            return;
        }
        let q = self.query.text();
        if q.trim().is_empty() {
            return;
        }
        let generation = Instant::now();
        self.last_search.set(Some(generation));
        self.search.set_enabled(false);
        self.status.set_text("Searching VNDB…");
        if let Some(a) = self.app.upgrade() {
            a.job(move || Update::Search(generation, net::search(&q)));
        }
    }
    fn search_done(&self, r: Result<Vec<Novel>>) {
        self.search.set_enabled(true);
        match r {
            Ok(v) => {
                self.results.set_collection(
                    v.iter()
                        .map(|n| format!("{} · {} ({})", n.native_title(), n.title, n.id))
                        .collect(),
                );
                self.status.set_text(if v.is_empty() {
                    "No results."
                } else {
                    "Select a result, then Link."
                });
                *self.novels.borrow_mut() = v;
            }
            Err(e) => self.status.set_text(&e),
        }
    }
    fn link(&self) {
        let exe = self.exe.text();
        if !valid_exe_path(&exe) {
            self.status.set_text("Choose a game executable.");
            return;
        }
        let Some(n) = self
            .results
            .selection()
            .and_then(|i| self.novels.borrow().get(i).cloned())
        else {
            self.status.set_text("Select a VNDB result.");
            return;
        };
        if let Some(a) = self.app.upgrade() {
            let mut s = a.state.borrow_mut();
            if s.settings
                .games
                .iter()
                .any(|g| same_exe(&g.exe, &exe) || g.novel.id == n.id)
            {
                drop(s);
                self.status
                    .set_text("This game or executable is already linked.");
                return;
            }
            let id = n.id.clone();
            s.settings.games.push(Game {
                exe,
                novel: n,
                ..Default::default()
            });
            drop(s);
            if a.persist() {
                a.refresh_library(Some(id));
                a.send_state();
                hide(&self.window);
            } else {
                a.state.borrow_mut().settings.games.pop();
            }
        }
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        self.processes.set_size(w.saturating_sub(32), 142);
        self.exe.set_size(w.saturating_sub(212), 28);
        self.browse.set_position(w as i32 - 182, 218);
        self.refresh.set_position(w as i32 - 90, 218);
        self.query.set_size(w.saturating_sub(130), 28);
        self.search.set_position(w as i32 - 100, 300);
        self.results
            .set_size(w.saturating_sub(32), h.saturating_sub(408));
        self.status.set_position(16, h as i32 - 44);
        self.link.set_position(w as i32 - 188, h as i32 - 46);
        self.cancel.set_position(w as i32 - 96, h as i32 - 46);
    }
}

struct Token {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    token: nwg::TextInput,
    create: nwg::Button,
    save: nwg::Button,
    cancel: nwg::Button,
    status: nwg::Label,
    _labels: Vec<nwg::Label>,
    handler: RefCell<Option<nwg::EventHandler>>,
}
impl Drop for Token {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Token {
    fn build(app: &Rc<App>) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Connect VNDB")
            .flags(nwg::WindowFlags::WINDOW | nwg::WindowFlags::VISIBLE)
            .size((540, 245))
            .position((350, 250))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let labels=vec![label(&window,"Create a token and enable both permissions:\r\nAccess private items on my list\r\nAdd/remove/edit items on my list",(16,16),(508,66))];
        let create = button(&window, "Create token on VNDB", (16, 90), (190, 28));
        let token = input(&window, "", (16, 134), (508, 28));
        token.set_password_char(Some('●'));
        let status = label(&window, "", (16, 172), (508, 28));
        let save = button(&window, "Connect", (334, 204), (90, 28));
        let cancel = button(&window, "Cancel", (434, 204), (90, 28));
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let t = Rc::new(Self {
            window,
            _theme: theme,
            token,
            create,
            save,
            cancel,
            status,
            _labels: labels,
            handler: RefCell::new(None),
        });
        let weak = Rc::downgrade(&t);
        let parent = Rc::downgrade(app);
        *t.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &t.window.handle,
            move |e, data, h| {
                if let Some(t) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            hide(&t.window);
                        }
                        nwg::Event::OnButtonClick if h == t.create => {
                            open_url("https://vndb.org/u/tokens")
                        }
                        nwg::Event::OnKeyEsc => hide(&t.window),
                        nwg::Event::OnButtonClick if h == t.cancel => hide(&t.window),
                        nwg::Event::OnKeyEnter | nwg::Event::OnButtonClick
                            if e == nwg::Event::OnKeyEnter || h == t.save =>
                        {
                            let token = t.token.text().trim().to_owned();
                            if !net::valid_token(&token) {
                                t.status.set_text("Paste a valid VNDB token.");
                                return;
                            }
                            if let Some(a) = parent.upgrade() {
                                t.save.set_enabled(false);
                                t.status.set_text("Connecting…");
                                a.connect_token(token, true);
                            }
                        }
                        _ => {}
                    }
                }
            },
        ));
        t
    }
}

struct Vote {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    score: nwg::TextInput,
    list: nwg::ListBox<String>,
    remove: nwg::Button,
    cancel: nwg::Button,
    _labels: Vec<nwg::Label>,
    handler: RefCell<Option<nwg::EventHandler>>,
}
impl Drop for Vote {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Vote {
    fn build(app: &Rc<App>) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Vote")
            .flags(nwg::WindowFlags::WINDOW | nwg::WindowFlags::VISIBLE)
            .size((260, 355))
            .position((650, 330))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let score = input(&window, "", (16, 14), (228, 28));
        let labels = vec![label(
            &window,
            "Type 1.0–10.0 and press Enter",
            (16, 48),
            (228, 24),
        )];
        let mut list = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .position((16, 80))
            .size((228, 218))
            .collection(
                [
                    "10 (masterpiece)",
                    "9 (excellent)",
                    "8 (very good)",
                    "7 (good)",
                    "6 (decent)",
                    "5 (so-so)",
                    "4 (weak)",
                    "3 (bad)",
                    "2 (awful)",
                    "1 (worst ever)",
                ]
                .iter()
                .map(|s| s.to_string())
                .collect(),
            )
            .build(&mut list)
            .unwrap();
        let remove = button(&window, "Remove vote", (16, 314), (118, 28));
        remove.set_enabled(
            app.state
                .borrow()
                .draft
                .as_ref()
                .is_some_and(|e| e.vote.is_some()),
        );
        let cancel = button(&window, "Cancel", (146, 314), (98, 28));
        let item_height =
            unsafe { SendMessageW(list.handle.hwnd().unwrap(), LB_GETITEMHEIGHT, 0, 0) } as f64
                / nwg::scale_factor();
        let height = (item_height * 10.).ceil() as u32 + 4;
        list.set_size(228, height);
        remove.set_position(16, height as i32 + 96);
        cancel.set_position(146, height as i32 + 96);
        window.set_size(260, height + 140);
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let v = Rc::new(Self {
            window,
            _theme: theme,
            score,
            list,
            remove,
            cancel,
            _labels: labels,
            handler: RefCell::new(None),
        });
        let weak = Rc::downgrade(&v);
        let parent = Rc::downgrade(app);
        *v.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &v.window.handle,
            move |e, data, h| {
                if let Some(v) = weak.upgrade() {
                    if e == nwg::Event::OnKeyEsc {
                        hide(&v.window);
                    } else if e == nwg::Event::OnWindowClose {
                        keep_open(&data);
                        hide(&v.window);
                    } else if e == nwg::Event::OnButtonClick && h == v.cancel {
                        hide(&v.window);
                    } else {
                        let vote = if e == nwg::Event::OnListBoxSelect && h == v.list {
                            v.list.selection().map(|i| Some(100 - i as i32 * 10))
                        } else if e == nwg::Event::OnKeyEnter
                            && unsafe { GetFocus() == v.list.handle.hwnd().unwrap() }
                        {
                            v.list.selection().map(|i| Some(100 - i as i32 * 10))
                        } else if e == nwg::Event::OnButtonClick && h == v.remove {
                            Some(None)
                        } else if e == nwg::Event::OnKeyEnter
                            && h == v.window
                            && unsafe { GetFocus() == v.score.handle.hwnd().unwrap() }
                        {
                            match v.score.text().trim().parse::<f64>() {
                                Ok(s) if s.is_finite() && (1.0..=10.0).contains(&s) => {
                                    Some(Some((s * 10.0).round() as i32))
                                }
                                _ => {
                                    error(&v.window, "Enter a vote from 1.0 to 10.0.");
                                    None
                                }
                            }
                        } else {
                            None
                        };
                        if let (Some(vote), Some(a)) = (vote, parent.upgrade()) {
                            let entry = a.state.borrow().draft.clone();
                            if let Some(mut entry) = entry {
                                entry.vote = vote;
                                a.vote.set_text(
                                    &vote
                                        .map(|v| format!("{:.1}", v as f64 / 10.0))
                                        .unwrap_or_else(|| "Vote".into()),
                                );
                                a.queue_entry(entry);
                                hide(&v.window);
                            }
                        }
                    }
                }
            },
        ));
        v
    }
}

// ponytail: WM_TIMER runs on the UI thread; no 1 ms animation worker for a one-second debounce.
#[allow(deprecated)]
struct Notes {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    text: nwg::TextBox,
    status: nwg::Label,
    save_close: nwg::Button,
    discard: nwg::Button,
    timer: nwg::Timer,
    notice: nwg::Notice,
    _labels: Vec<nwg::Label>,
    saved: RefCell<String>,
    saving: Cell<bool>,
    close_pending: Cell<bool>,
    rx: Receiver<(String, Result<()>)>,
    tx: Sender<(String, Result<()>)>,
    account: Account,
    id: String,
    handler: RefCell<Option<nwg::EventHandler>>,
    app: std::rc::Weak<App>,
}
impl Drop for Notes {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
#[allow(deprecated)]
impl Notes {
    fn build(app: &Rc<App>, account: Account, id: String, initial: String) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("VNDB notes")
            .size((680, 420))
            .position((350, 230))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let labels = vec![label(
            &window,
            "Saves automatically to VNDB · Notes follow your VNDB list privacy",
            (16, 12),
            (648, 28),
        )];
        let mut text = nwg::TextBox::default();
        nwg::TextBox::builder()
            .parent(&window)
            .text(&initial)
            .position((16, 48))
            .size((648, 312))
            .limit(50000)
            .flags(
                nwg::TextBoxFlags::VISIBLE
                    | nwg::TextBoxFlags::TAB_STOP
                    | nwg::TextBoxFlags::VSCROLL
                    | nwg::TextBoxFlags::AUTOVSCROLL,
            )
            .build(&mut text)
            .unwrap();
        let status = label(&window, "", (16, 380), (360, 28));
        let save_close = button(&window, "Save and close", (516, 378), (148, 28));
        let discard = button(&window, "Close without saving", (332, 378), (174, 28));
        discard.set_visible(false);
        let mut timer = nwg::Timer::default();
        nwg::Timer::builder()
            .parent(&window)
            .interval(1000)
            .stopped(true)
            .build(&mut timer)
            .unwrap();
        let notice = nwg::Notice::create(&window).unwrap();
        let (tx, rx) = mpsc::channel();
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let n = Rc::new(Self {
            window,
            _theme: theme,
            text,
            status,
            save_close,
            discard,
            timer,
            notice,
            _labels: labels,
            saved: RefCell::new(initial.replace("\r\n", "\n")),
            saving: Cell::new(false),
            close_pending: Cell::new(false),
            rx,
            tx,
            account,
            id,
            handler: RefCell::new(None),
            app: Rc::downgrade(app),
        });
        let weak = Rc::downgrade(&n);
        *n.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &n.window.handle,
            move |e, data, h| {
                if let Some(n) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            n.close_pending.set(true);
                            n.save();
                        }
                        nwg::Event::OnKeyEsc => {
                            n.close_pending.set(true);
                            n.save();
                        }
                        nwg::Event::OnButtonClick if h == n.save_close => {
                            n.close_pending.set(true);
                            n.save();
                        }
                        nwg::Event::OnButtonClick if h == n.discard && !n.saving.get() => {
                            n.text.set_text(&n.saved.borrow());
                            hide(&n.window);
                        }
                        nwg::Event::OnTextInput if h == n.text => {
                            n.timer.stop();
                            n.timer.start();
                        }
                        nwg::Event::OnTimerTick if h == n.timer => {
                            n.timer.stop();
                            n.save();
                        }
                        nwg::Event::OnNotice if h == n.notice => n.done(),
                        nwg::Event::OnMinMaxInfo => data.on_min_max().set_min_size(600, 380),
                        nwg::Event::OnResize if h == n.window => n.layout(),
                        _ => {}
                    }
                }
            },
        ));
        n
    }
    fn dirty(&self) -> bool {
        self.saving.get() || self.text.text().replace("\r\n", "\n") != *self.saved.borrow()
    }
    fn save(&self) {
        self.timer.stop();
        if self.saving.get() {
            return;
        }
        let text = self.text.text().replace("\r\n", "\n");
        if text == *self.saved.borrow() {
            if self.close_pending.replace(false) {
                hide(&self.window);
            }
            return;
        }
        self.saving.set(true);
        self.status.set_text("Saving to VNDB…");
        self.discard.set_visible(false);
        if self.close_pending.get() {
            self.text.set_readonly(true);
            self.save_close.set_enabled(false);
        }
        let a = self.account.clone();
        let id = self.id.clone();
        let tx = self.tx.clone();
        let notice = self.notice.sender();
        thread::spawn(move || {
            let result = a.save_notes(&id, &text);
            let _ = tx.send((text, result));
            notice.notice();
        });
    }
    fn done(&self) {
        while let Ok((text, result)) = self.rx.try_recv() {
            self.saving.set(false);
            self.text.set_readonly(false);
            self.save_close.set_enabled(true);
            match result {
                Ok(()) => {
                    if let Some(app) = self.app.upgrade() {
                        let mut s = app.state.borrow_mut();
                        if s.entry.as_ref().is_some_and(|(id, _)| id == &self.id) {
                            if let Some((_, e)) = &mut s.entry {
                                e.notes = text.clone();
                            }
                            if let Some(e) = &mut s.draft {
                                e.notes = text.clone();
                            }
                        }
                    }
                    *self.saved.borrow_mut() = text;
                    self.status.set_text("Saved to VNDB.");
                    if self.dirty() {
                        self.save();
                    } else if self.close_pending.replace(false) {
                        hide(&self.window);
                    }
                }
                Err(e) => {
                    self.close_pending.set(false);
                    self.status.set_text(&format!("Not saved: {e}"));
                    self.discard.set_visible(true);
                }
            }
        }
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        self.text
            .set_size(w.saturating_sub(32), h.saturating_sub(108));
        self.status.set_position(16, h as i32 - 40);
        self.save_close.set_position(w as i32 - 164, h as i32 - 42);
        self.discard.set_position(w as i32 - 348, h as i32 - 42);
    }
}

#[derive(serde::Deserialize)]
struct GuidePage {
    title: String,
    body: String,
}
struct Guide {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    sections: nwg::ListBox<String>,
    heading: nwg::Label,
    body: nwg::TextBox,
    back: nwg::Button,
    next: nwg::Button,
    close: nwg::Button,
    pages: Vec<GuidePage>,
    handler: RefCell<Option<nwg::EventHandler>>,
}
impl Drop for Guide {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Guide {
    fn build(app: &Rc<App>) -> Rc<Self> {
        let pages: Vec<GuidePage> = serde_json::from_str(include_str!("../guide.json")).unwrap();
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("VN Presence guide")
            .size((760, 500))
            .position((300, 200))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let mut sections = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .position((16, 16))
            .size((196, 424))
            .collection(pages.iter().map(|p| p.title.clone()).collect())
            .build(&mut sections)
            .unwrap();
        let heading = label(&window, "", (230, 16), (514, 32));
        let mut body = nwg::TextBox::default();
        nwg::TextBox::builder()
            .parent(&window)
            .position((230, 54))
            .size((514, 386))
            .flags(
                nwg::TextBoxFlags::VISIBLE
                    | nwg::TextBoxFlags::VSCROLL
                    | nwg::TextBoxFlags::AUTOVSCROLL
                    | nwg::TextBoxFlags::TAB_STOP,
            )
            .readonly(true)
            .build(&mut body)
            .unwrap();
        let back = button(&window, "Back", (474, 458), (80, 28));
        let next = button(&window, "Next", (566, 458), (80, 28));
        let close = button(&window, "Close", (658, 458), (86, 28));
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let g = Rc::new(Self {
            window,
            _theme: theme,
            sections,
            heading,
            body,
            back,
            next,
            close,
            pages,
            handler: RefCell::new(None),
        });
        let weak = Rc::downgrade(&g);
        *g.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &g.window.handle,
            move |e, data, h| {
                if let Some(g) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            hide(&g.window);
                        }
                        nwg::Event::OnListBoxSelect if h == g.sections => g.select(),
                        nwg::Event::OnButtonClick if h == g.back => {
                            if let Some(i) = g.sections.selection() {
                                g.sections.set_selection(Some(i.saturating_sub(1)));
                                g.select();
                            }
                        }
                        nwg::Event::OnButtonClick if h == g.next => {
                            if let Some(i) = g.sections.selection() {
                                if i + 1 == g.pages.len() {
                                    hide(&g.window);
                                } else {
                                    g.sections.set_selection(Some(i + 1));
                                    g.select();
                                }
                            }
                        }
                        nwg::Event::OnKeyEsc => hide(&g.window),
                        nwg::Event::OnButtonClick if h == g.close => hide(&g.window),
                        nwg::Event::OnMinMaxInfo => data.on_min_max().set_min_size(700, 470),
                        nwg::Event::OnResize if h == g.window => g.layout(),
                        _ => {}
                    }
                }
            },
        ));
        g.sections.set_selection(Some(0));
        g.select();
        g
    }
    fn select(&self) {
        if let Some(i) = self.sections.selection() {
            let p = &self.pages[i];
            self.heading
                .set_text(&format!("{} / {} — {}", i + 1, self.pages.len(), p.title));
            self.body.set_text(&p.body.replace('\n', "\r\n"));
            self.back.set_enabled(i > 0);
            self.next.set_text(if i + 1 == self.pages.len() {
                "Done"
            } else {
                "Next"
            });
        }
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        self.sections.set_size(196, h.saturating_sub(76));
        self.heading.set_size(w.saturating_sub(246), 32);
        self.body
            .set_size(w.saturating_sub(246), h.saturating_sub(114));
        self.back.set_position(w as i32 - 286, h as i32 - 42);
        self.next.set_position(w as i32 - 194, h as i32 - 42);
        self.close.set_position(w as i32 - 102, h as i32 - 42);
    }
}

pub fn run(settings: Settings, path: PathBuf, readonly: bool, background: bool) -> Result<()> {
    let _app = App::build(settings, path, readonly, background)?;
    nwg::dispatch_thread_events();
    Ok(())
}

pub fn self_test(mut settings: Settings, out: PathBuf) -> Result<()> {
    TEST_URLS.with(|urls| *urls.borrow_mut() = Some(vec![]));
    std::fs::create_dir_all(&out).map_err(|e| e.to_string())?;
    settings.vndb_token_protected = None;
    settings.cover_owner_protected = None;
    if settings.games.is_empty() {
        settings.games.push(Game {
            exe: r"C:\game\game.exe".into(),
            novel: Novel {
                id: "v1".into(),
                title: "HimaNatsu".into(),
                alttitle: Some("向日葵の教会と長い夏休み".into()),
                ..Default::default()
            },
            ..Default::default()
        });
    }
    let app = App::build(settings, out.join("test-settings.json"), true, false)?;
    pump(Duration::from_secs(2));
    #[link(name = "user32")]
    extern "system" {
        fn GetGuiResources(process: winapi::shared::ntdef::HANDLE, flags: u32) -> u32;
    }
    let before = unsafe { GetGuiResources(winapi::um::processthreadsapi::GetCurrentProcess(), 0) };
    for _ in 0..40 {
        app.layout();
    }
    let after = unsafe { GetGuiResources(winapi::um::processthreadsapi::GetCurrentProcess(), 0) };
    std::fs::write(
        out.join("resources.json"),
        serde_json::json!({"gdi_before":before,"gdi_after":after,"layouts":40}).to_string(),
    )
    .map_err(|e| e.to_string())?;
    assert!(
        after <= before + 4,
        "Repeated layout leaked drawing resources"
    );
    let ids = app
        .state
        .borrow()
        .settings
        .games
        .iter()
        .enumerate()
        .map(|(i, g)| (i, g.novel.id.clone()))
        .collect::<Vec<_>>();
    for (i, _) in ids {
        let n = app.state.borrow().order.iter().position(|&x| x == i);
        app.library.set_selection(n);
        app.select_game();
    }
    app.window.set_size(1240, 760);
    app.layout();
    assert!(app.library.size().0 >= 258);
    let game = app.selected().unwrap();
    let mut edited = game.clone();
    let current = now();
    app.state
        .borrow_mut()
        .settings
        .games
        .iter_mut()
        .find(|g| g.novel.id == game.novel.id)
        .unwrap()
        .last_played = current;
    edited.last_played = 0;
    assert!(app.replace_game(edited));
    assert_eq!(
        app.selected().unwrap().last_played,
        current,
        "Saving a dialog lost the latest play time"
    );
    let original_sort = app.state.borrow().settings.game_sort_order.clone();
    app.state.borrow_mut().settings.game_sort_order = "Last played".into();
    app.state.borrow_mut().active = None;
    let _ = app.tx.send(Update::Poll(
        Some((
            Running {
                pid: u32::MAX,
                started: current,
                ..Default::default()
            },
            game.novel.id.clone(),
        )),
        "Test game detection".into(),
    ));
    app.updates();
    assert_eq!(
        app.state.borrow().order,
        app.state.borrow().settings.sorted_indices(),
        "Last played order did not update"
    );
    assert_eq!(app.selected().unwrap().novel.id, game.novel.id);
    app.state.borrow_mut().settings.game_sort_order = original_sort;
    app.state.borrow_mut().active = None;
    let add = AddGame::build(&app);
    if let Some(process) = add.running.borrow().first().cloned() {
        let mut linked = game.clone();
        linked.exe = process.exe.clone();
        let detected = scan(Some(std::slice::from_ref(&linked)))?;
        let selected = choose(&detected, &[linked], process.pid, process.pid)
            .ok_or("Linked running process was not detected")?;
        assert_eq!(selected.0.pid, process.pid);
        assert!(selected.0.started > 0 && selected.0.started <= now());
    }
    add.window.set_size(940, 640);
    add.layout();
    pump(Duration::from_millis(200));
    for field in [&add.exe, &add.query] {
        let mut rect = unsafe { std::mem::zeroed() };
        unsafe {
            GetClientRect(field.handle.hwnd().unwrap(), &mut rect);
        }
        if !field.visible() || rect.bottom - rect.top < 12 {
            return Err(format!(
                "Add game input has no usable client area: {:?}",
                (rect.left, rect.top, rect.right, rect.bottom)
            ));
        }
    }
    for ch in "CLANNAD".encode_utf16() {
        unsafe {
            SendMessageW(add.query.handle.hwnd().unwrap(), WM_CHAR, ch as usize, 0);
        }
    }
    assert_eq!(add.query.text(), "CLANNAD");
    add.exe.set_text(&game.exe);
    add.search_done(Ok(vec![game.novel.clone()]));
    add.results.set_selection(Some(0));
    assert_eq!(add.results.collection().len(), 1);
    assert!(add.results.selection_string().is_some());
    *app.add_form.borrow_mut() = Some(add.clone());
    let generation = Instant::now();
    add.last_search.set(Some(generation));
    let _ = app.tx.send(Update::Search(
        generation - Duration::from_secs(1),
        Ok(vec![]),
    ));
    app.updates();
    assert_eq!(
        add.results.collection().len(),
        1,
        "Stale search replaced current results"
    );
    crate::images::capture(add.window.handle.hwnd().unwrap(), &out.join("add-game.png"))?;
    click(&add.link);
    assert!(add.window.visible());
    assert_eq!(
        add.status.text(),
        "This game or executable is already linked."
    );
    let original_games = app.state.borrow().settings.games.clone();
    let mut linked_novel = game.novel.clone();
    linked_novel.id = "v999999999".into();
    add.exe.set_text(r"C:\VN-Presence-Test\game.exe");
    add.search_done(Ok(vec![linked_novel]));
    add.results.set_selection(Some(0));
    click(&add.link);
    assert!(!add.window.visible() && app.window.enabled());
    assert_eq!(app.selected().unwrap().novel.id, "v999999999");
    app.state.borrow_mut().settings.games = original_games;
    app.state.borrow_mut().selected = None;
    app.refresh_library(Some(game.novel.id.clone()));
    add.window.set_visible(true);
    modal(&add.window, &app.window);
    click(&add.cancel);
    assert!(!add.window.visible() && app.window.enabled());
    if game.novel.image.is_some() {
        ui_cover::check_layout(&app, game.clone(), &out)?;
    }
    let d = Details::build(&app, game.clone());
    d.window.set_size(840, 600);
    d.layout();
    d.mode.set_selection(Some(1));
    d.custom.set_text(&"日本語の長いタイトル".repeat(12));
    d.preview();
    assert_eq!(d.result.text(), "日本語の長いタイトル".repeat(12));
    crate::images::capture(d.window.handle.hwnd().unwrap(), &out.join("details.png"))?;
    click(&d.cancel);
    assert!(!d.window.visible() && app.window.enabled());
    assert_eq!(
        app.selected().unwrap().custom_progress,
        game.custom_progress
    );
    let saved_details = Details::build(&app, game.clone());
    saved_details.mode.set_selection(Some(1));
    saved_details.custom.set_text("Chapter check");
    click(&saved_details.save);
    assert!(!saved_details.window.visible() && app.window.enabled());
    assert_eq!(
        app.selected().unwrap().custom_progress.as_deref(),
        Some("Chapter check")
    );
    assert!(app.replace_game(game.clone()));
    let v = Vote::build(&app);
    assert_eq!(v.list.collection().len(), 10);
    crate::images::capture(v.window.handle.hwnd().unwrap(), &out.join("vote.png"))?;
    click(&v.cancel);
    assert!(!v.window.visible() && app.window.enabled());
    let escaped_vote = Vote::build(&app);
    escape(&escaped_vote.window, escaped_vote.score.handle);
    assert!(!escaped_vote.window.visible() && app.window.enabled());
    let g = Guide::build(&app);
    for i in 0..g.pages.len() {
        g.sections.set_selection(Some(i));
        g.select();
        assert!(!g.body.text().is_empty());
    }
    click(&g.close);
    assert!(!g.window.visible() && app.window.enabled());
    let token = Token::build(&app);
    assert_eq!(
        unsafe { GetWindowLongW(token.window.handle.hwnd().unwrap(), GWL_STYLE) } as u32
            & WS_THICKFRAME,
        0
    );
    assert!(token.token.visible());
    click(&token.cancel);
    assert!(!token.window.visible() && app.window.enabled());
    let escaped_token = Token::build(&app);
    escape(&escaped_token.window, escaped_token.token.handle);
    assert!(!escaped_token.window.visible() && app.window.enabled());
    let escaped_details = Details::build(&app, game.clone());
    escape(&escaped_details.window, escaped_details.custom.handle);
    assert!(!escaped_details.window.visible() && app.window.enabled());
    let escaped_add = AddGame::build(&app);
    escape(&escaped_add.window, escaped_add.query.handle);
    assert!(!escaped_add.window.visible() && app.window.enabled());
    let escaped_guide = Guide::build(&app);
    escape(&escaped_guide.window, escaped_guide.sections.handle);
    assert!(!escaped_guide.window.visible() && app.window.enabled());
    app.sort.set_selection(Some(
        match app.state.borrow().settings.game_sort_order.as_str() {
            "Alphabetical" => 1,
            "Last played" => 2,
            _ => 0,
        },
    ));
    assert!(app.sort.selection_string().is_some());
    let fake = Account {
        token: "y".repeat(32),
        id: "u1".into(),
        username: "den-pa".into(),
        labels: [
            "Playing",
            "Finished",
            "Stalled",
            "Dropped",
            "Wishlist",
            "Blacklist",
        ]
        .iter()
        .enumerate()
        .map(|(i, n)| net::Label {
            id: i as i32 + 1,
            name: n.to_string(),
            private: false,
        })
        .collect(),
    };
    app.state.borrow_mut().account = Some(fake.clone());
    app.rebuild_labels();
    let e = Entry {
        vote: Some(80),
        labels: vec![1],
        notes: String::new(),
    };
    {
        let mut s = app.state.borrow_mut();
        s.entry = Some((game.novel.id.clone(), e.clone()));
        let mut draft = e.clone();
        draft.vote = Some(90);
        s.draft = Some(draft.clone());
        s.saving = true;
        let _ = app.tx.send(Update::Saved(
            game.novel.id.clone(),
            draft,
            Err("Test network failure".into()),
        ));
    }
    app.updates();
    assert!(app.entry_unsaved(), "Failed VNDB save lost its draft");
    assert_eq!(app.state.borrow().entry.as_ref().unwrap().1.vote, Some(80));
    {
        let mut s = app.state.borrow_mut();
        s.entry.as_mut().unwrap().1.notes = "new notes".into();
        s.draft.as_mut().unwrap().notes = "new notes".into();
        s.saving = true;
        let mut saved = e.clone();
        saved.vote = Some(90);
        let _ = app
            .tx
            .send(Update::Saved(game.novel.id.clone(), saved, Ok(())));
    }
    app.updates();
    assert_eq!(
        app.state.borrow().entry.as_ref().unwrap().1.notes,
        "new notes",
        "Vote save replaced newer notes"
    );
    assert!(!app.entry_unsaved());
    app.state.borrow_mut().entry = Some((game.novel.id.clone(), e.clone()));
    app.state.borrow_mut().draft = Some(e.clone());
    assert!(!app.entry_unsaved());
    app.state.borrow_mut().selected = None;
    *app.image_url.borrow_mut() = "stale cover".into();
    app.refresh_preview();
    assert!(app.image_url.borrow().is_empty());
    app.refresh_library(Some(game.novel.id.clone()));
    assert!(
        app.state.borrow().entry.is_none(),
        "Library replacement retained old VNDB entry"
    );
    assert!(!app.vote.enabled());
    let games = std::mem::take(&mut app.state.borrow_mut().settings.games);
    {
        let mut s = app.state.borrow_mut();
        s.entry = Some((game.novel.id.clone(), e.clone()));
        s.draft = Some(e.clone());
        s.selected = None;
    }
    app.refresh_library(None);
    assert!(
        app.state.borrow().entry.is_none(),
        "Removing the last game retained its VNDB controls"
    );
    assert!(!app.vote.enabled() && !app.notes.enabled());
    app.state.borrow_mut().settings.games = games;
    app.refresh_library(Some(game.novel.id.clone()));
    app.show_entry(&e);
    app.connection.set_text("Connected as den-pa");
    app.save_status.set_text("Saved to VNDB.");
    app.state.borrow_mut().settings.profile_url = Some("https://vndb.org/u1".into());
    app.state.borrow_mut().settings.show_profile_button = true;
    app.refresh_preview();
    assert_eq!(app.title.position().1, app.art.position().1);
    assert_eq!(app.brand.position().1, app.title.position().1 + 38);
    assert!(!app.details_preview.visible() && !app.elapsed.visible());
    click(&app.game_button);
    click(&app.profile_button);
    TEST_URLS.with(|urls| {
        let urls = urls.borrow();
        let urls = urls.as_ref().unwrap();
        assert!(
            urls.contains(&game.novel.url()),
            "Game button did not open its URL"
        );
        assert!(
            urls.iter().any(|url| url == "https://vndb.org/u1"),
            "Profile button did not open its URL"
        );
    });
    assert_eq!(app.labels.borrow().len(), 6);
    let last = app.labels.borrow()[5].position().1;
    assert!(last + 24 <= app.vote.position().1);
    let n = Notes::build(&app, fake, game.novel.id.clone(), "日本語のメモ".into());
    assert!(!n.dirty());
    n.close_pending.set(true);
    n.save();
    assert!(!n.window.visible());
    assert!(!n.saving.get());
    pump(Duration::from_secs(2));
    assert!(
        app.sort.selection_string().is_some(),
        "Sort selection disappeared"
    );
    crate::images::capture(app.window.handle.hwnd().unwrap(), &out.join("overview.png"))?;
    hide(&app.window);
    drop(n);
    drop(v);
    drop(d);
    app.shutdown();
    pump(Duration::from_millis(200));
    assert!(!out.join("test-settings.json").exists());
    std::fs::write(out.join("result.txt"),"PASS: Add game fields, typing and results; stale searches, failed save drafts, entry reset, cover reset; library, resizing, Japanese text, vote rows, labels fit, notes save-and-close, preview writes nothing\n").map_err(|e|e.to_string())?;
    Ok(())
}
fn click(button: &nwg::Button) {
    click_control(button.handle);
}
fn click_control(control: nwg::ControlHandle) {
    unsafe {
        let hwnd = control.hwnd().unwrap();
        SetForegroundWindow(GetAncestor(hwnd, GA_ROOT));
        SendMessageW(hwnd, BM_CLICK, 0, 0);
    }
}
fn escape(window: &nwg::Window, focus: nwg::ControlHandle) {
    window.set_focus();
    unsafe {
        SetFocus(focus.hwnd().unwrap());
        PostMessageW(focus.hwnd().unwrap(), WM_KEYDOWN, VK_ESCAPE as usize, 0);
    }
    pump(Duration::from_millis(100));
}
fn pump(duration: Duration) {
    let end = Instant::now() + duration;
    while Instant::now() < end {
        unsafe {
            let mut msg = std::mem::zeroed();
            while PeekMessageW(&mut msg, std::ptr::null_mut(), 0, 0, PM_REMOVE) != 0 {
                if msg.message != WM_QUIT {
                    if IsDialogMessageW(GetAncestor(msg.hwnd, GA_ROOT), &mut msg) == 0 {
                        TranslateMessage(&msg);
                        DispatchMessageW(&msg);
                    }
                }
            }
        }
        thread::sleep(Duration::from_millis(10));
    }
}
#[cfg(test)]
pub fn notes_check(account: Account) {
    let mut settings = Settings::default();
    settings.games.push(Game {
        exe: r"C:\game\game.exe".into(),
        novel: Novel {
            id: "v1".into(),
            title: "Test".into(),
            ..Default::default()
        },
        ..Default::default()
    });
    let app = App::build(
        settings,
        std::env::temp_dir().join("vn-rust-unused-test-settings.json"),
        true,
        true,
    )
    .unwrap();
    let mut ui_account = account.clone();
    ui_account.labels.insert(
        1,
        net::Label {
            id: 2,
            name: "Finished".into(),
            private: false,
        },
    );
    ui_account.labels.insert(
        2,
        net::Label {
            id: 5,
            name: "Wishlist".into(),
            private: false,
        },
    );
    app.state.borrow_mut().account = Some(ui_account);
    app.rebuild_labels();
    let initial = Entry {
        vote: Some(50),
        labels: vec![1, 20],
        notes: "old note".into(),
    };
    {
        let mut s = app.state.borrow_mut();
        s.entry = Some(("v1".into(), initial.clone()));
        s.draft = Some(initial.clone());
        s.readonly = false;
    }
    app.show_entry(&initial);
    let h = app.labels.borrow()[1].handle;
    click_control(h);
    assert!(
        !is_checked(&app.labels.borrow()[0]),
        "Progress labels were not exclusive"
    );
    assert!(is_checked(&app.labels.borrow()[3]), "Custom label was lost");
    pump(Duration::from_millis(400));
    app.state.borrow_mut().saving = true;
    let h = app.labels.borrow()[2].handle;
    click_control(h);
    let vote = Vote::build(&app);
    vote.score.set_text("7.6");
    vote.score.set_focus();
    unsafe {
        PostMessageW(
            vote.score.handle.hwnd().unwrap(),
            WM_KEYDOWN,
            VK_RETURN as usize,
            0,
        );
    }
    pump(Duration::from_millis(100));
    app.state.borrow_mut().saving = false;
    app.start_save();
    pump(Duration::from_millis(700));
    assert!(
        app.entry_unsaved(),
        "Failed save did not retain the latest vote and labels"
    );
    assert_eq!(app.state.borrow().draft.as_ref().unwrap().vote, Some(76));
    assert!(
        !vote.window.visible(),
        "Enter did not submit a decimal vote"
    );
    let retry = app.state.borrow().draft.clone().unwrap();
    app.queue_entry(retry);
    pump(Duration::from_millis(400));
    assert!(!app.entry_unsaved());
    let vote = Vote::build(&app);
    unsafe {
        SendMessageW(vote.remove.handle.hwnd().unwrap(), BM_CLICK, 0, 0);
    }
    pump(Duration::from_millis(400));
    assert_eq!(app.state.borrow().entry.as_ref().unwrap().1.vote, None);
    app.state.borrow_mut().readonly = true;
    let n = Notes::build(&app, account, "v1".into(), "old".into());
    n.text.set_text("first");
    n.save();
    n.text.set_text("second");
    n.close_pending.set(true);
    pump(Duration::from_millis(500));
    assert_eq!(&*n.saved.borrow(), "second");
    assert!(!n.dirty());
    assert!(!n.window.visible());
    assert!(app.window.enabled());
    n.window.set_visible(true);
    modal(&n.window, &app.window);
    n.text.set_text("third");
    click(&n.save_close);
    pump(Duration::from_millis(300));
    assert!(n.window.visible());
    assert!(n.dirty());
    assert_eq!(&*n.saved.borrow(), "second");
    assert!(n.discard.visible());
    assert!(n.save_close.enabled());
    assert!(!n.text.readonly());
    click(&n.save_close);
    pump(Duration::from_millis(300));
    assert_eq!(&*n.saved.borrow(), "third");
    assert!(!n.dirty());
    assert!(!n.window.visible());
    let add = AddGame::build(&app);
    *app.add_form.borrow_mut() = Some(add.clone());
    add.query.set_text("v1");
    add.query.set_focus();
    unsafe {
        PostMessageW(
            add.query.handle.hwnd().unwrap(),
            WM_KEYDOWN,
            VK_RETURN as usize,
            0,
        );
    }
    pump(Duration::from_millis(700));
    assert_eq!(add.novels.borrow().len(), 1, "Enter did not search VNDB");
    hide(&add.window);
    app.shutdown();
    pump(Duration::from_millis(100));
}
