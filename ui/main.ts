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
  if (!confirm("Remove the game files? Your saves are kept.")) return;
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
