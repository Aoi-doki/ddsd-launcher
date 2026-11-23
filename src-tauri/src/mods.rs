// The Mods tab: other people's DDLC mods, each in its own fresh copy of the player's DDLC (never touches the DDSD install).
//   Add a mod   a mod zip + the player's own full DDLC (zip or folder) -> <home>/mods/<name>/
//   My mods     play, open folder, remove
//   Browse      the Mod Hub catalog; hub-hosted mods install in one click, others open their own page
//   Share       POST to the Mod Hub for review; Report; beta codes
use crate::{client, emit, home, join_feed_pub, RPAS};
use serde::{Deserialize, Serialize};
use serde_json::Value;
use sha2::{Digest, Sha256};
use std::collections::HashMap;
use std::fs;
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use tauri::AppHandle;

const MODHUB: &str = "https://ddsd-modhub.callmerileyyyyy.workers.dev";
const CATALOG_URL: &str = "https://raw.githubusercontent.com/Aoi-doki/ddsd-mods/main/catalog.json";
const MOD_INFO: &str = "ddsd_mod.json";
const MAX_UPLOAD: u64 = 95 << 20;
const ASSET_DIRS: [&str; 10] = ["mod_assets", "images", "audio", "gui", "fonts", "python-packages", "submods", "tl", "characters", "game"];

#[cfg(windows)]
const DDLC_EXE: &str = "DDLC.exe";
#[cfg(not(windows))]
const DDLC_EXE: &str = "DDLC.sh";

fn mods_dir() -> PathBuf {
    home().join("mods")
}

fn hub() -> String {
    std::env::var("DDSD_MODHUB").unwrap_or_else(|_| MODHUB.to_string()).trim_end_matches('/').to_string()
}

#[derive(Serialize, Deserialize, Clone, Default)]
pub struct ModInfo {
    pub name: String,
    #[serde(default)]
    pub exe: String,
    #[serde(default)]
    pub added: String,
    #[serde(default)]
    pub size: u64,
    #[serde(default)]
    pub hub_id: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub license: Option<String>,
    #[serde(default)]
    pub author: Option<String>,
    #[serde(default)]
    pub share_id: Option<String>,
    #[serde(default)]
    pub share_state: Option<String>,
    #[serde(default)]
    pub dir: String,
}

fn clean_name(n: &str) -> String {
    let s: String = n.chars().map(|c| if c.is_alphanumeric() || " .-_()".contains(c) { c } else { ' ' }).collect();
    let s = s.trim().trim_matches('.').to_string();
    if s.is_empty() { "Mod".into() } else { s.chars().take(60).collect() }
}

fn mod_name_from_zip(zip: &str) -> String {
    let stem = Path::new(zip).file_stem().map(|s| s.to_string_lossy().replace('_', " ")).unwrap_or_default();
    let stem = if !stem.contains(' ') { stem.replace('.', " ") } else { stem };
    clean_name(&stem)
}

fn is_safe_rel(rel: &str) -> bool {
    !rel.is_empty() && !rel.starts_with('/') && !rel.contains("..") && !rel.contains(':')
}

#[tauri::command]
pub fn list_mods() -> Vec<ModInfo> {
    let mut out = Vec::new();
    if let Ok(rd) = fs::read_dir(mods_dir()) {
        for e in rd.filter_map(|e| e.ok()) {
            let p = e.path();
            let n = p.file_name().unwrap_or_default().to_string_lossy().to_string();
            if n.ends_with(".part") || n.ends_with(".old") {
                continue;
            }
            if let Ok(t) = fs::read_to_string(p.join(MOD_INFO)) {
                if let Ok(mut m) = serde_json::from_str::<ModInfo>(&t) {
                    if m.name.is_empty() {
                        m.name = n.clone();
                    }
                    if m.exe.is_empty() {
                        m.exe = DDLC_EXE.into();
                    }
                    m.dir = n;
                    out.push(m);
                }
            }
        }
    }
    out.sort_by_key(|m| m.name.to_lowercase());
    out
}

