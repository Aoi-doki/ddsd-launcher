// DDSD Launcher, part 3: the DDSD Mod Hub (backend: tools/modhub). Players share their mods here; Aoi reviews each one
// on Discord; accepted mods land in the public Aoi-doki/ddsd-mods catalog.json, which Browse reads.
// C# 5 only (Windows' csc): no $"" strings, no ?. operator.
//
//   Install   hub-hosted catalog entries (they have "download"): fetched to DDSD\download\mod-<id>.zip.part with the
//             PinkBar, checked against the catalog's size and sha256 (refused on a mismatch), then installed exactly like
//             "Add a mod" (StartAddMod), so they show up in My mods.
//   Share     a form: name, author, version, description (<= 500), licence, page, Discord name, the zip (<= 95 MB) or a
//             link to it, and the "I made this / have permission, no DDLC files" checkbox. Multipart POST to
//             MODHUB/submit; the reply's message / error goes in the status line. Until MODHUB is set (it still says
//             CHANGE-ME), Share only says "Mod sharing opens soon!".
//   Report    a short reason, POSTed as JSON {id, reason} to MODHUB/report.
// Testing: DDSD_CATALOG = a local catalog.json; DDSD_MODHUB = the hub's address (http://127.0.0.1:<port> for a mock; with
// either of those set, http://127.0.0.1 downloads are allowed too). DDSD_INSTALL = <catalog id> presses Install (no
// confirm); DDSD_VIEW = share opens the form; DDSD_SHAREFILL = <json file> fills it (name, author, version, description,
// license, page, contact, link, zip, agree); DDSD_SHARESEND = 1 presses Send; DDSD_REPORT = <catalog id> opens Report and
// DDSD_REPORTREASON = <text> fills it in and presses Send. DDSD_GETMOD = <catalog id> presses Get & install; DDSD_DOWNLOADS =
// the folder it watches instead of the real Downloads.

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DDSD
{
    public partial class Launcher
    {
        const string CATALOG_URL = "https://raw.githubusercontent.com/Aoi-doki/ddsd-mods/main/catalog.json";
        const long MAX_UPLOAD = 95L << 20;   // the hub's limit (Cloudflare's free plan)
        // Exactly the hub's list (tools/modhub/worker/src/index.js).
        static readonly string[] LICENSES = { "All rights reserved (free to play)", "CC BY 4.0", "CC BY-NC 4.0", "CC BY-SA 4.0", "MIT", "GPL-3.0", "Other (in the zip)" };
        static readonly Color FIELD = Color.FromArgb(255, 250, 253);

        static string ModHub() { return (Env("DDSD_MODHUB") ?? MODHUB).TrimEnd('/'); }
        static bool HubReady() { return !ModHub().Contains("CHANGE-ME"); }

        // Hub downloads: https only (plain http to 127.0.0.1 while testing with a local catalog / mock hub).
        static string SafeDownload(string u)
        {
            Uri uri;
            if (u == null || !Uri.TryCreate(u, UriKind.Absolute, out uri)) return null;
            if (uri.Scheme == Uri.UriSchemeHttps) return uri.AbsoluteUri;
            bool testing = Env("DDSD_CATALOG") != null || Env("DDSD_MODHUB") != null;
            return testing && uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1" ? uri.AbsoluteUri : null;
        }

        // ------------------------------------------------------------------ the Share / Report forms
        TextBox tbName, tbAuthor, tbVersion, tbDesc, tbPage, tbContact, tbLink, tbReason;
        PillButton pickZipBtn, cancelShareBtn, sendShareBtn, cancelReportBtn, sendReportBtn;
        readonly List<Control> shareControls = new List<Control>(), reportControls = new List<Control>();
        readonly List<KeyValuePair<int, Rectangle>> fieldBoxes = new List<KeyValuePair<int, Rectangle>>();
        string shareLicense, shareZip;
        bool shareAgree;
        Dictionary<string, object> reportEntry;
        ContextMenuStrip licenseMenu;
        static readonly Rectangle licenseRect = new Rectangle(186, 268, 258, 30), agreeRect = new Rectangle(476, 366, 384, 46),
            descCounter = new Rectangle(64, 418, 380, 16), reasonCounter = new Rectangle(64, 326, 792, 16);

        TextBox Box(int v, Rectangle r, bool multi, int max)
        {
            var t = new TextBox { BorderStyle = BorderStyle.None, BackColor = FIELD, ForeColor = INK, Multiline = multi, MaxLength = max,
                                  Font = new Font(aller, 10f * 96f / 72f * S, GraphicsUnit.Pixel), Visible = false };
            if (multi) { t.AcceptsReturn = true; t.Bounds = R(r.X + 10, r.Y + 8, r.Width - 20, r.Height - 16); }
            else t.Bounds = R(r.X + 10, r.Y + 7, r.Width - 20, r.Height - 12);
            Controls.Add(t);
            fieldBoxes.Add(new KeyValuePair<int, Rectangle>(v, r));
            (v == 3 ? shareControls : reportControls).Add(t);
            return t;
        }

        void BuildHubUi()
        {
            tbName = Box(3, new Rectangle(64, 152, 380, 30), false, 60);
            tbAuthor = Box(3, new Rectangle(64, 210, 380, 30), false, 60);
            tbVersion = Box(3, new Rectangle(64, 268, 110, 30), false, 20);
            tbDesc = Box(3, new Rectangle(64, 326, 380, 90), true, 500);
            tbPage = Box(3, new Rectangle(476, 152, 380, 30), false, 200);
            tbContact = Box(3, new Rectangle(476, 210, 380, 30), false, 60);
            tbLink = Box(3, new Rectangle(476, 326, 380, 30), false, 500);
            tbDesc.TextChanged += delegate { Invalidate(R(descCounter.X, descCounter.Y, descCounter.Width, descCounter.Height)); };
            pickZipBtn = new PillButton(this, "Pick the zip...", R(476, 264, 150, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            pickZipBtn.Click += delegate { PickShareZip(); };
            cancelShareBtn = new PillButton(this, "Cancel", R(556, 440, 140, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            cancelShareBtn.Click += delegate { SetView(1); };
            sendShareBtn = new PillButton(this, "Send to Aoi", R(704, 440, 160, 40), 12f, ROSE, ROSE_DARK, Color.White, riffic);
            sendShareBtn.Click += delegate { SendShare(); };
            shareControls.AddRange(new Control[] { pickZipBtn, cancelShareBtn, sendShareBtn });

            tbReason = Box(4, new Rectangle(64, 214, 792, 110), true, 300);
            tbReason.TextChanged += delegate { Invalidate(R(reasonCounter.X, reasonCounter.Y, reasonCounter.Width, reasonCounter.Height)); };
            cancelReportBtn = new PillButton(this, "Cancel", R(556, 440, 140, 40), 11f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            cancelReportBtn.Click += delegate { SetView(2); };
            sendReportBtn = new PillButton(this, "Send report", R(704, 440, 160, 40), 12f, ROSE, ROSE_DARK, Color.White, riffic);
            sendReportBtn.Click += delegate { SendReport(); };
            reportControls.AddRange(new Control[] { cancelReportBtn, sendReportBtn });
            foreach (var c in shareControls) c.Visible = false;
            foreach (var c in reportControls) c.Visible = false;

            licenseMenu = new ContextMenuStrip { ShowImageMargin = false, BackColor = Color.White,
                                                 Font = new Font(aller, 10f * 96f / 72f * S, GraphicsUnit.Pixel) };
            foreach (var l in LICENSES)
            {
                string lic = l;
                var item = new ToolStripMenuItem(lic) { ForeColor = INK, Padding = new Padding(0, Sc(3), 0, Sc(3)) };
                item.Click += delegate { shareLicense = lic; Invalidate(); };
                licenseMenu.Items.Add(item);
            }
        }

        void ShowHubControls(int v)
        {
            foreach (var c in shareControls) c.Visible = v == 3;
            foreach (var c in reportControls) c.Visible = v == 4;
        }

        // Hit areas drawn in PaintHubForm: 7 = licence picker, 8 = the rights checkbox (and its text).
        int ShareHit(Point lp)
        {
            if (licenseRect.Contains(lp)) return 7;
            if (agreeRect.Contains(lp)) return 8;
            return 0;
        }

        void ShareClick(int t)
        {
            if (t == 7) licenseMenu.Show(this, new Point(Sc(licenseRect.X), Sc(licenseRect.Bottom + 2)));
            if (t == 8) { shareAgree = !shareAgree; Invalidate(); }
        }

        void Label(Graphics g, string text, float x, float y)
        {
            using (var f = PF(riffic, 10f)) using (var b = new SolidBrush(PLUM)) g.DrawString(text, f, b, x, y);
        }

        void FieldBox(Graphics g, Rectangle r, bool hot)
        {
            using (var b = new SolidBrush(FIELD)) g.FillPath(b, RoundRect(r, 10));
            using (var pen = new Pen(hot ? ROSE : LINE, 2)) g.DrawPath(pen, RoundRect(r, 10));
        }

        void PaintHubForm(Graphics g)
        {
            foreach (var kv in fieldBoxes) if (kv.Key == view) FieldBox(g, kv.Value, false);
            var right = new StringFormat { Alignment = StringAlignment.Far };
            if (view == 3)
            {
                Label(g, "Mod name", 64, 134); Label(g, "Author", 64, 192); Label(g, "Version", 64, 250); Label(g, "Licence", 186, 250);
                Label(g, "Short description", 64, 308);
                Label(g, "The mod's page (optional)", 476, 134); Label(g, "Your Discord name (optional)", 476, 192);
                Label(g, "The mod's zip (up to 95 MB)", 476, 250); Label(g, "...or a link to the zip (bigger mods)", 476, 308);
                // licence picker
                FieldBox(g, licenseRect, hoverChrome == 7);
                using (var f = PF(aller, 10f)) using (var b = new SolidBrush(shareLicense != null ? INK : SOFT))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString(shareLicense ?? "Pick a licence...", f, b, new RectangleF(licenseRect.X + 10, licenseRect.Y, licenseRect.Width - 34, licenseRect.Height), sf);
                using (var b = new SolidBrush(PLUM))
                    g.FillPolygon(b, new[] { new PointF(licenseRect.Right - 22, licenseRect.Y + 12), new PointF(licenseRect.Right - 10, licenseRect.Y + 12),
                                             new PointF(licenseRect.Right - 16, licenseRect.Y + 19) });
                // the picked zip
                string zipText = shareZip == null ? "No zip picked yet" : Path.GetFileName(shareZip);
                try { if (shareZip != null) zipText += "  (" + FormatSize(new FileInfo(shareZip).Length) + ")"; } catch { }
                using (var f = PF(aller, 9.5f)) using (var b = new SolidBrush(shareZip != null ? INK : SOFT))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap) { LineAlignment = StringAlignment.Center, Trimming = StringTrimming.EllipsisPath })
                    g.DrawString(zipText, f, b, new RectangleF(636, 266, 222, 32), sf);
                // the rights checkbox
                var box = new RectangleF(agreeRect.X, agreeRect.Y + 6, 22, 22);
                using (var b = new SolidBrush(shareAgree ? ROSE : FIELD)) g.FillPath(b, RoundRect(box, 6));
                using (var pen = new Pen(shareAgree ? ROSE_DARK : (hoverChrome == 8 ? ROSE : LINE), 2)) g.DrawPath(pen, RoundRect(box, 6));
                if (shareAgree)
                    using (var pen = new Pen(Color.White, 3) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        g.DrawLines(pen, new[] { new PointF(box.X + 5, box.Y + 11), new PointF(box.X + 9.5f, box.Y + 16), new PointF(box.X + 17, box.Y + 6) });
                using (var f = PF(aller, 9.5f)) using (var b = new SolidBrush(INK))
                    g.DrawString("I made this mod or have the author's permission, and it contains no DDLC files.", f, b,
                                 new RectangleF(agreeRect.X + 32, agreeRect.Y + 3, agreeRect.Width - 32, agreeRect.Height));
                using (var f = PF(aller, 8.5f)) using (var b = new SolidBrush(SOFT))
                {
                    g.DrawString(tbDesc.TextLength + " / 500", f, b, descCounter, right);
                    g.DrawString("Aoi checks every mod (and a virus scan runs) before it shows up in Browse.", f, b, new RectangleF(64, 446, 470, 34));
                }
            }
            else if (view == 4 && reportEntry != null)
            {
                using (var f = PF(riffic, 13f)) using (var b = new SolidBrush(ROSE))
                using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter })
                    g.DrawString("Reporting: " + Str(reportEntry, "name") + (Str(reportEntry, "author") != null ? " by " + Str(reportEntry, "author") : ""),
                                 f, b, new RectangleF(64, 140, 792, 26), sf);
                using (var f = PF(aller, 10f)) using (var b = new SolidBrush(INK))
                    g.DrawString("What's wrong with it? For example: it's broken, it has DDLC's own files, it's someone else's work, or it isn't " +
                                 "what it says. Aoi gets this on Discord.", f, b, new RectangleF(64, 170, 792, 40));
                using (var f = PF(aller, 8.5f)) using (var b = new SolidBrush(SOFT))
                    g.DrawString(tbReason.TextLength + " / 300", f, b, reasonCounter, right);
            }
            right.Dispose();
        }

        // ------------------------------------------------------------------ sharing a mod the player installed
        string shareTarget;                         // the My mods folder whose zip is being shared (null: the plain form)
        HashSet<string> shareChecked = new HashSet<string>();

        void ShareInstalled(ModInfo m)
        {
            if (!HubReady()) { Status("Mod sharing opens soon!"); return; }
            OpenShare();
            tbName.Text = m.Name;
            shareZip = m.ZipPath != null && File.Exists(m.ZipPath) ? m.ZipPath : null;
            shareTarget = m.Dir;
            Status(shareZip != null ? "Fill in the rest and press Send. Only share mods you made or have the maker's permission for."
                                    : "Pick the mod's zip again (it moved), fill in the rest and press Send.");
            Invalidate();
        }

        static void UpdateModInfo(string dir, Dictionary<string, object> changes)
        {
            string fn = Path.Combine(dir, MOD_INFO);
            var ser = new JavaScriptSerializer();
            var j = ser.Deserialize<Dictionary<string, object>>(File.ReadAllText(fn));
            foreach (var kv in changes) j[kv.Key] = kv.Value;
            File.WriteAllText(fn, ser.Serialize(j));
        }

        // Asks the hub once per session how each shared mod is doing: pending, public (accepted) or gone (rejected).
        void CheckShareStates(List<ModInfo> mods)
        {
            if (!HubReady()) return;
            foreach (var m in mods)
            {
                if (m.ShareId == null || m.ShareState == "public" || m.ShareState == "gone" || shareChecked.Contains(m.ShareId)) continue;
                shareChecked.Add(m.ShareId);
                var mm = m;
                new Thread(delegate() {
                    try
                    {
                        string body;
                        using (var c = NewClient()) body = c.DownloadString(ModHub() + "/status/" + Uri.EscapeDataString(mm.ShareId));
                        string state = Str(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body), "state");
                        if (state == null || state == mm.ShareState) return;
                        var ch = new Dictionary<string, object> { { "share_state", state } };
                        if (state == "public") ch["hub_id"] = mm.ShareId;      // now it's a Mod Hub mod (updates, reports)
                        UpdateModInfo(mm.Dir, ch);
                        Ui(delegate { if (view == 1) ShowInstalled(true); });
                    }
                    catch { }
                }) { IsBackground = true }.Start();
            }
        }

        void OpenShare()
        {
            if (!HubReady()) { Status("Mod sharing opens soon!"); return; }
            shareTarget = null;          // ShareInstalled sets it again right after
            SetView(3);
            Status("Fill this in and press Send. Aoi reviews every mod before it's public.");
        }

        void PickShareZip()
        {
            using (var d = new OpenFileDialog {
                Title = "Share a mod: pick the mod's .zip",
                Filter = "Mod zip|*.zip",
                InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                CheckFileExists = true })
            {
                Activate();
                if (d.ShowDialog(this) == DialogResult.OK) { shareZip = d.FileName; Invalidate(); }
            }
        }

        void FillShare(string jsonPath)
        {
            var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(jsonPath));
            tbName.Text = Str(d, "name") ?? ""; tbAuthor.Text = Str(d, "author") ?? ""; tbVersion.Text = Str(d, "version") ?? "";
            tbDesc.Text = Str(d, "description") ?? ""; tbPage.Text = Str(d, "page") ?? ""; tbContact.Text = Str(d, "contact") ?? "";
            tbLink.Text = Str(d, "link") ?? "";
            shareLicense = Str(d, "license"); shareZip = Str(d, "zip");
            shareAgree = Str(d, "agree") == "True" || Str(d, "agree") == "yes";
            Invalidate();
        }

        void Problem(string msg) { Status(msg); TestShot(); }

        void SendShare()
        {
            if (busy) { Problem("One thing at a time: wait for the current job to finish."); return; }
            if (!HubReady()) { Problem("Mod sharing opens soon!"); return; }
            var fields = new Dictionary<string, string> {
                { "name", tbName.Text.Trim() }, { "author", tbAuthor.Text.Trim() }, { "version", tbVersion.Text.Trim() },
                { "description", tbDesc.Text.Trim() }, { "license", shareLicense ?? "" }, { "page", tbPage.Text.Trim() },
                { "contact", tbContact.Text.Trim() }, { "agree", "yes" } };
            var missing = new List<string>();
            if (fields["name"].Length == 0) missing.Add("the mod's name");
            if (fields["author"].Length == 0) missing.Add("the author");
            if (fields["description"].Length == 0) missing.Add("a short description");
            if (missing.Count > 0) { Problem("Please fill in " + string.Join(", ", missing.ToArray()) + "."); return; }
            if (shareLicense == null) { Problem("Please pick a licence."); return; }
            if (fields["page"].Length > 0 && SafeUrl(fields["page"]) == null) { Problem("The page link must start with http:// or https://."); return; }
            string zip = shareZip, link = tbLink.Text.Trim();
            if (zip == null && link.Length == 0) { Problem("Pick the mod's zip, or paste a link to it."); return; }
            if (zip == null && SafeUrl(link) == null) { Problem("The zip link must start with http:// or https://."); return; }
            if (zip != null)
            {
                if (!File.Exists(zip)) { Problem("Couldn't find " + Path.GetFileName(zip) + " any more. Pick it again."); return; }
                if (new FileInfo(zip).Length > MAX_UPLOAD)
                { Problem("That zip is over 95 MB. Upload it somewhere (Google Drive, MEGA, GitHub...) and paste the link instead."); return; }
            }
            else fields["link"] = link;
            if (!shareAgree) { Problem("Please tick the box: you made this mod (or have permission) and it has no DDLC files."); return; }

            busy = true; Refresh2();
            bar.Visible = true; bar.Value = 0; bar.Marquee = zip == null;
            Status(zip != null ? "Checking the zip..." : "Sending...");
            new Thread(delegate() {
                Dictionary<string, object> reply = null; string err = null;
                try
                {
                    if (zip != null)
                    {
                        PlanMod(zip);   // a zip that isn't a mod would only bounce
                        string ddlc = DdlcFileIn(zip);
                        if (ddlc != null) throw new Exception("the zip has DDLC's own " + ddlc + ". Take DDLC's files out (players bring their own copy).");
                        Ui(delegate { Status("Uploading " + Path.GetFileName(zip) + "..."); });
                    }
                    int shown = -1;
                    reply = PostShare(fields, zip, delegate(int pct) {
                        if (pct != shown) { shown = pct; Ui(delegate { bar.Value = pct; if (pct >= 100) { bar.Marquee = true; Status("Checking it on the hub..."); } }); }
                    });
                }
                catch (Exception ex) { err = ex is WebException ? "Couldn't reach the Mod Hub (" + ex.Message + "). Are you online?" : "Couldn't share it: " + ex.Message; }
                Ui(delegate {
                    busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                    if (err == null && HubOk(reply))
                    {
                        string sid = Str(reply, "id");
                        if (shareTarget != null && sid != null)
                            try { UpdateModInfo(shareTarget, new Dictionary<string, object> { { "share_id", sid }, { "share_state", "pending" } }); } catch { }
                        shareTarget = null;
                        foreach (var t in new[] { tbName, tbAuthor, tbVersion, tbDesc, tbPage, tbContact, tbLink }) t.Text = "";
                        shareZip = null; shareLicense = null; shareAgree = false;
                        SetView(1);
                    }
                    Status(err ?? HubText(reply));
                    TestShot();
                });
            }) { IsBackground = true }.Start();
        }

        // DDLC's own files must never be shared (the hub refuses them too).
        static string DdlcFileIn(string zip)
        {
            using (var z = System.IO.Compression.ZipFile.OpenRead(zip))
                foreach (var e in z.Entries)
                {
                    string f = Path.GetFileName(e.FullName.Replace('\\', '/')).ToLowerInvariant();
                    if (f == "audio.rpa" || f == "fonts.rpa" || f == "images.rpa" || f == "scripts.rpa" || f == "ddlc.exe") return f;
                }
            return null;
        }

        Dictionary<string, object> PostShare(Dictionary<string, string> fields, string zip, Action<int> pct)
        {
            string boundary = "----DDSDLauncher" + Guid.NewGuid().ToString("N");
            var head = new StringBuilder();
            foreach (var kv in fields)
                head.Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"" + kv.Key + "\"\r\n\r\n" + kv.Value + "\r\n");
            if (zip != null)
                head.Append("--" + boundary + "\r\nContent-Disposition: form-data; name=\"file\"; filename=\"" +
                            Path.GetFileName(zip).Replace("\"", "_").Replace("\r", "").Replace("\n", "") + "\"\r\nContent-Type: application/zip\r\n\r\n");
            byte[] headBytes = Encoding.UTF8.GetBytes(head.ToString());
            byte[] tailBytes = Encoding.UTF8.GetBytes((zip != null ? "\r\n" : "") + "--" + boundary + "--\r\n");
            long fileLen = zip != null ? new FileInfo(zip).Length : 0;
            return HubRequest("/submit", "multipart/form-data; boundary=" + boundary, headBytes.Length + fileLen + tailBytes.Length, delegate(Stream s) {
                s.Write(headBytes, 0, headBytes.Length);
                if (zip != null)
                    using (var f = File.OpenRead(zip))
                    {
                        var buf = new byte[1 << 18]; int n; long sent = 0;
                        while ((n = f.Read(buf, 0, buf.Length)) > 0) { s.Write(buf, 0, n); sent += n; pct((int)(sent * 100 / Math.Max(1, fileLen))); }
                    }
                s.Write(tailBytes, 0, tailBytes.Length);
            });
        }

        // POST to the hub. Its answers are always JSON {ok, message} or {ok:false, error}, also on 4xx/5xx.
        static Dictionary<string, object> HubRequest(string path, string type, long length, Action<Stream> write)
        {
            var req = (HttpWebRequest)WebRequest.Create(ModHub() + path);
            req.Method = "POST"; req.ContentType = type; req.ContentLength = length; req.AllowWriteStreamBuffering = false;
            req.UserAgent = "DDSD-Launcher/" + VERSION; req.Timeout = 10 * 60 * 1000; req.ReadWriteTimeout = 5 * 60 * 1000;
            using (var s = req.GetRequestStream()) write(s);
            HttpWebResponse resp;
            try { resp = (HttpWebResponse)req.GetResponse(); }
            catch (WebException ex) { resp = ex.Response as HttpWebResponse; if (resp == null) throw; }
            string body; int code;
            using (resp)
            {
                code = (int)resp.StatusCode;
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) body = r.ReadToEnd();
            }
            try
            {
                var d = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(body);
                if (d != null && (d.ContainsKey("message") || d.ContainsKey("error"))) return d;
            }
            catch { }
            return new Dictionary<string, object> { { "ok", false }, { "error", "The Mod Hub answered strangely (HTTP " + code + "). Try again later." } };
        }

        static bool HubOk(Dictionary<string, object> r) { object v; return r != null && r.TryGetValue("ok", out v) && v is bool && (bool)v; }
        static string HubText(Dictionary<string, object> r) { return Str(r, HubOk(r) ? "message" : "error") ?? (HubOk(r) ? "Done!" : "Something went wrong."); }

        // ------------------------------------------------------------------ report
        void OpenReport(Dictionary<string, object> entry)
        {
            if (!HubReady()) { Status("Reports open soon!"); return; }
            reportEntry = entry;
            tbReason.Text = "";
            SetView(4);
            Status("Keep it short and kind. Thank you for looking out for everyone!");
        }

        void SendReport()
        {
            if (busy || reportEntry == null) return;
            string reason = tbReason.Text.Trim(), id = Str(reportEntry, "id"), name = Str(reportEntry, "name");
            if (reason.Length == 0) { Problem("Write a short reason first."); return; }
            busy = true; Refresh2();
            bar.Visible = true; bar.Marquee = true;
            Status("Sending the report...");
            new Thread(delegate() {
                Dictionary<string, object> reply = null; string err = null;
                try
                {
                    byte[] json = Encoding.UTF8.GetBytes(new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "id", id }, { "reason", reason } }));
                    reply = HubRequest("/report", "application/json; charset=utf-8", json.Length, delegate(Stream s) { s.Write(json, 0, json.Length); });
                }
                catch (Exception ex) { err = "Couldn't reach the Mod Hub (" + ex.Message + "). Are you online?"; }
                Ui(delegate {
                    busy = false; bar.Visible = false; bar.Marquee = false; Refresh2();
                    if (err == null && HubOk(reply)) SetView(2);
                    Status(err ?? HubText(reply));
                    TestShot();
                });
            }) { IsBackground = true }.Start();
        }

        // ------------------------------------------------------------------ install from the hub
        // ------------------------------------------------------------------ Get & install (mods we don't host)
        // Opens the mod's own page, then watches Downloads for a new .zip (Aoi: "make the hub create new instances of DDLC
        // and install the mods to it"). The first zip that appears after the click, once its size stops changing, is
        // installed like Add a mod, into a fresh copy of the player's DDLC. Browsers write .crdownload / .part files
        // first, so those are skipped. Gives up after 30 minutes; pressing Cancel on the card stops it.
        Dictionary<string, object> watchEntry;
        System.Windows.Forms.Timer watchTimer;
        DateTime watchSince;
        string watchFile; long watchSize = -1; int watchStable;

        static string DownloadsDir { get { return Env("DDSD_DOWNLOADS") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"); } }

        void WatchForModZip(Dictionary<string, object> entry, string page)
        {
            if (busy) { Problem("One thing at a time: wait for the current job to finish."); return; }
            string name = CleanName(Str(entry, "name"));
            string dest = Path.Combine(ModsDir, name);
            if (Directory.Exists(dest) && ModRunning(dest)) { Problem("Close " + name + " first, then try again."); return; }
            watchEntry = entry; watchSince = DateTime.Now.AddSeconds(-2); watchFile = null; watchSize = -1; watchStable = 0;
            if (watchTimer == null)
            {
                watchTimer = new System.Windows.Forms.Timer { Interval = 1500 };
                watchTimer.Tick += delegate { WatchTick(); };
            }
            watchTimer.Start();
            Status("Download " + name + "'s zip on its page. I'll install it as soon as it's in your Downloads folder.");
            if (view == 2) ShowCatalog(true);
            if (Env("DDSD_SHOT") == null) Open(page);
        }

        void StopWatching(string why)
        {
            if (watchTimer != null) watchTimer.Stop();
            watchEntry = null;
            if (why != null) Status(why);
            if (view == 2) ShowCatalog(true);
        }

        void WatchTick()
        {
            if (watchEntry == null) return;
            if ((DateTime.Now - watchSince).TotalMinutes > 30) { StopWatching("Stopped waiting for the download. Press Get & install to try again."); return; }
            string name = CleanName(Str(watchEntry, "name"));
            try
            {
                FileInfo newest = null;
                foreach (var f in new DirectoryInfo(DownloadsDir).GetFiles())
                {
                    if (f.LastWriteTime < watchSince) continue;
                    string ext = f.Extension.ToLowerInvariant();
                    if (ext == ".rar" || ext == ".7z")
                    {
                        StopWatching(f.Name + " is a " + ext + " file: unpack it, zip the mod folder, then use Add a mod.");
                        return;
                    }
                    if (ext != ".zip") continue;
                    if (newest == null || f.LastWriteTime > newest.LastWriteTime) newest = f;
                }
                if (newest == null) return;
                // wait until the browser has finished writing it (same size twice in a row, and we can open it)
                if (newest.FullName != watchFile || newest.Length != watchSize) { watchFile = newest.FullName; watchSize = newest.Length; watchStable = 0; return; }
                if (++watchStable < 2) return;
                using (File.Open(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.Read)) { }
                string zip = newest.FullName;
                StopWatching(null);
                Status("Found " + newest.Name + ": installing " + name + "...");
                StartAddMod(delegate { return zip; }, name, null, null);
            }
            catch (IOException) { }      // still being written
            catch (Exception ex) { StopWatching("Couldn't watch Downloads: " + ex.Message); }
        }

        void InstallHosted(Dictionary<string, object> d, bool noConfirm)
        {
            if (busy) { Problem("One thing at a time: wait for the current job to finish."); return; }
            string url = SafeDownload(Str(d, "download"));
            if (url == null) { Problem("That mod's download link doesn't look right, so I didn't use it."); return; }
            string name = CleanName(Str(d, "name"));
            string dest = Path.Combine(ModsDir, name);
            if (Directory.Exists(dest))
            {
                if (ModRunning(dest)) { Problem("Close " + name + " first, then try again."); return; }
                if (!noConfirm && MessageBox.Show(this, "\"" + name + "\" is already installed. Replace it with the version from the Mod Hub?\n\n" +
                        "Its saves stay (they live in %APPDATA%\\RenPy).", "Replace mod", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            }
            string sha = Str(d, "sha256");
            long size; bool hasSize = long.TryParse(Str(d, "size") ?? "", out size) && size > 0;
            string tmp = Path.Combine(home, "download", "mod-" + CleanName(Str(d, "id") ?? name).Replace(' ', '-') + ".zip");
            StartAddMod(delegate {
                Directory.CreateDirectory(Path.GetDirectoryName(tmp));
                string part = tmp + ".part";
                try
                {
                    DownloadUrl(url, part, "Downloading " + name, hasSize ? size : -1);
                    long got = new FileInfo(part).Length;
                    if (hasSize && got != size)
                        throw new Exception("the download is " + FormatBytes(got) + " but the catalog says " + FormatBytes(size) + ", so it wasn't installed. Try again later.");
                    Ui(delegate { Status("Checking " + name + "..."); bar.Marquee = true; });
                    if (sha != null && !string.Equals(Sha256(part), sha, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("the download doesn't match the catalog's checksum, so it wasn't installed.");
                    if (File.Exists(tmp)) File.Delete(tmp);
                    File.Move(part, tmp);
                    return tmp;
                }
                finally { try { if (File.Exists(part)) File.Delete(part); } catch { } }
            }, name, null, d);
        }

        static string FormatBytes(long b) { return b < 1 << 20 ? b.ToString("N0") + " bytes" : FormatSize(b); }

        // Like FetchOnce, for any URL. sizeHint fills the bar when the server doesn't say how big the file is.
        void DownloadUrl(string url, string dest, string label, long sizeHint)
        {
            var done = new ManualResetEvent(false);
            Exception err = null;
            Ui(delegate { bar.Marquee = false; bar.Value = 0; Status(label + "..."); });
            using (var c = NewClient())
            {
                int shown = -1;
                c.DownloadProgressChanged += delegate(object s, DownloadProgressChangedEventArgs e) {
                    long total = e.TotalBytesToReceive > 0 ? e.TotalBytesToReceive : sizeHint;
                    int pct = total > 0 ? (int)Math.Min(100, e.BytesReceived * 100 / total) : 0;
                    if (pct == shown) return;
                    shown = pct;
                    Ui(delegate {
                        bar.Value = pct;
                        Status(total > 0 ? string.Format("{0}: {1:0.0} / {2:0.0} MB", label, e.BytesReceived / 1048576.0, total / 1048576.0)
                                         : string.Format("{0}: {1:0.0} MB", label, e.BytesReceived / 1048576.0));
                    });
                };
                c.DownloadFileCompleted += delegate(object s, System.ComponentModel.AsyncCompletedEventArgs e) { err = e.Error; done.Set(); };
                c.DownloadFileAsync(new Uri(url), dest);
                done.WaitOne();
            }
            if (err != null) throw new Exception("the download failed (" + err.Message + "). Check your internet and try again.");
        }

        // ------------------------------------------------------------------ test hooks
        bool HubTestHooks()
        {
            string inst = Env("DDSD_INSTALL"), rep = Env("DDSD_REPORT"), getmod = Env("DDSD_GETMOD");
            if (getmod != null)
            {
                SetView(2);
                WhenCatalog(delegate {
                    foreach (var d in catalog)
                        if (Str(d, "id") == getmod) { WatchForModZip(d, SafeUrl(Str(d, "page"))); return; }
                    Problem("No catalog entry " + getmod);
                });
                return true;
            }
            if (inst != null || rep != null)
            {
                SetView(2);
                WhenCatalog(delegate {
                    foreach (var d in catalog)
                        if (Str(d, "id") == (inst ?? rep))
                        {
                            if (inst != null) { InstallHosted(d, true); return; }
                            OpenReport(d);
                            string reason = Env("DDSD_REPORTREASON");
                            if (reason != null) { tbReason.Text = reason; SendReport(); } else TestShot();
                            return;
                        }
                    Problem("Test: no catalog mod with id " + (inst ?? rep));
                });
                return true;
            }
            if (Env("DDSD_VIEW") == "share")
            {
                SetView(1);
                OpenShare();
                string fill = Env("DDSD_SHAREFILL");
                if (fill != null && view == 3) FillShare(fill);
                if (Env("DDSD_SHARESEND") != null && view == 3) SendShare(); else TestShot();
                return true;
            }
            return false;
        }
    }
}
