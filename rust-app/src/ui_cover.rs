use super::*;
use crate::{
    images::{crop, Picture},
    net::CoverChoice,
};

enum CoverUpdate {
    Releases(Result<Vec<CoverChoice>>),
    Gallery(Result<Vec<CoverChoice>>),
    Image(String, Result<Vec<u8>>),
    Deleted(String, Result<()>),
}
pub(super) struct Covers {
    _theme: crate::theme::Theme,
    pub window: nwg::Window,
    list: nwg::ListBox<String>,
    shared: nwg::ListBox<String>,
    art: nwg::ImageFrame,
    custom: nwg::Button,
    adjust: nwg::Button,
    community: nwg::Button,
    remove: nwg::Button,
    save: nwg::Button,
    cancel: nwg::Button,
    status: nwg::Label,
    headings: Vec<nwg::Label>,
    choices: RefCell<Vec<CoverChoice>>,
    gallery: RefCell<Vec<CoverChoice>>,
    game: RefCell<Game>,
    chosen: RefCell<Option<crate::core::Image>>,
    bytes: RefCell<Vec<u8>>,
    bitmap: RefCell<Option<nwg::Bitmap>>,
    url: RefCell<String>,
    loading: Cell<bool>,
    pub busy: Cell<bool>,
    expanded: Cell<bool>,
    owner: RefCell<Option<String>>,
    notice: nwg::Notice,
    tx: Sender<CoverUpdate>,
    rx: Receiver<CoverUpdate>,
    handler: RefCell<Option<nwg::EventHandler>>,
    app: std::rc::Weak<App>,
    editor: RefCell<Option<Rc<Adjust>>>,
    custom_form: RefCell<Option<Rc<Custom>>>,
    cancel_load: std::sync::Arc<std::sync::atomic::AtomicBool>,
}
impl Drop for Covers {
    fn drop(&mut self) {
        self.cancel_load
            .store(true, std::sync::atomic::Ordering::Relaxed);
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Covers {
    pub fn build(app: &Rc<App>, game: Game) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Cover")
            .size((940, 470))
            .position((240, 180))
            .icon(Some(&app.icon))
            .build(&mut window)
            .unwrap();
        let mut list = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .position((16, 38))
            .size((564, 322))
            .build(&mut list)
            .unwrap();
        let mut shared = nwg::ListBox::default();
        nwg::ListBox::builder()
            .parent(&window)
            .position((16, 405))
            .size((564, 140))
            .build(&mut shared)
            .unwrap();
        shared.set_visible(false);
        let mut art = nwg::ImageFrame::default();
        nwg::ImageFrame::builder()
            .parent(&window)
            .position((608, 38))
            .size((310, 310))
            .build(&mut art)
            .unwrap();
        let headings = vec![
            label(&window, "VNDB covers", (16, 10), (564, 24)),
            label(&window, "Community covers", (16, 380), (564, 24)),
        ];
        headings[1].set_visible(false);
        let custom = button(&window, "Custom image", (16, 374), (130, 28));
        let adjust = button(&window, "Adjust", (158, 374), (100, 28));
        adjust.set_enabled(false);
        let community = button(&window, "Community covers", (270, 374), (158, 28));
        let remove = button(&window, "Remove upload", (440, 374), (140, 28));
        remove.set_visible(false);
        let status = label(&window, "Loading covers…", (16, 426), (700, 28));
        let save = button(&window, "Save", (744, 424), (80, 28));
        let cancel = button(&window, "Cancel", (836, 424), (80, 28));
        let mut choices = vec![
            CoverChoice {
                label: "Default".into(),
                image: game.novel.image.clone(),
                owned: false,
            },
            CoverChoice {
                label: "Custom image".into(),
                image: game.custom_cover.clone(),
                owned: false,
            },
        ];
        if let Some(im) = &game.cover {
            if !choices
                .iter()
                .any(|c| c.image.as_ref().is_some_and(|i| i.url == im.url))
            {
                choices.push(CoverChoice {
                    label: "Current cover".into(),
                    image: Some(im.clone()),
                    owned: false,
                });
            }
        }
        let owner = app
            .state
            .borrow()
            .settings
            .cover_owner_protected
            .as_deref()
            .and_then(|s| unprotect(s).ok());
        let chosen = game.cover.clone().or(game.novel.image.clone());
        let notice = nwg::Notice::create(&window).unwrap();
        let (tx, rx) = mpsc::channel();
        modal(&window, &app.window);
        let theme = crate::theme::Theme::new(&window, false);
        let c = Rc::new(Self {
            window,
            _theme: theme,
            list,
            shared,
            art,
            custom,
            adjust,
            community,
            remove,
            save,
            cancel,
            status,
            headings,
            choices: RefCell::new(choices),
            gallery: RefCell::new(vec![]),
            game: RefCell::new(game),
            chosen: RefCell::new(chosen),
            bytes: RefCell::new(vec![]),
            bitmap: RefCell::new(None),
            url: RefCell::new(String::new()),
            loading: Cell::new(false),
            busy: Cell::new(false),
            expanded: Cell::new(false),
            owner: RefCell::new(owner),
            notice,
            tx,
            rx,
            handler: RefCell::new(None),
            app: Rc::downgrade(app),
            editor: RefCell::new(None),
            custom_form: RefCell::new(None),
            cancel_load: std::sync::Arc::new(std::sync::atomic::AtomicBool::new(false)),
        });
        let weak = Rc::downgrade(&c);
        *c.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &c.window.handle,
            move |e, data, h| {
                if let Some(c) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            if !c.busy.get()
                                && !c.editor.borrow().as_ref().is_some_and(|e| e.busy.get())
                            {
                                c.cancel_load
                                    .store(true, std::sync::atomic::Ordering::Relaxed);
                                c.close();
                            }
                        }
                        nwg::Event::OnMinMaxInfo => data.on_min_max().set_min_size(880, 510),
                        nwg::Event::OnResize if h == c.window => c.layout(),
                        nwg::Event::OnNotice if h == c.notice => c.updates(),
                        nwg::Event::OnListBoxSelect
                            if !c.loading.get() && (h == c.list || h == c.shared) =>
                        {
                            let choice = if h == c.list {
                                c.list
                                    .selection()
                                    .and_then(|i| c.choices.borrow().get(i).cloned())
                            } else {
                                c.shared
                                    .selection()
                                    .and_then(|i| c.gallery.borrow().get(i).cloned())
                            };
                            if let Some(choice) = choice {
                                c.loading.set(true);
                                if h == c.list {
                                    c.shared.set_selection(None);
                                } else {
                                    c.list.set_selection(None);
                                }
                                c.loading.set(false);
                                c.remove.set_enabled(choice.owned);
                                *c.chosen.borrow_mut() = choice.image;
                                c.preview();
                            }
                        }
                        nwg::Event::OnKeyEsc | nwg::Event::OnButtonClick
                            if e == nwg::Event::OnKeyEsc || h == c.cancel =>
                        {
                            if !c.busy.get() {
                                c.cancel_load
                                    .store(true, std::sync::atomic::Ordering::Relaxed);
                                c.close();
                            }
                        }
                        nwg::Event::OnButtonClick if h == c.save => {
                            if !c.busy.get() {
                                let mut game = c.game.borrow().clone();
                                game.cover = c.chosen.borrow().clone();
                                if let Some(a) = c.app.upgrade() {
                                    if a.replace_game(game) {
                                        c.cancel_load
                                            .store(true, std::sync::atomic::Ordering::Relaxed);
                                        c.close();
                                    }
                                }
                            }
                        }
                        nwg::Event::OnButtonClick if h == c.custom => {
                            *c.custom_form.borrow_mut() = Some(Custom::build(&c));
                        }
                        nwg::Event::OnButtonClick if h == c.adjust => {
                            if !c.bytes.borrow().is_empty() {
                                match Picture::load(&c.bytes.borrow()) {
                                    Ok(p) => {
                                        *c.editor.borrow_mut() = Some(Adjust::build(&c, p));
                                    }
                                    Err(e) => c.status.set_text(&e),
                                }
                            }
                        }
                        nwg::Event::OnButtonClick if h == c.community => {
                            c.expanded.set(!c.expanded.get());
                            c.shared.set_visible(c.expanded.get());
                            c.headings[1].set_visible(c.expanded.get());
                            c.remove.set_visible(c.expanded.get());
                            let (w, h) = c.window.size();
                            c.window.set_size(
                                w,
                                if c.expanded.get() {
                                    h + 190
                                } else {
                                    h.saturating_sub(190)
                                },
                            );
                            c.layout();
                            if c.expanded.get() {
                                c.load_gallery();
                            }
                        }
                        nwg::Event::OnButtonClick if h == c.remove => c.delete(),
                        _ => {}
                    }
                }
            },
        ));
        c.refresh_list();
        c.preview();
        let id = c.game.borrow().novel.id.clone();
        let cancel = c.cancel_load.clone();
        c.job(move || CoverUpdate::Releases(net::release_covers(&id, &cancel)));
        c
    }
    fn job(&self, f: impl FnOnce() -> CoverUpdate + Send + 'static) {
        let tx = self.tx.clone();
        let n = self.notice.sender();
        thread::spawn(move || {
            let _ = tx.send(f());
            n.notice();
        });
    }
    pub fn pending(&self) -> bool {
        self.busy.get() || self.editor.borrow().as_ref().is_some_and(|e| e.busy.get())
    }
    fn close(&self) {
        self.cancel_load
            .store(true, std::sync::atomic::Ordering::Relaxed);
        hide(&self.window);
        if let Some(app) = self.app.upgrade() {
            let _ = app.tx.send(Update::TrimCover);
            app.notice.sender().notice();
        }
    }
    fn refresh_list(&self) {
        self.loading.set(true);
        self.list.set_collection(
            self.choices
                .borrow()
                .iter()
                .map(|c| c.label.clone())
                .collect(),
        );
        fit_horizontal(&self.list);
        let selected = self.choices.borrow().iter().position(|c| {
            c.image.as_ref().map(|im| &im.url) == self.chosen.borrow().as_ref().map(|im| &im.url)
        });
        self.list.set_selection(selected);
        self.loading.set(false);
    }
    fn preview(&self) {
        let url = self
            .chosen
            .borrow()
            .as_ref()
            .map(|im| im.url.clone())
            .unwrap_or_default();
        if *self.url.borrow() == url {
            return;
        }
        *self.url.borrow_mut() = url.clone();
        self.art.set_bitmap(None);
        *self.bitmap.borrow_mut() = None;
        self.bytes.borrow_mut().clear();
        self.adjust.set_enabled(false);
        if !url.is_empty() {
            self.status.set_text("Loading preview…");
            self.job(move || CoverUpdate::Image(url.clone(), net::image(&url)));
        }
    }
    fn show_image(&self, bytes: &[u8]) {
        let result = Picture::load(bytes)
            .and_then(|p| {
                p.output(
                    true,
                    1.,
                    (p.width as f32 / 2., p.height as f32 / 2.),
                    false,
                    image_pixels(310),
                )
            })
            .and_then(|p| p.checkerboard())
            .and_then(|p| p.png())
            .and_then(|bytes| nwg::Bitmap::from_bin(&bytes).map_err(|e| e.to_string()));
        match result {
            Ok(b) => {
                self.art.set_bitmap(Some(&b));
                *self.bitmap.borrow_mut() = Some(b);
                self.adjust.set_enabled(true);
                self.status.set_text("");
            }
            Err(e) => self.status.set_text(&e),
        }
    }
    fn load_gallery(&self) {
        let id = self.game.borrow().novel.id.clone();
        let owner = self.owner.borrow().clone();
        self.community.set_enabled(false);
        self.status.set_text("Loading community covers…");
        self.job(move || CoverUpdate::Gallery(net::gallery(&id, owner.as_deref())));
    }
    fn delete(&self) {
        if self
            .app
            .upgrade()
            .is_some_and(|a| a.state.borrow().readonly)
        {
            self.status
                .set_text("Removing uploads is off in preview mode.");
            return;
        }
        let choice = self
            .shared
            .selection()
            .and_then(|i| self.gallery.borrow().get(i).cloned());
        let Some(choice) = choice.filter(|c| c.owned) else {
            return;
        };
        let Some(im) = choice.image else {
            return;
        };
        let Some(owner) = self.owner.borrow().clone() else {
            return;
        };
        if !confirm(
            &self.window,
            "Remove upload",
            "Delete this hosted image? Anyone using its URL may lose their cover.",
        ) {
            return;
        }
        self.busy.set(true);
        self.remove.set_enabled(false);
        self.save.set_enabled(false);
        self.status.set_text("Removing upload…");
        self.job(move || CoverUpdate::Deleted(im.url.clone(), net::remove_cover(&im.url, &owner)));
    }
    fn updates(&self) {
        while let Ok(update) = self.rx.try_recv() {
            match update {
                CoverUpdate::Releases(result) => match result {
                    Ok(c) => {
                        self.choices.borrow_mut().extend(c);
                        self.refresh_list();
                        self.status.set_text("");
                    }
                    Err(e) => self.status.set_text(&e),
                },
                CoverUpdate::Gallery(result) => {
                    self.community.set_enabled(true);
                    match result {
                        Ok(c) => {
                            self.shared
                                .set_collection(c.iter().map(|c| c.label.clone()).collect());
                            self.status.set_text(if c.is_empty() {
                                "No community covers yet."
                            } else {
                                "Select a cover to preview it."
                            });
                            *self.gallery.borrow_mut() = c;
                        }
                        Err(e) => self.status.set_text(&e),
                    }
                }
                CoverUpdate::Image(url, result) => {
                    if url != *self.url.borrow() {
                        continue;
                    }
                    match result {
                        Ok(bytes) => {
                            self.show_image(&bytes);
                            *self.bytes.borrow_mut() = bytes;
                        }
                        Err(e) => self.status.set_text(&e),
                    }
                }
                CoverUpdate::Deleted(url, result) => {
                    self.busy.set(false);
                    self.save.set_enabled(true);
                    match result {
                        Ok(()) => {
                            if let Some(a) = self.app.upgrade() {
                                let mut s = a.state.borrow_mut();
                                for g in &mut s.settings.games {
                                    g.forget_cover(&url);
                                }
                                drop(s);
                                a.cache.borrow_mut().retain(|(cached, _)| cached != &url);
                                a.persist();
                                a.refresh_preview();
                                a.send_state();
                            }
                            self.game.borrow_mut().forget_cover(&url);
                            if self
                                .chosen
                                .borrow()
                                .as_ref()
                                .is_some_and(|im| im.url == url)
                            {
                                *self.chosen.borrow_mut() = self.game.borrow().image().cloned();
                            }
                            self.choices.borrow_mut().retain(|c| {
                                c.label == "Default"
                                    || c.label == "Custom image"
                                    || c.image.as_ref().is_none_or(|im| im.url != url)
                            });
                            self.choices.borrow_mut()[0].image =
                                self.game.borrow().novel.image.clone();
                            self.choices.borrow_mut()[1].image =
                                self.game.borrow().custom_cover.clone();
                            self.gallery
                                .borrow_mut()
                                .retain(|c| c.image.as_ref().is_none_or(|im| im.url != url));
                            self.shared.set_collection(
                                self.gallery
                                    .borrow()
                                    .iter()
                                    .map(|c| c.label.clone())
                                    .collect(),
                            );
                            self.remove.set_enabled(false);
                            self.refresh_list();
                            self.preview();
                            self.load_gallery();
                        }
                        Err(e) => self.status.set_text(&e),
                    }
                }
            }
        }
    }
    fn uploaded(&self, image: crate::core::Image) {
        self.game.borrow_mut().custom_cover = Some(image.clone());
        self.choices.borrow_mut()[1].image = Some(image.clone());
        *self.chosen.borrow_mut() = Some(image);
        self.refresh_list();
        self.preview();
        if self.expanded.get() {
            self.load_gallery();
        }
    }
    fn layout(&self) {
        let (w, h) = self.window.size();
        let left = w.saturating_sub(376);
        let extra = if self.expanded.get() { 190 } else { 0 };
        self.list.set_size(left, h.saturating_sub(148 + extra));
        self.art.set_position(w as i32 - 332, 38);
        let y = h as i32 - 96 - extra as i32;
        for (b, x) in [
            (&self.custom, 16),
            (&self.adjust, 158),
            (&self.community, 270),
        ] {
            b.set_position(x, y);
        }
        self.remove.set_position(w as i32 - 162, h as i32 - 90);
        self.shared.set_position(16, h as i32 - 216);
        self.shared.set_size(left, 140);
        self.headings[1].set_position(16, h as i32 - 242);
        self.status.set_position(16, h as i32 - 40);
        self.save.set_position(w as i32 - 196, h as i32 - 42);
        self.cancel.set_position(w as i32 - 104, h as i32 - 42);
    }
}