fn mod_path(dir_name: &str) -> Result<PathBuf, String> {
    if dir_name.contains('/') || dir_name.contains('\\') || dir_name.contains("..") || dir_name.is_empty() {
        return Err("bad mod name".into());
    }
    let p = mods_dir().join(dir_name);
    if !p.join(MOD_INFO).exists() {
        return Err("That is not one of the mods the launcher added.".into());
    }
    Ok(p)
}

#[tauri::command]
pub fn play_mod(dir: String) -> Result<(), String> {
    let p = mod_path(&dir)?;
    let info: ModInfo = serde_json::from_str(&fs::read_to_string(p.join(MOD_INFO)).map_err(|e| e.to_string())?).map_err(|e| e.to_string())?;
    let exe = p.join(if info.exe.is_empty() { DDLC_EXE } else { &info.exe });
    if !exe.exists() {
        return Err("The mod's start file is missing. Try adding it again.".into());
    }
    std::process::Command::new(&exe).current_dir(&p).spawn().map_err(|e| format!("Could not start it: {}", e))?;
    Ok(())
}

#[tauri::command]
pub fn open_mod_folder(dir: String) -> Result<String, String> {
    Ok(mod_path(&dir)?.to_string_lossy().into_owned())
}

#[tauri::command]
pub fn remove_mod(dir: String) -> Result<(), String> {
    let p = mod_path(&dir)?;
    // only ever a folder directly inside <home>/mods that we made
    if p.parent() != Some(mods_dir().as_path()) {
        return Err("Refusing to delete that.".into());
    }
    fs::remove_dir_all(&p).map_err(|e| e.to_string())
}

// ---------------------------------------------------------------------------------------------- reading a mod zip

struct Plan {
    files: Vec<(usize, String)>, // zip entry index -> path in the copy
    ships_scripts: bool,
    has_mod: bool,
    bytes: u64,
}

fn plan_mod(zip_path: &Path) -> Result<Plan, String> {
    let f = fs::File::open(zip_path).map_err(|e| e.to_string())?;
    let mut z = zip::ZipArchive::new(f).map_err(|_| "that is not a readable zip".to_string())?;
    let mut entries: Vec<(usize, String, u64)> = Vec::new();
    for i in 0..z.len() {
        let e = z.by_index(i).map_err(|e| e.to_string())?;
        let n = e.name().replace('\\', "/");
        let file = n.rsplit('/').next().unwrap_or("").to_string();
        if n.ends_with('/') || n.starts_with("__MACOSX/") || n.contains("/__MACOSX/") || file == ".DS_Store" || file == "Thumbs.db" {
            continue;
        }
        entries.push((i, n, e.size()));
    }
    // 1. the shallowest game/ folder marks the mod's root
    let mut root: Option<String> = None;
    let mut best = usize::MAX;
    for (_, n, _) in &entries {
        let parts: Vec<&str> = n.split('/').collect();
        for i in 0..parts.len().saturating_sub(1) {
            if i >= best {
                break;
            }
            if parts[i].eq_ignore_ascii_case("game") {
                best = i;
                root = Some(if i == 0 { String::new() } else { format!("{}/", parts[..i].join("/")) });
                break;
            }
        }
    }
    let has_game = root.is_some();
    // 2. no game/ folder: loose files go into game/; drop wrapper folders but never an asset folder
    let mut root = root.unwrap_or_default();
    if !has_game {
        loop {
            let mut top: Option<String> = None;
            let mut same = true;
            for (_, n, _) in &entries {
                let rest = &n[root.len().min(n.len())..];
                match rest.find('/') {
                    None => {
                        same = false;
                        break;
                    }
                    Some(s) => {
                        let t = rest[..s].to_string();
                        match &top {
                            None => top = Some(t),
                            Some(x) if x.eq_ignore_ascii_case(&t) => {}
                            _ => {
                                same = false;
                                break;
                            }
                        }
                    }
                }
            }
            match top {
                Some(t) if same && !ASSET_DIRS.contains(&t.to_lowercase().as_str()) => root = format!("{}{}/", root, t),
                _ => break,
            }
        }
    }
    let mut plan = Plan { files: Vec::new(), ships_scripts: false, has_mod: false, bytes: 0 };
    for (i, n, size) in entries {
        if !n.to_lowercase().starts_with(&root.to_lowercase()) {
            continue;
        }
        let rel = n[root.len()..].to_string();
        if !is_safe_rel(&rel) {
            continue;
        }
        let to = if has_game || rel.to_lowercase().starts_with("characters/") { rel.clone() } else { format!("game/{}", rel) };
        if to.to_lowercase().starts_with("game/") {
            let lower = n.to_lowercase();
            let ext = lower.rsplit('.').next().unwrap_or("");
            let file = lower.rsplit('/').next().unwrap_or("");
            if ["rpy", "rpyc", "rpym", "rpymc"].contains(&ext) {
                plan.ships_scripts = true;
                plan.has_mod = true;
            } else if ext == "rpa" {
                plan.has_mod = true;
                if !RPAS.contains(&file) {
                    plan.ships_scripts = true;
                }
            }
        }
        plan.files.push((i, to));
        plan.bytes += size;
    }
    if !plan.has_mod {
        return Err("this zip has no Ren'Py scripts or .rpa files, so it does not look like a DDLC mod.".into());
    }
    Ok(plan)
}

