// DDSD Launcher 2: installs, updates and starts Doki Doki Second Draft on Windows and Linux.
// The player brings their own DDLC files (audio.rpa, fonts.rpa, images.rpa); nothing of DDLC is downloaded.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use futures_util::StreamExt;
use serde::{Deserialize, Serialize};
use sha2::{Digest, Sha256};
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use tauri::{AppHandle, Emitter};

const FEED: &str = "https://github.com/Aoi-doki/ddsd-releases/releases/latest/download/";
const NEWS: &str = "https://aoi-doki.github.io/ddsd/news.json";
const RPAS: [&str; 3] = ["audio.rpa", "fonts.rpa", "images.rpa"];
const LAUNCHER_VERSION: &str = "2.0";

#[cfg(windows)]
const GAME_EXE: &str = "DokiDokiSecondDraft.exe";
#[cfg(not(windows))]
const GAME_EXE: &str = "DokiDokiSecondDraft.sh";

#[derive(Serialize, Deserialize, Clone, Default)]
struct Latest {
    version: String,
    #[serde(default)]
    notes: String,
    game_zip: String,
    game_sha256: String,
    #[serde(default)]
    game_size: u64,
    #[serde(default)]
    launcher_version: String,
}

#[derive(Serialize)]
struct Status {
    installed: Option<String>,
    ddlc_ok: bool,
    home: String,
    platform: &'static str,
    launcher_version: &'static str,
}

#[derive(Serialize, Clone)]
struct Progress {
    phase: String,
    done: u64,
    total: u64,
}

fn home() -> PathBuf {
    if let Ok(h) = std::env::var("DDSD_HOME") {
        return PathBuf::from(h);
    }
    dirs::data_local_dir().unwrap_or_else(|| PathBuf::from(".")).join("DDSD")
}

fn game_dir() -> PathBuf {
    home().join("game")
}

fn rpa_dir() -> PathBuf {
    game_dir().join("game")
}

fn installed_version() -> Option<String> {
    let v = fs::read_to_string(home().join("version.txt")).ok()?;
    if game_dir().join(GAME_EXE).exists() {
        Some(v.trim().to_string())
    } else {
        None
    }
}

fn has_ddlc() -> bool {
    RPAS.iter().all(|r| rpa_dir().join(r).exists())
}

#[tauri::command]
fn status() -> Status {
    Status {
        installed: installed_version(),
        ddlc_ok: has_ddlc(),
        home: home().to_string_lossy().into_owned(),
        platform: if cfg!(windows) { "windows" } else { "linux" },
        launcher_version: LAUNCHER_VERSION,
    }
}

fn feed_base() -> String {
    std::env::var("DDSD_FEED").unwrap_or_else(|_| FEED.to_string())
}

fn client() -> Result<reqwest::Client, String> {
    reqwest::Client::builder()
        .user_agent(format!("DDSD-Launcher/{}", LAUNCHER_VERSION))
        .build()
        .map_err(|e| e.to_string())
}

async fn fetch_text(url: &str) -> Result<String, String> {
    if !url.starts_with("http") {
        return fs::read_to_string(url).map_err(|e| e.to_string());
    }
    let r = client()?.get(url).send().await.map_err(|e| e.to_string())?;
    if !r.status().is_success() {
        return Err(format!("the server answered {}", r.status()));
    }
    r.text().await.map_err(|e| e.to_string())
}

fn join_feed(name: &str) -> String {
    let b = feed_base();
    if b.starts_with("http") {
        format!("{}{}", b, name)
    } else {
        Path::new(&b).join(name).to_string_lossy().into_owned()
    }
}

#[tauri::command]
async fn check_update() -> Result<Latest, String> {
    let t = fetch_text(&join_feed("latest.json")).await?;
    serde_json::from_str(&t).map_err(|e| format!("latest.json is not readable: {}", e))
}

#[tauri::command]
async fn news() -> Result<String, String> {
    fetch_text(NEWS).await
}

fn sha256_file(p: &Path) -> Result<String, String> {
    let mut f = fs::File::open(p).map_err(|e| e.to_string())?;
    let mut h = Sha256::new();
    let mut buf = vec![0u8; 1 << 20];
    loop {
        let n = f.read(&mut buf).map_err(|e| e.to_string())?;
        if n == 0 {
            break;
        }
        h.update(&buf[..n]);
    }
    Ok(hex::encode(h.finalize()))
}

fn emit(app: &AppHandle, phase: &str, done: u64, total: u64) {
    let _ = app.emit("progress", Progress { phase: phase.to_string(), done, total });
}

