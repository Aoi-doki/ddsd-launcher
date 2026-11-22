// DDSD Launcher: soft background music (Aoi: "play sum music on the launcher").
// Monika's Waltz (by Aoi, made for DDSD) loops quietly and fades in. The note button next to minimise turns it off,
// and that choice is remembered (a "music_off" file in the launcher's home). Played through Windows' own MCI
// (winmm), so no extra DLLs. Tests (DDSD_SHOT) and DDSD_MUTE=1 stay silent.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DDSD
{
    public partial class Launcher
    {
        [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
        static extern int mciSendString(string command, StringBuilder ret, int retLen, IntPtr callback);

        const int MUSIC_VOLUME = 220;      // of 1000: background, under the petals
        Rectangle musicRect;
        bool musicOpen;
        int musicFade;
        System.Windows.Forms.Timer musicTimer;

        string MusicOffFile { get { return Path.Combine(home, "music_off"); } }
        bool MusicOn { get { return !File.Exists(MusicOffFile); } }
        static bool Silent { get { return Env("DDSD_SHOT") != null || Env("DDSD_MUTE") != null; } }

        static void Mci(string cmd) { try { mciSendString(cmd, null, 0, IntPtr.Zero); } catch { } }

        void StartMusic()
        {
            if (Silent || !MusicOn || musicOpen) return;
            try
            {
                byte[] mp3 = Resource("monikas_waltz.mp3");
                if (mp3 == null) return;
                string path = Path.Combine(home, "music.mp3");
                if (!File.Exists(path) || new FileInfo(path).Length != mp3.Length)
                {
                    Directory.CreateDirectory(home);
                    File.WriteAllBytes(path, mp3);
                }
                Mci("open \"" + path + "\" type mpegvideo alias ddsd_bgm");
                Mci("setaudio ddsd_bgm volume to 0");
                Mci("play ddsd_bgm repeat");
                musicOpen = true;
                // fade in over about 2 seconds
                musicFade = 0;
                if (musicTimer == null)
                {
                    musicTimer = new System.Windows.Forms.Timer { Interval = 50 };
                    musicTimer.Tick += delegate {
                        musicFade = Math.Min(MUSIC_VOLUME, musicFade + MUSIC_VOLUME / 40 + 1);
                        Mci("setaudio ddsd_bgm volume to " + musicFade);
                        if (musicFade >= MUSIC_VOLUME) musicTimer.Stop();
                    };
                }
                musicTimer.Start();
            }
            catch { }
        }

        void StopMusic()
        {
            if (musicTimer != null) musicTimer.Stop();
            if (!musicOpen) return;
            Mci("stop ddsd_bgm");
            Mci("close ddsd_bgm");
            musicOpen = false;
        }

        void ToggleMusic()
        {
            try
            {
                if (MusicOn) { File.WriteAllText(MusicOffFile, "Launcher music is off. Delete this file (or press the note) to turn it back on.\n"); StopMusic(); }
                else { File.Delete(MusicOffFile); StartMusic(); }
            }
            catch { }
            Invalidate();
        }

        // A little eighth note; crossed out when the music is off.
        void PaintMusicButton(Graphics g, bool hover)
        {
            using (var b = new SolidBrush(hover ? LINE : Color.FromArgb(0, 0, 0, 0))) g.FillEllipse(b, musicRect);
            var st = g.Save();
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float x = musicRect.X, y = musicRect.Y;
            Color ink = MusicOn ? PLUM : SOFT;
            using (var b = new SolidBrush(ink))
            using (var pen = new Pen(ink, 2.2f))
            {
                g.FillEllipse(b, x + 8.5f, y + 17.5f, 7.5f, 6f);                 // note head
                g.DrawLine(pen, x + 15.2f, y + 20f, x + 15.2f, y + 7.5f);        // stem
                using (var flag = new Pen(ink, 2.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawBezier(flag, x + 15.2f, y + 7.5f, x + 18f, y + 10f, x + 22f, y + 11f, x + 20.5f, y + 15.5f);
            }
            if (!MusicOn)
                using (var pen = new Pen(ROSE, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                    g.DrawLine(pen, x + 8f, y + 8f, x + 22f, y + 22f);
            g.Restore(st);
        }
    }
}