// ---------------------------------------------------------------------------------------------- finding a full DDLC

// The folder inside a zip that holds DDLC.exe / DDLC.sh at its top, or None.
fn ddlc_zip_root(z: &mut zip::ZipArchive<fs::File>) -> Option<String> {
    let mut best: Option<String> = None;
    for i in 0..z.len() {
        if let Ok(e) = z.by_index(i) {
            let n = e.name().replace('\\', "/");
            let lower = n.to_lowercase();
            let hit = lower.ends_with("ddlc.exe") || lower.ends_with("ddlc.sh");
            if !hit {
                continue;
            }
            let root = n[..n.rfind('/').map(|x| x + 1).unwrap_or(0)].to_string();
            if best.as_ref().map(|b| root.len() < b.len()).unwrap_or(true) {
                best = Some(root);
            }
        }
    }
    best
}

fn full_ddlc(path: &Path) -> bool {
    if path.is_file() {
        let Ok(f) = fs::File::open(path) else { return false };
        let Ok(mut z) = zip::ZipArchive::new(f) else { return false };
        return ddlc_zip_root(&mut z).is_some();
    }
    let has_launcher = path.join("DDLC.exe").exists() || path.join("DDLC.sh").exists();
    has_launcher && RPAS.iter().all(|r| path.join("game").join(r).exists())
}

fn ddlc_candidates() -> Vec<PathBuf> {
    let mut c = Vec::new();
    if let Ok(t) = fs::read_to_string(home().join("ddlc_source.txt")) {
        c.push(PathBuf::from(t.trim()));
    }
    if let Ok(p) = std::env::var("DDSD_DDLC") {
        c.insert(0, PathBuf::from(p));
    }
    c.extend(crate::candidates_pub());
    c
}

#[tauri::command]
pub fn find_full_ddlc() -> Option<String> {
    ddlc_candidates().into_iter().find(|p| p.exists() && full_ddlc(p)).map(|p| p.to_string_lossy().into_owned())
}

#[tauri::command]
pub fn check_full_ddlc(path: String) -> bool {
    full_ddlc(Path::new(&path))
}

struct Job {
    from: JobFrom,
    to: String,
    size: u64,
}
enum JobFrom {
    Entry(usize),
    File(PathBuf),
}

fn ddlc_wanted(rel: &str, keep_scripts: bool) -> bool {
    let r = rel.to_lowercase();
    if let Some(g) = r.strip_prefix("game/") {
        if RPAS.contains(&g) {
            return true;
        }
        if g == "scripts.rpa" {
            return keep_scripts;
        }
        return g == "firstrun" || g.starts_with("python-packages/");
    }
    if r.starts_with("ddlc.app/") || r.starts_with("lib/darwin") {
        return false;
    }
    if cfg!(windows) {
        !(r.starts_with("lib/linux") || r.ends_with(".sh"))
    } else {
        !(r.starts_with("lib/windows") || r.ends_with(".exe"))
    }
}

