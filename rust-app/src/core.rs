use base64::{engine::general_purpose::STANDARD, Engine};
use regex::Regex;
use serde::{Deserialize, Serialize};
use serde_json::{json, Value};
use std::{
    collections::BTreeMap,
    fs,
    path::{Path, PathBuf},
    ptr,
    time::{SystemTime, UNIX_EPOCH},
};
use winapi::{
    shared::minwindef::{FILETIME, LPARAM, TRUE},
    um::{
        dpapi::*, handleapi::*, processthreadsapi::*, tlhelp32::*, winbase::*, wincrypt::DATA_BLOB,
        winuser::*,
    },
};

pub type Result<T> = std::result::Result<T, String>;
pub fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(Some(0)).collect()
}
pub fn last_error() -> String {
    std::io::Error::last_os_error().to_string()
}
#[link(name = "kernel32")]
extern "system" {
    fn CompareStringOrdinal(
        a: *const u16,
        a_len: i32,
        b: *const u16,
        b_len: i32,
        ignore_case: i32,
    ) -> i32;
}
pub fn same_exe(a: &str, b: &str) -> bool {
    let (a, b) = (wide(a), wide(b));
    unsafe {
        CompareStringOrdinal(
            a.as_ptr(),
            (a.len() - 1) as i32,
            b.as_ptr(),
            (b.len() - 1) as i32,
            1,
        ) == 2
    }
}
pub fn valid_exe_path(s: &str) -> bool {
    !s.contains('\0') && Path::new(s).is_absolute() && s.to_ascii_lowercase().ends_with(".exe")
}
pub fn now() -> i64 {
    SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_secs() as i64
}
pub fn valid_id(s: &str, prefix: char) -> bool {
    s.starts_with(prefix)
        && s.len() > 1
        && s.as_bytes()[1] != b'0'
        && s[1..].bytes().all(|b| b.is_ascii_digit())
}
pub fn profile_url(s: &str) -> Option<String> {
    let s = s.trim().to_ascii_lowercase();
    let id = s
        .strip_prefix("https://vndb.org/")
        .unwrap_or(&s)
        .trim_end_matches('/');
    valid_id(id, 'u').then(|| format!("https://vndb.org/{id}"))
}

#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct VnTitle {
    #[serde(alias = "lang")]
    pub lang: String,
    #[serde(alias = "title")]
    pub title: String,
    #[serde(alias = "main")]
    pub main: bool,
    #[serde(default, alias = "latin")]
    pub latin: Option<String>,
}
#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct Image {
    #[serde(alias = "url")]
    pub url: String,
    #[serde(default, alias = "thumbnail")]
    pub thumbnail: Option<String>,
}
#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct Developer {
    #[serde(alias = "name")]
    pub name: String,
    #[serde(default, alias = "id")]
    pub id: Option<String>,
}
#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct Novel {
    #[serde(alias = "id")]
    pub id: String,
    #[serde(alias = "title")]
    pub title: String,
    #[serde(default, alias = "alttitle")]
    pub alttitle: Option<String>,
    #[serde(default, alias = "olang")]
    pub olang: String,
    #[serde(default, alias = "titles")]
    pub titles: Vec<VnTitle>,
    #[serde(default, alias = "image")]
    pub image: Option<Image>,
    #[serde(default, alias = "developers")]
    pub developers: Option<Vec<Developer>>,
}
impl Novel {
    pub fn native_title(&self) -> &str {
        self.titles
            .iter()
            .find(|t| t.lang == self.olang)
            .or_else(|| self.titles.iter().find(|t| t.main))
            .map(|t| t.title.as_str())
            .or(self.alttitle.as_deref())
            .unwrap_or(&self.title)
    }
    pub fn brand(&self) -> String {
        self.developers
            .as_deref()
            .unwrap_or(&[])
            .iter()
            .map(|d| d.name.as_str())
            .collect::<Vec<_>>()
            .join(", ")
    }
    pub fn url(&self) -> String {
        format!("https://vndb.org/{}", self.id)
    }
    pub fn brand_url(&self) -> Option<String> {
        let devs = self.developers.as_deref().unwrap_or(&[]);
        if devs.len() == 1 {
            devs[0]
                .id
                .as_ref()
                .filter(|s| valid_id(s, 'p'))
                .map(|s| format!("https://vndb.org/{s}"))
        } else if devs.len() > 1 {
            Some(self.url())
        } else {
            None
        }
    }
}
#[derive(Clone, Default, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct Game {
    pub exe: String,
    pub novel: Novel,
    #[serde(default)]
    pub custom_progress: Option<String>,
    #[serde(default)]
    pub remove_progress_text: Option<String>,
    #[serde(default)]
    pub cover: Option<Image>,
    #[serde(default)]
    pub custom_cover: Option<Image>,
    #[serde(default)]
    pub window_title_prefix: Option<String>,
    #[serde(default)]
    pub trim_edges: Option<String>,
    #[serde(default)]
    pub last_played: i64,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}
