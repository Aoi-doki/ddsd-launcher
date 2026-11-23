import { invoke } from "@tauri-apps/api/core";
import { listen } from "@tauri-apps/api/event";
import { open as pickFile } from "@tauri-apps/plugin-dialog";
import { openUrl } from "@tauri-apps/plugin-opener";

interface Status {
  installed: string | null;
  ddlc_ok: boolean;
  home: string;
  platform: "windows" | "linux";
  launcher_version: string;
}
interface Latest {
  version: string;
  notes: string;
  game_size: number;
}
interface NewsItem {
  kind: string;
  title: string;
  body: string;
  date?: string;
  url?: string;
}
interface Progress {
  phase: string;
  done: number;
  total: number;
}

// Webviews do not reliably support confirm()/prompt(), so these use a <dialog>.
function askDialog(text: string, input: boolean): Promise<string | null> {
  const dlg = document.getElementById("dlg") as HTMLDialogElement;
  const field = document.getElementById("dlginput") as HTMLInputElement;
  (document.getElementById("dlgtext") as HTMLElement).textContent = text;
  field.hidden = !input;
  field.value = "";
  return new Promise((resolve) => {
    const done = (v: string | null) => {
      dlg.close();
      resolve(v);
    };
    (document.getElementById("dlgok") as HTMLElement).onclick = (e) => {
      e.preventDefault();
      done(input ? field.value.trim() || null : "yes");
    };
    (document.getElementById("dlgcancel") as HTMLElement).onclick = () => done(null);
    dlg.oncancel = () => done(null);
    dlg.showModal();
    if (input) field.focus();
  });
}
const confirmAsk = async (t: string) => (await askDialog(t, false)) !== null;
const promptAsk = (t: string) => askDialog(t, true);

const $ = <T extends HTMLElement>(id: string) => document.getElementById(id) as T;
const mainBtn = $<HTMLButtonElement>("main");
const ddlcBtn = $<HTMLButtonElement>("ddlc");
const msg = $("msg");
const bar = $("bar");
const fill = $("fill");

let status: Status;
let latest: Latest | null = null;
let busy = false;

function say(text: string, error = false) {
  msg.textContent = text;
  msg.classList.toggle("error", error);
}

function needsUpdate(): boolean {
  return !!latest && status.installed !== latest.version;
}

function render() {
  $("v-installed").textContent = status.installed ?? "Not installed";
  $("v-latest").textContent = latest ? latest.version : "unknown (offline?)";
  $("v-ddlc").textContent = status.ddlc_ok ? "Found" : "Missing";
  $("v-ddlc").className = status.ddlc_ok ? "ok" : "warn";
  $("ver").textContent = `Launcher ${status.launcher_version}`;
  $("uninstall").hidden = status.installed === null;
  ddlcBtn.hidden = status.installed === null || status.ddlc_ok;
  mainBtn.disabled = busy;
  if (busy) return;
  if (status.installed === null) {
    mainBtn.textContent = latest ? "Install" : "Retry";
    mainBtn.dataset.mode = latest ? "install" : "retry";
  } else if (!status.ddlc_ok) {
    mainBtn.textContent = "Find my DDLC copy";
    mainBtn.dataset.mode = "ddlc";
  } else if (needsUpdate()) {
    mainBtn.textContent = `Update to ${latest!.version}`;
    mainBtn.dataset.mode = "install";
  } else {
    mainBtn.textContent = "Play";
    mainBtn.dataset.mode = "play";
  }
}

async function refresh() {
  status = await invoke<Status>("status");
  try {
    latest = await invoke<Latest>("check_update");
  } catch {
    latest = null;
    say("Could not reach the update server. You can still play what is installed.", true);
  }
  render();
  if (!status.ddlc_ok && status.installed) {
    if (await invoke<boolean>("find_ddlc")) {
      status = await invoke<Status>("status");
      say("Found your DDLC files.");
      render();
    } else {
      say("DDSD needs your own copy of DDLC (audio, fonts and images files). Point me at it.");
    }
  }
}

async function chooseDdlc() {
  const picked = await pickFile({
    title: "Pick your DDLC zip, or any file in your DDLC folder",
    multiple: false,
    filters: [{ name: "DDLC zip or game", extensions: ["zip", "exe", "rpa", "sh"] }],
  });
  if (!picked) return;
  const ok = await invoke<boolean>("use_ddlc", { path: picked });
  status = await invoke<Status>("status");
  say(ok ? "Got it. DDLC files copied." : "That does not look like DDLC. It needs audio.rpa, fonts.rpa and images.rpa.", !ok);
  render();
}