fn run_zip_jobs(zip_path: &Path, jobs: &[Job], dest: &Path, done: &mut u64, total: u64, mut tick: impl FnMut(u64, u64)) -> Result<(), String> {
    let f = fs::File::open(zip_path).map_err(|e| e.to_string())?;
    let mut z = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
    for j in jobs {
        let JobFrom::Entry(i) = j.from else { continue };
        let mut e = z.by_index(i).map_err(|e| e.to_string())?;
        let to = dest.join(&j.to);
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
        *done += j.size;
        tick(*done, total);
    }
    Ok(())
}

fn pick_exe(dir: &Path) -> String {
    let ext = if cfg!(windows) { "exe" } else { "sh" };
    if let Ok(rd) = fs::read_dir(dir) {
        for e in rd.filter_map(|e| e.ok()) {
            let p = e.path();
            if p.extension().map(|x| x.eq_ignore_ascii_case(ext)).unwrap_or(false) {
                let stem = p.file_stem().unwrap_or_default().to_string_lossy().to_string();
                if stem.eq_ignore_ascii_case("ddlc") || stem.to_lowercase().starts_with("lib") {
                    continue;
                }
                if dir.join(format!("{}.py", stem)).exists() {
                    return p.file_name().unwrap().to_string_lossy().into_owned();
                }
            }
        }
    }
    DDLC_EXE.to_string()
}

fn build_mod(app: &AppHandle, zip_path: &Path, ddlc: &Path, name: &str, hub_entry: Option<&Value>) -> Result<(), String> {
    let plan = plan_mod(zip_path)?;
    let dest = mods_dir().join(name);
    let part = mods_dir().join(format!("{}.part", name));
    let _ = fs::remove_dir_all(&part);
    fs::create_dir_all(&part).map_err(|e| e.to_string())?;
    let res = (|| -> Result<(), String> {
        // 1. DDLC's own files
        let mut ddlc_jobs: Vec<Job> = Vec::new();
        let mut ddlc_zip = false;
        if ddlc.is_file() {
            ddlc_zip = true;
            let f = fs::File::open(ddlc).map_err(|e| e.to_string())?;
            let mut z = zip::ZipArchive::new(f).map_err(|e| e.to_string())?;
            let root = ddlc_zip_root(&mut z).ok_or("that DDLC zip does not look right")?;
            for i in 0..z.len() {
                let e = z.by_index(i).map_err(|e| e.to_string())?;
                let n = e.name().replace('\\', "/");
                if !n.to_lowercase().starts_with(&root.to_lowercase()) || n.ends_with('/') {
                    continue;
                }
                let rel = n[root.len()..].to_string();
                if is_safe_rel(&rel) && ddlc_wanted(&rel, !plan.ships_scripts) {
                    ddlc_jobs.push(Job { from: JobFrom::Entry(i), to: rel, size: e.size() });
                }
            }
        } else {
            for e in walkdir::WalkDir::new(ddlc).into_iter().filter_map(|e| e.ok()).filter(|e| e.file_type().is_file()) {
                let rel = e.path().strip_prefix(ddlc).unwrap().to_string_lossy().replace('\\', "/");
                if ddlc_wanted(&rel, !plan.ships_scripts) {
                    ddlc_jobs.push(Job { from: JobFrom::File(e.path().to_path_buf()), to: rel, size: e.metadata().map(|m| m.len()).unwrap_or(0) });
                }
            }
        }
        let total: u64 = ddlc_jobs.iter().map(|j| j.size).sum::<u64>() + plan.bytes;
        let mut done = 0u64;
        let mut last = 101u64;
        let mut tick = |d: u64, t: u64| {
            let pct = d * 100 / t.max(1);
            if pct != last {
                last = pct;
                emit(app, "copy", d, t);
            }
        };
        if ddlc_zip {
            run_zip_jobs(ddlc, &ddlc_jobs, &part, &mut done, total, &mut tick)?;
        } else {
            for j in &ddlc_jobs {
                if let JobFrom::File(p) = &j.from {
                    let to = part.join(&j.to);
                    if let Some(d) = to.parent() {
                        fs::create_dir_all(d).map_err(|e| e.to_string())?;
                    }
                    fs::copy(p, &to).map_err(|e| e.to_string())?;
                    done += j.size;
                    tick(done, total);
                }
            }
        }
        // 2. the mod's files on top
        let jobs: Vec<Job> = plan.files.iter().map(|(i, to)| Job { from: JobFrom::Entry(*i), to: to.clone(), size: 0 }).collect();
        run_zip_jobs(zip_path, &jobs, &part, &mut done, total, &mut tick)?;
        let exe = pick_exe(&part);
        let size: u64 = walkdir::WalkDir::new(&part).into_iter().filter_map(|e| e.ok()).filter_map(|e| e.metadata().ok()).filter(|m| m.is_file()).map(|m| m.len()).sum();
        let mut info = serde_json::json!({
            "name": name, "zip": zip_path.file_name().unwrap_or_default().to_string_lossy(),
            "added": chrono::Local::now().format("%Y-%m-%d").to_string(),
            "exe": exe, "size": size,
            "ddlc_scripts_rpa": if plan.ships_scripts { "left out (the mod has its own scripts)" } else { "kept" }
        });
        if let Some(h) = hub_entry {
            for k in ["id", "version", "license", "author"] {
                if let Some(v) = h.get(k).and_then(|v| v.as_str()) {
                    info[if k == "id" { "hub_id" } else { k }] = Value::String(v.into());
                }
            }
        }
        fs::write(part.join(MOD_INFO), serde_json::to_string(&info).unwrap()).map_err(|e| e.to_string())?;
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt;
            for e in walkdir::WalkDir::new(&part).into_iter().filter_map(|e| e.ok()).filter(|e| e.file_type().is_file()) {
                let p = e.path().to_string_lossy().to_string();
                if p.ends_with(".sh") || p.contains("/lib/linux") {
                    let _ = fs::set_permissions(e.path(), fs::Permissions::from_mode(0o755));
                }
            }
        }
        if dest.exists() {
            let old = mods_dir().join(format!("{}.old", name));
            let _ = fs::remove_dir_all(&old);
            fs::rename(&dest, &old).map_err(|e| e.to_string())?;
            fs::rename(&part, &dest).map_err(|e| e.to_string())?;
            let _ = fs::remove_dir_all(&old);
        } else {
            fs::rename(&part, &dest).map_err(|e| e.to_string())?;
        }
        Ok(())
    })();
    if res.is_err() {
        let _ = fs::remove_dir_all(&part);
    }
    res
}

