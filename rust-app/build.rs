use std::{env, path::PathBuf, process::Command};
fn main() {
    println!("cargo:rerun-if-changed=../app.ico");
    println!("cargo:rerun-if-changed=app.manifest");
    let out = PathBuf::from(env::var_os("OUT_DIR").unwrap());
    let rc = out.join("app.rc");
    let root = PathBuf::from(env::var_os("CARGO_MANIFEST_DIR").unwrap());
    let icon = root.join("../app.ico").canonicalize().unwrap();
    let manifest = root.join("app.manifest");
    std::fs::write(
        &rc,
        format!(
            "1 ICON \"{}\"\n1 24 \"{}\"\n",
            icon.to_string_lossy().replace('\\', "/"),
            manifest.to_string_lossy().replace('\\', "/")
        ),
    )
    .unwrap();
    let obj = out.join("app.o");
    assert!(Command::new("windres")
        .arg(&rc)
        .arg(&obj)
        .status()
        .expect("MinGW windres")
        .success());
    println!("cargo:rustc-link-arg={}", obj.display());
}