impl Game {
    pub fn forget_cover(&mut self, url: &str) {
        for image in [
            &mut self.cover,
            &mut self.custom_cover,
            &mut self.novel.image,
        ] {
            if image.as_ref().is_some_and(|im| im.url == url) {
                *image = None;
            }
        }
    }
    pub fn image(&self) -> Option<&Image> {
        self.cover.as_ref().or(self.novel.image.as_ref())
    }
    pub fn progress(&self, caption: &str) -> Option<String> {
        if let Some(custom) = &self.custom_progress {
            return nonblank(custom);
        }
        let mut candidates = Vec::new();
        if let Some(prefix) = self
            .window_title_prefix
            .as_deref()
            .filter(|s| !s.trim().is_empty())
        {
            candidates.push(prefix.trim().to_owned());
        } else {
            candidates.extend(
                [Some(self.novel.title.clone()), self.novel.alttitle.clone()]
                    .into_iter()
                    .flatten(),
            );
            for t in &self.novel.titles {
                candidates.push(t.title.clone());
                candidates.extend(t.latin.clone());
            }
            candidates.extend(
                candidates
                    .clone()
                    .iter()
                    .map(|s| s.split(['~', '～']).next().unwrap_or(s).trim().to_owned()),
            );
            candidates.sort_by_key(|s| std::cmp::Reverse(s.len()));
            candidates.dedup();
        }
        let mut progress = None;
        for t in candidates.into_iter().filter(|s| !s.is_empty()) {
            // Rust's regex engine has no lookahead: check the title boundary separately.
            let pattern = Regex::new(r"\b(?:o|wo)\b")
                .unwrap()
                .replace_all(&regex::escape(&t), "(?:o|wo)")
                .into_owned();
            if let Ok(re) = Regex::new(&format!("(?i)^{pattern}")) {
                if let Some(m) = re.find(caption) {
                    let rest = &caption[m.end()..];
                    if rest
                        .chars()
                        .next()
                        .is_none_or(|c| c.is_whitespace() || ",:-–—～~".contains(c))
                    {
                        progress = Some(rest.trim().to_owned());
                        break;
                    }
                }
            }
        }
        let mut p = progress?;
        if let Some(remove) = &self.remove_progress_text {
            for phrase in remove.split(';').map(str::trim).filter(|s| !s.is_empty()) {
                p = Regex::new(&format!("(?i){}", regex::escape(phrase)))
                    .ok()?
                    .replace_all(&p, " ")
                    .into_owned();
            }
            p = p.split_whitespace().collect::<Vec<_>>().join(" ");
        }
        if let Some(edges) = &self.trim_edges {
            p = p
                .trim_matches(|c: char| c.is_whitespace() || edges.contains(c))
                .to_owned();
        }
        nonblank(&p)
    }
}
pub fn nonblank(s: &str) -> Option<String> {
    (!s.trim().is_empty()).then(|| s.trim().to_owned())
}