#[tauri::command]
pub async fn add_mod(app: AppHandle, zip: String, ddlc: Option<String>) -> Result<String, String> {
    let name = mod_name_from_zip(&zip);
    let ddlc = match ddlc {
        Some(d) => PathBuf::from(d),
        None => PathBuf::from(find_full_ddlc().ok_or("NEED_DDLC")?),
    };
    if !full_ddlc(&ddlc) {
        return Err("That is not a full DDLC copy (it needs DDLC.exe or DDLC.sh and the game folder).".into());
    }
    let _ = fs::write(home().join("ddlc_source.txt"), ddlc.to_string_lossy().as_bytes());
    fs::create_dir_all(mods_dir()).map_err(|e| e.to_string())?;
    let (a, b, n) = (PathBuf::from(&zip), ddlc.clone(), name.clone());
    let app2 = app.clone();
    tauri::async_runtime::spawn_blocking(move || build_mod(&app2, &a, &b, &n, None)).await.map_err(|e| e.to_string())??;
    Ok(name)
}

// ---------------------------------------------------------------------------------------------- browse + hub

fn safe_url(u: &str) -> bool {
    u.starts_with("https://") || u.starts_with("http://")
}

#[tauri::command]
pub async fn catalog() -> Result<Value, String> {
    let get = |url: String| async move {
        let t = if let Ok(local) = std::env::var("DDSD_CATALOG") {
            fs::read_to_string(local).map_err(|e| e.to_string())?
        } else {
            client()?.get(&url).send().await.map_err(|e| e.to_string())?.text().await.map_err(|e| e.to_string())?
        };
        let v: Value = serde_json::from_str(&t).map_err(|e| e.to_string())?;
        let arr = if v.is_array() { v } else { v.get("mods").cloned().unwrap_or(Value::Array(vec![])) };
        let list: Vec<Value> = arr
            .as_array()
            .cloned()
            .unwrap_or_default()
            .into_iter()
            .filter(|d| {
                d.get("name").and_then(|x| x.as_str()).is_some()
                    && (d.get("page").and_then(|x| x.as_str()).map(safe_url).unwrap_or(false)
                        || d.get("download").and_then(|x| x.as_str()).map(|u| u.starts_with("https://")).unwrap_or(false)
                        || d.get("direct").and_then(|x| x.as_str()).map(|u| u.starts_with("https://")).unwrap_or(false))
            })
            .collect();
        Ok::<Vec<Value>, String>(list)
    };
    let mut list = get(format!("{}?t={}", CATALOG_URL, chrono::Local::now().timestamp())).await.unwrap_or_default();
    if list.is_empty() && std::env::var("DDSD_CATALOG").is_err() {
        list = get(join_feed_pub("mods.json")).await.unwrap_or_default();
    }
    Ok(Value::Array(list))
}

