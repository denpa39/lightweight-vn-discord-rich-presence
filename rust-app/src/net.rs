use crate::core::*;
use serde_json::{json, Value};
use std::{
    mem, ptr,
    time::{Duration, Instant},
};
use winapi::{
    shared::ntdef::HANDLE,
    um::{
        errhandlingapi::GetLastError, fileapi::*, handleapi::*, ioapiset::*,
        minwinbase::OVERLAPPED, synchapi::*, winbase::*, winhttp::*,
    },
};

pub const API: &str = "https://api.vndb.org/kana/";
pub const HOST: &str = "https://vn-presence-covers.vn-presence-denpa39.workers.dev";
#[cfg(test)]
static TEST_API: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);
#[cfg(test)]
static TEST_COVERS: std::sync::Mutex<Option<String>> = std::sync::Mutex::new(None);
pub struct Url {
    pub host: String,
    pub path: String,
    pub port: u16,
    pub secure: bool,
}
pub fn parse_url(s: &str) -> Result<Url> {
    if s.len() > 8192 || s.contains(['\r', '\n', '\0']) {
        return Err("Invalid URL.".into());
    }
    let text = wide(s);
    let mut u: URL_COMPONENTS = unsafe { mem::zeroed() };
    u.dwStructSize = mem::size_of::<URL_COMPONENTS>() as u32;
    u.dwHostNameLength = u32::MAX;
    u.dwUrlPathLength = u32::MAX;
    u.dwExtraInfoLength = u32::MAX;
    u.dwUserNameLength = u32::MAX;
    u.dwPasswordLength = u32::MAX;
    if unsafe { WinHttpCrackUrl(text.as_ptr(), (text.len() - 1) as u32, 0, &mut u) } == 0
        || u.dwHostNameLength == 0
        || u.dwUserNameLength > 0
        || u.dwPasswordLength > 0
        || ![INTERNET_SCHEME_HTTP, INTERNET_SCHEME_HTTPS].contains(&u.nScheme)
    {
        return Err("Use a direct HTTP or HTTPS image URL.".into());
    }
    let part = |p: *mut u16, n: u32| {
        if n == 0 {
            String::new()
        } else {
            unsafe { String::from_utf16_lossy(std::slice::from_raw_parts(p, n as usize)) }
        }
    };
    let mut path = part(u.lpszUrlPath, u.dwUrlPathLength);
    path.push_str(&part(u.lpszExtraInfo, u.dwExtraInfoLength));
    if path.is_empty() {
        path.push('/');
    }
    Ok(Url {
        host: part(u.lpszHostName, u.dwHostNameLength),
        path,
        port: u.nPort,
        secure: u.nScheme == INTERNET_SCHEME_HTTPS,
    })
}
pub fn valid_image_url(s: &str) -> bool {
    s.len() <= 300 && parse_url(s).is_ok()
}
pub fn hosted(s: &str) -> bool {
    let Some(path) = s.strip_prefix(&format!("{HOST}/covers/")) else {
        return false;
    };
    let hash = path
        .strip_suffix(".png")
        .or_else(|| path.strip_suffix(".jpg"));
    hash.is_some_and(|h| {
        h.len() == 64
            && h.bytes()
                .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b))
    })
}
struct HttpHandle(HINTERNET);
impl Drop for HttpHandle {
    fn drop(&mut self) {
        unsafe {
            WinHttpCloseHandle(self.0);
        }
    }
}
fn checked(h: HINTERNET) -> Result<HttpHandle> {
    if h.is_null() {
        Err(last_error())
    } else {
        Ok(HttpHandle(h))
    }
}
pub struct Response {
    pub status: u32,
    pub bytes: Vec<u8>,
}
pub fn request(
    method: &str,
    url: &str,
    headers: &str,
    body: &[u8],
    limit: usize,
) -> Result<Response> {
    #[cfg(test)]
    let redirected = TEST_COVERS
        .lock()
        .unwrap()
        .as_ref()
        .and_then(|base| url.strip_prefix(HOST).map(|path| format!("{base}{path}")));
    #[cfg(test)]
    let url = redirected.as_deref().unwrap_or(url);
    let u = parse_url(url)?;
    let agent = wide("VN Presence/2.0");
    let host = wide(&u.host);
    let method = wide(method);
    let path = wide(&u.path);
    let headers = wide(headers);
    unsafe {
        let session = checked(WinHttpOpen(
            agent.as_ptr(),
            WINHTTP_ACCESS_TYPE_AUTOMATIC_PROXY,
            ptr::null(),
            ptr::null(),
            0,
        ))?;
        WinHttpSetTimeouts(session.0, 8000, 8000, 8000, 8000);
        let connection = checked(WinHttpConnect(session.0, host.as_ptr(), u.port, 0))?;
        let req = checked(WinHttpOpenRequest(
            connection.0,
            method.as_ptr(),
            path.as_ptr(),
            ptr::null(),
            ptr::null(),
            ptr::null_mut(),
            if u.secure { WINHTTP_FLAG_SECURE } else { 0 },
        ))?;
        // No redirects: credentials and ownership keys must stay on the requested origin.
        let never = 0u32;
        if WinHttpSetOption(
            req.0,
            WINHTTP_OPTION_REDIRECT_POLICY,
            &never as *const _ as _,
            4,
        ) == 0
        {
            return Err(last_error());
        }
        if WinHttpSendRequest(
            req.0,
            headers.as_ptr(),
            (headers.len() - 1) as u32,
            if body.is_empty() {
                ptr::null_mut()
            } else {
                body.as_ptr() as _
            },
            body.len() as u32,
            body.len() as u32,
            0,
        ) == 0
            || WinHttpReceiveResponse(req.0, ptr::null_mut()) == 0
        {
            return Err(last_error());
        }
        let mut status = 0u32;
        let mut size = 4u32;
        if WinHttpQueryHeaders(
            req.0,
            WINHTTP_QUERY_STATUS_CODE | WINHTTP_QUERY_FLAG_NUMBER,
            ptr::null(),
            &mut status as *mut _ as _,
            &mut size,
            ptr::null_mut(),
        ) == 0
        {
            return Err(last_error());
        }
        let mut bytes = Vec::new();
        let mut buf = [0u8; 16384];
        loop {
            let mut n = 0;
            if WinHttpReadData(req.0, buf.as_mut_ptr() as _, buf.len() as u32, &mut n) == 0 {
                return Err(last_error());
            }
            if n == 0 {
                break;
            }
            if bytes.len() + n as usize > limit {
                return Err("Response is too large.".into());
            }
            bytes.extend_from_slice(&buf[..n as usize]);
        }
        Ok(Response { status, bytes })
    }
}
pub fn api(method: &str, path: &str, body: Option<Value>, token: Option<&str>) -> Result<Value> {
    if path.contains(['\r', '\n']) {
        return Err("Invalid API path.".into());
    }
    let mut headers = "Content-Type: application/json\r\n".to_owned();
    if let Some(t) = token {
        if !valid_token(t) {
            return Err("Paste a valid VNDB API token.".into());
        }
        headers.push_str(&format!("Authorization: Token {t}\r\n"));
    }
    let bytes = body.map(|v| v.to_string().into_bytes()).unwrap_or_default();
    #[cfg(test)]
    let base = TEST_API
        .lock()
        .unwrap()
        .clone()
        .unwrap_or_else(|| API.into());
    #[cfg(not(test))]
    let base = API;
    let r = request(
        method,
        &format!("{base}{path}"),
        &headers,
        &bytes,
        2 * 1024 * 1024,
    )?;
    match r.status {
        204 => Ok(Value::Null),
        200..=299 => {
            serde_json::from_slice(&r.bytes).map_err(|e| format!("Invalid VNDB response: {e}"))
        }
        401 => Err("VNDB token is invalid or revoked. Connect again.".into()),
        429 => Err("VNDB request limit reached. Try again in a minute.".into()),
        n => Err(format!("VNDB request failed ({n}). Try again.")),
    }
}
pub fn valid_token(s: &str) -> bool {
    let s = s.replace('-', "");
    s.len() == 32
        && s.bytes()
            .all(|b| b"ybndrfg8ejkmcpqxot1uwisza345h769".contains(&b))
}
pub fn image(url: &str) -> Result<Vec<u8>> {
    if !valid_image_url(url) {
        return Err("Invalid image URL.".into());
    }
    let r = request("GET", url, "", &[], 8 * 1024 * 1024)?;
    if !(200..300).contains(&r.status) {
        return Err(format!("Could not load image ({}).", r.status));
    }
    Ok(r.bytes)
}
pub fn search_title(s: &str) -> String {
    let s = s.trim();
    let re=regex::Regex::new(r"(?i)(?:\s+|[-–—|]\s*)(?:ver(?:sion)?\.?\s*\d+(?:\.\d+)*|v\s*\d+(?:\.\d+)+|R-?18\b|18\+(?:$|\s)|(?:day|chapter|epilogue)\s+\d+\b|(?:January|February|March|April|May|June|July|August|September|October|November|December)\s+\d{1,2}(?:st|nd|rd|th)?\b|\d{1,2}\s+(?:January|February|March|April|May|June|July|August|September|October|November|December)\b|\d{1,2}月\d{1,2}日).*$").unwrap();
    re.find(s)
        .map(|m| {
            s[..m.start()]
                .trim_end_matches([' ', '-', '–', '—', '|'])
                .to_owned()
        })
        .filter(|s| !s.is_empty())
        .unwrap_or_else(|| s.to_owned())
}
pub fn search(query: &str) -> Result<Vec<Novel>> {
    let q = search_title(query);
    if q.is_empty() {
        return Err("Enter a game title or VNDB URL.".into());
    }
    let id = q
        .trim_end_matches('/')
        .strip_prefix("https://vndb.org/")
        .or_else(|| q.trim_end_matches('/').strip_prefix("http://vndb.org/"))
        .unwrap_or(q.trim_end_matches('/'))
        .to_ascii_lowercase();
    let is_id = valid_id(&id, 'v');
    let normalized = regex::Regex::new(r"(?i)\bwo\b")
        .unwrap()
        .replace_all(&q, "o")
        .into_owned();
    let mut filters = if is_id {
        json!(["id", "=", id])
    } else {
        json!(["search", "=", q])
    };
    if !is_id && normalized != q {
        filters = json!(["or", filters, ["search", "=", normalized]]);
    }
    let result = api(
        "POST",
        "vn",
        Some(
            json!({"filters":filters,"sort":if is_id||normalized!=q{"id"}else{"searchrank"},"fields":"title,alttitle,olang,titles.lang,titles.title,titles.main,titles.latin,image.url,image.thumbnail,developers.id,developers.name","results":30}),
        ),
        None,
    )?;
    serde_json::from_value(result["results"].clone()).map_err(|e| e.to_string())
}
#[derive(Clone)]
pub struct Account {
    pub token: String,
    pub id: String,
    pub username: String,
    pub labels: Vec<Label>,
}
#[derive(Clone)]
pub struct Label {
    pub id: i32,
    pub name: String,
    pub private: bool,
}
#[derive(Clone, Default, PartialEq)]
pub struct Entry {
    pub vote: Option<i32>,
    pub labels: Vec<i32>,
    pub notes: String,
}
impl Account {
    pub fn connect(token: &str) -> Result<Self> {
        let data = api("GET", "authinfo", None, Some(token))?;
        let perms = data["permissions"]
            .as_array()
            .ok_or("Invalid VNDB permissions")?;
        if !["listread", "listwrite"]
            .iter()
            .all(|p| perms.contains(&json!(p)))
        {
            return Err("Enable both token permissions: Access private items on my list and Add/remove/edit items on my list.".into());
        }
        let id = data["id"]
            .as_str()
            .filter(|s| valid_id(s, 'u'))
            .ok_or("Invalid VNDB user ID")?
            .to_owned();
        let username = data["username"]
            .as_str()
            .ok_or("Invalid VNDB username")?
            .to_owned();
        let data = api("GET", "ulist_labels", None, Some(token))?;
        let mut labels = vec![];
        for l in data["labels"].as_array().ok_or("Invalid VNDB labels")? {
            let id = l["id"].as_i64().ok_or("Invalid VNDB label ID")? as i32;
            if id == 0 || id == 7 {
                continue;
            }
            labels.push(Label {
                id,
                name: l["label"].as_str().ok_or("Invalid VNDB label name")?.into(),
                private: l["private"].as_bool().unwrap_or(false),
            });
        }
        Ok(Self {
            token: token.into(),
            id,
            username,
            labels,
        })
    }
    pub fn read(&self, id: &str) -> Result<Entry> {
        if !valid_id(id, 'v') {
            return Err("Invalid game ID.".into());
        }
        let data = api(
            "POST",
            "ulist",
            Some(
                json!({"user":self.id,"filters":["id","=",id],"fields":"vote,labels.id,notes","results":1}),
            ),
            Some(&self.token),
        )?;
        let entries = data["results"].as_array().ok_or("Invalid VNDB entry")?;
        let Some(e) = entries.first() else {
            return Ok(Entry::default());
        };
        Ok(Entry {
            vote: e["vote"].as_i64().map(|v| v as i32),
            labels: e["labels"]
                .as_array()
                .ok_or("Invalid VNDB labels")?
                .iter()
                .filter_map(|l| l["id"].as_i64().map(|v| v as i32))
                .collect(),
            notes: e["notes"].as_str().unwrap_or("").into(),
        })
    }
    pub fn save(&self, id: &str, old: &Entry, new: &Entry) -> Result<()> {
        if !valid_id(id, 'v') {
            return Err("Invalid game ID.".into());
        }
        let changes = changes(old, new)?;
        api(
            "PATCH",
            &format!("ulist/{id}"),
            Some(changes),
            Some(&self.token),
        )?;
        Ok(())
    }
    pub fn save_notes(&self, id: &str, notes: &str) -> Result<()> {
        if !valid_id(id, 'v') {
            return Err("Invalid game ID.".into());
        }
        api(
            "PATCH",
            &format!("ulist/{id}"),
            Some(
                json!({"notes":if notes.is_empty(){Value::Null}else{json!(notes.replace("\r\n","\n"))}}),
            ),
            Some(&self.token),
        )?;
        Ok(())
    }
}
pub fn changes(old: &Entry, new: &Entry) -> Result<Value> {
    if new.vote.is_some_and(|v| !(10..=100).contains(&v)) {
        return Err("Vote must be from 1.0 to 10.0.".into());
    }
    if new.labels.iter().filter(|id| (1..=4).contains(*id)).count() > 1 {
        return Err("Choose one of Playing, Finished, Stalled or Dropped.".into());
    }
    let set: Vec<_> = new
        .labels
        .iter()
        .filter(|id| **id != 0 && **id != 7 && !old.labels.contains(id))
        .collect();
    let unset: Vec<_> = old
        .labels
        .iter()
        .filter(|id| **id != 0 && **id != 7 && !new.labels.contains(id))
        .collect();
    Ok(json!({"vote":new.vote,"labels_set":set,"labels_unset":unset}))
}