async fn download(app: &AppHandle, name: &str, dest: &Path) -> Result<(), String> {
    let url = join_feed(name);
    if !url.starts_with("http") {
        fs::copy(&url, dest).map_err(|e| e.to_string())?;
        return Ok(());
    }
    let r = client()?.get(&url).send().await.map_err(|e| e.to_string())?;
    if !r.status().is_success() {
        return Err(format!("the download failed ({})", r.status()));
    }
    let total = r.content_length().unwrap_or(0);
    let mut out = fs::File::create(dest).map_err(|e| e.to_string())?;
    let mut done = 0u64;
    let mut last = 0u64;
    let mut stream = r.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk.map_err(|e| e.to_string())?;
        out.write_all(&chunk).map_err(|e| e.to_string())?;
        done += chunk.len() as u64;
        if done - last > 400_000 || done == total {
            last = done;
            emit(app, "download", done, total);
        }
    }
    Ok(())
}

// Which parts of the engine zip belong to this platform.
fn wanted(rel: &str) -> bool {
    if rel.starts_with("DokiDokiSecondDraft.app/") || rel.starts_with("lib/py3-mac") {
        return false;
    }
    if cfg!(windows) {
        !(rel.starts_with("lib/py3-linux") || rel.ends_with(".sh") || rel == "README.linux")
    } else {
        !(rel.starts_with("lib/py3-windows") || rel.ends_with(".exe"))
    }
}