#[tauri::command]
pub async fn install_hub_mod(app: AppHandle, entry: Value, ddlc: Option<String>) -> Result<String, String> {
    let name = clean_name(entry.get("name").and_then(|v| v.as_str()).unwrap_or("Mod"));
    // "download" = hosted by the hub (checked against its size and checksum); "direct" = a zip link on the mod
    // author's own site, which the launcher fetches for the player (nothing is re-hosted).
    let url = entry
        .get("download")
        .or_else(|| entry.get("direct"))
        .and_then(|v| v.as_str())
        .ok_or("This mod has no direct download. Open its page instead.")?
        .to_string();
    if !url.starts_with("https://") {
        return Err("The download link is not https, so it was refused.".into());
    }
    let ddlc = match ddlc {
        Some(d) => PathBuf::from(d),
        None => PathBuf::from(find_full_ddlc().ok_or("NEED_DDLC")?),
    };
    let tmp = home().join("download");
    fs::create_dir_all(&tmp).map_err(|e| e.to_string())?;
    let zip = tmp.join(format!("mod-{}.zip.part", entry.get("id").and_then(|v| v.as_str()).unwrap_or("x")));
    let r = client()?.get(&url).send().await.map_err(|e| e.to_string())?;
    if !r.status().is_success() {
        return Err(format!("The download failed ({}).", r.status()));
    }
    use futures_util::StreamExt;
    let total = r.content_length().unwrap_or(0);
    let mut out = fs::File::create(&zip).map_err(|e| e.to_string())?;
    let mut h = Sha256::new();
    let mut got = 0u64;
    let mut stream = r.bytes_stream();
    while let Some(c) = stream.next().await {
        let c = c.map_err(|e| e.to_string())?;
        h.update(&c);
        out.write_all(&c).map_err(|e| e.to_string())?;
        got += c.len() as u64;
        emit(&app, "download", got, total);
    }
    drop(out);
    let want_size = entry.get("size").and_then(|v| v.as_u64());
    let want_sha = entry.get("sha256").and_then(|v| v.as_str()).map(|s| s.to_lowercase());
    let bad = |msg: &str| {
        let _ = fs::remove_file(&zip);
        Err::<String, String>(msg.to_string())
    };
    if let Some(s) = want_size {
        if s != got {
            return bad("The download is not the size the catalog says, so it was not installed. Try again.");
        }
    }
    if let Some(s) = want_sha {
        if hex::encode(h.finalize()) != s {
            return bad("The download does not match the catalog's checksum, so it was not installed.");
        }
    }
    fs::create_dir_all(mods_dir()).map_err(|e| e.to_string())?;
    let _ = fs::write(home().join("ddlc_source.txt"), ddlc.to_string_lossy().as_bytes());
    let (z, d, n, e2) = (zip.clone(), ddlc, name.clone(), entry.clone());
    let app2 = app.clone();
    let res = tauri::async_runtime::spawn_blocking(move || build_mod(&app2, &z, &d, &n, Some(&e2))).await.map_err(|e| e.to_string())?;
    let _ = fs::remove_file(&zip);
    res?;
    Ok(name)
}

