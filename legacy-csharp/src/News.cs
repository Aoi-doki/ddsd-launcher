// DDSD Launcher: the News tab (Aoi picked "Launcher news + changelog").
// Reads news.json from the website (website/news.json, made by tools/make_news.py from the changelog and the Discord
// devlog / sneak peek posts) and shows it as cards, newest first: updates, devlogs and sneak peeks, each with an
// optional picture and a "Read more" link. Pictures are relative to the website (assets/news/...).
// Testing: DDSD_NEWS = a local news.json (its pictures are read relative to that file); DDSD_VIEW = news screenshots it.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Web.Script.Serialization;

namespace DDSD
{
    public partial class Launcher
    {
        const string NEWS_URL = "https://aoi-doki.github.io/ddsd/news.json";
        List<Dictionary<string, object>> news;
        string newsError;
        bool newsLoading;
        Action afterNews;
        Dictionary<string, Image> newsImages = new Dictionary<string, Image>();

        void WhenNews(Action a)
        {
            if (news != null || newsError != null) a();
            else { afterNews = a; if (!newsLoading) LoadNews(); }
        }

        void ShowNews()
        {
            var items = new List<ModCard>();
            if (news == null)
            {
                items.Add(new ModCard { Note = true, Body = newsError ?? "Loading the news..." });
                if (newsError == null && !newsLoading) LoadNews();
                cards.SetItems(items, false);
                return;
            }
            if (news.Count == 0) items.Add(new ModCard { Note = true, Body = "No news yet. Check back soon!" });
            foreach (var d in news)
            {
                string kind = Str(d, "kind") ?? "news", link = SafeUrl(Str(d, "link")), img = Str(d, "image");
                string tag = kind == "update" ? "Update" : kind == "devlog" ? "Devlog" : kind == "sneak" ? "Sneak peek" : "News";
                Image pic = null;
                if (img != null) newsImages.TryGetValue(img, out pic);
                string l = link;
                items.Add(new ModCard {
                    Title = Str(d, "title") ?? tag,
                    Meta = tag + (Str(d, "date") != null ? "   ·   " + Str(d, "date") : ""),
                    Body = (Str(d, "body") ?? "").Replace("\r\n", "\n").Replace("\n", "  "),
                    H = 150, ImgW = pic != null ? 224 : 0, Img = pic,
                    Buttons = l != null ? new[] { "Read more" } : new string[0],
                    Click = delegate(int b) { if (l != null) Open(l); } });
            }
            cards.SetItems(items, true);
        }

        void LoadNews()
        {
            newsLoading = true;
            new Thread(delegate() {
                string local = Env("DDSD_NEWS");
                List<Dictionary<string, object>> list = null; string err = null;
                try
                {
                    string text = local != null ? File.ReadAllText(local) : NewClient().DownloadString(NEWS_URL + "?t=" + DateTime.UtcNow.Ticks);
                    list = new List<Dictionary<string, object>>();
                    var root = new JavaScriptSerializer().DeserializeObject(text) as Dictionary<string, object>;
                    var arr = root != null && root.ContainsKey("items") ? root["items"] as object[] : null;
                    if (arr != null)
                        foreach (var o in arr) { var d = o as Dictionary<string, object>; if (d != null && Str(d, "title") != null) list.Add(d); }
                }
                catch (Exception ex) { err = "Couldn't load the news (are you online?). " + ex.Message; list = null; }
                Ui(delegate {
                    news = list; newsError = err; newsLoading = false;
                    if (view == 5) ShowNews();
                    if (list != null) return;          // with news: run afterNews once the pictures are in (below)
                    var a = afterNews; afterNews = null;
                    if (a != null) a();
                });
                if (list == null) return;
                // Pictures after the cards are up: relative to the website (or to the local test file), 3 MB at most.
                string baseUrl = NEWS_URL.Substring(0, NEWS_URL.LastIndexOf('/') + 1);
                foreach (var d in list)
                {
                    string img = Str(d, "image");
                    if (img == null || img.Contains("..") || SafeUrl(img) != null) continue;   // our own pictures only
                    try
                    {
                        byte[] data = local != null ? File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(local)), img))
                                                    : NewClient().DownloadData(baseUrl + img);
                        if (data.Length > 3 << 20) continue;
                        var image = Image.FromStream(new MemoryStream(data));
                        string key = img;
                        Ui(delegate { newsImages[key] = image; if (view == 5) ShowNews(); });
                    }
                    catch { }
                }
                Ui(delegate { var a = afterNews; afterNews = null; if (a != null) a(); });   // test shot once pictures are in
            }) { IsBackground = true }.Start();
        }
    }
}
