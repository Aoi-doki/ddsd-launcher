// DDSD Launcher: installer + updater + play button for Doki Doki Second Draft.
// Built with Windows' own C# compiler (tools/build_launcher.sh), so players need nothing else installed.
// C# 5 only (that compiler): no $"" strings, no ?. operator.
//
//   Setup       players download DDSD-Setup.exe (this same program). Run from anywhere else, it copies itself to
//               %LOCALAPPDATA%\DDSD, makes Desktop + Start Menu shortcuts, adds "Doki Doki Second Draft" to Windows'
//               Installed apps (with an uninstaller), then reopens from there. So self-updates always hit the
//               installed copy, never the one left in Downloads.
//   Uninstall   "DDSD Launcher.exe --uninstall" (Installed apps > Uninstall): removes the game, the launcher and the
//               shortcuts. Saves stay in %APPDATA%\RenPy, so a reinstall picks them back up.
//   Install /   downloads latest.json from the newest GitHub release (Aoi-doki/ddsd-releases), then the game zip;
//   Update      checks its SHA-256, swaps the game files in, keeps saves and DDLC's three .rpa files.
//   DDLC files  finds audio.rpa / fonts.rpa / images.rpa (Steam's original DDLC, ddlc.moe zips in Downloads...),
//               or lets the player pick a folder or zip. They are never downloaded or redistributed by us.
//   Self-update if latest.json has a newer launcher, it replaces itself and restarts.
//   Mods        the Mods tab (Mods.cs): other DDLC mods from their .zip, each in its own copy of the player's DDLC,
//               plus a Browse list. ModHub.cs: the DDSD Mod Hub's catalog (one-click Install for mods shared there),
//               Share a mod (sends a mod to Aoi for review) and Report.
// Testing: DDSD_FEED = a local folder that holds latest.json + the files (no GitHub involved);
// DDSD_HOME = install somewhere else; DDSD_NO_SHORTCUTS = 1; DDSD_AUTO = install (installs, then)
// DDSD_SHOT = <png> (screenshots the window and quits when idle); DDSD_LOCATE = <zip or DDLC.exe> runs Locate DDLC on it. With DDSD_HOME set, the Installed-apps entry is
// "DokiDokiSecondDraft-test", so tests never touch the real one. While DDSD_SHOT is set the window opens off-screen
// without taking focus. The Mods tab's own hooks are listed in Mods.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: AssemblyTitle("DDSD Launcher")]
[assembly: AssemblyProduct("Doki Doki Second Draft")]
[assembly: AssemblyVersion(DDSD.Launcher.VERSION_NUM)]

namespace DDSD
{
    public partial class Launcher : Form
    {
        public const string VERSION = "1.8";
        public const string VERSION_NUM = "1.8.0.0";
        const string FEED = "https://github.com/Aoi-doki/ddsd-releases/releases/latest/download/";
        // The DDSD Mod Hub worker (tools/modhub). Until it's deployed, Share / Report say "opens soon" (ModHub.cs).
        const string MODHUB = "https://ddsd-modhub.callmerileyyyyy.workers.dev";
        const string GAME_EXE = "DokiDokiSecondDraft.exe";
        static readonly string[] RPAS = { "audio.rpa", "fonts.rpa", "images.rpa" };
        const string DDLC_URL = "https://ddlc.moe";
        const string DISCORD_URL = "https://discord.gg/FV4pHRJVMU";

        internal static readonly Color BG =Color.FromArgb(255, 238, 246), CARD = Color.White, INK = Color.FromArgb(90, 61, 79),
            SOFT = Color.FromArgb(160, 128, 148), ROSE = Color.FromArgb(232, 91, 154), ROSE_DARK = Color.FromArgb(204, 66, 128),
            LINE = Color.FromArgb(247, 205, 225), DOT = Color.FromArgb(255, 222, 237), PLUM = Color.FromArgb(187, 85, 153),
            SOFT_BTN = Color.FromArgb(255, 226, 239), SOFT_BTN_HOVER = Color.FromArgb(252, 210, 229);

        string home = Environment.GetEnvironmentVariable("DDSD_HOME") ??
                      Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDSD");
        string GameDir { get { return Path.Combine(home, "game"); } }
        string RpaDir { get { return Path.Combine(GameDir, "game"); } }
        string VersionFile { get { return Path.Combine(home, "version.txt"); } }
        // SHA-256 of the installed game zip: a rebuilt release with the same version name still counts as an update.
        string BuildFile { get { return Path.Combine(home, "build.txt"); } }

        [DllImport("gdi32.dll")] static extern IntPtr AddFontMemResourceEx(IntPtr pbFont, uint cbFont, IntPtr pdv, ref uint pcFonts);
        PrivateFontCollection fonts = new PrivateFontCollection();
        FontFamily riffic, aller;
        string statusText = "", versionText = "", notesTitleText = "What's new";
        TextBox notes;
        PillButton playBtn, updateBtn, locateBtn, ddlcBtn, discordBtn, gameFolderBtn;
        PinkScroll notesScroll;
        PinkBar bar;
        Dictionary<string, object> latest;
        bool busy;
        Image logo;
        // drifting petals (drawn in code, not DDLC's art: the launcher is distributed on its own)
        List<float[]> petals = new List<float[]>();
        Random rnd = new Random(7);
        System.Windows.Forms.Timer anim;
        Point dragFrom;
        Rectangle closeRect, minRect;
        int hoverChrome = 0;

        // Sharp on high-DPI screens (players: "the render quality is a bit low, some texts are lq"). Without this, Windows
        // draws the launcher at 100% and stretches the bitmap to 125%/150%. Now we say we handle DPI ourselves: the layout
        // stays in 920x580 "logical" pixels and everything is drawn at S = screen DPI / 96.
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("shcore.dll")] static extern int SetProcessDpiAwareness(int value);
        public static float S = 1f;
        static int Sc(float v) { return (int)Math.Round(v * S); }
        static Rectangle R(int x, int y, int w, int h) { return new Rectangle(Sc(x), Sc(y), Sc(w), Sc(h)); }
        static Point Unscale(Point p) { return new Point((int)(p.X / S), (int)(p.Y / S)); }
        // Fonts drawn under the S transform: sized in 96-DPI pixels, so they aren't scaled twice.
        internal static Font PF(FontFamily fam, float pt, FontStyle style = FontStyle.Regular) { return new Font(fam, pt * 96f / 72f, style, GraphicsUnit.Pixel); }