struct Handle(HANDLE);
impl Drop for Handle {
    fn drop(&mut self) {
        unsafe {
            CloseHandle(self.0);
        }
    }
}
unsafe impl Send for Handle {}
pub struct Discord {
    pipe: Option<Handle>,
    pub connected: bool,
    nonce: u64,
}
impl Default for Discord {
    fn default() -> Self {
        Self {
            pipe: None,
            connected: false,
            nonce: 0,
        }
    }
}
impl Discord {
    pub fn disconnect(&mut self) {
        self.pipe = None;
        self.connected = false;
    }
    fn transfer(&mut self, buf: &mut [u8], write: bool) -> Result<()> {
        let pipe = self.pipe.as_ref().ok_or("Discord is not running.")?.0;
        let event = unsafe { CreateEventW(ptr::null_mut(), 1, 0, ptr::null()) };
        if event.is_null() {
            return Err(last_error());
        }
        let event = Handle(event);
        let deadline = Instant::now() + Duration::from_secs(8);
        let mut offset = 0;
        while offset < buf.len() {
            let mut overlapped: OVERLAPPED = unsafe { mem::zeroed() };
            overlapped.hEvent = event.0;
            unsafe {
                ResetEvent(event.0);
            }
            let mut n = 0;
            let ok = unsafe {
                if write {
                    WriteFile(
                        pipe,
                        buf[offset..].as_ptr() as _,
                        (buf.len() - offset) as u32,
                        &mut n,
                        &mut overlapped,
                    )
                } else {
                    ReadFile(
                        pipe,
                        buf[offset..].as_mut_ptr() as _,
                        (buf.len() - offset) as u32,
                        &mut n,
                        &mut overlapped,
                    )
                }
            };
            if ok == 0 {
                let err = unsafe { GetLastError() };
                if err != 997 {
                    return Err(pipe_error(err));
                }
                let remaining = deadline
                    .saturating_duration_since(Instant::now())
                    .as_millis()
                    .min(8000) as u32;
                let wait = unsafe { WaitForSingleObject(event.0, remaining) };
                if wait != 0 {
                    unsafe {
                        CancelIoEx(pipe, &mut overlapped);
                        GetOverlappedResult(pipe, &mut overlapped, &mut n, 1);
                    }
                    return Err("Discord is not responding. Retrying…".into());
                }
                if unsafe { GetOverlappedResult(pipe, &mut overlapped, &mut n, 0) } == 0 {
                    return Err(pipe_error(unsafe { GetLastError() }));
                }
            }
            if n == 0 {
                return Err("Discord is not running.".into());
            }
            offset += n as usize;
        }
        Ok(())
    }
    fn write(&mut self, opcode: u32, body: &Value) -> Result<()> {
        let body = body.to_string();
        let mut frame = Vec::with_capacity(8 + body.len());
        frame.extend(opcode.to_le_bytes());
        frame.extend((body.len() as u32).to_le_bytes());
        frame.extend(body.as_bytes());
        self.transfer(&mut frame, true)
    }
    fn read(&mut self) -> Result<Value> {
        let deadline = Instant::now() + Duration::from_secs(8);
        loop {
            if Instant::now() > deadline {
                return Err("Discord is not responding. Retrying…".into());
            }
            let mut header = [0u8; 8];
            self.transfer(&mut header, false)?;
            let op = u32::from_le_bytes(header[..4].try_into().unwrap());
            let len = u32::from_le_bytes(header[4..].try_into().unwrap()) as usize;
            if len > 1024 * 1024 {
                return Err("Invalid Discord response size.".into());
            }
            let mut bytes = vec![0; len];
            self.transfer(&mut bytes, false)?;
            let data: Value =
                serde_json::from_slice(&bytes).map_err(|_| "Invalid Discord response.")?;
            match op {
                1 => return Ok(data),
                2 => return Err("Discord closed the connection.".into()),
                3 => self.write(4, &data)?,
                _ => {}
            }
        }
    }
    pub fn connect(&mut self, id: &str) -> Result<()> {
        self.connect_to(id, "discord-ipc-")
    }
    fn connect_to(&mut self, id: &str, prefix: &str) -> Result<()> {
        if self.connected {
            return Ok(());
        }
        self.disconnect();
        for i in 0..10 {
            let path = wide(&format!(r"\\.\pipe\{prefix}{i}"));
            let pipe = unsafe {
                CreateFileW(
                    path.as_ptr(),
                    0xc0000000,
                    0,
                    ptr::null_mut(),
                    OPEN_EXISTING,
                    FILE_FLAG_OVERLAPPED,
                    ptr::null_mut(),
                )
            };
            if pipe != INVALID_HANDLE_VALUE {
                self.pipe = Some(Handle(pipe));
                break;
            }
        }
        if self.pipe.is_none() {
            return Err("Discord is not running.".into());
        }
        self.write(0, &json!({"v":1,"client_id":id}))?;
        let r = self.read()?;
        if r["evt"] != "READY" {
            self.disconnect();
            return Err("Discord did not accept the connection.".into());
        }
        self.connected = true;
        Ok(())
    }
    pub fn update(&mut self, a: Option<Value>) -> Result<()> {
        self.nonce += 1;
        let nonce = format!("{}-{}", std::process::id(), self.nonce);
        self.write(1,&json!({"cmd":"SET_ACTIVITY","args":{"pid":std::process::id(),"activity":a},"nonce":nonce}))?;
        let deadline = Instant::now() + Duration::from_secs(8);
        loop {
            if Instant::now() > deadline {
                return Err("Discord is not responding. Retrying…".into());
            }
            let r = self.read()?;
            if r["nonce"] != nonce {
                continue;
            }
            if r["evt"] == "ERROR" {
                return Err(r["data"]["message"]
                    .as_str()
                    .unwrap_or("Discord rejected the activity.")
                    .into());
            }
            return Ok(());
        }
    }
}
fn pipe_error(code: u32) -> String {
    if [2, 109, 232, 233].contains(&code) {
        "Discord is not running.".into()
    } else {
        format!("Discord connection failed ({code}).")
    }
}