const phases: Record<string, string> = {
  download: "Downloading",
  check: "Checking the download",
  unpack: "Unpacking",
  install: "Installing",
  done: "Done",
};

async function install() {
  busy = true;
  bar.hidden = false;
  fill.style.width = "0%";
  render();
  try {
    const v = await invoke<string>("install");
    status = await invoke<Status>("status");
    say(status.ddlc_ok ? `Installed ${v}. Ready to play.` : `Installed ${v}. One more step: show me your DDLC copy.`);
  } catch (e) {
    say(String(e), true);
  } finally {
    busy = false;
    bar.hidden = true;
    render();
  }
}

mainBtn.addEventListener("click", async () => {
  switch (mainBtn.dataset.mode) {
    case "install":
      return install();
    case "retry":
      return refresh();
    case "ddlc":
      return chooseDdlc();
    case "play":
      try {
        await invoke("play");
        say("Starting the game...");
      } catch (e) {
        say(String(e), true);
      }
  }
});
ddlcBtn.addEventListener("click", chooseDdlc);
$("uninstall").addEventListener("click", async () => {
  if (!(await confirmAsk("Remove the game files? Your saves are kept."))) return;
  try {
    await invoke("uninstall");
    status = await invoke<Status>("status");
    say("Uninstalled. Your saves are still there.");
  } catch (e) {
    say(String(e), true);
  }
  render();
});

document.querySelectorAll<HTMLAnchorElement>("a[data-open]").forEach((a) =>
  a.addEventListener("click", (ev) => {
    ev.preventDefault();
    void openUrl(a.href);
  }),
);

listen<Progress>("progress", (e) => {
  const p = e.payload;
  say(phases[p.phase] ?? p.phase);
  if (p.phase === "download" && p.total > 0) {
    fill.classList.remove("indeterminate");
    fill.style.width = `${Math.round((p.done / p.total) * 100)}%`;
    say(`Downloading ${(p.done / 1e6).toFixed(0)} of ${(p.total / 1e6).toFixed(0)} MB`);
  } else {
    fill.classList.add("indeterminate");
    fill.style.width = "100%";
  }
});

function paintNews(items: NewsItem[]) {
  const box = $("news");
  box.replaceChildren();
  for (const it of items.slice(0, 12)) {
    const a = document.createElement("article");
    a.className = `card post ${it.kind}`;
    const h = document.createElement("h2");
    h.textContent = it.title;
    const p = document.createElement("p");
    p.textContent = it.body;
    a.append(h, p);
    box.append(a);
  }
  if (!items.length) box.innerHTML = '<p class="muted">Nothing new yet.</p>';
}

async function loadNews() {
  try {
    const raw = await invoke<string>("news");
    paintNews((JSON.parse(raw).items ?? []) as NewsItem[]);
  } catch {
    $("news").innerHTML = '<p class="muted">The news could not be loaded. Check your connection.</p>';
  }
}

void refresh();
void loadNews();


// ----------------------------------------------------------------------------------------------- tabs, mods, hub
interface ModInfo {
  name: string;
  dir: string;
  exe: string;
  added: string;
  size: number;
  hub_id?: string;
  version?: string;
  license?: string;
  author?: string;
}
interface CatalogEntry {
  id?: string;
  name: string;
  author?: string;
  description?: string;
  page?: string;
  image?: string;
  download?: string;
  direct?: string;
  version?: string;
  license?: string;
  size?: number;
  sha256?: string;
}

const msg2 = $("msg2");
function say2(text: string, error = false) {
  msg2.textContent = text;
  msg2.classList.toggle("error", error);
}
function mb(n: number): string {
  return n > 1e9 ? `${(n / 1e9).toFixed(1)} GB` : `${Math.max(1, Math.round(n / 1e6))} MB`;
}
function el<K extends keyof HTMLElementTagNameMap>(tag: K, cls?: string, text?: string): HTMLElementTagNameMap[K] {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}
function button(label: string, fn: () => void, cls = ""): HTMLButtonElement {
  const b = el("button", cls, label);
  b.type = "button";
  b.addEventListener("click", fn);
  return b;
}