fn ddlc_file_in(zip_path: &Path) -> Option<String> {
    let f = fs::File::open(zip_path).ok()?;
    let mut z = zip::ZipArchive::new(f).ok()?;
    for i in 0..z.len() {
        let e = z.by_index(i).ok()?;
        let n = e.name().replace('\\', "/").to_lowercase();
        let file = n.rsplit('/').next().unwrap_or("").to_string();
        if ["audio.rpa", "fonts.rpa", "images.rpa", "scripts.rpa", "ddlc.exe"].contains(&file.as_str()) {
            return Some(file);
        }
    }
    None
}

#[tauri::command]
pub async fn share_mod(fields: HashMap<String, String>, zip: Option<String>) -> Result<String, String> {
    if let Some(z) = &zip {
        let p = Path::new(z);
        let len = fs::metadata(p).map_err(|_| "Could not find that zip any more. Pick it again.".to_string())?.len();
        if len > MAX_UPLOAD {
            return Err("That zip is over 95 MB. Upload it somewhere (Google Drive, MEGA, GitHub...) and paste the link instead.".into());
        }
        plan_mod(p)?;
        if let Some(d) = ddlc_file_in(p) {
            return Err(format!("The zip has DDLC's own {}. Take DDLC's files out (players bring their own copy).", d));
        }
    }
    let mut form = reqwest::multipart::Form::new();
    for (k, v) in fields {
        form = form.text(k, v);
    }
    if let Some(z) = zip {
        let bytes = fs::read(&z).map_err(|e| e.to_string())?;
        let fname = Path::new(&z).file_name().unwrap_or_default().to_string_lossy().replace('"', "_");
        form = form.part("file", reqwest::multipart::Part::bytes(bytes).file_name(fname).mime_str("application/zip").unwrap());
    }
    let r = client()?
        .post(format!("{}/submit", hub()))
        .multipart(form)
        .send()
        .await
        .map_err(|e| format!("Could not reach the Mod Hub ({}). Are you online?", e))?;
    hub_answer(r).await
}

async fn hub_answer(r: reqwest::Response) -> Result<String, String> {
    let v: Value = r.json().await.map_err(|_| "The Mod Hub gave an answer I could not read.".to_string())?;
    if v.get("ok").and_then(|x| x.as_bool()).unwrap_or(false) {
        Ok(v.get("message").and_then(|m| m.as_str()).unwrap_or("Sent!").to_string())
    } else {
        Err(v.get("error").and_then(|m| m.as_str()).unwrap_or("The Mod Hub said no.").to_string())
    }
}

#[tauri::command]
pub async fn report_mod(id: String, reason: String) -> Result<String, String> {
    if reason.trim().is_empty() {
        return Err("Please say why.".into());
    }
    let r = client()?
        .post(format!("{}/report", hub()))
        .json(&serde_json::json!({ "id": id, "reason": reason.trim() }))
        .send()
        .await
        .map_err(|e| format!("Could not reach the Mod Hub ({}). Are you online?", e))?;
    hub_answer(r).await
}

// ---------------------------------------------------------------------------------------------- beta codes

fn beta_file() -> PathBuf {
    home().join("beta.json")
}

pub fn saved_beta() -> Option<String> {
    let v: Value = serde_json::from_str(&fs::read_to_string(beta_file()).ok()?).ok()?;
    v.get("code").and_then(|c| c.as_str()).map(|s| s.to_string())
}

pub async fn ask_beta(code: &str) -> Result<Value, String> {
    let r = client()?
        .get(format!("{}/beta/{}", hub(), code.trim()))
        .send()
        .await
        .map_err(|e| format!("Could not reach the Mod Hub ({}). Are you online?", e))?;
    r.json::<Value>().await.map_err(|e| e.to_string())
}

#[tauri::command]
pub fn beta_state() -> Option<String> {
    saved_beta()
}

