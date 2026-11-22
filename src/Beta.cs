// DDSD Launcher: beta codes (Aoi: "a beta code testers type in the launcher, so they get beta updates immediately").
// Home > "Beta code": the tester types the code Aoi gave them (tools/beta_code.py makes them). The Mod Hub worker checks
// it (GET <hub>/beta/<code>, tools/modhub/worker/src/beta.js) and answers with the release folder to update from: the
// newest tester build, or the public one once that's newer. The launcher then updates from there right away, and keeps
// the code in beta.json in its home, re-checking it on every start (a code Aoi removed drops the tester back to normal).
// "Leave beta" goes back to the public releases. Test hook: DDSD_BETA = <code> enters it without the dialog.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Net;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace DDSD
{
    public partial class Launcher
    {
        static string betaFeed;          // the feed the hub gave for the saved code (null: public releases)
        bool betaInstallAfterCheck;      // a code was just entered: update as soon as the check is done
        PillButton betaBtn;

        string BetaFile { get { return Path.Combine(home, "beta.json"); } }

        string SavedBetaCode()
        {
            try { return Str(new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(BetaFile)), "code"); }
            catch { return null; }
        }

        void BuildBetaUi()
        {
            betaBtn = new PillButton(this, "Beta code", R(770, 540, 112, 28), 9f, SOFT_BTN, SOFT_BTN_HOVER, PLUM, riffic);
            betaBtn.Click += delegate { BetaClicked(); };
        }

        /// Asks the hub what the saved code unlocks (on the update-check thread). Returns a message for the status line.
        string ResolveBeta()
        {
            betaFeed = null;
            string code = SavedBetaCode();
            if (code == null || Env("DDSD_FEED") != null) return null;
            try
            {
                var d = AskHubForBeta(code);
                if (Str(d, "ok") == "True") { betaFeed = Str(d, "feed"); return null; }
                try { File.Delete(BetaFile); } catch { }
                return "Your beta code was turned off, so you're back on the normal version.";
            }
            catch { return null; }      // offline: just use the public feed this time
        }

        Dictionary<string, object> AskHubForBeta(string code)
        {
            using (var c = NewClient())
            {
                try { return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(c.DownloadString(ModHub() + "/beta/" + Uri.EscapeDataString(code.Trim()))); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r == null) throw;
                    using (var sr = new StreamReader(r.GetResponseStream()))
                        return new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(sr.ReadToEnd());
                }
            }
        }

        void BetaClicked()
        {
            if (busy) { Status("One thing at a time: wait for the current job to finish."); return; }
            if (SavedBetaCode() != null)
            {
                if (Env("DDSD_SHOT") == null && MessageBox.Show(this, "You're on the DDSD beta.\n\nLeave it and go back to the normal version? " +
                        "The launcher will offer the normal version as an update. Your saves stay.", "Leave beta",
                        MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
                try { File.Delete(BetaFile); } catch { }
                betaFeed = null;
                Status("Left the beta. Checking the normal version...");
                CheckForUpdates();
                return;
            }
            string code = Env("DDSD_BETA") ?? AskForCode();
            if (string.IsNullOrEmpty(code)) return;
            Status("Checking your beta code...");
            new Thread(delegate() {
                Dictionary<string, object> d = null; string err = null;
                try { d = AskHubForBeta(code); } catch (Exception ex) { err = "Couldn't reach the Mod Hub (" + ex.Message + "). Are you online?"; }
                Ui(delegate {
                    if (err != null) { Status(err); TestShot(); return; }
                    if (Str(d, "ok") != "True") { Status(Str(d, "error") ?? "That beta code didn't work."); TestShot(); return; }
                    try { File.WriteAllText(BetaFile, new JavaScriptSerializer().Serialize(new Dictionary<string, object> { { "code", code.Trim().ToUpperInvariant() } })); } catch { }
                    betaFeed = Str(d, "feed");
                    Status("Welcome to the beta! Getting " + (Str(d, "version") ?? "the beta build") + "...");
                    betaInstallAfterCheck = true;
                    CheckForUpdates();
                });
            }) { IsBackground = true }.Start();
        }

        // A small pink dialog with one text box.
        string AskForCode()
        {
            using (var f = new Form { Text = "Beta code", FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false,
                                      StartPosition = FormStartPosition.CenterParent, ClientSize = R(0, 0, 360, 150).Size, BackColor = BG, ShowInTaskbar = false })
            {
                var lab = new Label { Text = "Type the beta code Aoi gave you:", Bounds = R(18, 16, 330, 24), ForeColor = INK, Font = new Font(aller, 11f) };
                var box = new TextBox { Bounds = R(18, 46, 324, 28), CharacterCasing = CharacterCasing.Upper, Font = new Font(aller, 12f) };
                var ok = new Button { Text = "Join the beta", Bounds = R(160, 100, 182, 34), DialogResult = DialogResult.OK, BackColor = ROSE, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
                ok.FlatAppearance.BorderSize = 0;
                f.Controls.Add(lab); f.Controls.Add(box); f.Controls.Add(ok);
                f.AcceptButton = ok;
                return f.ShowDialog(this) == DialogResult.OK ? box.Text.Trim() : null;
            }
        }
    }
}