fn client_id() -> String {
    "1554580268285698068".into()
}
fn yes() -> bool {
    true
}
fn added() -> String {
    "Added".into()
}
#[derive(Clone, Deserialize, Serialize)]
#[serde(rename_all = "PascalCase")]
pub struct Settings {
    #[serde(default = "client_id")]
    pub client_id: String,
    #[serde(default)]
    pub profile_url: Option<String>,
    #[serde(default = "yes")]
    pub show_profile_button: bool,
    #[serde(default)]
    pub guide_dismissed: bool,
    #[serde(default)]
    pub vndb_token_protected: Option<String>,
    #[serde(default)]
    pub cover_owner_protected: Option<String>,
    #[serde(default)]
    pub games: Vec<Game>,
    #[serde(default = "added")]
    pub game_sort_order: String,
    #[serde(flatten)]
    pub extra: BTreeMap<String, Value>,
}
impl Default for Settings {
    fn default() -> Self {
        serde_json::from_str("{}").unwrap()
    }
}
impl Settings {
    pub fn directory() -> PathBuf {
        PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default()).join("VnPresence")
    }
    pub fn load(path: &Path) -> Result<Self> {
        if !path.exists() {
            return Ok(Self::default());
        }
        let bytes = fs::read(path).map_err(|e| e.to_string())?;
        if bytes.len() > 16 * 1024 * 1024 {
            return Err("Settings file is too large.".into());
        }
        let mut s: Self =
            serde_json::from_slice(bytes.strip_prefix(&[239, 187, 191]).unwrap_or(&bytes))
                .map_err(|e| format!("Could not read settings: {e}"))?;
        if s.client_id.trim().is_empty() {
            s.client_id = client_id();
        }
        s.validate()?;
        Ok(s)
    }
    pub fn save(&self, path: &Path) -> Result<()> {
        let parent = path.parent().ok_or("Invalid settings path")?;
        fs::create_dir_all(parent).map_err(|e| e.to_string())?;
        let temp = path.with_extension("json.tmp");
        let mut file = fs::File::create(&temp).map_err(|e| e.to_string())?;
        serde_json::to_writer_pretty(&mut file, self).map_err(|e| e.to_string())?;
        file.sync_all().map_err(|e| e.to_string())?;
        drop(file);
        let from = wide(&temp.to_string_lossy());
        let to = wide(&path.to_string_lossy());
        if unsafe {
            MoveFileExW(
                from.as_ptr(),
                to.as_ptr(),
                MOVEFILE_REPLACE_EXISTING | MOVEFILE_WRITE_THROUGH,
            )
        } == 0
        {
            Err(last_error())
        } else {
            Ok(())
        }
    }
    pub fn export(&self) -> Value {
        json!({"Games":self.games,"ProfileUrl":self.profile_url,"ShowProfileButton":self.show_profile_button,"GameSortOrder":self.game_sort_order})
    }
    pub fn read_backup(path: &Path) -> Result<Self> {
        if fs::metadata(path).map_err(|e| e.to_string())?.len() > 16 * 1024 * 1024 {
            return Err("Settings file is too large.".into());
        }
        let bytes = fs::read(path).map_err(|e| e.to_string())?;
        let data: Value =
            serde_json::from_slice(bytes.strip_prefix(&[239, 187, 191]).unwrap_or(&bytes))
                .map_err(|e| e.to_string())?;
        if !data["Games"].is_array() {
            return Err("Choose a VN Presence settings export.".into());
        }
        Self::load(path)
    }
    pub fn validate(&self) -> Result<()> {
        if self.games.len() > 10000 {
            return Err("Invalid game list.".into());
        }
        for g in &self.games {
            if !valid_exe_path(&g.exe)
                || !valid_id(&g.novel.id, 'v')
                || g.novel.title.trim().is_empty()
                || g.novel.titles.iter().any(|t| t.title.trim().is_empty())
                || g.novel
                    .developers
                    .as_deref()
                    .unwrap_or(&[])
                    .iter()
                    .any(|d| d.name.trim().is_empty())
            {
                return Err("Settings contain an invalid game.".into());
            }
            for im in [
                g.novel.image.as_ref(),
                g.cover.as_ref(),
                g.custom_cover.as_ref(),
            ]
            .into_iter()
            .flatten()
            {
                for url in [Some(&im.url), im.thumbnail.as_ref()].into_iter().flatten() {
                    if !crate::net::valid_image_url(url) {
                        return Err("Settings contain an invalid image URL.".into());
                    }
                }
            }
        }
        if self
            .profile_url
            .as_deref()
            .is_some_and(|s| !s.is_empty() && profile_url(s).is_none())
        {
            return Err("Invalid VNDB profile.".into());
        }
        Ok(())
    }
    pub fn sorted_indices(&self) -> Vec<usize> {
        let mut ix: Vec<_> = (0..self.games.len()).collect();
        let title = |i: usize| self.games[i].novel.native_title().to_lowercase();
        match self.game_sort_order.as_str() {
            "Alphabetical" => ix.sort_by_key(|&i| title(i)),
            "Last played" => {
                ix.sort_by_key(|&i| (std::cmp::Reverse(self.games[i].last_played), title(i)))
            }
            _ => {}
        };
        ix
    }
    pub fn owner(&mut self, path: &Path) -> Result<String> {
        if let Some(s) = &self.cover_owner_protected {
            let key = unprotect(s)?;
            if key.len() == 64
                && key
                    .bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
            {
                return Ok(key);
            }
            return Err("Invalid cover ownership key.".into());
        }
        let mut bytes = [0u8; 32];
        if unsafe {
            winapi::shared::bcrypt::BCryptGenRandom(ptr::null_mut(), bytes.as_mut_ptr(), 32, 2)
        } < 0
        {
            return Err("Could not generate cover ownership key.".into());
        }
        let key = bytes.iter().map(|b| format!("{b:02x}")).collect::<String>();
        self.cover_owner_protected = Some(protect(&key)?);
        if let Err(e) = self.save(path) {
            self.cover_owner_protected = None;
            return Err(e);
        }
        Ok(key)
    }
}
fn crypt(bytes: &[u8], encrypt: bool) -> Result<Vec<u8>> {
    let mut input = DATA_BLOB {
        cbData: bytes.len() as u32,
        pbData: bytes.as_ptr() as *mut _,
    };
    let mut output: DATA_BLOB = unsafe { std::mem::zeroed() };
    let ok = unsafe {
        if encrypt {
            CryptProtectData(
                &mut input,
                ptr::null(),
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
                1,
                &mut output,
            )
        } else {
            CryptUnprotectData(
                &mut input,
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
                ptr::null_mut(),
                1,
                &mut output,
            )
        }
    };
    if ok == 0 {
        return Err(last_error());
    }
    let result =
        unsafe { std::slice::from_raw_parts(output.pbData, output.cbData as usize).to_vec() };
    unsafe {
        LocalFree(output.pbData as _);
    };
    Ok(result)
}
pub fn protect(s: &str) -> Result<String> {
    Ok(STANDARD.encode(crypt(s.as_bytes(), true)?))
}
pub fn unprotect(s: &str) -> Result<String> {
    String::from_utf8(crypt(
        &STANDARD
            .decode(s)
            .map_err(|_| "Could not read saved token.")?,
        false,
    )?)
    .map_err(|_| "Could not read saved token.".into())
}

