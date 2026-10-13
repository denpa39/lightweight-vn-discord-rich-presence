use crate::core::{last_error, Result};
use std::{ffi::c_void, ptr};
use winapi::{
    shared::guiddef::GUID,
    um::{
        combaseapi::{CreateStreamOnHGlobal, GetHGlobalFromStream},
        objidlbase::IStream,
        winbase::{GlobalLock, GlobalSize, GlobalUnlock},
    },
};
type Image = *mut c_void;
#[repr(C)]
struct StartupInput {
    version: u32,
    callback: *mut c_void,
    no_thread: i32,
    no_codecs: i32,
}
#[link(name = "gdiplus")]
extern "system" {
    fn GdiplusStartup(token: *mut usize, input: *const StartupInput, output: *mut c_void) -> i32;
    fn GdiplusShutdown(token: usize);
    fn GdipLoadImageFromStream(stream: *mut IStream, image: *mut Image) -> i32;
    fn GdipDisposeImage(image: Image) -> i32;
    fn GdipGetImageWidth(image: Image, width: *mut u32) -> i32;
    fn GdipGetImageHeight(image: Image, height: *mut u32) -> i32;
    fn GdipCreateBitmapFromScan0(
        w: i32,
        h: i32,
        stride: i32,
        format: i32,
        scan: *mut u8,
        image: *mut Image,
    ) -> i32;
    fn GdipGetImageGraphicsContext(image: Image, graphics: *mut Image) -> i32;
    fn GdipDeleteGraphics(graphics: Image) -> i32;
    fn GdipSetInterpolationMode(graphics: Image, mode: i32) -> i32;
    fn GdipGraphicsClear(graphics: Image, color: u32) -> i32;
    fn GdipDrawImageRectRect(
        graphics: Image,
        image: Image,
        x: f32,
        y: f32,
        w: f32,
        h: f32,
        sx: f32,
        sy: f32,
        sw: f32,
        sh: f32,
        unit: i32,
        attributes: Image,
        callback: Image,
        data: Image,
    ) -> i32;
    fn GdipSaveImageToStream(
        image: Image,
        stream: *mut IStream,
        encoder: *const GUID,
        params: Image,
    ) -> i32;
    #[cfg(test)]
    fn GdipBitmapGetPixel(image: Image, x: i32, y: i32, color: *mut u32) -> i32;
    fn GdipCreateSolidFill(color: u32, brush: *mut Image) -> i32;
    fn GdipDeleteBrush(brush: Image) -> i32;
    fn GdipFillRectangle(graphics: Image, brush: Image, x: f32, y: f32, w: f32, h: f32) -> i32;
    fn GdipCreateBitmapFromHBITMAP(
        bitmap: winapi::shared::windef::HBITMAP,
        palette: winapi::shared::windef::HPALETTE,
        image: *mut Image,
    ) -> i32;
}
#[link(name = "shlwapi")]
extern "system" {
    fn SHCreateMemStream(data: *const u8, size: u32) -> *mut IStream;
}
fn ok(status: i32) -> Result<()> {
    if status == 0 {
        Ok(())
    } else {
        Err(format!("Image operation failed ({status})."))
    }
}
pub struct Imaging(usize);
impl Imaging {
    pub fn start() -> Result<Self> {
        let mut token = 0;
        ok(unsafe {
            GdiplusStartup(
                &mut token,
                &StartupInput {
                    version: 1,
                    callback: ptr::null_mut(),
                    no_thread: 0,
                    no_codecs: 0,
                },
                ptr::null_mut(),
            )
        })?;
        Ok(Self(token))
    }
}
impl Drop for Imaging {
    fn drop(&mut self) {
        unsafe {
            GdiplusShutdown(self.0);
        }
    }
}
struct Stream(*mut IStream);
impl Drop for Stream {
    fn drop(&mut self) {
        unsafe {
            (*self.0).Release();
        }
    }
}
pub struct Picture {
    image: Image,
    _stream: Option<Stream>,
    pub width: u32,
    pub height: u32,
}
impl Drop for Picture {
    fn drop(&mut self) {
        unsafe {
            GdipDisposeImage(self.image);
        }
    }
}
impl Picture {
    pub fn load(bytes: &[u8]) -> Result<Self> {
        if bytes.is_empty() || bytes.len() > 8 * 1024 * 1024 {
            return Err("Image is empty or too large.".into());
        }
        let stream = unsafe { SHCreateMemStream(bytes.as_ptr(), bytes.len() as u32) };
        if stream.is_null() {
            return Err(last_error());
        }
        let stream = Stream(stream);
        let mut image = ptr::null_mut();
        ok(unsafe { GdipLoadImageFromStream(stream.0, &mut image) })?;
        let mut result = Self {
            image,
            _stream: Some(stream),
            width: 0,
            height: 0,
        };
        ok(unsafe { GdipGetImageWidth(image, &mut result.width) })?;
        ok(unsafe { GdipGetImageHeight(image, &mut result.height) })?;
        if result.width == 0
            || result.height == 0
            || result.width as u64 * result.height as u64 > 25_000_000
        {
            return Err("Choose an image below 25 megapixels.".into());
        }
        Ok(result)
    }
    pub(crate) fn blank(w: u32, h: u32) -> Result<Self> {
        let mut image = ptr::null_mut();
        ok(unsafe {
            GdipCreateBitmapFromScan0(w as i32, h as i32, 0, 0x26200a, ptr::null_mut(), &mut image)
        })?;
        Ok(Self {
            image,
            _stream: None,
            width: w,
            height: h,
        })
    }
    fn draw(&self, source: &Self, d: [f32; 4], s: [f32; 4], blur: bool) -> Result<()> {
        let mut graphics = ptr::null_mut();
        ok(unsafe { GdipGetImageGraphicsContext(self.image, &mut graphics) })?;
        let result = (|| {
            ok(unsafe { GdipGraphicsClear(graphics, 0) })?;
            ok(unsafe { GdipSetInterpolationMode(graphics, if blur { 2 } else { 7 }) })?;
            ok(unsafe {
                GdipDrawImageRectRect(
                    graphics,
                    source.image,
                    d[0],
                    d[1],
                    d[2],
                    d[3],
                    s[0],
                    s[1],
                    s[2],
                    s[3],
                    2,
                    ptr::null_mut(),
                    ptr::null_mut(),
                    ptr::null_mut(),
                )
            })
        })();
        unsafe {
            GdipDeleteGraphics(graphics);
        };
        result
    }
    pub fn output(
        &self,
        fit: bool,
        zoom: f32,
        center: (f32, f32),
        blur: bool,
        size: u32,
    ) -> Result<Self> {
        let output = Self::blank(size, size)?;
        let source = if fit {
            [0., 0., self.width as f32, self.height as f32]
        } else {
            crop(self.width, self.height, zoom, center)
        };
        let scale = if fit {
            size as f32 / self.width.max(self.height) as f32
        } else {
            size as f32 / source[2]
        };
        let w = source[2] * scale;
        let h = source[3] * scale;
        let dest = [(size as f32 - w) / 2., (size as f32 - h) / 2., w, h];
        if blur {
            let tiny = Self::blank(16, 16)?;
            tiny.draw(self, [0., 0., 16., 16.], source, false)?;
            output.draw(&tiny, dest, [0., 0., 16., 16.], true)?;
        } else {
            output.draw(self, dest, source, false)?;
        }
        Ok(output)
    }
    pub fn png(&self) -> Result<Vec<u8>> {
        let mut stream = ptr::null_mut();
        if unsafe { CreateStreamOnHGlobal(ptr::null_mut(), 1, &mut stream) } < 0 {
            return Err("Could not create image stream.".into());
        }
        let stream = Stream(stream);
        let png = GUID {
            Data1: 0x557cf406,
            Data2: 0x1a04,
            Data3: 0x11d3,
            Data4: [0x9a, 0x73, 0, 0, 0xf8, 0x1e, 0xf3, 0x2e],
        };
        ok(unsafe { GdipSaveImageToStream(self.image, stream.0, &png, ptr::null_mut()) })?;
        let mut global = ptr::null_mut();
        if unsafe { GetHGlobalFromStream(stream.0, &mut global) } < 0 {
            return Err("Could not encode image.".into());
        }
        let mut stats = unsafe { std::mem::zeroed::<winapi::um::objidlbase::STATSTG>() };
        if unsafe { (*stream.0).Stat(&mut stats, 1) } < 0 {
            return Err("Could not read image stream.".into());
        }
        let size = unsafe { *stats.cbSize.QuadPart() } as usize;
        if size > 8 * 1024 * 1024 || size > unsafe { GlobalSize(global) } {
            return Err("Encoded image is too large.".into());
        }
        let data = unsafe { GlobalLock(global) };
        if data.is_null() {
            return Err(last_error());
        }
        let bytes = unsafe { std::slice::from_raw_parts(data as *const u8, size).to_vec() };
        unsafe {
            GlobalUnlock(global);
        };
        Ok(bytes)
    }
    pub fn checkerboard(&self) -> Result<Self> {
        let output = Self::blank(self.width, self.height)?;
        let mut graphics = ptr::null_mut();
        ok(unsafe { GdipGetImageGraphicsContext(output.image, &mut graphics) })?;
        let result = (|| {
            ok(unsafe { GdipGraphicsClear(graphics, 0xffeeeeee) })?;
            let mut brush = ptr::null_mut();
            ok(unsafe { GdipCreateSolidFill(0xffcccccc, &mut brush) })?;
            for y in (0..self.height).step_by(12) {
                for x in (0..self.width).step_by(12) {
                    if (x / 12 + y / 12) % 2 == 0 {
                        unsafe {
                            GdipFillRectangle(graphics, brush, x as f32, y as f32, 12., 12.);
                        }
                    }
                }
            }
            unsafe {
                GdipDeleteBrush(brush);
            };
            ok(unsafe {
                GdipDrawImageRectRect(
                    graphics,
                    self.image,
                    0.,
                    0.,
                    self.width as f32,
                    self.height as f32,
                    0.,
                    0.,
                    self.width as f32,
                    self.height as f32,
                    2,
                    ptr::null_mut(),
                    ptr::null_mut(),
                    ptr::null_mut(),
                )
            })
        })();
        unsafe {
            GdipDeleteGraphics(graphics);
        };
        result?;
        Ok(output)
    }
    #[cfg(test)]
    fn pixel(&self, x: i32, y: i32) -> u32 {
        let mut v = 0;
        ok(unsafe { GdipBitmapGetPixel(self.image, x, y, &mut v) }).unwrap();
        v
    }
}
pub fn crop(w: u32, h: u32, zoom: f32, center: (f32, f32)) -> [f32; 4] {
    let side = w.min(h) as f32 / zoom.clamp(1., 4.);
    let half = side / 2.;
    [
        center.0.clamp(half, w as f32 - half) - half,
        center.1.clamp(half, h as f32 - half) - half,
        side,
        side,
    ]
}
pub fn capture(hwnd: winapi::shared::windef::HWND, path: &std::path::Path) -> Result<()> {
    use winapi::um::{wingdi::*, winuser::*};
    unsafe {
        let mut rect = std::mem::zeroed();
        GetWindowRect(hwnd, &mut rect);
        let width = rect.right - rect.left;
        let height = rect.bottom - rect.top;
        let dc = GetDC(hwnd);
        let memory = CreateCompatibleDC(dc);
        let bitmap = CreateCompatibleBitmap(dc, width, height);
        let previous = SelectObject(memory, bitmap as _);
        SendMessageW(
            hwnd,
            WM_PRINT,
            memory as usize,
            (PRF_CLIENT | PRF_NONCLIENT | PRF_CHILDREN | PRF_ERASEBKGND) as isize,
        );
        SelectObject(memory, previous);
        DeleteDC(memory);
        ReleaseDC(hwnd, dc);
        let mut image = ptr::null_mut();
        let result = ok(GdipCreateBitmapFromHBITMAP(
            bitmap,
            ptr::null_mut(),
            &mut image,
        ));
        DeleteObject(bitmap as _);
        result?;
        let picture = Picture {
            image,
            _stream: None,
            width: width as u32,
            height: height as u32,
        };
        std::fs::write(path, picture.png()?).map_err(|e| e.to_string())
    }
}
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn transparent_fit_and_crop() {
        let _engine = Imaging::start().unwrap();
        let p = Picture::blank(100, 200).unwrap();
        let mut g = ptr::null_mut();
        unsafe {
            GdipGetImageGraphicsContext(p.image, &mut g);
            GdipGraphicsClear(g, 0xffff0000);
            GdipDeleteGraphics(g);
        }
        let fit = p.output(true, 1., (50., 100.), false, 512).unwrap();
        assert_eq!(fit.pixel(0, 256) >> 24, 0);
        assert!(fit.pixel(256, 256) >> 24 > 250);
        let cropped = p.output(false, 1., (50., 100.), false, 512).unwrap();
        assert!(cropped.pixel(5, 5) >> 24 > 250);
        let blurred = p.output(true, 1., (50., 100.), true, 512).unwrap();
        assert_eq!(blurred.pixel(0, 256) >> 24, 0);
        let bytes = fit.png().unwrap();
        let decoded = Picture::load(&bytes).unwrap();
        assert_eq!((decoded.width, decoded.height), (512, 512));
        assert_eq!(decoded.pixel(0, 256) >> 24, 0);
        assert_eq!(crop(100, 200, 2., (-99., 999.)), [0., 150., 50., 50.]);
    }
}