let currentView = "home";
function showView(v: string) {
  currentView = v;
  document.querySelectorAll<HTMLElement>(".view").forEach((s) => (s.hidden = s.id !== `view-${v}`));
  document.querySelectorAll<HTMLButtonElement>(".tabs button").forEach((b) => b.setAttribute("aria-selected", String(b.dataset.view === v)));
  say2("");
  if (v === "mods") void loadMods();
  if (v === "browse") void loadCatalog();
}
document.querySelectorAll<HTMLButtonElement>(".tabs button").forEach((b) => b.addEventListener("click", () => showView(b.dataset.view!)));

// A full DDLC (zip or folder) is needed for mods; ask once if it cannot be found.
async function needFullDdlc(): Promise<string | null | undefined> {
  const found = await invoke<string | null>("find_full_ddlc");
  if (found) return undefined; // the backend finds it itself
  say2("Mods need your whole DDLC copy. Pick the DDLC zip (ddlc-win.zip / the Linux zip) or DDLC.exe / DDLC.sh in your DDLC folder.");
  const picked = await pickFile({ title: "Find your full DDLC", multiple: false });
  if (!picked) return null;
  const p = String(picked);
  const path = /\.zip$/i.test(p) ? p : p.replace(/[\\/][^\\/]*$/, "");
  if (!(await invoke<boolean>("check_full_ddlc", { path }))) {
    say2("That does not look like a full DDLC copy.", true);
    return null;
  }
  return path;
}

async function runBusy(label: string, job: () => Promise<string>) {
  busy = true;
  render();
  bar.hidden = false;
  fill.style.width = "0%";
  say2(label);
  try {
    say2(await job());
  } catch (e) {
    const t = String(e);
    say2(t === "NEED_DDLC" ? "Mods need your whole DDLC copy first." : t, true);
  } finally {
    busy = false;
    bar.hidden = true;
    render();
  }
}

async function addMod() {
  const z = await pickFile({ title: "Pick the mod's zip", multiple: false, filters: [{ name: "Mod zip", extensions: ["zip"] }] });
  if (!z) return;
  const ddlc = await needFullDdlc();
  if (ddlc === null) return;
  await runBusy("Copying your DDLC and adding the mod...", async () => {
    const name = await invoke<string>("add_mod", { zip: String(z), ddlc: ddlc ?? null });
    await loadMods();
    return `Added ${name}. Press Play on its card.`;
  });
}
$("addmod").addEventListener("click", () => void addMod());

async function loadMods() {
  const box = $("modlist");
  box.replaceChildren();
  const mods = await invoke<ModInfo[]>("list_mods");
  if (!mods.length) {
    box.append(el("p", "muted", "No mods yet. Add a mod zip, or browse the catalog."));
    return;
  }
  for (const m of mods) {
    const card = el("article", "card mod");
    card.append(el("h2", undefined, m.name));
    card.append(el("p", "meta", [m.version, m.author && `by ${m.author}`, m.license, mb(m.size), m.added].filter(Boolean).join(" · ")));
    const btns = el("div", "btns");
    btns.append(
      button("Play", async () => {
        try {
          await invoke("play_mod", { dir: m.dir });
          say2(`Starting ${m.name}...`);
        } catch (e) {
          say2(String(e), true);
        }
      }, "primary small"),
      button("Folder", async () => invoke("open_dir", { path: await invoke<string>("open_mod_folder", { dir: m.dir }) })),
      button("Remove", async () => {
        if (!(await confirmAsk(`Remove ${m.name}? Its saves are kept.`))) return;
        try {
          await invoke("remove_mod", { dir: m.dir });
          say2(`Removed ${m.name}.`);
          await loadMods();
        } catch (e) {
          say2(String(e), true);
        }
      }),
    );
    card.append(btns);
    box.append(card);
  }
}