fn unpack(zip_path: &Path, dest: &Path) -> Result<(), String> {
    let f = fs::File::open(zip_path).map_err(|e| e.to_string())?;
    let mut z = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
    for i in 0..z.len() {
        let mut e = z.by_index(i).map_err(|e| e.to_string())?;
        let name = e.name().replace('\\', "/");
        let rel = match name.split_once('/') {
            Some((_, r)) if !r.is_empty() && !r.ends_with('/') => r.to_string(),
            _ => continue,
        };
        if rel.contains("..") || !wanted(&rel) {
            continue;
        }
        let to = dest.join(&rel);
        if let Some(p) = to.parent() {
            fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
        let mut out = fs::File::create(&to).map_err(|e| e.to_string())?;
        std::io::copy(&mut e, &mut out).map_err(|e| e.to_string())?;
        #[cfg(unix)]
        if let Some(mode) = e.unix_mode() {
            use std::os::unix::fs::PermissionsExt;
            let _ = fs::set_permissions(&to, fs::Permissions::from_mode(mode));
        }
    }
    Ok(())
}

// Kept on update: saves, DDLC's rpa files, logs.
fn keep(rel: &Path) -> bool {
    let s = rel.to_string_lossy().replace('\\', "/").to_lowercase();
    s.starts_with("game/saves")
        || RPAS.iter().any(|r| s == format!("game/{}", r))
        || s == "log.txt"
        || s == "traceback.txt"
        || s == "errors.txt"
}

fn swap_in(src: &Path, dst: &Path) -> Result<(), String> {
    fs::create_dir_all(dst).map_err(|e| e.to_string())?;
    for e in walkdir::WalkDir::new(dst).into_iter().filter_map(|e| e.ok()).filter(|e| e.file_type().is_file()) {
        let rel = e.path().strip_prefix(dst).unwrap();
        if !keep(rel) && !src.join(rel).exists() {
            let _ = fs::remove_file(e.path());
        }
    }
    for e in walkdir::WalkDir::new(src).into_iter().filter_map(|e| e.ok()).filter(|e| e.file_type().is_file()) {
        let rel = e.path().strip_prefix(src).unwrap();
        if keep(rel) {
            continue;
        }
        let to = dst.join(rel);
        if let Some(p) = to.parent() {
            fs::create_dir_all(p).map_err(|e| e.to_string())?;
        }
        fs::copy(e.path(), &to).map_err(|e| e.to_string())?;
    }
    Ok(())
}

fn game_running() -> bool {
    #[cfg(windows)]
    {
        std::process::Command::new("tasklist")
            .args(["/FI", "IMAGENAME eq DokiDokiSecondDraft.exe"])
            .output()
            .map(|o| String::from_utf8_lossy(&o.stdout).contains("DokiDokiSecondDraft.exe"))
            .unwrap_or(false)
    }
    #[cfg(not(windows))]
    {
        std::process::Command::new("pgrep")
            .args(["-f", "lib/py3-linux-x86_64/DokiDokiSecondDraft"])
            .output()
            .map(|o| o.status.success())
            .unwrap_or(false)
    }
}

#[tauri::command]
async fn install(app: AppHandle) -> Result<String, String> {
    if game_running() {
        return Err("Close Doki Doki Second Draft first, then try again.".into());
    }
    let latest = check_update().await?;
    let tmp = home().join("download");
    fs::create_dir_all(&tmp).map_err(|e| e.to_string())?;
    let zip = tmp.join(&latest.game_zip);
    download(&app, &latest.game_zip, &zip).await?;
    emit(&app, "check", 0, 0);
    let a = zip.clone();
    let got = tauri::async_runtime::spawn_blocking(move || sha256_file(&a)).await.map_err(|e| e.to_string())??;
    if !got.eq_ignore_ascii_case(&latest.game_sha256) {
        return Err("The download was damaged (checksum mismatch). Please try again.".into());
    }
    emit(&app, "unpack", 0, 0);
    let root = home().join("new");
    let _ = fs::remove_dir_all(&root);
    let (z, r) = (zip.clone(), root.clone());
    tauri::async_runtime::spawn_blocking(move || unpack(&z, &r)).await.map_err(|e| e.to_string())??;
    if !root.join(GAME_EXE).exists() {
        return Err(format!("The download does not contain {}.", GAME_EXE));
    }
    emit(&app, "install", 0, 0);
    let (s, d) = (root.clone(), game_dir());
    tauri::async_runtime::spawn_blocking(move || swap_in(&s, &d)).await.map_err(|e| e.to_string())??;
    let _ = fs::remove_dir_all(&root);
    fs::write(home().join("version.txt"), &latest.version).map_err(|e| e.to_string())?;
    fs::write(home().join("build.txt"), &latest.game_sha256).map_err(|e| e.to_string())?;
    let _ = fs::remove_dir_all(&tmp);
    if !has_ddlc() {
        find_ddlc_inner();
    }
    emit(&app, "done", 0, 0);
    Ok(latest.version)
}

fn steam_libraries() -> Vec<PathBuf> {
    let mut libs = Vec::new();
    let mut steam_roots: Vec<PathBuf> = Vec::new();
    if let Some(h) = dirs::home_dir() {
        steam_roots.push(h.join(".local/share/Steam"));
        steam_roots.push(h.join(".steam/steam"));
        steam_roots.push(h.join(".var/app/com.valvesoftware.Steam/.local/share/Steam"));
    }
    #[cfg(windows)]
    {
        steam_roots.push(PathBuf::from(r"C:\Program Files (x86)\Steam"));
        steam_roots.push(PathBuf::from(r"C:\Program Files\Steam"));
    }
    for root in steam_roots {
        if !root.exists() {
            continue;
        }
        libs.push(root.clone());
        if let Ok(t) = fs::read_to_string(root.join("steamapps/libraryfolders.vdf")) {
            for line in t.lines() {
                let l = line.trim();
                if l.starts_with("\"path\"") {
                    if let Some(p) = l.split('"').nth(3) {
                        libs.push(PathBuf::from(p.replace("\\\\", "\\")));
                    }
                }
            }
        }
    }
    libs
}

fn candidates() -> Vec<PathBuf> {
    let mut c: Vec<PathBuf> = steam_libraries()
        .into_iter()
        .map(|l| l.join("steamapps/common/Doki Doki Literature Club"))
        .collect();
    if let Some(h) = dirs::home_dir() {
        for place in ["Downloads", "Desktop", "Documents", ""] {
            let p = h.join(place);
            if let Ok(rd) = fs::read_dir(&p) {
                for e in rd.filter_map(|e| e.ok()) {
                    let n = e.file_name().to_string_lossy().to_lowercase();
                    if n.starts_with("ddlc") || n.starts_with("doki doki") {
                        c.push(e.path());
                    }
                }
            }
        }
    }
    c
}

// Copies the three rpa files from a DDLC folder or a DDLC zip.
fn take_rpas(path: &Path) -> bool {
    let _ = fs::create_dir_all(rpa_dir());
    if path.is_file() && path.extension().map(|e| e.eq_ignore_ascii_case("zip")).unwrap_or(false) {
        let f = match fs::File::open(path) { Ok(f) => f, Err(_) => return false };
        let mut z = match zip::ZipArchive::new(f) { Ok(z) => z, Err(_) => return false };
        let mut found = Vec::new();
        for i in 0..z.len() {
            if let Ok(e) = z.by_index(i) {
                let n = e.name().replace('\\', "/").to_lowercase();
                for r in RPAS {
                    if n.ends_with(&format!("game/{}", r)) {
                        found.push((i, r));
                    }
                }
            }
        }
        if found.len() != RPAS.len() {
            return false;
        }
        for (i, r) in found {
            if let Ok(mut e) = z.by_index(i) {
                if let Ok(mut out) = fs::File::create(rpa_dir().join(r)) {
                    let _ = std::io::copy(&mut e, &mut out);
                }
            }
        }
        return has_ddlc();
    }
    let dir = if path.is_file() { path.parent().map(|p| p.to_path_buf()).unwrap_or_default() } else { path.to_path_buf() };
    for sub in ["game", ""] {
        let d = if sub.is_empty() { dir.clone() } else { dir.join(sub) };
        if RPAS.iter().all(|r| d.join(r).exists()) {
            for r in RPAS {
                let _ = fs::copy(d.join(r), rpa_dir().join(r));
            }
            return has_ddlc();
        }
    }
    false
}

fn find_ddlc_inner() -> bool {
    for c in candidates() {
        if take_rpas(&c) {
            return true;
        }
    }
    false
}

#[tauri::command]
fn find_ddlc() -> bool {
    has_ddlc() || find_ddlc_inner()
}

#[tauri::command]
fn use_ddlc(path: String) -> bool {
    take_rpas(Path::new(&path))
}

#[tauri::command]
fn play() -> Result<(), String> {
    let exe = game_dir().join(GAME_EXE);
    if !exe.exists() {
        return Err("The game is not installed yet.".into());
    }
    let mut cmd = std::process::Command::new(&exe);
    cmd.current_dir(game_dir());
    cmd.spawn().map_err(|e| format!("Could not start the game: {}", e))?;
    Ok(())
}

#[tauri::command]
fn uninstall() -> Result<(), String> {
    if game_running() {
        return Err("Close the game first.".into());
    }
    // Everything except the saves goes.
    let g = game_dir();
    if g.exists() {
        for e in walkdir::WalkDir::new(&g).contents_first(true).into_iter().filter_map(|e| e.ok()) {
            let rel = e.path().strip_prefix(&g).unwrap();
            if keep(rel) && rel.to_string_lossy().replace('\\', "/").to_lowercase().starts_with("game/saves") {
                continue;
            }
            if e.file_type().is_file() {
                let _ = fs::remove_file(e.path());
            } else {
                let _ = fs::remove_dir(e.path());
            }
        }
    }
    let _ = fs::remove_file(home().join("version.txt"));
    let _ = fs::remove_file(home().join("build.txt"));
    Ok(())
}

fn main() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_opener::init())
        .invoke_handler(tauri::generate_handler![status, check_update, news, install, find_ddlc, use_ddlc, play, uninstall])
        .run(tauri::generate_context!())
        .expect("error while running the launcher");
}