        [STAThread]
        static void Main(string[] args)
        {
            try { SetProcessDpiAwareness(1); } catch { try { SetProcessDPIAware(); } catch { } }   // system-DPI aware
            using (var screen = Graphics.FromHwnd(IntPtr.Zero)) S = Math.Max(1f, screen.DpiX / 96f);
            float forced;   // test hook: DDSD_SCALE=1.5 draws as on a 150% screen
            if (float.TryParse(Environment.GetEnvironmentVariable("DDSD_SCALE") ?? "", System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out forced) && forced > 0) S = forced;
            ServicePointManager.SecurityProtocol = (SecurityProtocolType)3072;   // TLS 1.2 (GitHub needs it)
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (args.Length == 2 && args[0] == "--finish-self-update") { Thread.Sleep(800); }
            if (args.Length >= 1 && args[0] == "--uninstall") { Uninstall(args.Length >= 2 && args[1] == "--quiet"); return; }
            if (args.Length >= 1 && args[0] == "--from-setup") { Thread.Sleep(300); }
            Application.Run(new Launcher());
        }

        // ------------------------------------------------------------------ look
        const int W = 920, H = 580;

        public Launcher()
        {
            Text = "Doki Doki Second Draft";
            ClientSize = new Size(Sc(W), Sc(H));
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = BG;
            DoubleBuffered = true;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            Region = new Region(RoundRect(new RectangleF(0, 0, Sc(W), Sc(H)), 22 * S));
            LoadFonts();
            logo = LoadImage("ddsd_logo.png");
            closeRect = new Rectangle(W - 46, 12, 30, 30);
            minRect = new Rectangle(W - 82, 12, 30, 30);
            musicRect = new Rectangle(W - 118, 12, 30, 30);   // launcher music on/off (Music.cs)

            playBtn = new PillButton(this, "Play", R(560, 200, 300, 78), 30f, ROSE, ROSE_DARK, Color.White, riffic);
            playBtn.Click += delegate { Play(); };
            updateBtn = new PillButton(this, "Install", R(560, 292, 300, 56), 18f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            updateBtn.Click += delegate { InstallOrUpdate(); };
            discordBtn = new PillButton(this, "Join the Discord", R(560, 360, 145, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            discordBtn.Click += delegate { Open(DISCORD_URL); };
            gameFolderBtn = new PillButton(this, "Game folder", R(715, 360, 145, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            gameFolderBtn.Click += delegate { if (Directory.Exists(GameDir)) Open(GameDir); };
            locateBtn = new PillButton(this, "Locate DDLC...", R(560, 412, 145, 40), 11f, ROSE, ROSE_DARK, Color.White, riffic);   // zip or DDLC.exe (LocateDdlc)
            locateBtn.Click += delegate { LocateDdlc(); };
            ddlcBtn = new PillButton(this, "Get DDLC (free)", R(715, 412, 145, 40), 11f, ROSE, ROSE_DARK, Color.White, riffic);
            ddlcBtn.Click += delegate { Open(DDLC_URL); };
            locateBtn.Visible = ddlcBtn.Visible = false;

            notes = new TextBox { Multiline = true, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = CARD, ForeColor = INK,
                                  Font = new Font(aller, 10f * 96f / 72f * S, GraphicsUnit.Pixel), ScrollBars = ScrollBars.None, Bounds = R(66, 336, 430, 150),   // no grey Windows bar:
                                  Text = "Checking for news...", TabStop = false };
            Controls.Add(notes);
            notesScroll = new PinkScroll(notes) { Bounds = R(502, 338, 10, 146) };   // our own pink one (players asked for a slider)
            Controls.Add(notesScroll);
            bar = new PinkBar { Bounds = R(560, 470, 300, 14), Visible = false };
            Controls.Add(bar);
            BuildModsUi();
            BuildBetaUi();
            if (Env("DDSD_SHOT") != null) { StartPosition = FormStartPosition.Manual; Location = new Point(-32000, -32000); ShowInTaskbar = false; }

            for (int i = 0; i < 26; i++) petals.Add(NewPetal(true));
            anim = new System.Windows.Forms.Timer { Interval = 33 };
            anim.Tick += delegate { StepPetals(); notesScroll.Sync(); };
            anim.Start();

            MouseDown += delegate(object s, MouseEventArgs e) { if (e.Button == MouseButtons.Left) dragFrom = e.Location; };
            MouseMove += delegate(object s, MouseEventArgs e) {
                var lp = Unscale(e.Location);
                int h = closeRect.Contains(lp) ? 1 : minRect.Contains(lp) ? 2 : musicRect.Contains(lp) ? -2 : TabAt(lp);
                if (h != hoverChrome) { hoverChrome = h; Invalidate(); Cursor = h >= 3 || h == -2 ? Cursors.Hand : Cursors.Default; }
                if (e.Button == MouseButtons.Left && hoverChrome == 0) Location = new Point(Location.X + e.X - dragFrom.X, Location.Y + e.Y - dragFrom.Y);
            };
            MouseUp += delegate(object s, MouseEventArgs e) {
                if (closeRect.Contains(Unscale(e.Location))) Close();
                else if (minRect.Contains(Unscale(e.Location))) WindowState = FormWindowState.Minimized;
                else if (musicRect.Contains(Unscale(e.Location))) ToggleMusic();
                else TabClick(TabAt(Unscale(e.Location)));
            };

            Shown += delegate { FirstRunSetup(); if (relaunched) return; StartMusic(); Refresh2(); CheckForUpdates(); };
            FormClosing += delegate { StopMusic(); };
        }

        float[] NewPetal(bool anywhere)
        {
            // x, y, size, speed, sway phase, rotation, spin
            return new float[] { rnd.Next(0, W), anywhere ? rnd.Next(0, H) : -20, rnd.Next(7, 15), 0.4f + (float)rnd.NextDouble() * 0.7f,
                                 (float)(rnd.NextDouble() * 6.28), rnd.Next(0, 360), (float)(rnd.NextDouble() * 2 - 1) };
        }

        void StepPetals()
        {
            foreach (var p in petals)
            {
                p[1] += p[3]; p[4] += 0.02f; p[5] += p[6];
                p[0] += (float)Math.Sin(p[4]) * 0.35f;
                if (p[1] > H + 20) { var n = NewPetal(false); Array.Copy(n, p, n.Length); }
            }
            if (view != 0)   // Mods: petals drift around the big card, which covers them
            {
                Invalidate(R(0, 0, W, 122)); Invalidate(R(0, 506, W, H - 506));
                Invalidate(R(0, 122, 40, 384)); Invalidate(R(880, 122, W - 880, 384));
                return;
            }
            Invalidate(R(0, 0, 540, H));   // petals live behind the left side; buttons stay still
            Invalidate(R(540, 0, W - 540, 190));
            Invalidate(R(540, 496, W - 540, H - 496));
        }

        internal static GraphicsPath RoundRect(RectangleF r, float rad)
        {
            var p = new GraphicsPath();
            float d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Riffic text with a thick white outline and a pink edge, like the game's titles.
        void OutlinedText(Graphics g, string text, float size, RectangleF r, Color fill, Color edge, StringAlignment align)
        {
            using (var path = new GraphicsPath())
            using (var sf = new StringFormat { Alignment = align, LineAlignment = StringAlignment.Center })
            {
                path.AddString(text, riffic, 0, size * 1.333f, r, sf);
                using (var p1 = new Pen(edge, size * 0.42f) { LineJoin = LineJoin.Round }) g.DrawPath(p1, path);
                using (var p2 = new Pen(Color.White, size * 0.2f) { LineJoin = LineJoin.Round }) g.DrawPath(p2, path);
                using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.ScaleTransform(S, S);   // draw in logical 920x580 pixels, at the screen's real resolution
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAlias;   // no grid-fit: under the DPI transform it spaced letters unevenly;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var bg = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(255, 244, 249), Color.FromArgb(255, 228, 240), 60f))
                g.FillRectangle(bg, 0, 0, W, H);
            using (var b = new SolidBrush(DOT))
                for (int y = -10; y < H; y += 56)
                    for (int x = ((y / 56) % 2 == 0 ? 0 : 28) - 10; x < W; x += 56)
                        g.FillEllipse(b, x, y, 20, 20);
            // petals
            foreach (var p in petals)
            {
                var st = g.Save();
                g.TranslateTransform(p[0], p[1]); g.RotateTransform(p[5]);
                using (var b = new SolidBrush(Color.FromArgb(150, 255, 172, 205))) g.FillEllipse(b, -p[2] / 2, -p[2] / 4, p[2], p[2] / 2);
                g.Restore(st);
            }
            if (view != 0) PaintMods(g);
            else PaintHome(g);
            PaintTabs(g);
            // window chrome
            using (var b = new SolidBrush(hoverChrome == 1 ? ROSE : Color.FromArgb(0, 0, 0, 0))) g.FillEllipse(b, closeRect);
            using (var b = new SolidBrush(hoverChrome == 2 ? LINE : Color.FromArgb(0, 0, 0, 0))) g.FillEllipse(b, minRect);
            using (var pen = new Pen(hoverChrome == 1 ? Color.White : PLUM, 2.4f))
            {
                g.DrawLine(pen, closeRect.X + 10, closeRect.Y + 10, closeRect.Right - 10, closeRect.Bottom - 10);
                g.DrawLine(pen, closeRect.Right - 10, closeRect.Y + 10, closeRect.X + 10, closeRect.Bottom - 10);
            }
            using (var pen = new Pen(PLUM, 2.4f)) g.DrawLine(pen, minRect.X + 9, minRect.Y + 16, minRect.Right - 9, minRect.Y + 16);
            PaintMusicButton(g, hoverChrome == -2);
            using (var pen = new Pen(Color.FromArgb(240, 170, 205), 3)) g.DrawPath(pen, RoundRect(new RectangleF(1, 1, W - 3, H - 3), 21));
        }

        void PaintHome(Graphics g)
        {
            // logo + title
            if (logo != null) g.DrawImage(logo, new Rectangle(40, 34, 180, 180));
            OutlinedText(g, "Doki Doki", 32f, new RectangleF(232, 54, 330, 60), PLUM, Color.FromArgb(240, 150, 196), StringAlignment.Near);
            OutlinedText(g, "Second Draft", 32f, new RectangleF(232, 110, 330, 60), ROSE, Color.FromArgb(240, 150, 196), StringAlignment.Near);
            using (var f = PF(aller, 11.5f, FontStyle.Italic)) using (var b = new SolidBrush(SOFT))
                g.DrawString("Same club. Kinder story.", f, b, 240, 172);
            using (var f = PF(aller, 10f)) using (var b = new SolidBrush(SOFT))
                g.DrawString(versionText, f, b, 240, 196);
            // news card
            var card = new RectangleF(40, 264, 496, 238);
            using (var sh = new SolidBrush(Color.FromArgb(28, 150, 60, 110))) g.FillPath(sh, RoundRect(new RectangleF(card.X + 4, card.Y + 7, card.Width, card.Height), 18));
            using (var b = new SolidBrush(CARD)) g.FillPath(b, RoundRect(card, 18));
            using (var pen = new Pen(LINE, 3)) g.DrawPath(pen, RoundRect(card, 18));
            using (var f = PF(riffic, 15f)) using (var b = new SolidBrush(ROSE)) g.DrawString(notesTitleText, f, b, 62, 284);
            // status + footer
            using (var f = PF(riffic, 11f)) using (var b = new SolidBrush(PLUM))   // Riffic, like the buttons above it
                g.DrawString(statusText, f, b, new RectangleF(560, 494, 320, 44));
            using (var f = PF(aller, 9f)) using (var b = new SolidBrush(SOFT))
                g.DrawString("Fan mod, not affiliated with Team Salvato. Needs DDLC (free).   Launcher " + VERSION, f, b, 40, H - 32);
        }

        protected override bool ShowWithoutActivation { get { return Env("DDSD_SHOT") != null; } }   // tests never steal focus

        void LoadFonts()
        {
            foreach (var name in new[] { "RifficFree-Bold.ttf", "Aller_Rg.ttf" })
            {
                byte[] data = Resource(name);
                if (data == null) continue;
                IntPtr p = Marshal.AllocCoTaskMem(data.Length);   // kept for the app's lifetime (fonts need it)
                Marshal.Copy(data, 0, p, data.Length);
                fonts.AddMemoryFont(p, data.Length);             // for GDI+ drawing
                uint n = 0;
                AddFontMemResourceEx(p, (uint)data.Length, IntPtr.Zero, ref n);   // and for normal controls (the news box)
            }
            riffic = aller = FontFamily.GenericSansSerif;
            foreach (var f in fonts.Families)
            {
                if (f.Name.StartsWith("Riffic")) riffic = f;
                if (f.Name.StartsWith("Aller")) aller = f;
            }
        }

        static byte[] Resource(string name)
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) return null;
                var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray();
            }
        }

        static Image LoadImage(string name)
        {
            byte[] d = Resource(name);
            return d == null ? null : Image.FromStream(new MemoryStream(d));
        }

        // ------------------------------------------------------------------ state
        string Installed()
        {
            return File.Exists(VersionFile) && File.Exists(Path.Combine(GameDir, GAME_EXE)) ? File.ReadAllText(VersionFile).Trim() : null;
        }

        bool SameBuild()
        {
            string want = latest != null ? Str(latest, "game_sha256") : null;
            if (string.IsNullOrEmpty(want)) return true;
            string have = File.Exists(BuildFile) ? File.ReadAllText(BuildFile).Trim() : "";
            return string.Equals(have, want, StringComparison.OrdinalIgnoreCase);
        }

        bool HasDdlcFiles()
        {
            foreach (var r in RPAS) if (!File.Exists(Path.Combine(RpaDir, r))) return false;
            return true;
        }

        void Refresh2()
        {
            string inst = Installed();
            string avail = latest != null ? Str(latest, "version") : null;
            bool rebuilt = inst != null && avail != null && avail == inst && !SameBuild();
            versionText = inst == null ? "Not installed yet" : "Installed: " + inst + (avail != null && avail != inst ? "   (new: " + avail + ")" : rebuilt ? "   (new build)" : "");
            if (betaFeed != null) versionText += "   · beta";
            if (betaBtn != null) betaBtn.Text = SavedBetaCode() != null ? "Beta: on" : "Beta code";
            bool needUpdate = inst == null || (avail != null && avail != inst) || rebuilt;
            updateBtn.Text = inst == null ? "Install" : (needUpdate && avail != null ? "Update" : "Up to date");
            updateBtn.Enabled = !busy && (inst == null || needUpdate) && (latest != null || inst == null);
            playBtn.Enabled = !busy && inst != null && HasDdlcFiles();
            bool missingDdlc = inst != null && !HasDdlcFiles();
            locateBtn.Visible = ddlcBtn.Visible = missingDdlc && view == 0;
            addModBtn.Enabled = sendShareBtn.Enabled = sendReportBtn.Enabled = !busy;
            if (!busy && view == 0)
            {
                if (missingDdlc) Status("DDSD needs DDLC's files (it's free). Locate your DDLC folder or zip.");
                else if (inst == null) Status(latest == null ? "Checking for the game..." : "Ready to install " + avail + ".");
                else if (needUpdate && avail != null) Status("An update is ready: " + avail + ".");
                else Status("Ready to play!");
            }
        }

        void Status(string s) { statusText = s; Invalidate(); }

        void Ui(Action a) { if (InvokeRequired) BeginInvoke(a); else a(); }

        // ------------------------------------------------------------------ first run: install the launcher itself
        bool relaunched;

        static string UninstallKey()
        {
            return "Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\" +
                   (Environment.GetEnvironmentVariable("DDSD_HOME") != null ? "DokiDokiSecondDraft-test" : "DokiDokiSecondDraft");
        }

        // Windows' Installed apps entry (per user, no admin needed).
        void RegisterUninstall(string target)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey()))
                {
                    k.SetValue("DisplayName", "Doki Doki Second Draft");
                    k.SetValue("DisplayVersion", Installed() ?? VERSION);
                    k.SetValue("Publisher", "Aoi (fan mod, not affiliated with Team Salvato)");
                    k.SetValue("DisplayIcon", target + ",0");
                    k.SetValue("InstallLocation", home);
                    k.SetValue("UninstallString", "\"" + target + "\" --uninstall");
                    k.SetValue("QuietUninstallString", "\"" + target + "\" --uninstall --quiet");
                    k.SetValue("URLInfoAbout", "https://aoi-doki.github.io/ddsd/");
                    k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                    k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                    long bytes = 0;
                    try { foreach (var f in Directory.GetFiles(home, "*", SearchOption.AllDirectories)) bytes += new FileInfo(f).Length; } catch { }
                    k.SetValue("EstimatedSize", (int)(bytes / 1024), RegistryValueKind.DWord);
                }
            }
            catch { }
        }