let catalog: CatalogEntry[] | null = null;
async function loadCatalog() {
  const box = $("catalog");
  if (catalog === null) {
    catalog = await invoke<CatalogEntry[]>("catalog");
  }
  box.replaceChildren();
  if (!catalog.length) {
    box.append(el("p", "muted", "No catalog yet, or you are offline."));
    return;
  }
  for (const d of catalog) {
    const card = el("article", "card mod");
    const h = el("h2", undefined, d.name);
    if (d.download) h.append(el("span", "tag", "hosted"));
    card.append(h);
    card.append(el("p", "meta", [d.version, d.author && `by ${d.author}`, d.license, d.size && mb(d.size)].filter(Boolean).join(" · ")));
    if (d.description) card.append(el("p", undefined, d.description));
    const btns = el("div", "btns");
    if (d.download || d.direct) {
      btns.append(
        button("Get and install", async () => {
          const ddlc = await needFullDdlc();
          if (ddlc === null) return;
          await runBusy(`Downloading ${d.name}...`, async () => {
            const name = await invoke<string>("install_hub_mod", { entry: d, ddlc: ddlc ?? null });
            return `Installed ${name}. It is in My mods.`;
          });
        }, "primary small"),
      );
    }
    if (d.page && /^https?:\/\//.test(d.page)) btns.append(button("Open its page", () => void openUrl(d.page!)));
    if (d.download && d.id) {
      btns.append(
        button("Report", async () => {
          const why = await promptAsk(`Why are you reporting ${d.name}?`);
          if (!why) return;
          try {
            say2(await invoke<string>("report_mod", { id: d.id, reason: why }));
          } catch (e) {
            say2(String(e), true);
          }
        }),
      );
    }
    card.append(btns);
    box.append(card);
  }
}

// Share form
let shareZip: string | null = null;
$("pickzip").addEventListener("click", async () => {
  const z = await pickFile({ title: "Pick the mod's zip", multiple: false, filters: [{ name: "Mod zip", extensions: ["zip"] }] });
  if (!z) return;
  shareZip = String(z);
  $("zipname").textContent = shareZip.split(/[\\/]/).pop() ?? shareZip;
});
$<HTMLFormElement>("shareform").addEventListener("submit", async (ev) => {
  ev.preventDefault();
  const f = ev.target as HTMLFormElement;
  const data = new FormData(f);
  const fields: Record<string, string> = {};
  for (const k of ["name", "author", "version", "description", "license", "page", "contact"]) fields[k] = String(data.get(k) ?? "").trim();
  const link = String(data.get("link") ?? "").trim();
  if (!fields.name || !fields.author || !fields.description) return say2("Please fill in the name, the author and a short description.", true);
  if (!fields.license) return say2("Please pick a licence.", true);
  if (!shareZip && !link) return say2("Pick the mod's zip, or paste a link to it.", true);
  if (!data.get("agree")) return say2("Please tick the box: you made this mod (or have permission) and it has no DDLC files.", true);
  fields.agree = "yes";
  if (!shareZip) fields.link = link;
  await runBusy("Sending your mod for review...", async () => {
    const m = await invoke<string>("share_mod", { fields, zip: shareZip });
    f.reset();
    shareZip = null;
    $("zipname").textContent = "No zip picked";
    return m;
  });
});

// Beta code
$("beta").addEventListener("click", async () => {
  const saved = await invoke<string | null>("beta_state");
  if (saved) {
    if (!(await confirmAsk("You are on the DDSD beta. Leave it and go back to the normal version? Your saves stay."))) return;
    await invoke("beta_leave");
    say("Left the beta. Checking the normal version...");
    await refresh();
    return;
  }
  const code = await promptAsk("Type the beta code you were given:");
  if (!code) return;
  try {
    const v = await invoke<string>("beta_join", { code });
    say(`Welcome to the beta! Getting ${v}...`);
    await refresh();
    if (needsUpdate()) await install();
  } catch (e) {
    say(String(e), true);
  }
});

// Music (Monika's Waltz, piano by Aoi): off until you turn it on.
const music = new Audio("/waltz.mp3");
music.loop = true;
music.volume = 0.35;
const musicBtn = $<HTMLButtonElement>("music");
function paintMusic(on: boolean) {
  musicBtn.textContent = on ? "Music: on" : "Music: off";
  musicBtn.setAttribute("aria-pressed", String(on));
}
musicBtn.addEventListener("click", () => {
  if (music.paused) {
    void music.play().catch(() => undefined);
    paintMusic(true);
    try { localStorage.setItem("music", "1"); } catch { /* storage may be blocked */ }
  } else {
    music.pause();
    paintMusic(false);
    try { localStorage.setItem("music", "0"); } catch { /* storage may be blocked */ }
  }
});
void currentView;