pub(super) fn check_layout(app: &Rc<App>, game: Game, out: &std::path::Path) -> Result<()> {
    let source = game
        .novel
        .image
        .as_ref()
        .ok_or("Test game has no cover")?
        .url
        .clone();
    let bytes = net::image(&source)?;
    let picture = Picture::load(&bytes)?;
    let c = Covers::build(app, game);
    c.show_image(&bytes);
    c.window.set_size(1060, 470);
    c.layout();
    let custom = Custom::build(&c);
    custom.url.set_text("");
    unsafe {
        SendMessageW(custom.save.handle.hwnd().unwrap(), BM_CLICK, 0, 0);
    }
    assert!(c.game.borrow().custom_cover.is_none());
    assert_eq!(
        c.chosen.borrow().as_ref().map(|im| &im.url),
        c.game.borrow().novel.image.as_ref().map(|im| &im.url)
    );
    assert!(c.window.enabled());
    unsafe {
        SendMessageW(c.community.handle.hwnd().unwrap(), BM_CLICK, 0, 0);
    }
    assert!(c.shared.visible() && c.expanded.get());
    unsafe {
        SendMessageW(c.community.handle.hwnd().unwrap(), BM_CLICK, 0, 0);
    }
    assert!(!c.shared.visible() && !c.expanded.get());
    crate::images::capture(c.window.handle.hwnd().unwrap(), &out.join("cover.png"))?;
    let a = Adjust::build(&c, picture);
    a.preview();
    assert!(is_checked(&a.fit));
    unsafe {
        let mut info: winapi::um::wingdi::BITMAP = std::mem::zeroed();
        winapi::um::wingdi::GetObjectW(
            a.bitmap.borrow().as_ref().unwrap().handle as _,
            std::mem::size_of_val(&info) as i32,
            &mut info as *mut _ as _,
        );
        assert_eq!(
            (info.bmWidth, info.bmHeight),
            (image_pixels(360) as i32, image_pixels(360) as i32)
        );
    }
    assert!(!a.busy.get());
    crate::images::capture(a.window.handle.hwnd().unwrap(), &out.join("fit.png"))?;
    a.fit.set_check_state(nwg::CheckBoxState::Unchecked);
    a.zoom.set_pos(15);
    a.center.set((-99999., 99999.));
    a.preview();
    let rect = crop(a.picture.width, a.picture.height, 1.5, (-99999., 99999.));
    assert_eq!(
        a.center.get(),
        (rect[0] + rect[2] / 2., rect[1] + rect[3] / 2.)
    );
    unsafe {
        SendMessageW(
            a.art.handle.hwnd().unwrap(),
            WM_MOUSEWHEEL,
            (120u32 << 16) as usize,
            0,
        );
    }
    assert_eq!(a.zoom.pos(), 17);
    a.center
        .set((a.picture.width as f32 / 2., a.picture.height as f32 / 2.));
    a.preview();
    let previous_center = a.center.get();
    unsafe {
        SetFocus(a.art.handle.hwnd().unwrap());
        PostMessageW(
            a.art.handle.hwnd().unwrap(),
            WM_KEYDOWN,
            VK_RIGHT as usize,
            0,
        );
    }
    pump(std::time::Duration::from_millis(100));
    assert!(
        a.center.get().0 > previous_center.0,
        "Arrow key did not move the crop"
    );
    crate::images::capture(a.window.handle.hwnd().unwrap(), &out.join("crop.png"))?;
    a.blur.set_check_state(nwg::CheckBoxState::Checked);
    a.preview();
    crate::images::capture(a.window.handle.hwnd().unwrap(), &out.join("blur.png"))?;
    std::fs::write(out.join("saved-cover.png"), a.output()?.png()?).map_err(|e| e.to_string())?;
    a.window.set_focus();
    unsafe {
        SendMessageW(a.reset.handle.hwnd().unwrap(), BM_CLICK, 0, 0);
    }
    assert_eq!(a.zoom.pos(), 10);
    unsafe {
        SendMessageW(a.cancel.handle.hwnd().unwrap(), BM_CLICK, 0, 0);
    }
    assert!(!a.window.visible(), "Cancel did not close Adjust cover");
    assert!(c.window.enabled());
    let escaped = Adjust::build(&c, Picture::load(&bytes)?);
    escape(&escaped.window, escaped.cancel.handle);
    assert!(!escaped.window.visible() && c.window.enabled());
    let custom = Custom::build(&c);
    escape(&custom.window, custom.url.handle);
    assert!(!custom.window.visible() && c.window.enabled());
    let deleted = crate::core::Image {
        url: format!("{}/covers/{}.png", net::HOST, "e".repeat(64)),
        thumbnail: None,
    };
    c.game.borrow_mut().cover = Some(deleted.clone());
    c.game.borrow_mut().custom_cover = Some(deleted.clone());
    c.choices.borrow_mut()[1].image = Some(deleted.clone());
    c.choices.borrow_mut().push(CoverChoice {
        label: "Current cover".into(),
        image: Some(deleted.clone()),
        owned: true,
    });
    *c.chosen.borrow_mut() = Some(deleted.clone());
    *c.url.borrow_mut() = deleted.url.clone();
    let _ = c.tx.send(CoverUpdate::Deleted(
        deleted.url.clone(),
        Err("Test removal failure".into()),
    ));
    c.updates();
    assert!(c.game.borrow().cover.is_some());
    let _ = c.tx.send(CoverUpdate::Deleted(deleted.url.clone(), Ok(())));
    c.updates();
    assert!(c.game.borrow().cover.is_none() && c.game.borrow().custom_cover.is_none());
    assert!(
        c.choices
            .borrow()
            .iter()
            .all(|choice| choice.image.as_ref().is_none_or(|im| im.url != deleted.url)),
        "Deleted cover remained selectable"
    );
    c.cancel_load
        .store(true, std::sync::atomic::Ordering::Relaxed);
    c.close();
    assert!(app.window.enabled());
    Ok(())
}
struct Custom {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    url: nwg::TextInput,
    save: nwg::Button,
    cancel: nwg::Button,
    _label: nwg::Label,
    handler: RefCell<Option<nwg::EventHandler>>,
}
impl Drop for Custom {
    fn drop(&mut self) {
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Custom {
    fn build(c: &Rc<Covers>) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Custom image")
            .flags(nwg::WindowFlags::WINDOW | nwg::WindowFlags::VISIBLE)
            .size((660, 130))
            .position((350, 260))
            .build(&mut window)
            .unwrap();
        if let Some(a) = c.app.upgrade() {
            window.set_icon(Some(&a.icon));
        }
        let caption = label(
            &window,
            "Direct image URL (clear to remove)",
            (16, 12),
            (628, 24),
        );
        let url = input(
            &window,
            c.game
                .borrow()
                .custom_cover
                .as_ref()
                .map(|im| im.url.as_str())
                .unwrap_or(""),
            (16, 42),
            (628, 28),
        );
        let save = button(&window, "Save", (464, 86), (80, 28));
        let cancel = button(&window, "Cancel", (556, 86), (88, 28));
        modal(&window, &c.window);
        let theme = crate::theme::Theme::new(&window, false);
        let f = Rc::new(Self {
            window,
            _theme: theme,
            url,
            save,
            cancel,
            _label: caption,
            handler: RefCell::new(None),
        });
        let weak = Rc::downgrade(&f);
        let parent = Rc::downgrade(c);
        *f.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &f.window.handle,
            move |e, data, h| {
                if let Some(f) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            hide(&f.window);
                        }
                        nwg::Event::OnKeyEsc => hide(&f.window),
                        nwg::Event::OnButtonClick if h == f.cancel => hide(&f.window),
                        nwg::Event::OnKeyEnter | nwg::Event::OnButtonClick
                            if e == nwg::Event::OnKeyEnter || h == f.save =>
                        {
                            let url = f.url.text().trim().to_owned();
                            if !url.is_empty() && !net::valid_image_url(&url) {
                                error(&f.window, "Use a direct HTTP or HTTPS image URL.");
                                return;
                            }
                            if let Some(c) = parent.upgrade() {
                                let image = (!url.is_empty()).then(|| crate::core::Image {
                                    url,
                                    thumbnail: None,
                                });
                                c.game.borrow_mut().custom_cover = image.clone();
                                c.choices.borrow_mut()[1].image = image.clone();
                                *c.chosen.borrow_mut() =
                                    image.or(c.game.borrow().novel.image.clone());
                                c.refresh_list();
                                c.preview();
                                hide(&f.window);
                            }
                        }
                        _ => {}
                    }
                }
            },
        ));
        f
    }
}
struct Adjust {
    _theme: crate::theme::Theme,
    window: nwg::Window,
    art: nwg::ImageFrame,
    fit: nwg::CheckBox,
    blur: nwg::CheckBox,
    zoom: nwg::TrackBar,
    reset: nwg::Button,
    save: nwg::Button,
    upload: nwg::Button,
    cancel: nwg::Button,
    status: nwg::Label,
    _labels: Vec<nwg::Label>,
    picture: Picture,
    center: Cell<(f32, f32)>,
    drag: Cell<Option<(i32, i32)>>,
    bitmap: RefCell<Option<nwg::Bitmap>>,
    pub busy: Cell<bool>,
    notice: nwg::Notice,
    rx: Receiver<Result<crate::core::Image>>,
    tx: Sender<Result<crate::core::Image>>,
    handler: RefCell<Option<nwg::EventHandler>>,
    covers: std::rc::Weak<Covers>,
    keys: nwg::RawEventHandler,
}
impl Drop for Adjust {
    fn drop(&mut self) {
        let _ = nwg::unbind_raw_event_handler(&self.keys);
        if let Some(h) = self.handler.get_mut().take() {
            nwg::unbind_event_handler(&h);
        }
    }
}
impl Adjust {
    fn build(c: &Rc<Covers>, picture: Picture) -> Rc<Self> {
        let mut window = nwg::Window::default();
        nwg::Window::builder()
            .title("Adjust cover")
            .flags(nwg::WindowFlags::WINDOW | nwg::WindowFlags::VISIBLE)
            .size((600, 462))
            .position((380, 240))
            .build(&mut window)
            .unwrap();
        if let Some(a) = c.app.upgrade() {
            window.set_icon(Some(&a.icon));
        }
        let mut art = nwg::ImageFrame::default();
        nwg::ImageFrame::builder()
            .parent(&window)
            .position((16, 16))
            .size((360, 360))
            .build(&mut art)
            .unwrap();
        let fit = check(&window, "Fit whole cover", (394, 24), true);
        let blur = check(&window, "Blur cover", (394, 62), false);
        let instructions = label(
            &window,
            "Drag to move\r\nScroll to zoom",
            (394, 108),
            (190, 48),
        );
        let zoomlabel = label(&window, "Zoom", (394, 164), (190, 24));
        let mut zoom = nwg::TrackBar::default();
        nwg::TrackBar::builder()
            .parent(&window)
            .position((388, 188))
            .size((190, 36))
            .range(Some(10..41))
            .pos(Some(10))
            .build(&mut zoom)
            .unwrap();
        let reset = button(&window, "Reset", (394, 240), (90, 28));
        let save = button(&window, "Save image", (16, 410), (126, 28));
        let upload = button(&window, "Upload and use", (326, 410), (142, 28));
        let cancel = button(&window, "Cancel", (480, 410), (104, 28));
        let status = label(&window, "", (16, 382), (568, 24));
        let notice = nwg::Notice::create(&window).unwrap();
        let (tx, rx) = mpsc::channel();
        let center = (picture.width as f32 / 2., picture.height as f32 / 2.);
        let keys = nwg::bind_raw_event_handler(&art.handle, 0x10001, |_, message, _, _| {
            (message == WM_GETDLGCODE).then_some(DLGC_WANTARROWS as isize)
        })
        .unwrap();
        modal(&window, &c.window);
        let theme = crate::theme::Theme::new(&window, false);
        let a = Rc::new(Self {
            window,
            _theme: theme,
            art,
            fit,
            blur,
            zoom,
            reset,
            save,
            upload,
            cancel,
            status,
            _labels: vec![instructions, zoomlabel],
            picture,
            center: Cell::new(center),
            drag: Cell::new(None),
            bitmap: RefCell::new(None),
            busy: Cell::new(false),
            notice,
            rx,
            tx,
            handler: RefCell::new(None),
            covers: Rc::downgrade(c),
            keys,
        });
        let weak = Rc::downgrade(&a);
        *a.handler.borrow_mut() = Some(nwg::full_bind_event_handler(
            &a.window.handle,
            move |e, data, h| {
                if let Some(a) = weak.upgrade() {
                    match e {
                        nwg::Event::OnWindowClose => {
                            keep_open(&data);
                            if !a.busy.get() {
                                hide(&a.window);
                            }
                        }
                        nwg::Event::OnKeyEsc | nwg::Event::OnButtonClick
                            if e == nwg::Event::OnKeyEsc || h == a.cancel =>
                        {
                            if !a.busy.get() {
                                hide(&a.window);
                            }
                        }
                        nwg::Event::OnButtonClick if h == a.fit || h == a.blur => a.preview(),
                        nwg::Event::OnHorizontalScroll | nwg::Event::TrackBarUpdated
                            if h == a.zoom =>
                        {
                            a.preview()
                        }
                        nwg::Event::OnButtonClick if h == a.reset => {
                            a.zoom.set_pos(10);
                            a.center
                                .set((a.picture.width as f32 / 2., a.picture.height as f32 / 2.));
                            a.preview();
                        }
                        nwg::Event::OnButtonClick if h == a.save => {
                            if let Some(path) = file_dialog(
                                &a.window,
                                "Save cover",
                                "PNG image (*.png)|*.png",
                                true,
                            ) {
                                match a.output().and_then(|p| p.png()).and_then(|bytes| {
                                    std::fs::write(path, bytes).map_err(|e| e.to_string())
                                }) {
                                    Ok(()) => a.status.set_text("Image saved."),
                                    Err(e) => a.status.set_text(&e),
                                }
                            }
                        }
                        nwg::Event::OnButtonClick if h == a.upload => a.upload(),
                        nwg::Event::OnNotice if h == a.notice => a.done(),
                        nwg::Event::OnMousePress(nwg::MousePressEvent::MousePressLeftDown)
                            if h == a.art =>
                        {
                            a.fit.set_check_state(nwg::CheckBoxState::Unchecked);
                            a.drag.set(Some(nwg::GlobalCursor::position()));
                            unsafe {
                                SetFocus(a.art.handle.hwnd().unwrap());
                                SetCapture(a.art.handle.hwnd().unwrap());
                            }
                            a.preview();
                        }
                        nwg::Event::OnMousePress(nwg::MousePressEvent::MousePressLeftUp)
                            if a.drag.get().is_some() =>
                        {
                            a.drag.set(None);
                            unsafe {
                                ReleaseCapture();
                            }
                        }
                        nwg::Event::OnMouseMove if h == a.art => {
                            if let Some((x, y)) = a.drag.get() {
                                let (nx, ny) = nwg::GlobalCursor::position();
                                let mut rect = unsafe { std::mem::zeroed() };
                                unsafe {
                                    GetWindowRect(a.art.handle.hwnd().unwrap(), &mut rect);
                                }
                                let scale = crop(
                                    a.picture.width,
                                    a.picture.height,
                                    a.zoom.pos() as f32 / 10.,
                                    a.center.get(),
                                )[2] / (rect.right - rect.left).max(1) as f32;
                                let (cx, cy) = a.center.get();
                                a.center.set((
                                    cx - (nx - x) as f32 * scale,
                                    cy - (ny - y) as f32 * scale,
                                ));
                                a.drag.set(Some((nx, ny)));
                                a.preview();
                            }
                        }
                        nwg::Event::OnMouseWheel if h == a.art => {
                            if let nwg::EventData::OnMouseWheel(delta) = data {
                                a.fit.set_check_state(nwg::CheckBoxState::Unchecked);
                                a.zoom.set_pos(
                                    (a.zoom.pos() as i32 + delta.signum() * 2).clamp(10, 40)
                                        as usize,
                                );
                                a.preview();
                            }
                        }
                        nwg::Event::OnKeyPress if h == a.art => {
                            let key = data.on_key();
                            if (37..=40).contains(&key) {
                                a.fit.set_check_state(nwg::CheckBoxState::Unchecked);
                                let (x, y) = a.center.get();
                                let step = a.picture.width.min(a.picture.height) as f32 / 50.;
                                a.center.set((
                                    x + if key == 37 {
                                        -step
                                    } else if key == 39 {
                                        step
                                    } else {
                                        0.
                                    },
                                    y + if key == 38 {
                                        -step
                                    } else if key == 40 {
                                        step
                                    } else {
                                        0.
                                    },
                                ));
                                a.preview();
                            }
                        }
                        _ => {}
                    }
                }
            },
        ));
        a.preview();
        a
    }
    fn output(&self) -> Result<Picture> {
        self.picture.output(
            is_checked(&self.fit),
            self.zoom.pos() as f32 / 10.,
            self.center.get(),
            is_checked(&self.blur),
            512,
        )
    }
    fn preview(&self) {
        if !is_checked(&self.fit) {
            let r = crop(
                self.picture.width,
                self.picture.height,
                self.zoom.pos() as f32 / 10.,
                self.center.get(),
            );
            self.center.set((r[0] + r[2] / 2., r[1] + r[3] / 2.));
        }
        let r = self
            .picture
            .output(
                is_checked(&self.fit),
                self.zoom.pos() as f32 / 10.,
                self.center.get(),
                is_checked(&self.blur),
                image_pixels(360),
            )
            .and_then(|p| p.checkerboard())
            .and_then(|p| p.png())
            .and_then(|bytes| {
                let mut b = nwg::Bitmap::default();
                nwg::Bitmap::builder()
                    .source_bin(Some(&bytes))
                    .build(&mut b)
                    .map_err(|e| e.to_string())?;
                Ok(b)
            });
        match r {
            Ok(b) => {
                self.art.set_bitmap(Some(&b));
                *self.bitmap.borrow_mut() = Some(b);
            }
            Err(e) => self.status.set_text(&e),
        }
        self.zoom.set_enabled(!is_checked(&self.fit));
    }
    fn upload(&self) {
        if self.busy.get() {
            return;
        }
        let Some(c) = self.covers.upgrade() else {
            return;
        };
        let Some(a) = c.app.upgrade() else {
            return;
        };
        if a.state.borrow().readonly {
            self.status.set_text("Uploads are off in preview mode.");
            return;
        }
        let owner = {
            let mut s = a.state.borrow_mut();
            let path = s.path.clone();
            s.settings.owner(&path)
        };
        let bytes = self.output().and_then(|p| p.png());
        let (owner, bytes) = match (owner, bytes) {
            (Ok(o), Ok(b)) => (o, b),
            (Err(e), _) | (_, Err(e)) => {
                self.status.set_text(&e);
                return;
            }
        };
        if bytes.len() > 1280 * 1024 {
            self.status.set_text("Image is too large to upload.");
            return;
        }
        *c.owner.borrow_mut() = Some(owner.clone());
        let id = c.game.borrow().novel.id.clone();
        self.busy.set(true);
        self.upload.set_enabled(false);
        self.save.set_enabled(false);
        self.status.set_text("Uploading…");
        let tx = self.tx.clone();
        let notice = self.notice.sender();
        thread::spawn(move || {
            let _ = tx.send(net::upload(&bytes, &id, &owner));
            notice.notice();
        });
    }
    fn done(&self) {
        while let Ok(r) = self.rx.try_recv() {
            self.busy.set(false);
            self.upload.set_enabled(true);
            self.save.set_enabled(true);
            match r {
                Ok(image) => {
                    if let Some(c) = self.covers.upgrade() {
                        c.uploaded(image);
                    }
                    hide(&self.window);
                }
                Err(e) => self.status.set_text(&e),
            }
        }
    }
}