#[derive(Clone, Default)]
pub struct Running {
    pub pid: u32,
    pub exe: String,
    pub caption: String,
    pub started: i64,
}
unsafe extern "system" fn window_caption(hwnd: winapi::shared::windef::HWND, param: LPARAM) -> i32 {
    let found = &mut *(param as *mut BTreeMap<u32, String>);
    if IsWindowVisible(hwnd) == 0 || !GetWindow(hwnd, GW_OWNER).is_null() {
        return TRUE;
    }
    let mut pid = 0;
    GetWindowThreadProcessId(hwnd, &mut pid);
    if pid == std::process::id() || found.contains_key(&pid) {
        return TRUE;
    }
    let size = GetWindowTextLengthW(hwnd).min(32767) + 1;
    let mut buf = vec![0; size as usize];
    let n = GetWindowTextW(hwnd, buf.as_mut_ptr(), size);
    if n > 0 {
        found.insert(pid, String::from_utf16_lossy(&buf[..n as usize]));
    }
    TRUE
}
pub fn foreground_pid() -> u32 {
    let mut p = 0;
    unsafe {
        GetWindowThreadProcessId(GetForegroundWindow(), &mut p);
    };
    p
}
pub fn scan(games: Option<&[Game]>) -> Result<Vec<Running>> {
    if games.is_some_and(|g| g.is_empty()) {
        return Ok(vec![]);
    }
    let mut windows: BTreeMap<u32, String> = BTreeMap::new();
    unsafe {
        EnumWindows(Some(window_caption), &mut windows as *mut _ as LPARAM);
    }
    let names = games.map(|g| {
        g.iter()
            .filter_map(|g| Path::new(&g.exe).file_name())
            .map(|s| s.to_string_lossy().into_owned())
            .collect::<Vec<_>>()
    });
    let snapshot = unsafe { CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0) };
    if snapshot == INVALID_HANDLE_VALUE {
        return Err(last_error());
    }
    let mut list = vec![];
    unsafe {
        let mut entry: PROCESSENTRY32W = std::mem::zeroed();
        entry.dwSize = std::mem::size_of::<PROCESSENTRY32W>() as u32;
        let mut ok = Process32FirstW(snapshot, &mut entry);
        while ok != 0 {
            let name = String::from_utf16_lossy(
                &entry.szExeFile[..entry.szExeFile.iter().position(|&x| x == 0).unwrap_or(260)],
            );
            if entry.th32ProcessID != std::process::id()
                && names
                    .as_ref()
                    .map_or(windows.contains_key(&entry.th32ProcessID), |n| {
                        n.iter().any(|n| same_exe(n, &name))
                    })
            {
                let process = OpenProcess(0x1000, 0, entry.th32ProcessID);
                if !process.is_null() {
                    let mut path = vec![0u16; 32768];
                    let mut len = path.len() as u32;
                    let mut created: FILETIME = std::mem::zeroed();
                    let mut exited = created;
                    let mut kernel = created;
                    let mut user = created;
                    if QueryFullProcessImageNameW(process, 0, path.as_mut_ptr(), &mut len) != 0
                        && GetProcessTimes(
                            process,
                            &mut created,
                            &mut exited,
                            &mut kernel,
                            &mut user,
                        ) != 0
                    {
                        let ft =
                            ((created.dwHighDateTime as u64) << 32) | created.dwLowDateTime as u64;
                        list.push(Running {
                            pid: entry.th32ProcessID,
                            exe: String::from_utf16_lossy(&path[..len as usize]),
                            caption: windows
                                .get(&entry.th32ProcessID)
                                .cloned()
                                .unwrap_or_default(),
                            started: (ft / 10_000_000) as i64 - 11_644_473_600,
                        });
                    }
                    CloseHandle(process);
                }
            }
            ok = Process32NextW(snapshot, &mut entry);
        }
        let error = winapi::um::errhandlingapi::GetLastError();
        CloseHandle(snapshot);
        if error != 18 {
            return Err(format!("Could not scan games ({error})."));
        }
    }
    Ok(list)
}
pub fn choose(
    running: &[Running],
    games: &[Game],
    foreground: u32,
    previous: u32,
) -> Option<(Running, usize)> {
    running
        .iter()
        .flat_map(|p| {
            games
                .iter()
                .enumerate()
                .filter(move |(_, g)| same_exe(&p.exe, &g.exe))
                .map(move |(i, _)| (p, i))
        })
        .max_by_key(|(p, _)| (p.pid == foreground, p.pid == previous, p.started))
        .map(|(p, i)| (p.clone(), i))
}
pub fn profile_label(name: Option<&str>) -> String {
    let s = name
        .map(|n| format!("Visit {n}’s VNDB profile"))
        .unwrap_or_else(|| "VNDB profile".into());
    if s.encode_utf16().count() <= 32 {
        s
    } else {
        let mut n = 0;
        let mut label = String::new();
        for c in s.chars() {
            if n + c.len_utf16() > 31 {
                break;
            }
            label.push(c);
            n += c.len_utf16();
        }
        label.push('…');
        label
    }
}
pub fn activity(g: &Game, p: &Running, s: &Settings, username: Option<&str>) -> Value {
    let short = |s: &str| s.chars().take(120).collect::<String>();
    let mut a = json!({"type":0,"name":short(g.novel.native_title()),"status_display_type":0,"timestamps":{"start":p.started},"buttons":[{"label":"View on VNDB","url":g.novel.url()}]});
    let brand = g.novel.brand();
    if !brand.is_empty() {
        a["details"] = json!(short(&brand));
        if let Some(url) = g.novel.brand_url() {
            a["details_url"] = json!(url);
        }
    }
    if let Some(text) = g.progress(&p.caption) {
        a["state"] = json!(short(&text));
    }
    if s.show_profile_button {
        if let Some(url) = s.profile_url.as_deref().and_then(profile_url) {
            a["buttons"]
                .as_array_mut()
                .unwrap()
                .push(json!({"label":profile_label(username),"url":url}));
        }
    }
    if let Some(im) = g.image() {
        a["assets"] = json!({"large_image":im.url,"large_text":short(g.novel.native_title()),"large_url":g.novel.url()});
    }
    a
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn settings_detection_and_activity() {
        let mut s:Settings=serde_json::from_value(json!({"Games":[{"Exe":"C:\\game\\game.exe","Novel":{"Id":"v1","Title":"Himawari o","Olang":"ja","Titles":[{"Lang":"ja","Title":"向日葵","Main":false,"Latin":"Himawari o"}]},"RemoveProgressText":"R18","TrimEdges":"-()","LastPlayed":12}]})).unwrap();
        assert_eq!(
            s.games[0].progress("Himawari wo - Chapter 2 R18 -"),
            Some("Chapter 2".into())
        );
        assert_eq!(
            s.games[0].progress("向日葵 (long-time-no-see)"),
            Some("long-time-no-see".into())
        );
        assert_eq!(s.games[0].progress("向日葵extra"), None);
        s.games[0].custom_progress = Some("".into());
        assert_eq!(s.games[0].progress("向日葵 Chapter"), None);
        let secret = protect("test-token").unwrap();
        assert_eq!(unprotect(&secret).unwrap(), "test-token");
        s.vndb_token_protected = Some(secret);
        s.cover_owner_protected = Some("secret".into());
        assert!(!s.export().to_string().contains("secret"));
        assert!(!s.export().to_string().contains("Token"));
        s.validate().unwrap();
        s.profile_url = Some("https://vndb.org/u1".into());
        let a = activity(
            &s.games[0],
            &Running {
                started: 99,
                ..Default::default()
            },
            &s,
            Some("den-pa"),
        );
        assert_eq!(a["timestamps"]["start"], 99);
        assert_eq!(a["buttons"].as_array().unwrap().len(), 2);
        assert!(profile_label(Some(&"あ".repeat(40))).encode_utf16().count() <= 32);
        assert!(profile_url("https://vndb.org/u1?x").is_none());
        assert!(!valid_id("v0", 'v'));
        assert!(same_exe(r"C:\Games\ÉTÉ.EXE", r"c:\games\été.exe"));
        assert!(!same_exe(r"C:\one.exe", r"C:\two.exe"));
        assert!(!valid_exe_path("C:\\bad\0.exe"));
        assert!(!valid_exe_path(r"C:\cover.png"));
        let path = std::env::temp_dir().join(format!("vn-rust-test-{}.json", std::process::id()));
        s.save(&path).unwrap();
        assert_eq!(Settings::load(&path).unwrap().games[0].last_played, 12);
        let mut backup = vec![239, 187, 191];
        backup.extend(serde_json::to_vec(&s.export()).unwrap());
        fs::write(&path, backup).unwrap();
        assert_eq!(Settings::read_backup(&path).unwrap().games.len(), 1);
        let mut g = s.games[0].clone();
        g.cover = Some(Image {
            url: "https://example.com/a.png".into(),
            thumbnail: None,
        });
        g.custom_cover = g.cover.clone();
        g.forget_cover("https://example.com/a.png");
        assert!(g.cover.is_none() && g.custom_cover.is_none());
        fs::remove_file(path).unwrap();
    }
}