        static void Uninstall(bool quiet)
        {
            string home = Path.GetDirectoryName(Application.ExecutablePath);
            if (!quiet && MessageBox.Show("Uninstall Doki Doki Second Draft?\n\nThe game, the launcher, the shortcuts and any mods added in the launcher are removed. " +
                    "Your saves stay (in %APPDATA%\\RenPy), so if you install DDSD again you carry on where you left off.",
                    "Uninstall DDSD", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (Process pr in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GAME_EXE)))
                try { pr.Kill(); pr.WaitForExit(3000); } catch { }
            if (Environment.GetEnvironmentVariable("DDSD_NO_SHORTCUTS") == null)
                foreach (var where in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
                    try { File.Delete(ShortcutPath(where)); } catch { }
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey(), false); } catch { }
            // Only ever delete our own install folder (it must hold the launcher and our version file).
            string expected = Environment.GetEnvironmentVariable("DDSD_HOME") ??
                              Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DDSD");
            if (!string.Equals(Path.GetFullPath(home).TrimEnd('\\'), Path.GetFullPath(expected).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)
                || !File.Exists(Path.Combine(home, "DDSD Launcher.exe")))
                return;
            foreach (var d in new[] { "game", "download", "new", "mods" })
                try { if (Directory.Exists(Path.Combine(home, d))) Directory.Delete(Path.Combine(home, d), true); } catch { }
            // The running exe can't delete itself: a hidden cmd finishes the job once we've exited.
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + home + "\"")
                          { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
            if (!quiet) MessageBox.Show("Doki Doki Second Draft has been uninstalled. Thanks for playing!", "Uninstall DDSD");
        }

        void FirstRunSetup()
        {
            try
            {
                Directory.CreateDirectory(home);
                string me = Application.ExecutablePath;
                string target = Path.Combine(home, "DDSD Launcher.exe");
                if (!string.Equals(Path.GetFullPath(me), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    // Running as the setup (e.g. from Downloads): install this launcher unless the installed one is newer,
                    // then hand over to the installed copy, so later self-updates replace that one.
                    bool installedNewer = false;
                    try { installedNewer = File.Exists(target) && new Version(FileVersionInfo.GetVersionInfo(target).FileVersion) > new Version(VERSION_NUM); } catch { }
                    if (!installedNewer)
                        try { File.Copy(me, target, true); }
                        catch (IOException) { if (!File.Exists(target)) throw; }   // the installed launcher is open: use it as is
                    MakeShortcuts(target);
                    RegisterUninstall(target);
                    Process.Start(new ProcessStartInfo(target, "--from-setup") { WorkingDirectory = home, UseShellExecute = false });
                    relaunched = true;
                    Close();
                    return;
                }
                if (!File.Exists(ShortcutPath(Environment.SpecialFolder.DesktopDirectory)))
                    MakeShortcuts(target);
                RegisterUninstall(target);   // keeps the size / version in Installed apps current
                string old = target + ".old";
                if (File.Exists(old)) try { File.Delete(old); } catch { }
            }
            catch (Exception ex) { Status("Couldn't set up the launcher: " + ex.Message); }
        }

        static string ShortcutPath(Environment.SpecialFolder where)
        {
            return Path.Combine(Environment.GetFolderPath(where), "Doki Doki Second Draft.lnk");
        }

        void MakeShortcuts(string target)
        {
            if (Environment.GetEnvironmentVariable("DDSD_NO_SHORTCUTS") != null) return;
            foreach (var where in new[] { Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolder.Programs })
            {
                try
                {
                    Type t = Type.GetTypeFromProgID("WScript.Shell");
                    object shell = Activator.CreateInstance(t);
                    object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { ShortcutPath(where) });
                    Type lt = lnk.GetType();
                    lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                    lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { home });
                    lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Doki Doki Second Draft" });
                    lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                    lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                }
                catch { }
            }
        }

        // ------------------------------------------------------------------ feed
        static string FeedBase()
        {
            string f = Environment.GetEnvironmentVariable("DDSD_FEED");
            if (string.IsNullOrEmpty(f)) f = betaFeed;          // a beta code's feed (Beta.cs)
            if (string.IsNullOrEmpty(f)) return FEED;
            return f.EndsWith("/") || f.EndsWith("\\") ? f : f + "/";
        }

        static bool LocalFeed(string b) { return !b.StartsWith("http", StringComparison.OrdinalIgnoreCase); }

        static string Str(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v != null ? v.ToString() : null;
        }

        void CheckForUpdates()
        {
            new Thread(delegate() {
                try
                {
                    string betaMsg = betaInstallAfterCheck ? null : ResolveBeta();
                    if (betaMsg != null) Ui(delegate { Status(betaMsg); });
                    string b = FeedBase();
                    string json = LocalFeed(b) ? File.ReadAllText(Path.Combine(b, "latest.json"))
                                               : NewClient().DownloadString(b + "latest.json?t=" + DateTime.UtcNow.Ticks);
                    var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
                    Ui(delegate {
                        latest = d;
                        notesTitleText = "What's new in " + Str(d, "version");
                        notes.Text = (Str(d, "notes") ?? "").Replace("\r\n", "\n").Replace("\n", "\r\n");
                        Refresh2();
                        MaybeSelfUpdate();
                        if (betaInstallAfterCheck)
                        {
                            betaInstallAfterCheck = false;
                            if (updateBtn.Enabled) InstallOrUpdate();
                            else Status("You're on the beta, and already up to date.");
                        }
                        RunTestHooks();
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate {
                        notes.Text = "Couldn't check for updates (are you online?).\r\n\r\n" + ex.Message;
                        Refresh2();
                        RunTestHooks();
                    });
                }
            }) { IsBackground = true }.Start();
        }

        static WebClient NewClient()
        {
            var c = new WebClient();
            c.Headers.Add("User-Agent", "DDSD-Launcher/" + VERSION);
            return c;
        }

        // ------------------------------------------------------------------ install / update the game
        void InstallOrUpdate()
        {
            if (latest == null || busy) return;
            if (GameRunning())
            {
                Status("Close Doki Doki Second Draft first, then press Update again.");
                return;
            }
            busy = true; Refresh2();
            bar.Visible = true; bar.Value = 0; bar.Marquee = false;
            string zipName = Str(latest, "game_zip"), sha = Str(latest, "game_sha256"), ver = Str(latest, "version");
            string tmp = Path.Combine(home, "download");
            new Thread(delegate() {
                try
                {
                    Directory.CreateDirectory(tmp);
                    string zip = Path.Combine(tmp, zipName);
                    Fetch(zipName, zip, "Downloading " + ver);
                    Ui(delegate { Status("Checking the download..."); });
                    if (!string.Equals(Sha256(zip), sha, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("the download was damaged (checksum mismatch). Please try again.");
                    Ui(delegate { Status("Unpacking..."); bar.Marquee = true; });
                    string root = Path.Combine(home, "new");   // short path: keeps deep engine files under MAX_PATH
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                    Unpack(zip, root);
                    if (!File.Exists(Path.Combine(root, GAME_EXE))) throw new Exception("the download doesn't contain " + GAME_EXE + ".");
                    Ui(delegate { Status("Installing..."); });
                    SwapIn(root, GameDir);
                    try { Directory.Delete(root, true); } catch { }
                    File.WriteAllText(VersionFile, ver);
                    File.WriteAllText(BuildFile, sha);
                    RegisterUninstall(Path.Combine(home, "DDSD Launcher.exe"));   // new version + size in Installed apps
                    try { Directory.Delete(tmp, true); } catch { }
                    if (!HasDdlcFiles()) FindDdlcFiles();
                    Ui(delegate {
                        busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                        if (HasDdlcFiles()) Status("Installed " + ver + ". Ready to play!");
                        TestShot();
                    });
                }
                catch (Exception ex)
                {
                    Ui(delegate {
                        busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                        Status("Install failed: " + ex.Message);
                        TestShot();
                    });
                }
            }) { IsBackground = true }.Start();
        }

        bool GameRunning()
        {
            foreach (Process pr in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(GAME_EXE)))
            {
                try { if (pr.MainModule.FileName.StartsWith(home, StringComparison.OrdinalIgnoreCase)) return true; }
                catch { return true; }   // can't tell where it runs from: play it safe
            }
            return false;
        }

        // Downloads to <dest>.part, then renames, so a cut-off download never looks finished. Retries 3 times.
        void Fetch(string name, string dest, string label)
        {
            Exception last = null;
            for (int attempt = 1; attempt <= 3; attempt++)
            {
                try
                {
                    FetchOnce(name, dest + ".part", attempt == 1 ? label : label + " (try " + attempt + " of 3)");
                    if (File.Exists(dest)) File.Delete(dest);
                    File.Move(dest + ".part", dest);
                    return;
                }
                catch (Exception ex)
                {
                    last = ex;
                    try { File.Delete(dest + ".part"); } catch { }
                    if (attempt < 3) Thread.Sleep(2000 * attempt);
                }
            }
            throw new Exception("the download kept failing (" + last.Message + "). Check your internet and try again.");
        }

        void FetchOnce(string name, string dest, string label)
        {
            string b = FeedBase();
            if (LocalFeed(b)) { File.Copy(Path.Combine(b, name), dest, true); return; }
            var done = new ManualResetEvent(false);
            Exception err = null;
            using (var c = NewClient())
            {
                c.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e) {
                    Ui(delegate {
                        bar.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
                        Status(string.Format("{0}: {1:0.0} / {2:0.0} MB", label, e.BytesReceived / 1048576.0, e.TotalBytesToReceive / 1048576.0));
                    });
                };
                c.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e) { err = e.Error; done.Set(); };
                c.DownloadFileAsync(new Uri(b + name), dest);
                done.WaitOne();
            }
            if (err != null) throw err;
        }

        // Unzip without the top folder (DokiDokiSecondDraft-<version>-...), skipping the macOS / Linux parts.
        static void Unpack(string zip, string dest)
        {
            using (var z = ZipFile.OpenRead(zip))
            {
                foreach (var e in z.Entries)
                {
                    string name = e.FullName.Replace('\\', '/');
                    int slash = name.IndexOf('/');
                    if (slash < 0) continue;
                    string rel = name.Substring(slash + 1);
                    if (rel.Length == 0 || rel.EndsWith("/")) continue;
                    if (rel.StartsWith("DokiDokiSecondDraft.app/") || rel.StartsWith("lib/py3-linux") || rel.StartsWith("lib/py3-mac")
                        || rel.EndsWith(".sh")) continue;
                    string to = Path.Combine(dest, rel.Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(to));
                    e.ExtractToFile(to, true);
                }
            }
        }

        static string Sha256(string path)
        {
            using (var s = File.OpenRead(path))
            using (var h = SHA256.Create())
                return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // Replace the old game files with the new ones. Kept: saves, DDLC's .rpa files, logs.
        static bool Keep(string rel)
        {
            rel = rel.Replace('/', '\\').ToLowerInvariant();
            if (rel.StartsWith("game\\saves\\") || rel == "game\\saves") return true;
            foreach (var r in RPAS) if (rel == "game\\" + r) return true;
            return rel == "log.txt" || rel == "traceback.txt" || rel == "errors.txt";
        }

        static void SwapIn(string src, string dst)
        {
            Directory.CreateDirectory(dst);
            // 1. delete files that the new version doesn't have (except the kept ones)
            foreach (var f in Directory.GetFiles(dst, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(dst.Length + 1);
                if (Keep(rel)) continue;
                if (!File.Exists(Path.Combine(src, rel))) File.Delete(f);
            }
            // 2. copy the new files over
            foreach (var f in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
            {
                string rel = f.Substring(src.Length + 1);
                if (Keep(rel)) continue;
                string to = Path.Combine(dst, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(to));
                File.Copy(f, to, true);
            }
        }

        // ------------------------------------------------------------------ DDLC's files
        // Looks for the three .rpa files in the usual places. Never downloads them: the player brings their own copy.
        void FindDdlcFiles()
        {
            foreach (var cand in DdlcCandidates())
            {
                try { if (TryTakeRpas(cand)) return; } catch { }
            }
        }

        IEnumerable<string> DdlcCandidates()
        {
            var list = new List<string>();
            foreach (var lib in SteamLibraries())
                list.Add(Path.Combine(lib, "steamapps\\common\\Doki Doki Literature Club"));
            string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            foreach (var place in new[] { "Downloads", "Desktop", "Documents" })
            {
                string p = Path.Combine(user, place);
                if (!Directory.Exists(p)) continue;
                try
                {
                    foreach (var d in Directory.GetDirectories(p, "DDLC*")) list.Add(d);
                    foreach (var d in Directory.GetDirectories(p, "Doki Doki*")) list.Add(d);
                    foreach (var z in Directory.GetFiles(p, "ddlc*.zip")) list.Add(z);
                }
                catch { }
            }
            return list;
        }

        static IEnumerable<string> SteamLibraries()
        {
            var libs = new List<string>();
            string steam = null;
            try { steam = Registry.GetValue("HKEY_CURRENT_USER\\Software\\Valve\\Steam", "SteamPath", null) as string; } catch { }
            if (steam == null) steam = "C:\\Program Files (x86)\\Steam";
            steam = steam.Replace('/', '\\');
            libs.Add(steam);
            string vdf = Path.Combine(steam, "steamapps\\libraryfolders.vdf");
            try
            {
                foreach (var line in File.ReadAllLines(vdf))
                {
                    string t = line.Trim();
                    if (!t.StartsWith("\"path\"")) continue;
                    int a = t.IndexOf('"', 6), b = t.LastIndexOf('"');
                    if (a > 0 && b > a) libs.Add(t.Substring(a + 1, b - a - 1).Replace("\\\\", "\\"));
                }
            }
            catch { }
            return libs;
        }

        // A DDLC folder (with game\*.rpa, or the rpas directly) or a ddlc zip.
        bool TryTakeRpas(string path)
        {
            Directory.CreateDirectory(RpaDir);
            if (File.Exists(path) && path.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using (var z = ZipFile.OpenRead(path))
                {
                    var found = new Dictionary<string, ZipArchiveEntry>();
                    foreach (var e in z.Entries)
                        foreach (var r in RPAS)
                            if (e.FullName.Replace('\\', '/').EndsWith("game/" + r, StringComparison.OrdinalIgnoreCase)) found[r] = e;
                    if (found.Count != RPAS.Length) return false;
                    foreach (var kv in found) kv.Value.ExtractToFile(Path.Combine(RpaDir, kv.Key), true);
                    return true;
                }
            }
            if (!Directory.Exists(path)) return false;
            foreach (var sub in new[] { "game", "" })
            {
                string dir = Path.Combine(path, sub);
                bool all = true;
                foreach (var r in RPAS) if (!File.Exists(Path.Combine(dir, r))) all = false;
                if (!all) continue;
                foreach (var r in RPAS) File.Copy(Path.Combine(dir, r), Path.Combine(RpaDir, r), true);
                return true;
            }
            return false;
        }

        // One ordinary file picker for both cases: the DDLC zip, or any file in the DDLC folder (DDLC.exe, an .rpa).
        // (The old "Yes = zip / No = folder" box used Windows' legacy folder dialog, which players said "does nothing":
        // it can open behind the launcher and can't see inside zips. The copy also froze the window; now it runs in the
        // background with the progress bar.)
        void LocateDdlc()
        {
            if (busy) return;
            string pick = Environment.GetEnvironmentVariable("DDSD_LOCATE");   // test hook: skip the dialog
            if (pick == null)
            using (var d = new OpenFileDialog {
                Title = "Find DDLC: pick ddlc-win.zip, or DDLC.exe in your DDLC folder",
                Filter = "DDLC zip or game|*.zip;DDLC.exe;*.rpa;*.exe|All files|*.*",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                CheckFileExists = true })
            {
                Activate();
                if (d.ShowDialog(this) == DialogResult.OK) pick = d.FileName;
            }
            if (pick == null) return;
            string source = pick.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? pick : Path.GetDirectoryName(pick);
            busy = true; Refresh2();
            bar.Visible = true; bar.Marquee = true;
            Status("Copying DDLC's files... (about 200 MB, this takes a moment)");
            new Thread(delegate() {
                bool ok = false; string err = null;
                try
                {
                    ok = TryTakeRpas(source);
                    if (!ok) ok = TryTakeRpas(Path.GetDirectoryName(source) ?? source);   // picked something in DDLC\game\
                    // remembered for the Mods tab, which needs the whole DDLC folder
                    foreach (var full in new[] { source, Path.GetDirectoryName(source) })
                        if (ok && full != null && FullDdlc(full, false)) { File.WriteAllText(DdlcSourceFile, full); break; }
                }
                catch (Exception ex) { err = ex.Message; }
                Ui(delegate {
                    busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                    if (ok) { Status("Found DDLC's files. Ready to play!"); TestShot(); return; }
                    if (err != null) Status("Couldn't copy DDLC's files: " + err);
                    MessageBox.Show(this, "That doesn't look like DDLC: I couldn't find audio.rpa, fonts.rpa and images.rpa in\n" + source +
                                    "\n\nPick ddlc-win.zip (from ddlc.moe), or DDLC.exe inside your Doki Doki Literature Club folder.\n" +
                                    "DDLC Plus doesn't have these files: you need the original (free) DDLC from ddlc.moe or Steam.",
                                    "Not quite", MessageBoxButtons.OK, MessageBoxIcon.Information);
                });
            }) { IsBackground = true }.Start();
        }

        // Test hook: screenshot this window to DDSD_SHOT, then quit.
        void TestShot()
        {
            string path = Environment.GetEnvironmentVariable("DDSD_SHOT");
            if (path == null) return;
            var t = new System.Windows.Forms.Timer { Interval = 700 };
            t.Tick += delegate {
                t.Stop();
                using (var bmp = new Bitmap(Width, Height))
                {
                    DrawToBitmap(bmp, new Rectangle(0, 0, Width, Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Close();
            };
            t.Start();
        }

        // ------------------------------------------------------------------ play
        void Play()
        {
            string exe = Path.Combine(GameDir, GAME_EXE);
            if (!File.Exists(exe)) { Refresh2(); return; }
            try
            {
                Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = GameDir, UseShellExecute = true });
                Close();
            }
            catch (Exception ex) { Status("Couldn't start the game: " + ex.Message); }
        }

        static void Open(string url) { try { Process.Start(url); } catch { } }

        // ------------------------------------------------------------------ launcher self-update
        void MaybeSelfUpdate()
        {
            string lv = Str(latest, "launcher_version"), lexe = Str(latest, "launcher_exe"), lsha = Str(latest, "launcher_sha256");
            if (lv == null || lexe == null || NewerThan(lv, VERSION) == false || busy) return;
            if (MessageBox.Show(this, "There's a new version of the launcher (" + lv + "). Update it now? It takes a second.",
                                "Launcher update", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            busy = true; Refresh2();
            new Thread(delegate() {
                try
                {
                    string me = Application.ExecutablePath;
                    string fresh = me + ".new";
                    Fetch(lexe, fresh, "Downloading the launcher");
                    if (lsha != null && !string.Equals(Sha256(fresh), lsha, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("checksum mismatch");
                    string old = me + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(me, old);              // a running exe can be renamed, not overwritten
                    File.Move(fresh, me);
                    Process.Start(me, "--finish-self-update x");
                    Ui(delegate { Close(); });
                }
                catch (Exception ex)
                {
                    Ui(delegate { busy = false; Refresh2(); Status("Launcher update failed: " + ex.Message); });
                }
            }) { IsBackground = true }.Start();
        }

        static bool NewerThan(string a, string b)
        {
            try { return new Version(a.Split('-')[0]) > new Version(b.Split('-')[0]); } catch { return false; }
        }
    }

    // A pink pill button drawn by hand (rounded, shadow, hover lift).
    public class PillButton : Control
    {
        Color fill, hover, fore; float size; FontFamily fam; bool over, down;
        public PillButton(Control parent, string text, Rectangle r, float size, Color fill, Color hover, Color fore, FontFamily fam)
        {
            Text = text; Bounds = r; this.size = size; this.fill = fill; this.hover = hover; this.fore = fore; this.fam = fam;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
            parent.Controls.Add(this);
        }
        protected override void OnMouseEnter(EventArgs e) { over = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { over = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias; g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            float lift = Enabled && over && !down ? 0 : 2;
            float rad = (Height - 8) / 2f;
            var body = new RectangleF(2, lift, Width - 4, Height - 8);
            using (var sh = new SolidBrush(Color.FromArgb(Enabled ? 45 : 18, 150, 60, 110)))
                g.FillPath(sh, Path(new RectangleF(2, 6, Width - 4, Height - 8), rad));
            Color c = !Enabled ? Color.FromArgb(240, 226, 233) : (over ? hover : fill);
            using (var b = new SolidBrush(c)) g.FillPath(b, Path(body, rad));
            using (var pen = new Pen(Color.FromArgb(Enabled ? 255 : 160, 255, 255, 255), 3)) g.DrawPath(pen, Path(new RectangleF(body.X + 1.5f, body.Y + 1.5f, body.Width - 3, body.Height - 3), rad - 1.5f));
            using (var f = new Font(fam, size * 96f / 72f * Launcher.S, GraphicsUnit.Pixel))   // same scale as the window
            using (var b = new SolidBrush(Enabled ? fore : Color.FromArgb(200, 175, 190)))
            using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                g.DrawString(Text, f, b, body, sf);
        }
        static GraphicsPath Path(RectangleF r, float rad)
        {
            var p = new GraphicsPath(); float d = Math.Max(1, rad * 2);
            p.AddArc(r.X, r.Y, d, d, 90, 180); p.AddArc(r.Right - d, r.Y, d, d, 270, 180); p.CloseFigure();
            return p;
        }
    }

    // A thin pink scrollbar for the "What's new" box (a borderless TextBox can't style its own). Polls the box's scroll
    // position on the launcher's animation tick; drag the thumb or click the track to scroll.
    public class PinkScroll : Control
    {
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);
        const int EM_GETFIRSTVISIBLELINE = 0xCE, EM_GETLINECOUNT = 0xBA, EM_LINESCROLL = 0xB6;
        readonly TextBox box; int first = -1, count = -1; int dragY = -1, dragFirst;
        public PinkScroll(TextBox box)
        {
            this.box = box;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent; Cursor = Cursors.Hand;
        }
        int First { get { return (int)SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero); } }
        int Count { get { return (int)SendMessage(box.Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero); } }
        int Visible { get { return Math.Max(1, box.ClientSize.Height / Math.Max(1, box.Font.Height)); } }
        public void Sync()
        {
            if (!box.IsHandleCreated) return;
            int f = First, c = Count;
            if (f != first || c != count) { first = f; count = c; Invalidate(); }
        }
        RectangleF Thumb()
        {
            int vis = Visible, total = Math.Max(Count, 1);
            if (total <= vis) return RectangleF.Empty;
            float h = Math.Max(Width * 2.5f, Height * vis / (float)total);
            float y = (Height - h) * First / (float)Math.Max(1, total - vis);
            return new RectangleF(0, y, Width, h);
        }
        void ScrollTo(int line) { SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(line - First)); Sync(); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            var t = Thumb(); if (t.IsEmpty) return;
            if (t.Contains(e.Location)) { dragY = e.Y; dragFirst = First; return; }
            ScrollTo(First + (e.Y < t.Y ? -Visible : Visible));   // page up / down
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (dragY < 0) return;
            var t = Thumb(); int total = Count, vis = Visible;
            float perLine = (Height - t.Height) / (float)Math.Max(1, total - vis);
            if (perLine > 0) ScrollTo(dragFirst + (int)Math.Round((e.Y - dragY) / perLine));
        }
        protected override void OnMouseUp(MouseEventArgs e) { dragY = -1; }
        protected override void OnMouseWheel(MouseEventArgs e) { ScrollTo(First - e.Delta / 40); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            var t = Thumb(); if (t.IsEmpty) return;
            float r = Width / 2f;
            using (var b = new SolidBrush(Color.FromArgb(252, 226, 238))) g.FillPath(b, Pill(new RectangleF(0, 0, Width, Height), r));
            using (var b = new SolidBrush(Color.FromArgb(232, 91, 154))) g.FillPath(b, Pill(t, r));
        }
        static GraphicsPath Pill(RectangleF rc, float r)
        {
            var p = new GraphicsPath(); float d = Math.Min(2 * r, Math.Min(rc.Width, rc.Height));
            p.AddArc(rc.X, rc.Y, d, d, 180, 180); p.AddArc(rc.X, rc.Bottom - d, d, d, 0, 180); p.CloseFigure();
            return p;
        }
    }

    // A rounded pink progress bar (with a sliding stripe while the total isn't known).
    public class PinkBar : Control
    {
        int value; bool marquee; float phase;
        System.Windows.Forms.Timer t = new System.Windows.Forms.Timer { Interval = 30 };
        public PinkBar()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            t.Tick += delegate { phase = (phase + 0.02f) % 1.3f; Invalidate(); };
        }
        public int Value { get { return value; } set { this.value = Math.Max(0, Math.Min(100, value)); Invalidate(); } }
        public bool Marquee { get { return marquee; } set { marquee = value; if (value) t.Start(); else t.Stop(); Invalidate(); } }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics; g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = Height / 2f;
            using (var b = new SolidBrush(Color.FromArgb(252, 214, 231))) g.FillPath(b, Pill(0, Width, r));
            float x0 = 0, x1 = Width * value / 100f;
            if (marquee) { x0 = (phase - 0.3f) * Width; x1 = x0 + Width * 0.3f; }
            x0 = Math.Max(0, x0); x1 = Math.Min(Width, x1);
            if (x1 - x0 > 1)
                using (var b = new LinearGradientBrush(new Rectangle(0, 0, Width, Height), Color.FromArgb(240, 120, 175), Color.FromArgb(214, 72, 138), 0f))
                    g.FillPath(b, Pill(x0, x1, r));
        }
        GraphicsPath Pill(float a, float b, float r)
        {
            var p = new GraphicsPath(); float w = Math.Max(b - a, 2 * r);
            p.AddArc(a, 0, 2 * r, Height - 1, 90, 180); p.AddArc(a + w - 2 * r, 0, 2 * r, Height - 1, 270, 180); p.CloseFigure();
            return p;
        }
    }
}
