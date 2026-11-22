// DDSD Launcher, part 2: the Mods tab. Other people's DDLC mods, each in its own fresh copy of the player's DDLC.
// C# 5 only (Windows' csc): no $"" strings, no ?. operator.
//
//   Add a mod   the player picks a mod's .zip (downloaded from the mod's own page). We build %LOCALAPPDATA%\DDSD\mods\<name>\
//               from their own DDLC (ddlc-win.zip, Steam's folder... the same places we look for DDLC's .rpa files), then lay
//               the mod on top: a zip with its own game\ folder goes over the DDLC folder, loose .rpy/.rpyc/.rpa files go into
//               game\. DDLC's scripts.rpa is left out when the mod brings its own scripts (the usual DDLC mod install rule:
//               otherwise labels like "start" exist twice). The copy is built in <name>.part and renamed when done.
//   My mods     Play (the mod's own Ren'Py exe if it ships one, else DDLC.exe), Folder, Remove (only that mod's folder under
//               DDSD\mods, after a confirm). Ren'Py keeps saves in %APPDATA%\RenPy\<save_directory>, so they survive a remove.
//   Browse      cards from the DDSD Mod Hub's catalog.json (ModHub.cs; falls back to mods.json next to latest.json).
//               Mods the hub hosts (shared by their makers, reviewed by Aoi) get Install + Report; the others only open the
//               mod's OWN page in the browser. We never re-host mods without their makers, and never redistribute DDLC's files.
// Testing: DDSD_VIEW = mods / browse opens that view; DDSD_ADDMOD = <zip> adds it with no dialog; DDSD_DDLC = <zip or folder>
// is the DDLC to copy (skips the search); DDSD_PLAYMOD / DDSD_REMOVEMOD = <mod folder name> press Play / Remove with no
// confirm. While DDSD_SHOT is set, Play only says what it would start.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DDSD
{
    public partial class Launcher
    {
        const string MOD_INFO = "ddsd_mod.json";
        string ModsDir { get { return Path.Combine(home, "mods"); } }
        // The last DDLC zip / folder that worked (Locate DDLC or a mod install), tried first next time.
        string DdlcSourceFile { get { return Path.Combine(home, "ddlc_source.txt"); } }

        bool betaTestDone;
        int view;   // 0 = Home, 1 = My mods, 2 = Browse mods, 3 = Share a mod, 4 = Report a mod, 5 = News (News.cs)
        PillButton addModBtn, modsFolderBtn, shareBtn;
        ModCards cards;
        // logical-pixel hit areas, drawn in OnPaint like the window chrome
        static readonly Rectangle tabHome = new Rectangle(524, 13, 82, 32), tabMods = new Rectangle(612, 13, 82, 32),
            tabNews = new Rectangle(700, 13, 82, 32),
            tabMine = new Rectangle(60, 134, 118, 32), tabBrowse = new Rectangle(186, 134, 136, 32);

        List<Dictionary<string, object>> catalog;
        bool catalogLoading;
        Action afterCatalog;
        Dictionary<string, Image> catalogImages = new Dictionary<string, Image>();

        static string Env(string name) { return Environment.GetEnvironmentVariable(name); }

        void BuildModsUi()
        {
            modsFolderBtn = new PillButton(this, "Mods folder", R(556, 132, 140, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            modsFolderBtn.Click += delegate { Directory.CreateDirectory(ModsDir); Open(ModsDir); };
            addModBtn = new PillButton(this, "Add a mod...", R(704, 132, 160, 40), 12f, ROSE, ROSE_DARK, Color.White, riffic);
            addModBtn.Click += delegate { AddMod(); };
            shareBtn = new PillButton(this, "Share a mod", R(408, 132, 140, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            shareBtn.Click += delegate { OpenShare(); };
            cards = new ModCards(riffic, aller) { Bounds = R(54, 182, 812, 314) };
            Controls.Add(cards);
            addModBtn.Visible = modsFolderBtn.Visible = shareBtn.Visible = cards.Visible = false;
            BuildHubUi();
        }

        void SetView(int v)
        {
            view = v;
            bool homeView = v == 0, list = v == 1 || v == 2;
            playBtn.Visible = updateBtn.Visible = discordBtn.Visible = gameFolderBtn.Visible = notes.Visible = notesScroll.Visible = homeView;
            if (betaBtn != null) betaBtn.Visible = homeView;
            addModBtn.Visible = modsFolderBtn.Visible = shareBtn.Visible = list;
            cards.Visible = list || v == 5;
            cards.Bounds = v == 5 ? R(54, 134, 812, 362) : R(54, 182, 812, 314);
            ShowHubControls(v);
            bar.Bounds = homeView ? R(560, 470, 300, 14) : R(660, 520, 220, 14);
            if (v == 1) ShowInstalled(false);
            if (v == 2) ShowCatalog(false);
            if (v == 5) ShowNews();
            Refresh2();
            if (!busy && v == 1) Status("Each mod gets its own copy of your DDLC, so DDSD and your DDLC stay untouched.");
            if (!busy && v == 2) Status("Check each mod's content warnings first!");
            if (!busy && v == 5) Status("Updates, devlogs and sneak peeks from the DDSD Discord.");
            Invalidate();
        }

        // Window-chrome style hit test for the tabs: 3 Home, 4 Mods, 5 My mods, 6 Browse; 7+ the Share form's own areas.
        int TabAt(Point lp)
        {
            if (tabHome.Contains(lp)) return 3;
            if (tabMods.Contains(lp)) return 4;
            if (tabNews.Contains(lp)) return 20;
            if ((view == 1 || view == 2) && tabMine.Contains(lp)) return 5;
            if ((view == 1 || view == 2) && tabBrowse.Contains(lp)) return 6;
            if (view == 3) return ShareHit(lp);
            return 0;
        }

        void TabClick(int t)
        {
            if (t == 3 && view != 0) SetView(0);
            else if (t == 4 && view != 1 && view != 2) SetView(1);
            else if (t == 5 && view != 1) SetView(1);
            else if (t == 6 && view != 2) SetView(2);
            else if (t == 20 && view != 5) SetView(5);
            else if (t >= 7 && t < 20) ShareClick(t);
        }

        void DrawTab(Graphics g, Rectangle r, string text, bool active, bool hot, float size)
        {
            Color fill = active ? ROSE : hot ? SOFT_BTN_HOVER : SOFT_BTN;
            using (var b = new SolidBrush(fill)) g.FillPath(b, RoundRect(r, r.Height / 2f));
            using (var f = PF(riffic, size)) using (var b = new SolidBrush(active ? Color.White : PLUM))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, b, new RectangleF(r.X, r.Y + 1, r.Width, r.Height), sf);
        }

        void PaintTabs(Graphics g)
        {
            DrawTab(g, tabHome, "Home", view == 0, hoverChrome == 3, 11f);
            DrawTab(g, tabMods, "Mods", view >= 1 && view <= 4, hoverChrome == 4, 11f);
            DrawTab(g, tabNews, "News", view == 5, hoverChrome == 20, 11f);
        }

        void PaintMods(Graphics g)
        {
            if (logo != null) g.DrawImage(logo, new Rectangle(40, 22, 86, 86));
            OutlinedText(g, view == 3 ? "Share a mod" : view == 4 ? "Report a mod" : view == 5 ? "News" : "Mods", 28f, new RectangleF(138, 22, 480, 54),
                         ROSE, Color.FromArgb(240, 150, 196), StringAlignment.Near);
            using (var f = PF(aller, 11f, FontStyle.Italic)) using (var b = new SolidBrush(SOFT))
                g.DrawString(view == 3 ? "Made a DDLC mod? Share it with everyone who plays DDSD." :
                             view == 4 ? "Something wrong with a mod in Browse? Tell Aoi." :
                             view == 5 ? "What's new with Doki Doki Second Draft." :
                             "Other DDLC mods, each in its own fresh copy of your DDLC.", f, b, 144, 80);
            var card = new RectangleF(40, 122, 840, 384);
            using (var sh = new SolidBrush(Color.FromArgb(28, 150, 60, 110))) g.FillPath(sh, RoundRect(new RectangleF(card.X + 4, card.Y + 7, card.Width, card.Height), 18));
            using (var b = new SolidBrush(CARD)) g.FillPath(b, RoundRect(card, 18));
            using (var pen = new Pen(LINE, 3)) g.DrawPath(pen, RoundRect(card, 18));
            if (view == 1 || view == 2)
            {
                DrawTab(g, tabMine, "My mods", view == 1, hoverChrome == 5, 12f);
                DrawTab(g, tabBrowse, "Browse mods", view == 2, hoverChrome == 6, 12f);
            }
            else if (view == 3 || view == 4) PaintHubForm(g);
            using (var f = PF(riffic, 11f)) using (var b = new SolidBrush(PLUM))
            using (var sf = new StringFormat { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisWord })
                g.DrawString(statusText, f, b, new RectangleF(44, 510, bar.Visible ? 604 : 836, 34), sf);
            using (var f = PF(aller, 9f)) using (var b = new SolidBrush(SOFT))
                g.DrawString(view == 5 ? "Launcher " + VERSION : "Mods belong to their makers. Removing a mod keeps its saves (in %APPDATA%\\RenPy).   Launcher " + VERSION, f, b, 40, H - 32);
        }

        // ------------------------------------------------------------------ my mods
        class ModInfo { public string Dir, Name, Exe, Zip, ZipPath, Added, HubId, Version, License, ShareId, ShareState; public long Size; }

        List<ModInfo> InstalledMods()
        {
            var list = new List<ModInfo>();
            if (!Directory.Exists(ModsDir)) return list;
            foreach (var d in Directory.GetDirectories(ModsDir))
            {
                if (d.EndsWith(".part") || d.EndsWith(".old")) continue;   // half-built or being replaced
                string info = Path.Combine(d, MOD_INFO);
                if (!File.Exists(info)) continue;                           // not one of ours
                try
                {
                    var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(info));
                    long size; long.TryParse(Str(j, "size") ?? "0", out size);
                    list.Add(new ModInfo { Dir = d, Name = Str(j, "name") ?? Path.GetFileName(d), Exe = Str(j, "exe") ?? "DDLC.exe",
                                           Zip = Str(j, "zip"), Added = Str(j, "added") ?? "", Size = size,
                                           HubId = Str(j, "hub_id"), Version = Str(j, "version"), License = Str(j, "license"),
                                           ZipPath = Str(j, "zip_path"), ShareId = Str(j, "share_id"), ShareState = Str(j, "share_state") });
                }
                catch { }
            }
            list.Sort(delegate(ModInfo a, ModInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
            return list;
        }

        static string FormatSize(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes < 1L << 20) return Math.Max(1, bytes >> 10) + " KB";
            return (bytes >> 20) + " MB";
        }

        void ShowInstalled(bool keepScroll)
        {
            var items = new List<ModCard>();
            var mods = InstalledMods();
            if (mods.Count == 0)
                items.Add(new ModCard { Title = "No mods yet",
                    Body = "Download a mod's .zip from its own page, then press \"Add a mod...\" and pick it. Not sure where to start? Browse mods.",
                    Buttons = new[] { "Browse mods" }, Click = delegate(int b) { SetView(2); } });
            foreach (var m in mods)
            {
                // A mod the player added themself is theirs only. "Share" sends it to Aoi's review; once accepted it's
                // public in Browse for everyone (Aoi: "it's there just for them until I accept it").
                string share = m.ShareId == null ? "" : m.ShareState == "public" ? " Shared: it's public in Browse now."
                    : m.ShareState == "gone" ? " Shared, but it wasn't added to the Mod Hub." : " Shared: waiting for Aoi's review.";
                bool canShare = m.HubId == null && (m.ShareId == null || m.ShareState == "gone");
                var mm = m;
                items.Add(new ModCard { Title = m.Name,
                    Meta = FormatSize(m.Size) + "   ·   added " + m.Added + "   ·   starts " + m.Exe,
                    Body = (m.HubId != null ? "From the DDSD Mod Hub" + (m.Version != null ? ", version " + m.Version : "") + (m.License != null ? " (" + m.License + ")" : "")
                            : m.Zip != null ? "From " + m.Zip + ", only on this PC" : "Added by hand") + "." + share + " Its saves live in %APPDATA%\\RenPy, so removing it keeps them.",
                    Buttons = canShare ? new[] { "Play", "Share", "Folder", "Remove" } : new[] { "Play", "Folder", "Remove" },
                    Click = delegate(int b) {
                        if (canShare && b == 1) { ShareInstalled(mm); return; }
                        if (canShare && b > 1) b--;
                        if (b == 0) PlayMod(mm); else if (b == 1) Open(mm.Dir); else RemoveMod(mm, false); } });
            }
            cards.SetItems(items, keepScroll);
            CheckShareStates(mods);
        }

        bool ModRunning(string dir)
        {
            string d = Path.GetFullPath(dir).TrimEnd('\\') + "\\";
            try
            {
                foreach (var exe in Directory.GetFiles(dir, "*.exe"))
                    foreach (Process pr in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                        try { if (pr.MainModule.FileName.StartsWith(d, StringComparison.OrdinalIgnoreCase)) return true; } catch { }
            }
            catch { }
            return false;
        }

        void PlayMod(ModInfo m)
        {
            string exe = Path.Combine(m.Dir, m.Exe);
            if (!Inside(exe, m.Dir) || !File.Exists(exe)) { Status("Couldn't find " + m.Exe + " in that mod's folder. Try adding it again."); return; }
            if (Env("DDSD_SHOT") != null) { Status("Test: Play would start " + m.Name + "\\" + m.Exe); return; }
            try
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = m.Dir, UseShellExecute = true });
                Close();
            }
            catch (Exception ex) { Status("Couldn't start the mod: " + ex.Message); }
        }

        void RemoveMod(ModInfo m, bool noConfirm)
        {
            if (busy) { Status("One thing at a time: wait for the current job to finish."); return; }
            // Only ever delete a folder directly inside DDSD\mods that we made (it has our info file).
            string parent = Path.GetDirectoryName(Path.GetFullPath(m.Dir).TrimEnd('\\'));
            if (!string.Equals(parent, Path.GetFullPath(ModsDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.Combine(m.Dir, MOD_INFO)))
            { Status("That folder isn't one of the launcher's mods, so I left it alone."); return; }
            if (ModRunning(m.Dir)) { Status("Close " + m.Name + " first, then press Remove again."); return; }
            if (!noConfirm && MessageBox.Show(this, "Remove \"" + m.Name + "\"?\n\nThis deletes its folder:\n" + m.Dir +
                    "\n\nIts saves stay (Ren'Py keeps them in %APPDATA%\\RenPy), so adding the mod again picks them back up.",
                    "Remove mod", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            busy = true; Refresh2();
            bar.Visible = true; bar.Marquee = true;
            Status("Removing " + m.Name + "...");
            new Thread(delegate() {
                string err = null;
                try { DeleteTree(m.Dir); } catch (Exception ex) { err = ex.Message; }
                Ui(delegate {
                    busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                    ShowInstalled(true);
                    Status(err == null ? "Removed " + m.Name + ". Its saves are still in %APPDATA%\\RenPy." : "Couldn't remove it all: " + err);
                    TestShot();
                });
            }) { IsBackground = true }.Start();
        }

        static void ClearReadOnly(string dir)
        {
            foreach (var f in Directory.GetFiles(dir)) try { File.SetAttributes(f, FileAttributes.Normal); } catch { }
            foreach (var d in Directory.GetDirectories(dir))
                if ((File.GetAttributes(d) & FileAttributes.ReparsePoint) == 0) ClearReadOnly(d);   // never walk into links
        }

        static void DeleteTree(string dir) { ClearReadOnly(dir); Directory.Delete(dir, true); }

        static bool Inside(string path, string dir)
        {
            return Path.GetFullPath(path).StartsWith(Path.GetFullPath(dir).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
        }

        // ------------------------------------------------------------------ add a mod
        static string ModName(string zip)
        {
            string n = Path.GetFileNameWithoutExtension(zip).Replace('_', ' ');
            if (n.IndexOf(' ') < 0) n = n.Replace('.', ' ');   // "Virtual.Love" -> "Virtual Love"
            return CleanName(n);
        }

        // A safe folder name for a mod (also used for catalog names, which keep their dots).
        static string CleanName(string n)
        {
            n = n ?? "";
            foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, ' ');
            while (n.Contains("  ")) n = n.Replace("  ", " ");
            n = n.Trim(' ', '.');
            if (n.Length > 48) n = n.Substring(0, 48).Trim(' ', '.');
            return n.Length == 0 ? "Mod" : n;
        }

        void AddMod()
        {
            if (busy) { Status("One thing at a time: wait for the current job to finish."); return; }
            string zip = Env("DDSD_ADDMOD");   // test hook: skip the dialog
            bool test = zip != null;
            if (zip == null)
            using (var d = new OpenFileDialog {
                Title = "Add a mod: pick the mod's .zip (download it from the mod's own page first)",
                Filter = "Mod zip|*.zip|All files|*.*",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                CheckFileExists = true })
            {
                Activate();
                if (d.ShowDialog(this) == DialogResult.OK) zip = d.FileName;
            }
            if (zip == null) return;
            if (!zip.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                Status("That isn't a .zip. If the mod came as .rar or .7z, unpack it and zip the folder up again.");
                TestShot();
                return;
            }
            string name = ModName(zip);
            string dest = Path.Combine(ModsDir, name);
            if (Directory.Exists(dest))
            {
                if (ModRunning(dest)) { Status("Close " + name + " first, then add it again."); return; }
                if (!test && MessageBox.Show(this, "\"" + name + "\" is already installed. Replace it with this zip?\n\nIts saves stay (they live in %APPDATA%\\RenPy).",
                        "Replace mod", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            StartAddMod(delegate { return zip; }, name, null, null);
        }

        // getZip runs first on the worker thread (a hub install downloads + checks the zip there). hub = the catalog entry
        // for a hub install (its temp zip is deleted afterwards), null for a zip the player picked.
        void StartAddMod(Func<string> getZip, string name, string ddlc, Dictionary<string, object> hub)
        {
            busy = true; Refresh2();
            bar.Visible = true; bar.Value = 0; bar.Marquee = true;
            Status(hub != null ? "Downloading " + name + "..." : "Reading the mod's zip...");
            new Thread(delegate() {
                string dest = Path.Combine(ModsDir, name), part = dest + ".part", zip = null;
                bool keepZip = false;
                try
                {
                    zip = getZip();
                    Ui(delegate { Status("Reading " + (hub != null ? name : Path.GetFileName(zip)) + "..."); bar.Marquee = true; });
                    var plan = PlanMod(zip);   // fails fast on a zip that isn't a mod
                    string src = ddlc ?? FindFullDdlc();
                    if (src == null)
                    {
                        keepZip = true;
                        string z = zip;
                        Ui(delegate { busy = false; bar.Visible = false; bar.Marquee = false; Refresh2(); AskForDdlc(z, name, hub); });
                        return;
                    }
                    try { File.WriteAllText(DdlcSourceFile, src); } catch { }
                    Ui(delegate { Status("Copying your DDLC for " + name + "..."); bar.Marquee = false; bar.Value = 0; });
                    if (Directory.Exists(part)) DeleteTree(part);
                    Directory.CreateDirectory(part);
                    long total = 0, done = 0; int shown = -1;
                    Action<long> add = delegate(long n) {
                        done += n;
                        int pct = (int)(done * 100 / Math.Max(1, total));
                        if (pct != shown) { shown = pct; Ui(delegate { bar.Value = pct; }); }
                    };
                    var ddlcJobs = DdlcJobs(src, !plan.ShipsScripts);
                    foreach (var j in ddlcJobs) total += j.Size;
                    total += plan.Bytes;
                    RunJobs(src, ddlcJobs, part, add);
                    Ui(delegate { Status("Adding " + name + "'s files..."); });
                    var modJobs = new List<CopyJob>();
                    foreach (var kv in plan.Files) modJobs.Add(new CopyJob { From = kv.Key, To = kv.Value });
                    RunJobs(zip, modJobs, part, add);
                    string exe = PickExe(part);
                    long size = 0;
                    foreach (var f in Directory.GetFiles(part, "*", SearchOption.AllDirectories)) size += new FileInfo(f).Length;
                    var info = new Dictionary<string, object> {
                        { "name", name }, { "zip", Path.GetFileName(zip) }, { "zip_path", hub == null ? zip : null },
                        { "added", DateTime.Now.ToString("yyyy-MM-dd") },
                        { "exe", exe }, { "size", size }, { "ddlc_scripts_rpa", plan.ShipsScripts ? "left out (the mod has its own scripts)" : "kept" } };
                    if (hub != null)
                        foreach (var k in new[] { "id", "version", "license", "author" })
                            if (Str(hub, k) != null) info[k == "id" ? "hub_id" : k] = Str(hub, k);
                    File.WriteAllText(Path.Combine(part, MOD_INFO), new JavaScriptSerializer().Serialize(info));
                    if (Directory.Exists(dest))
                    {
                        string old = dest + ".old";
                        if (Directory.Exists(old)) DeleteTree(old);
                        Directory.Move(dest, old);
                        Directory.Move(part, dest);
                        try { DeleteTree(old); } catch { }
                    }
                    else Directory.Move(part, dest);
                    Ui(delegate {
                        busy = false; bar.Visible = false; bar.Marquee = false;
                        if (view != 1) SetView(1); else { Refresh2(); ShowInstalled(false); }
                        Status("Added " + name + "! Press Play on its card.");
                        TestShot();
                    });
                }
                catch (Exception ex)
                {
                    try { if (Directory.Exists(part)) DeleteTree(part); } catch { }
                    Ui(delegate {
                        busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                        Status((hub != null ? "Couldn't install " + name + ": " : "Couldn't add the mod: ") + ex.Message);
                        TestShot();
                    });
                }
                finally
                {
                    if (hub != null && !keepZip && zip != null) try { File.Delete(zip); } catch { }   // the hub download, not the player's own zip
                }
            }) { IsBackground = true }.Start();
        }

        // No full DDLC found: let the player point at it (same picker as Locate DDLC).
        void AskForDdlc(string zip, string name, Dictionary<string, object> hub)
        {
            Status("Mods need a full copy of DDLC (it's free). Pick ddlc-win.zip, or DDLC.exe in your DDLC folder.");
            if (Env("DDSD_SHOT") != null) { TestShot(); return; }   // tests never open dialogs
            string pick = null;
            using (var d = new OpenFileDialog {
                Title = "Mods need DDLC: pick ddlc-win.zip, or DDLC.exe in your DDLC folder",
                Filter = "DDLC zip or game|*.zip;DDLC.exe|All files|*.*",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                CheckFileExists = true })
            {
                Activate();
                if (d.ShowDialog(this) == DialogResult.OK) pick = d.FileName;
            }
            if (pick == null) { if (hub != null) try { File.Delete(zip); } catch { } return; }   // drop the hub download
            string src = pick.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? pick : Path.GetDirectoryName(pick);
            bool ok = false;
            try { ok = FullDdlc(src, false); } catch { }
            if (!ok)
            {
                MessageBox.Show(this, "That doesn't look like a full DDLC: I need DDLC.exe, the lib and renpy folders and game\\images.rpa.\n\n" +
                                "Pick ddlc-win.zip (from ddlc.moe), or DDLC.exe inside your Doki Doki Literature Club folder.\n" +
                                "DDLC Plus doesn't work for mods: they need the original (free) DDLC.",
                                "Not quite", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            StartAddMod(delegate { return zip; }, name, src, hub);
        }

        // ------------------------------------------------------------------ finding a full DDLC
        // Mods need DDLC's whole folder (DDLC.exe, lib, renpy, game\*.rpa), not just the three .rpa files DDSD uses.
        string FindFullDdlc()
        {
            string forced = Env("DDSD_DDLC");   // test hook
            if (forced != null) return FullDdlc(forced, false) ? forced : null;
            var cands = new List<string>();
            try { if (File.Exists(DdlcSourceFile)) cands.Add(File.ReadAllText(DdlcSourceFile).Trim()); } catch { }
            var found = new List<string>(DdlcCandidates());
            foreach (var c in found) if (c.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) cands.Add(c);   // zips are untouched DDLC
            foreach (var c in found) if (!c.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) cands.Add(c);
            // A folder that another mod (MAS...) was installed into may have lost scripts.rpa: prefer complete ones.
            foreach (var strict in new[] { true, false })
                foreach (var c in cands)
                    try { if (FullDdlc(c, strict)) return c; } catch { }
            return null;
        }

        static readonly string[] DDLC_PARTS = { "renpy/", "lib/" };

        bool FullDdlc(string path, bool needScripts)
        {
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var z = ZipFile.OpenRead(path))
                {
                    string root = DdlcZipRoot(z);
                    if (root == null) return false;
                    var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var e in z.Entries) names.Add(e.FullName.Replace('\\', '/'));
                    foreach (var r in RPAS) if (!names.Contains(root + "game/" + r)) return false;
                    if (needScripts && !names.Contains(root + "game/scripts.rpa")) return false;
                    foreach (var p in DDLC_PARTS)
                    {
                        bool any = false;
                        foreach (var n in names) if (n.StartsWith(root + p, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                        if (!any) return false;
                    }
                    return true;
                }
            }
            if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "DDLC.exe"))) return false;
            if (!Directory.Exists(Path.Combine(path, "renpy")) || !Directory.Exists(Path.Combine(path, "lib"))) return false;
            foreach (var r in RPAS) if (!File.Exists(Path.Combine(path, "game\\" + r))) return false;
            return !needScripts || File.Exists(Path.Combine(path, "game\\scripts.rpa"));
        }

        // The folder inside the zip that holds DDLC.exe ("DDLC-1.1.1-pc/" in ddlc.moe's zip), or null.
        static string DdlcZipRoot(ZipArchive z)
        {
            string best = null;
            foreach (var e in z.Entries)
            {
                string n = e.FullName.Replace('\\', '/');
                if (!n.EndsWith("DDLC.exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (n.Length > 8 && n[n.Length - 9] != '/') continue;
                string root = n.Substring(0, n.Length - 8);
                if (best == null || root.Length < best.Length) best = root;
            }
            return best;
        }

        class CopyJob { public string From, To; public long Size; }

        // DDLC's own files, minus anything another mod left in game\ (only DDLC's archives, firstrun and python-packages
        // go in) and minus the macOS / Linux parts. scripts.rpa only when the mod doesn't bring its own scripts.
        List<CopyJob> DdlcJobs(string src, bool keepScripts)
        {
            var jobs = new List<CopyJob>();
            Func<string, bool> wanted = delegate(string rel) {
                string r = rel.ToLowerInvariant();
                if (r.StartsWith("game/"))
                {
                    string g = r.Substring(5);
                    foreach (var a in RPAS) if (g == a) return true;
                    if (g == "scripts.rpa") return keepScripts;
                    return g == "firstrun" || g.StartsWith("python-packages/");
                }
                return !(r.StartsWith("ddlc.app/") || r.StartsWith("lib/darwin") || r.StartsWith("lib/linux") || r.EndsWith(".sh"));
            };
            if (File.Exists(src))
            {
                using (var z = ZipFile.OpenRead(src))
                {
                    string root = DdlcZipRoot(z);
                    foreach (var e in z.Entries)
                    {
                        string n = e.FullName.Replace('\\', '/');
                        if (!n.StartsWith(root, StringComparison.OrdinalIgnoreCase) || n.EndsWith("/")) continue;
                        string rel = n.Substring(root.Length);
                        if (rel.Length > 0 && wanted(rel)) jobs.Add(new CopyJob { From = e.FullName, To = rel.Replace('/', '\\'), Size = e.Length });
                    }
                }
                return jobs;
            }
            string full = Path.GetFullPath(src).TrimEnd('\\');
            foreach (var f in Directory.GetFiles(full, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(full.Length + 1);
                if (wanted(rel.Replace('\\', '/'))) jobs.Add(new CopyJob { From = f, To = rel, Size = new FileInfo(f).Length });
            }
            return jobs;
        }

        // Copies jobs from a zip (From = entry name) or from disk (From = file path) into dest, never outside it.
        static void RunJobs(string src, List<CopyJob> jobs, string dest, Action<long> add)
        {
            ZipArchive z = null;
            var entries = new Dictionary<string, ZipArchiveEntry>();
            if (File.Exists(src) && src.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                z = ZipFile.OpenRead(src);
                foreach (var e in z.Entries) entries[e.FullName] = e;
            }
            try
            {
                var buf = new byte[1 << 20];
                foreach (var j in jobs)
                {
                    string to = Path.Combine(dest, j.To);
                    if (!Inside(to, dest)) throw new Exception("the zip has a file that would land outside its folder (" + j.To + ").");
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    DateTime stamp;
                    using (var from = z != null ? entries[j.From].Open() : File.OpenRead(j.From))
                    using (var o = File.Create(to))
                    {
                        int n;
                        while ((n = from.Read(buf, 0, buf.Length)) > 0) { o.Write(buf, 0, n); add(n); }
                    }
                    stamp = z != null ? entries[j.From].LastWriteTime.DateTime : File.GetLastWriteTime(j.From);
                    try { File.SetLastWriteTime(to, stamp); } catch { }   // keeps .rpy / .rpyc pairs in step
                }
            }
            finally { if (z != null) z.Dispose(); }
        }

        // A mod that ships its own Ren'Py build has <Name>.exe + <Name>.py next to each other: start that one.
        static string PickExe(string dir)
        {
            foreach (var f in Directory.GetFiles(dir, "*.exe"))
            {
                string b = Path.GetFileNameWithoutExtension(f);
                if (!b.Equals("DDLC", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(dir, b + ".py"))) return Path.GetFileName(f);
            }
            return "DDLC.exe";
        }

        // ------------------------------------------------------------------ reading a mod zip
        class ModPlan
        {
            public List<KeyValuePair<string, string>> Files = new List<KeyValuePair<string, string>>();   // entry name -> path in the copy
            public bool ShipsScripts, HasMod; public long Bytes;
        }

        // Folders Ren'Py mods keep their files in: never mistaken for the zip's wrapper folder.
        static readonly string[] ASSET_DIRS = { "mod_assets", "images", "audio", "gui", "fonts", "python-packages", "submods", "tl", "characters", "game" };

        static ModPlan PlanMod(string zip)
        {
            var plan = new ModPlan();
            using (var z = ZipFile.OpenRead(zip))
            {
                var entries = new List<ZipArchiveEntry>();
                foreach (var e in z.Entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    string file = n.Substring(n.LastIndexOf('/') + 1);
                    if (n.EndsWith("/") || n.StartsWith("__MACOSX/") || n.Contains("/__MACOSX/") || file == ".DS_Store" || file == "Thumbs.db") continue;
                    entries.Add(e);
                }
                // 1. A game/ folder (the shallowest one) marks the mod's root: ModName/game/..., or game/... at the top.
                //    Everything under that root goes over the DDLC folder (a mod's own exe, renpy/, characters/ too).
                string root = null; int best = int.MaxValue;
                foreach (var e in entries)
                {
                    var parts = e.FullName.Replace('\\', '/').Split('/');
                    for (int i = 0; i < parts.Length - 1 && i < best; i++)
                        if (parts[i].Equals("game", StringComparison.OrdinalIgnoreCase)) { best = i; root = i == 0 ? "" : string.Join("/", parts, 0, i) + "/"; break; }
                }
                bool hasGame = root != null;
                // 2. No game/ folder: loose .rpy/.rpyc/.rpa files that go into game/. Drop the wrapper folders the zip
                //    was made with ("Mod v1.0/..."), but never an asset folder like mod_assets/.
                if (!hasGame)
                {
                    root = "";
                    while (true)
                    {
                        string top = null; bool same = true;
                        foreach (var e in entries)
                        {
                            string rest = e.FullName.Replace('\\', '/').Substring(root.Length);
                            int s = rest.IndexOf('/');
                            if (s < 0) { same = false; break; }
                            string t = rest.Substring(0, s);
                            if (top == null) top = t;
                            else if (!t.Equals(top, StringComparison.OrdinalIgnoreCase)) { same = false; break; }
                        }
                        if (!same || top == null || Array.IndexOf(ASSET_DIRS, top.ToLowerInvariant()) >= 0) break;
                        root += top + "/";
                    }
                }
                foreach (var e in entries)
                {
                    string n = e.FullName.Replace('\\', '/');
                    if (!n.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;   // e.g. a readme next to the mod's folder
                    string rel = n.Substring(root.Length);
                    string to = hasGame || rel.StartsWith("characters/", StringComparison.OrdinalIgnoreCase) ? rel : "game/" + rel;
                    if (to.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
                    {
                        string ext = Path.GetExtension(n).ToLowerInvariant(), file = Path.GetFileName(n).ToLowerInvariant();
                        if (ext == ".rpy" || ext == ".rpyc" || ext == ".rpym" || ext == ".rpymc") plan.ShipsScripts = plan.HasMod = true;
                        else if (ext == ".rpa")
                        {
                            plan.HasMod = true;
                            if (Array.IndexOf(RPAS, file) < 0) plan.ShipsScripts = true;   // scripts.rpa or the mod's own archive
                        }
                    }
                    plan.Files.Add(new KeyValuePair<string, string>(e.FullName, to.Replace('/', '\\')));
                    plan.Bytes += e.Length;
                }
            }
            if (!plan.HasMod)
                throw new Exception("this zip has no Ren'Py scripts or .rpa files, so it doesn't look like a DDLC mod.");
            return plan;
        }

        // ------------------------------------------------------------------ browse (catalog)
        static string SafeUrl(string u)
        {
            Uri uri;
            if (u == null || !Uri.TryCreate(u, UriKind.Absolute, out uri)) return null;
            return uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp ? uri.AbsoluteUri : null;   // never a program or file
        }

        void WhenCatalog(Action a)
        {
            if (catalog != null) a();
            else { afterCatalog = a; if (!catalogLoading) LoadCatalog(); }
        }

        void LoadCatalog()
        {
            catalogLoading = true;
            new Thread(delegate() {
                string b = FeedBase();
                // The hub's catalog first (DDSD_CATALOG = a local file for tests), then the feed's mods.json.
                var list = ParseCatalog(delegate {
                    string local = Env("DDSD_CATALOG");
                    if (local != null) return File.ReadAllText(local);
                    return NewClient().DownloadString(CATALOG_URL + "?t=" + DateTime.UtcNow.Ticks);
                });
                if (list.Count == 0 && Env("DDSD_CATALOG") == null)
                    list = ParseCatalog(delegate {
                        return LocalFeed(b) ? File.ReadAllText(Path.Combine(b, "mods.json"))
                                            : NewClient().DownloadString(b + "mods.json?t=" + DateTime.UtcNow.Ticks);
                    });
                Ui(delegate {
                    catalog = list; catalogLoading = false;
                    if (view == 2) ShowCatalog(true);
                    var a = afterCatalog; afterCatalog = null;
                    if (a != null) a();
                });
                // Optional pictures, after the cards are up. Remote feeds: http(s) only, 3 MB at most.
                foreach (var d in list)
                {
                    string img = Str(d, "image");
                    if (img == null) continue;
                    try
                    {
                        byte[] data = null;
                        if (LocalFeed(b) && SafeUrl(img) == null)
                        {
                            string p = Path.Combine(b, img);
                            if (Inside(p, b)) data = File.ReadAllBytes(p);
                        }
                        else if (SafeUrl(img) != null) data = NewClient().DownloadData(SafeUrl(img));
                        if (data == null || data.Length > 3 << 20) continue;
                        var image = Image.FromStream(new MemoryStream(data));
                        string key = img;
                        Ui(delegate { catalogImages[key] = image; if (view == 2) ShowCatalog(true); });
                    }
                    catch { }
                }
            }) { IsBackground = true }.Start();
        }

        // {mods:[...]} or a bare list; keeps entries with a name and a page or hub download we'd open. Empty on any failure
        // (no catalog yet / offline: the "No catalog yet" card).
        static List<Dictionary<string, object>> ParseCatalog(Func<string> read)
        {
            var list = new List<Dictionary<string, object>>();
            try
            {
                object root = new JavaScriptSerializer().DeserializeObject(read());
                var arr = root as object[];
                var dict = root as Dictionary<string, object>;
                if (arr == null && dict != null && dict.ContainsKey("mods")) arr = dict["mods"] as object[];
                if (arr != null)
                    foreach (var o in arr)
                    {
                        var d = o as Dictionary<string, object>;
                        if (d != null && Str(d, "name") != null && (SafeUrl(Str(d, "page")) != null || SafeDownload(Str(d, "download")) != null)) list.Add(d);
                    }
            }
            catch { }
            return list;
        }

        void ShowCatalog(bool keepScroll)
        {
            var items = new List<ModCard>();
            if (catalog == null)
            {
                items.Add(new ModCard { Title = "Loading the mod list...", Body = "Just a moment." });
                cards.SetItems(items, false);
                if (!catalogLoading) LoadCatalog();
                return;
            }
            if (catalog.Count == 0)
                items.Add(new ModCard { Title = "No catalog yet",
                    Body = "The mod list isn't up yet (or you're offline). Check back soon! You can still add any mod's .zip in My mods.",
                    Buttons = new[] { "My mods" }, Click = delegate(int b) { SetView(1); } });
            else
                items.Add(new ModCard { Note = true, Body = "Made by players and other teams, not by DDSD. Install puts a hub mod in My mods; Get & install opens the mod's site and installs the zip you download there." });
            var installed = new Dictionary<string, string>();
            foreach (var m in InstalledMods()) if (m.HubId != null) installed[m.HubId] = m.Version ?? "";
            foreach (var d in catalog)
            {
                var entry = d;
                string page = SafeUrl(Str(d, "page")), download = SafeDownload(Str(d, "download")), id = Str(d, "id");
                string author = Str(d, "author"), version = Str(d, "version"), license = Str(d, "license"), img = Str(d, "image");
                Image pic = null;
                if (img != null) catalogImages.TryGetValue(img, out pic);
                var meta = new List<string>();
                if (author != null) meta.Add("by " + author);
                if (version != null) meta.Add("v" + version.TrimStart('v', 'V'));
                if (license != null) meta.Add(license);
                long size;
                if (download != null && long.TryParse(Str(d, "size") ?? "", out size) && size > 0) meta.Add(FormatSize(size));
                if (page != null) meta.Add(new Uri(page).Host.Replace("www.", ""));
                // Hub-hosted: Install (Update / Reinstall when we have it), its page if any, Report. Otherwise: Mod page.
                var buttons = new List<string>(); var acts = new List<Action>();
                if (download != null)
                {
                    string have;
                    buttons.Add(id != null && installed.TryGetValue(id, out have) ? (have == (version ?? "") ? "Reinstall" : "Update") : "Install");
                    acts.Add(delegate { InstallHosted(entry, false); });
                }
                // Not hosted by us: "Get & install" opens the mod's own page and installs the zip as soon as it lands in
                // Downloads (WatchForModZip). The download still comes from the makers' own site.
                if (download == null && page != null)
                {
                    buttons.Add(watchEntry == entry ? "Cancel" : "Get & install");
                    acts.Add(delegate { if (watchEntry == entry) StopWatching("Stopped waiting."); else WatchForModZip(entry, page); });
                }
                if (page != null) { buttons.Add("Mod page"); acts.Add(delegate { Open(page); }); }
                if (download != null && id != null) { buttons.Add("Report"); acts.Add(delegate { OpenReport(entry); }); }
                items.Add(new ModCard { Title = Str(d, "name"), Meta = string.Join("   ·   ", meta.ToArray()),
                    Body = Str(d, "description") ?? "", Img = pic,
                    Buttons = buttons.ToArray(), Click = delegate(int b) { acts[b](); } });
            }
            cards.SetItems(items, keepScroll);
        }

        // Test hooks, once the update check is done (or failed).
        void RunTestHooks()
        {
            if (Env("DDSD_LOCATE") != null) { LocateDdlc(); return; }
            if (Env("DDSD_AUTO") == "install" && updateBtn.Enabled) { InstallOrUpdate(); return; }
            if (Env("DDSD_BETA") != null)
            {
                if (SavedBetaCode() == null && !betaTestDone) { betaTestDone = true; BetaClicked(); return; }
                if (busy) return;        // the beta install is running; it takes the screenshot when it's done
            }
            if (Env("DDSD_ADDMOD") != null) { SetView(1); AddMod(); return; }
            string play = Env("DDSD_PLAYMOD"), remove = Env("DDSD_REMOVEMOD");
            if (play != null || remove != null)
            {
                SetView(1);
                foreach (var m in InstalledMods())
                    if (Path.GetFileName(m.Dir) == (play ?? remove))
                    {
                        if (play != null) { PlayMod(m); TestShot(); } else RemoveMod(m, true);
                        return;
                    }
                Status("Test: no mod called " + (play ?? remove));
                TestShot();
                return;
            }
            if (HubTestHooks()) return;
            string v = Env("DDSD_VIEW");
            if (v == "browse") { SetView(2); WhenCatalog(TestShot); return; }
            if (v == "mods") SetView(1);
            if (v == "news") { SetView(5); WhenNews(TestShot); return; }
            TestShot();
        }
    }

    // One row in the Mods list.
    public class ModCard
    {
        public string Title = "", Meta = "", Body = "";
        public string[] Buttons = new string[0];
        public Action<int> Click;
        public Image Img;
        public bool Note;   // a plain line of text, no card
        public float H, ImgW;   // News cards: taller, with a wide picture (0 = the mod-card defaults)
    }

    // The scrolling list of mod cards, drawn by hand in logical pixels (like the rest of the launcher) with its own pill
    // buttons and a thin pink scrollbar. Wheel, drag the bar, click a button.
    public class ModCards : Control
    {
        List<ModCard> items = new List<ModCard>();
        float scroll, dragY = -1, dragScroll;
        int hoverCard = -1, hoverBtn = -1, downCard = -1, downBtn = -1;
        readonly FontFamily riffic, aller;
        const float CARD_H = 100, NOTE_H = 26, GAP = 10, BTN_H = 40;

        public ModCards(FontFamily riffic, FontFamily aller)
        {
            this.riffic = riffic; this.aller = aller;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.Selectable, true);
            BackColor = Launcher.CARD; TabStop = true;
        }

        public void SetItems(List<ModCard> list, bool keepScroll)
        {
            items = list;
            if (!keepScroll) scroll = 0;
            Clamp();
            hoverCard = hoverBtn = downCard = downBtn = -1;
            Invalidate();
        }

        static float S { get { return Launcher.S; } }
        float LW { get { return Width / S; } }
        float LH { get { return Height / S; } }
        static float Hgt(ModCard c) { return c.Note ? NOTE_H : c.H > 0 ? c.H : CARD_H; }
        float Content { get { float h = 0; foreach (var c in items) h += Hgt(c) + GAP; return Math.Max(0, h - GAP); } }
        float MaxScroll { get { return Math.Max(0, Content - LH); } }
        void Clamp() { scroll = Math.Max(0, Math.Min(MaxScroll, scroll)); }
        float CardW { get { return LW - (MaxScroll > 0 ? 18 : 2); } }

        RectangleF CardRect(int i)
        {
            float y = 0;
            for (int k = 0; k < i; k++) y += Hgt(items[k]) + GAP;
            return new RectangleF(1, y, CardW, Hgt(items[i]));
        }

        static RectangleF BtnRect(RectangleF r, int n, int b)
        {
            float w = n == 1 ? 132 : 96, gap = 8;
            float x = r.Right - 16 - (n - b) * w - (n - 1 - b) * gap;
            return new RectangleF(x, r.Y + (r.Height - BTN_H) / 2, w, BTN_H);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            g.ScaleTransform(S, S);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            for (int i = 0; i < items.Count; i++)
            {
                var r = CardRect(i); r.Y -= scroll;
                if (r.Bottom < 0 || r.Y > LH) continue;
                DrawCard(g, i, r);
            }
            if (MaxScroll > 0)
            {
                var track = new RectangleF(LW - 9, 2, 8, LH - 4);
                float th = Math.Max(30, track.Height * LH / Content);
                var thumb = new RectangleF(track.X, track.Y + (track.Height - th) * scroll / MaxScroll, 8, th);
                using (var b = new SolidBrush(Color.FromArgb(252, 226, 238))) g.FillPath(b, Launcher.RoundRect(track, 4));
                using (var b = new SolidBrush(Launcher.ROSE)) g.FillPath(b, Launcher.RoundRect(thumb, 4));
            }
        }

        void DrawCard(Graphics g, int i, RectangleF r)
        {
            var c = items[i];
            if (c.Note)
            {
                using (var f = Launcher.PF(aller, 9.5f, FontStyle.Italic)) using (var b = new SolidBrush(Launcher.SOFT))
                using (var sf = new StringFormat { LineAlignment = StringAlignment.Center })
                    g.DrawString(c.Body, f, b, new RectangleF(r.X + 8, r.Y, r.Width - 16, r.Height), sf);
                return;
            }
            using (var b = new SolidBrush(Color.FromArgb(255, 247, 251))) g.FillPath(b, Launcher.RoundRect(r, 14));
            using (var pen = new Pen(Launcher.LINE, 2)) g.DrawPath(pen, Launcher.RoundRect(r, 14));
            float tx = r.X + 20;
            if (c.Img != null)
            {
                var ir = new RectangleF(r.X + 12, r.Y + 12, c.ImgW > 0 ? c.ImgW : 136, r.Height - 24);
                var st = g.Save();
                g.SetClip(Launcher.RoundRect(ir, 10));
                float k = Math.Max(ir.Width / c.Img.Width, ir.Height / c.Img.Height);   // cover, centred
                float w = c.Img.Width * k, h = c.Img.Height * k;
                g.DrawImage(c.Img, ir.X + (ir.Width - w) / 2, ir.Y + (ir.Height - h) / 2, w, h);
                g.Restore(st);
                tx = ir.Right + 16;
            }
            int n = c.Buttons.Length;
            float right = n > 0 ? BtnRect(r, n, 0).X - 14 : r.Right - 20;
            using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
            {
                using (var f = Launcher.PF(riffic, 13f)) using (var b = new SolidBrush(Launcher.ROSE))
                    g.DrawString(c.Title, f, b, new RectangleF(tx, r.Y + 12, right - tx, 26), sf);
                using (var f = Launcher.PF(aller, 9f)) using (var b = new SolidBrush(Launcher.SOFT))
                    g.DrawString(c.Meta, f, b, new RectangleF(tx, r.Y + 39, right - tx, 18), sf);
            }
            using (var f = Launcher.PF(aller, 9.5f)) using (var b = new SolidBrush(Launcher.INK))
            using (var sf = new StringFormat { Trimming = StringTrimming.EllipsisWord })
            {
                float by = c.Meta.Length > 0 ? r.Y + 58 : r.Y + 42;
                g.DrawString(c.Body, f, b, new RectangleF(tx, by, right - tx, Math.Max(36, r.Bottom - 10 - by)), sf);
            }
            for (int k = 0; k < n; k++)
                DrawPill(g, BtnRect(r, n, k), c.Buttons[k], k == 0, hoverCard == i && hoverBtn == k, downCard == i && downBtn == k);
        }

        // Same look as PillButton (shadow, hover lift, white rim).
        void DrawPill(Graphics g, RectangleF r, string text, bool primary, bool over, bool down)
        {
            float lift = over && !down ? 0 : 2, rad = (r.Height - 8) / 2f;
            var body = new RectangleF(r.X + 2, r.Y + lift, r.Width - 4, r.Height - 8);
            using (var sh = new SolidBrush(Color.FromArgb(45, 150, 60, 110))) g.FillPath(sh, Launcher.RoundRect(new RectangleF(r.X + 2, r.Y + 6, r.Width - 4, r.Height - 8), rad));
            Color fill = primary ? (over ? Launcher.ROSE_DARK : Launcher.ROSE) : (over ? Launcher.SOFT_BTN_HOVER : Launcher.SOFT_BTN);
            using (var b = new SolidBrush(fill)) g.FillPath(b, Launcher.RoundRect(body, rad));
            using (var pen = new Pen(Color.White, 2.5f)) g.DrawPath(pen, Launcher.RoundRect(new RectangleF(body.X + 1.5f, body.Y + 1.5f, body.Width - 3, body.Height - 3), rad - 1.5f));
            using (var f = Launcher.PF(riffic, 11f)) using (var b = new SolidBrush(primary ? Color.White : Launcher.PLUM))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(text, f, b, body, sf);
        }

        void HitTest(Point p, out int card, out int btn)
        {
            card = btn = -1;
            float x = p.X / S, y = p.Y / S + scroll;
            for (int i = 0; i < items.Count; i++)
            {
                var r = CardRect(i);
                if (!r.Contains(x, y)) continue;
                card = i;
                int n = items[i].Buttons.Length;
                for (int b = 0; b < n; b++) if (BtnRect(r, n, b).Contains(x, y)) btn = b;
                return;
            }
        }

        void Hover(Point p)
        {
            int c, b; HitTest(p, out c, out b);
            if (b < 0) c = -1;
            if (c != hoverCard || b != hoverBtn) { hoverCard = c; hoverBtn = b; Invalidate(); }
            Cursor = b >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseEnter(EventArgs e) { Focus(); base.OnMouseEnter(e); }   // so the wheel reaches us
        protected override void OnMouseLeave(EventArgs e) { hoverCard = hoverBtn = downCard = downBtn = -1; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (MaxScroll > 0 && e.X / S > LW - 16) { dragY = e.Y / S; dragScroll = scroll; return; }
            HitTest(e.Location, out downCard, out downBtn);
            if (downBtn < 0) downCard = -1;
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragY >= 0)
            {
                float track = Math.Max(1, LH - Math.Max(30, LH * LH / Content));
                scroll = dragScroll + (e.Y / S - dragY) * MaxScroll / track;
                Clamp(); Invalidate();
                return;
            }
            Hover(e.Location);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (dragY >= 0) { dragY = -1; return; }
            int c, b; HitTest(e.Location, out c, out b);
            bool fire = b >= 0 && c == downCard && b == downBtn;
            downCard = downBtn = -1;
            Invalidate();
            if (fire && items[c].Click != null) items[c].Click(b);
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120f * 56;
            Clamp(); Invalidate();
            Hover(e.Location);
        }

        protected override void OnResize(EventArgs e) { Clamp(); Invalidate(); base.OnResize(e); }
    }
}