#[derive(Clone, Default)]
pub struct CoverChoice {
    pub label: String,
    pub image: Option<crate::core::Image>,
    pub owned: bool,
}
pub fn release_covers(
    id: &str,
    cancel: &std::sync::atomic::AtomicBool,
) -> Result<Vec<CoverChoice>> {
    if !valid_id(id, 'v') {
        return Err("Invalid game ID.".into());
    }
    let mut covers = vec![];
    for page in 1..=100 {
        if cancel.load(std::sync::atomic::Ordering::Relaxed) {
            return Err("Cover loading cancelled.".into());
        }
        if page > 1 {
            std::thread::sleep(Duration::from_secs(3));
        }
        let data = api(
            "POST",
            "release",
            Some(
                json!({"filters":["vn","=",["id","=",id]],"fields":"id,title,released,images.url,images.thumbnail,images.type,images.vn","sort":"released","results":100,"page":page}),
            ),
            None,
        )?;
        for release in data["results"].as_array().ok_or("Invalid VNDB covers")? {
            for im in release["images"].as_array().ok_or("Invalid VNDB images")? {
                if !["pkgfront", "dig"].contains(&im["type"].as_str().unwrap_or(""))
                    || im["vn"].as_str().is_some_and(|vn| vn != id)
                {
                    continue;
                }
                let Some(url) = im["url"].as_str().filter(|u| valid_image_url(u)) else {
                    continue;
                };
                if covers
                    .iter()
                    .any(|c: &CoverChoice| c.image.as_ref().is_some_and(|im| im.url == url))
                {
                    continue;
                }
                covers.push(CoverChoice {
                    label: format!(
                        "{} · {} ({})",
                        release["released"].as_str().unwrap_or(""),
                        release["title"].as_str().unwrap_or(""),
                        release["id"].as_str().unwrap_or("")
                    ),
                    image: Some(crate::core::Image {
                        url: url.into(),
                        thumbnail: im["thumbnail"].as_str().map(str::to_owned),
                    }),
                    owned: false,
                });
            }
        }
        if data["more"] != true {
            return Ok(covers);
        }
    }
    Err("Too many release covers. Try a more specific cover URL.".into())
}
fn owner_header(owner: Option<&str>) -> Result<String> {
    match owner {
        Some(o)
            if o.len() == 64
                && o.bytes()
                    .all(|b| b.is_ascii_digit() || (b'a'..=b'f').contains(&b)) =>
        {
            Ok(format!("X-Cover-Owner: {o}\r\n"))
        }
        Some(_) => Err("Invalid cover ownership key.".into()),
        None => Ok(String::new()),
    }
}
pub fn gallery(id: &str, owner: Option<&str>) -> Result<Vec<CoverChoice>> {
    if !valid_id(id, 'v') {
        return Err("Invalid game ID.".into());
    }
    let response = request(
        "GET",
        &format!("{HOST}/gallery/{id}"),
        &owner_header(owner)?,
        &[],
        32768,
    )?;
    if response.status != 200 {
        return Err(format!(
            "Could not load community covers ({}).",
            response.status
        ));
    }
    let data: Value =
        serde_json::from_slice(&response.bytes).map_err(|_| "Invalid community cover response")?;
    let mut covers = vec![];
    for item in data
        .as_array()
        .ok_or("Invalid community cover list")?
        .iter()
        .take(40)
    {
        let Some(url) = item["url"].as_str().filter(|u| hosted(u)) else {
            continue;
        };
        let owned = item["canRemove"] == true;
        covers.push(CoverChoice {
            label: format!(
                "Community cover {}{}",
                covers.len() + 1,
                if owned { " (your upload)" } else { "" }
            ),
            image: Some(crate::core::Image {
                url: url.into(),
                thumbnail: None,
            }),
            owned,
        });
    }
    Ok(covers)
}
pub fn remove_cover(url: &str, owner: &str) -> Result<()> {
    if !hosted(url) {
        return Err("Invalid hosted cover.".into());
    }
    let r = request("DELETE", url, &owner_header(Some(owner))?, &[], 4096)?;
    match r.status {
        204 | 404 => Ok(()),
        403 => Err("Only the original uploader can remove this image.".into()),
        n => Err(format!("Could not remove upload ({n}).")),
    }
}
pub fn touch(url: &str, id: &str) -> Result<()> {
    if !hosted(url) || !valid_id(id, 'v') {
        return Err("Invalid hosted cover.".into());
    }
    let r = request("POST", url, &format!("X-VNDB-ID: {id}\r\n"), &[], 4096)?;
    if r.status == 204 {
        Ok(())
    } else {
        Err(format!("Could not record cover use ({}).", r.status))
    }
}
pub fn upload(bytes: &[u8], id: &str, owner: &str) -> Result<crate::core::Image> {
    if !valid_id(id, 'v') || bytes.len() > 1280 * 1024 || !bytes.starts_with(b"\x89PNG\r\n\x1a\n") {
        return Err("Invalid cover upload.".into());
    }
    let headers = format!(
        "Content-Type: image/png\r\nX-VNDB-ID: {id}\r\n{}",
        owner_header(Some(owner))?
    );
    let r = request("POST", &format!("{HOST}/upload"), &headers, bytes, 4096)?;
    if !(200..300).contains(&r.status) {
        return Err(format!("Upload failed ({}). Try again.", r.status));
    }
    let data: Value = serde_json::from_slice(&r.bytes).map_err(|_| "Invalid upload response")?;
    let url = data["url"]
        .as_str()
        .filter(|u| hosted(u) && u.ends_with(".png"))
        .ok_or("Invalid uploaded cover URL")?;
    if url != format!("{HOST}/covers/{}.png", sha256(bytes)?) {
        return Err("Upload response did not match the image.".into());
    }
    Ok(crate::core::Image {
        url: url.into(),
        thumbnail: None,
    })
}
fn sha256(bytes: &[u8]) -> Result<String> {
    use winapi::shared::bcrypt::*;
    unsafe {
        let mut provider = ptr::null_mut();
        if BCryptOpenAlgorithmProvider(&mut provider, wide("SHA256").as_ptr(), ptr::null(), 0) < 0 {
            return Err("Could not hash cover.".into());
        }
        let mut hash = ptr::null_mut();
        let mut output = [0u8; 32];
        let mut status = BCryptCreateHash(
            provider,
            &mut hash,
            ptr::null_mut(),
            0,
            ptr::null_mut(),
            0,
            0,
        );
        if status >= 0 {
            status = BCryptHashData(hash, bytes.as_ptr() as _, bytes.len() as u32, 0);
            if status >= 0 {
                status = BCryptFinishHash(hash, output.as_mut_ptr(), 32, 0);
            }
            BCryptDestroyHash(hash);
        }
        BCryptCloseAlgorithmProvider(provider, 0);
        if status < 0 {
            return Err("Could not hash cover.".into());
        }
        Ok(output.iter().map(|b| format!("{b:02x}")).collect())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    fn receive(socket: &mut std::net::TcpStream) -> (String, Vec<u8>) {
        use std::io::Read;
        socket
            .set_read_timeout(Some(Duration::from_secs(5)))
            .unwrap();
        let mut received = vec![];
        loop {
            let mut b = [0; 4096];
            let n = socket.read(&mut b).unwrap();
            assert!(n > 0);
            received.extend_from_slice(&b[..n]);
            if let Some(end) = received.windows(4).position(|b| b == b"\r\n\r\n") {
                let header = String::from_utf8_lossy(&received[..end]).to_string();
                let len = header
                    .lines()
                    .find_map(|l| {
                        l.to_ascii_lowercase()
                            .strip_prefix("content-length:")
                            .and_then(|s| s.trim().parse::<usize>().ok())
                    })
                    .unwrap_or(0);
                if received.len() >= end + 4 + len {
                    return (header, received[end + 4..end + 4 + len].to_vec());
                }
            }
        }
    }
    #[test]
    fn cover_client_protocol() {
        use std::io::Write;
        let _engine = crate::images::Imaging::start().unwrap();
        let png = crate::images::Picture::blank(512, 512)
            .unwrap()
            .png()
            .unwrap();
        let fixture =
            std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("target/cover-protocol.png");
        std::fs::write(fixture, &png).unwrap();
        let hash = sha256(&png).unwrap();
        let url = format!("{HOST}/covers/{hash}.png");
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        *TEST_COVERS.lock().unwrap() = Some(format!("http://{}", listener.local_addr().unwrap()));
        let (expected_png, expected_url) = (png.clone(), url.clone());
        let server = std::thread::spawn(move || {
            for step in 0..9 {
                let (mut socket, _) = listener.accept().unwrap();
                let (header, body) = receive(&mut socket);
                let (status, response) = match step {
                    0 | 1 | 8 => {
                        assert!(header.starts_with("POST /upload "));
                        assert_eq!(body, expected_png);
                        assert!(header.contains("X-VNDB-ID: v1"));
                        assert!(header.contains(&format!("X-Cover-Owner: {}", "a".repeat(64))));
                        let url = if step == 8 {
                            format!("{HOST}/covers/{}.png", "0".repeat(64))
                        } else {
                            expected_url.clone()
                        };
                        (200, json!({"url":url}).to_string().into_bytes())
                    }
                    2 => {
                        assert!(header.starts_with("GET /gallery/v1 "));
                        (200, json!([{"url":expected_url,"canRemove":true},{"url":"https://example.com/bad.png","canRemove":true}]).to_string().into_bytes())
                    }
                    3 => {
                        assert!(header.starts_with("POST /covers/"));
                        assert!(header.contains("X-VNDB-ID: v1"));
                        (204, vec![])
                    }
                    4 | 5 => {
                        assert!(header.starts_with("DELETE /covers/"));
                        assert!(header.contains(&format!("X-Cover-Owner: {}", "a".repeat(64))));
                        (if step == 4 { 403 } else { 204 }, vec![])
                    }
                    6 => {
                        assert!(header.starts_with("GET /gallery/v1 "));
                        assert!(!header.contains("X-Cover-Owner:"));
                        (200, b"[]".to_vec())
                    }
                    _ => {
                        assert!(header.starts_with("GET /covers/"));
                        (200, expected_png.clone())
                    }
                };
                socket.write_all(format!("HTTP/1.1 {status} Test\r\nContent-Length: {}\r\nConnection: close\r\n\r\n", response.len()).as_bytes()).unwrap();
                socket.write_all(&response).unwrap();
            }
        });
        let owner = "a".repeat(64);
        assert_eq!(upload(&png, "v1", &owner).unwrap().url, url);
        assert_eq!(upload(&png, "v1", &owner).unwrap().url, url);
        let covers = gallery("v1", Some(&owner)).unwrap();
        assert_eq!(covers.len(), 1);
        assert!(covers[0].owned);
        touch(&url, "v1").unwrap();
        assert!(remove_cover(&url, &owner)
            .unwrap_err()
            .contains("original uploader"));
        remove_cover(&url, &owner).unwrap();
        assert!(gallery("v1", None).unwrap().is_empty());
        assert_eq!(image(&url).unwrap(), png);
        assert!(upload(&png, "v1", &owner)
            .err()
            .unwrap()
            .contains("did not match"));
        server.join().unwrap();
        *TEST_COVERS.lock().unwrap() = None;
    }
    #[test]
    fn account_and_discord_protocols() {
        use std::io::{Read, Write};
        let listener = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let addr = listener.local_addr().unwrap();
        let server = std::thread::spawn(move || {
            for step in 0..15 {
                let (mut socket, _) = listener.accept().unwrap();
                socket
                    .set_read_timeout(Some(Duration::from_secs(5)))
                    .unwrap();
                let mut received = vec![];
                let mut b = [0u8; 2048];
                let (header, body) = loop {
                    let n = socket.read(&mut b).unwrap();
                    assert!(n > 0);
                    received.extend(&b[..n]);
                    if let Some(end) = received.windows(4).position(|b| b == b"\r\n\r\n") {
                        let header = String::from_utf8_lossy(&received[..end]).to_string();
                        let len = header
                            .lines()
                            .find_map(|l| {
                                l.to_ascii_lowercase()
                                    .strip_prefix("content-length:")
                                    .and_then(|s| s.trim().parse::<usize>().ok())
                            })
                            .unwrap_or(0);
                        if received.len() >= end + 4 + len {
                            break (header, received[end + 4..end + 4 + len].to_vec());
                        }
                    }
                };
                if step < 14 {
                    assert!(header.contains(&format!("Authorization: Token {}", "y".repeat(32))));
                }
                let data: Value = if body.is_empty() {
                    Value::Null
                } else {
                    serde_json::from_slice(&body).unwrap()
                };
                let response = match step {
                    0 => {
                        assert!(header.starts_with("GET /authinfo "));
                        json!({"id":"u1","username":"test","permissions":["listread","listwrite"]})
                    }
                    1 => {
                        json!({"labels":[{"id":0,"label":"virtual","private":false},{"id":1,"label":"Playing","private":false},{"id":7,"label":"virtual","private":false},{"id":20,"label":"Custom","private":true}]})
                    }
                    2 => {
                        assert_eq!(data["user"], "u1");
                        json!({"results":[{"vote":50,"labels":[{"id":1},{"id":20}],"notes":"old note"}]})
                    }
                    3 => {
                        assert!(header.starts_with("PATCH /ulist/v1 "));
                        assert_eq!(data["vote"], 56);
                        assert!(data.get("notes").is_none());
                        Value::Null
                    }
                    4 => {
                        assert_eq!(data, json!({"notes":"Hello\nWorld"}));
                        Value::Null
                    }
                    5 => {
                        assert_eq!(data, json!({"notes":null}));
                        Value::Null
                    }
                    6 => {
                        assert_eq!(data, json!({"vote":50,"labels_set":[2],"labels_unset":[1]}));
                        Value::Null
                    }
                    7 | 8 => {
                        assert_eq!(data, json!({"vote":76,"labels_set":[5],"labels_unset":[]}));
                        Value::Null
                    }
                    9 => {
                        assert_eq!(data, json!({"vote":null,"labels_set":[],"labels_unset":[]}));
                        Value::Null
                    }
                    10 => {
                        assert_eq!(data, json!({"notes":"first"}));
                        Value::Null
                    }
                    11 => {
                        assert_eq!(data, json!({"notes":"second"}));
                        Value::Null
                    }
                    12 | 13 => {
                        assert_eq!(data, json!({"notes":"third"}));
                        Value::Null
                    }
                    _ => {
                        assert!(header.starts_with("POST /vn "));
                        assert_eq!(data["filters"], json!(["id", "=", "v1"]));
                        json!({"results":[{"id":"v1","title":"Test","titles":[],"developers":[]}]})
                    }
                };
                if (3..14).contains(&step) {
                    socket.write_all(if step==7 || step==12{b"HTTP/1.1 500 Error\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"}else{b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"}).unwrap();
                } else {
                    let text = response.to_string();
                    socket.write_all(format!("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",text.len(),text).as_bytes()).unwrap();
                }
            }
        });
        *TEST_API.lock().unwrap() = Some(format!("http://{addr}/"));
        let a = Account::connect(&"y".repeat(32)).unwrap();
        assert_eq!(a.labels.len(), 2);
        assert!(a.labels[1].private);
        let e = a.read("v1").unwrap();
        assert_eq!(e.notes, "old note");
        let new = Entry {
            vote: Some(56),
            labels: vec![2, 20],
            ..e.clone()
        };
        a.save("v1", &e, &new).unwrap();
        a.save_notes("v1", "Hello\r\nWorld").unwrap();
        a.save_notes("v1", "").unwrap();
        crate::ui::notes_check(a.clone());
        server.join().unwrap();
        *TEST_API.lock().unwrap() = None;
        assert_eq!(
            sha256(b"abc").unwrap(),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        use std::os::windows::io::FromRawHandle;
        use winapi::um::namedpipeapi::*;
        let prefix = format!("vn-rust-test-{}-", std::process::id());
        let name = wide(&format!(r"\\.\pipe\{prefix}0"));
        let handle =
            unsafe { CreateNamedPipeW(name.as_ptr(), 3, 0, 1, 4096, 4096, 0, ptr::null_mut()) };
        assert_ne!(handle, INVALID_HANDLE_VALUE);
        let handle = Handle(handle);
        let server = std::thread::spawn(move || {
            let ok = unsafe { ConnectNamedPipe(handle.0, ptr::null_mut()) };
            assert!(ok != 0 || unsafe { GetLastError() } == 535);
            let raw = handle.0;
            std::mem::forget(handle);
            let mut file = unsafe { std::fs::File::from_raw_handle(raw as _) };
            fn read(f: &mut std::fs::File) -> (u32, Value) {
                let mut h = [0u8; 8];
                f.read_exact(&mut h).unwrap();
                let len = u32::from_le_bytes(h[4..].try_into().unwrap()) as usize;
                assert!(len < 1024 * 1024);
                let mut b = vec![0; len];
                f.read_exact(&mut b).unwrap();
                (
                    u32::from_le_bytes(h[..4].try_into().unwrap()),
                    serde_json::from_slice(&b).unwrap(),
                )
            }
            fn write(f: &mut std::fs::File, op: u32, v: Value) {
                let b = v.to_string();
                f.write_all(&op.to_le_bytes()).unwrap();
                f.write_all(&(b.len() as u32).to_le_bytes()).unwrap();
                f.write_all(b.as_bytes()).unwrap();
            }
            let (op, hello) = read(&mut file);
            assert_eq!(op, 0);
            assert_eq!(hello["v"], 1);
            write(&mut file, 1, json!({"evt":"READY"}));
            write(&mut file, 3, json!({"ping":1}));
            let (op, a) = read(&mut file);
            assert_eq!(op, 1);
            assert_eq!(a["cmd"], "SET_ACTIVITY");
            assert_eq!(a["args"]["activity"]["timestamps"]["start"], 99);
            let (pong, p) = read(&mut file);
            assert_eq!(pong, 4);
            assert_eq!(p["ping"], 1);
            write(&mut file, 1, json!({"nonce":a["nonce"],"data":{}}));
        });
        let mut client = Discord::default();
        client.connect_to("1", &prefix).unwrap();
        client
            .update(Some(json!({"timestamps":{"start":99}})))
            .unwrap();
        client.disconnect();
        server.join().unwrap();
        assert_eq!(client.update(None).unwrap_err(), "Discord is not running.");
    }
    #[test]
    #[ignore = "Read-only checks against live services; requires internet"]
    fn public_service_reads() {
        let novels = search("https://vndb.org/v4").unwrap();
        assert_eq!(novels.len(), 1);
        assert_eq!(novels[0].id, "v4");
        let title_results = search("CLANNAD").unwrap();
        assert!(title_results.iter().any(|n| n.id == "v4"));
        let covers = release_covers("v4", &std::sync::atomic::AtomicBool::new(false)).unwrap();
        assert!(!covers.is_empty());
        let bytes = image(&covers[0].image.as_ref().unwrap().url).unwrap();
        let _imaging = crate::images::Imaging::start().unwrap();
        let picture = crate::images::Picture::load(&bytes).unwrap();
        assert!(picture.width > 0 && picture.height > 0);
        gallery("v4", None).unwrap();
        let path = Settings::directory().join("settings-rust.json");
        let settings = Settings::load(&path).unwrap();
        if let Some(token) = settings.vndb_token_protected.as_deref() {
            let account = Account::connect(&unprotect(token).unwrap()).unwrap();
            account.read("v4").unwrap();
        }
    }
    #[test]
    fn api_boundaries() {
        assert!(!valid_image_url("https://user:pass@vndb.org/a.png"));
        assert!(!valid_image_url("file:///C:/a"));
        let h = format!("{HOST}/covers/{}.png", "a".repeat(64));
        assert!(hosted(&h));
        assert!(!hosted(&(h + "?x")));
        assert_eq!(search_title("Kanon January 7th, Thursday"), "Kanon");
        assert_eq!(search_title("Fate/stay night"), "Fate/stay night");
        let old = Entry {
            vote: Some(50),
            labels: vec![1, 6, 20],
            notes: "leave this alone".into(),
        };
        let new = Entry {
            vote: Some(56),
            labels: vec![2, 6, 20],
            ..old.clone()
        };
        let c = changes(&old, &new).unwrap();
        assert_eq!(c["labels_set"], json!([2]));
        assert_eq!(c["labels_unset"], json!([1]));
        assert!(c.get("notes").is_none());
        assert!(changes(
            &old,
            &Entry {
                labels: vec![1, 2],
                ..new
            }
        )
        .is_err());
    }
}