#[cfg(test)]
mod tests {
    use super::*;

    // Run with DDSD_TEST_ZIP=<path to ddsd-game.zip>: unpacks it into a temp dir and checks the platform filter.
    #[test]
    fn unpack_and_swap() {
        let Ok(zip) = std::env::var("DDSD_TEST_ZIP") else { return };
        let base = std::env::temp_dir().join("ddsd-launcher-test");
        let _ = fs::remove_dir_all(&base);
        let (a, b) = (base.join("new"), base.join("game"));
        unpack(Path::new(&zip), &a).unwrap();
        assert!(a.join(GAME_EXE).exists(), "game launcher script/exe missing");
        assert!(a.join("game/scripts.rpa").exists());
        assert!(a.join("game/mod_assets.rpa").exists());
        if cfg!(windows) {
            assert!(!a.join("lib/py3-linux-x86_64").exists());
        } else {
            assert!(!a.join("DokiDokiSecondDraft.exe").exists());
            assert!(a.join("lib/py3-linux-x86_64/DokiDokiSecondDraft").exists());
            use std::os::unix::fs::PermissionsExt;
            let m = fs::metadata(a.join("lib/py3-linux-x86_64/DokiDokiSecondDraft")).unwrap().permissions().mode();
            assert!(m & 0o111 != 0, "engine binary is not executable");
        }
        // a save and a DDLC rpa must survive a swap
        fs::create_dir_all(b.join("game/saves")).unwrap();
        fs::write(b.join("game/saves/1-1.save"), "x").unwrap();
        fs::write(b.join("game/audio.rpa"), "x").unwrap();
        fs::write(b.join("old-file.txt"), "x").unwrap();
        swap_in(&a, &b).unwrap();
        assert!(b.join("game/saves/1-1.save").exists());
        assert!(b.join("game/audio.rpa").exists());
        assert!(!b.join("old-file.txt").exists());
        assert!(b.join("game/scripts.rpa").exists());
        let _ = fs::remove_dir_all(&base);
    }

    #[test]
    fn ddlc_zip_and_folder() {
        let base = std::env::temp_dir().join("ddsd-launcher-test2");
        let _ = fs::remove_dir_all(&base);
        std::env::set_var("DDSD_HOME", &base);
        let src = base.join("DDLC-1.1.1/game");
        fs::create_dir_all(&src).unwrap();
        for r in RPAS {
            fs::write(src.join(r), "x").unwrap();
        }
        assert!(take_rpas(&base.join("DDLC-1.1.1")));
        assert!(has_ddlc());
        let _ = fs::remove_dir_all(&base);
    }
}