#[tauri::command]
pub async fn beta_join(code: String) -> Result<String, String> {
    let d = ask_beta(&code).await?;
    if !d.get("ok").and_then(|x| x.as_bool()).unwrap_or(false) {
        return Err(d.get("error").and_then(|e| e.as_str()).unwrap_or("That beta code did not work.").to_string());
    }
    fs::write(beta_file(), serde_json::json!({ "code": code.trim().to_uppercase() }).to_string()).map_err(|e| e.to_string())?;
    Ok(d.get("version").and_then(|v| v.as_str()).unwrap_or("the beta build").to_string())
}

#[tauri::command]
pub fn beta_leave() {
    let _ = fs::remove_file(beta_file());
}

// The feed to update from: the beta's folder when a saved code is still valid, else None (public releases).
pub async fn beta_feed() -> Option<String> {
    if std::env::var("DDSD_FEED").is_ok() {
        return None;
    }
    let code = saved_beta()?;
    match ask_beta(&code).await {
        Ok(d) if d.get("ok").and_then(|x| x.as_bool()).unwrap_or(false) => d.get("feed").and_then(|f| f.as_str()).map(|s| s.to_string()),
        Ok(_) => {
            let _ = fs::remove_file(beta_file()); // a code Aoi removed drops the tester back to normal
            None
        }
        Err(_) => None,
    }
}

#[allow(dead_code)]
fn _unused(_: &mut dyn Read) {}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::Write;

    fn make_zip(path: &Path, files: &[&str]) {
        let f = fs::File::create(path).unwrap();
        let mut w = zip::ZipWriter::new(f);
        for n in files {
            w.start_file(*n, zip::write::SimpleFileOptions::default()).unwrap();
            w.write_all(b"x").unwrap();
        }
        w.finish().unwrap();
    }

    fn targets(p: &Plan) -> Vec<String> {
        p.files.iter().map(|(_, t)| t.clone()).collect()
    }

    #[test]
    fn plans() {
        let d = std::env::temp_dir().join("ddsd-mods-test");
        let _ = fs::remove_dir_all(&d);
        fs::create_dir_all(&d).unwrap();
        // 1. wrapper + game folder: root is the wrapper
        let a = d.join("a.zip");
        make_zip(&a, &["Cool Mod v1/game/script-ch1.rpy", "Cool Mod v1/game/mod_assets/x.png", "Cool Mod v1/README.txt", "other/readme"]);
        let p = plan_mod(&a).unwrap();
        assert!(p.has_mod && p.ships_scripts);
        assert!(targets(&p).contains(&"game/script-ch1.rpy".to_string()));
        assert!(!targets(&p).iter().any(|t| t.contains("other")));
        // 2. loose files with a wrapper: wrapper dropped, files go into game/
        let b = d.join("b.zip");
        make_zip(&b, &["MyMod/a.rpy", "MyMod/mod_assets/p.png"]);
        let p = plan_mod(&b).unwrap();
        assert!(targets(&p).contains(&"game/a.rpy".to_string()));
        assert!(targets(&p).contains(&"game/mod_assets/p.png".to_string()));
        // 3. only a rpa of DDLC's own names: a mod, but keeps DDLC scripts
        let c = d.join("c.zip");
        make_zip(&c, &["game/images.rpa"]);
        assert!(!plan_mod(&c).unwrap().ships_scripts);
        // 4. not a mod
        let e = d.join("e.zip");
        make_zip(&e, &["notes.txt"]);
        assert!(plan_mod(&e).is_err());
        // 5. path traversal is never planned
        let f = d.join("f.zip");
        make_zip(&f, &["game/ok.rpy", "game/../../evil.rpy"]);
        assert!(targets(&plan_mod(&f).unwrap()).iter().all(|t| !t.contains("..")));
        assert_eq!(mod_name_from_zip("/x/Virtual.Love.zip"), "Virtual Love");
        assert!(ddlc_wanted("game/audio.rpa", false) && !ddlc_wanted("game/scripts.rpa", false) && ddlc_wanted("game/scripts.rpa", true));
        let _ = fs::remove_dir_all(&d);
    }
}
