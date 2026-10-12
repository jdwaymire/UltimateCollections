// Artwork.cs
//
// Makes each collection's poster and backdrop inside the plugin (3x3 mosaic of the
// members' own images, darkened and tinted; posters get the title in Bebas Neue). Three parts: PosterStyle (how a
// collection looks, from its settings), PosterRenderer (draws the JPEG) and ArtworkSync (decides when to make and set
// one, and remembers what was set).
//
// NOTE: SkiaSharp is Emby's own copy (compile-only reference, nothing shipped with the plugin). If Emby ever ships a
// different SkiaSharp, this file is what breaks, which is why Syncer only calls in here inside a try.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SkiaSharp;

namespace UltimateCollections.Core
{
    // ---- style: how a collection's poster looks ----

    // Built from optional collection keys. None of them are in the list fingerprint, so changing the look never
    // costs a list download.
    //   posters no                     no generated poster/backdrop for this collection ([global] posters no: none at all)
    //   poster_title WINTER|TIME       title lines ('|' = new line; default: the name on up to two lines). Always drawn in
    //                                  capitals; characters the font cannot draw (emoji, symbols, other scripts) are left out
    //                                  with a warning, accented letters it lacks lose the accent. ' #' starts a comment.
    //   poster_accent 150,205,255      accent colour (bars + first title lines), r,g,b 0-255
    //   poster_tint 20,45,80           colour the picture is tinted with (default: a dark shade of the accent)
    //   poster_sepia yes               old-photo look (used for the pre-50's and 50's)
    // NOTE: colours are r,g,b on purpose, not #rrggbb: in the settings text '#' starts a comment.
    public sealed class PosterStyle
    {
        // Default accents. A collection without poster_accent gets one picked by a hash of its name, so it keeps the
        // same colour from run to run.
        static readonly SKColor[] Palette =
        {
            new SKColor(240, 50, 50), new SKColor(255, 135, 20), new SKColor(240, 195, 40), new SKColor(140, 220, 120),
            new SKColor(60, 230, 120), new SKColor(120, 220, 210), new SKColor(90, 220, 255), new SKColor(150, 205, 255),
            new SKColor(255, 90, 140), new SKColor(255, 60, 200),
        };

        public string[] Lines { get; set; }
        public SKColor Accent { get; set; }
        public SKColor Tint { get; set; }
        public bool Sepia { get; set; }
        // Characters left out of the title because the font cannot draw them (for the log).
        public string Removed { get; set; } = "";

        public static bool Enabled(CollectionsConfig cfg, CollectionDefinition c) =>
            !IsNo(c.Get("posters") ?? (cfg.Global.TryGetValue("posters", out var g) ? g : "yes"));

        static bool IsNo(string v) => new[] { "no", "false", "off", "0" }.Contains(v.Trim().ToLowerInvariant());

        public static PosterStyle For(CollectionDefinition c)
        {
            var title = c.Get("poster_title");
            var removed = new StringBuilder();
            var lines = Clean(title != null ? title.Split('|') : DefaultLines(c.Name), removed);
            if (lines.Length == 0 && title != null)     // nothing drawable left: fall back to the collection name
                lines = Clean(DefaultLines(c.Name), removed);
            var accent = c.Get("poster_accent") != null ? ParseColor(c.Get("poster_accent"), "poster_accent") : DefaultAccent(c.Name);
            var tint = c.Get("poster_tint") != null
                ? ParseColor(c.Get("poster_tint"), "poster_tint")
                : new SKColor((byte)(accent.Red * 0.25), (byte)(accent.Green * 0.25), (byte)(accent.Blue * 0.25));
            return new PosterStyle
            {
                Lines = lines, Accent = accent, Tint = tint, Sepia = c.Get("poster_sepia") != null && !IsNo(c.Get("poster_sepia")),
                Removed = removed.ToString(),
            };
        }

        // Capitals, single spaces, and only characters Bebas Neue has a glyph for. An accented letter the font lacks is
        // tried without its accent first (an accented E becomes a plain E); anything else it cannot draw is dropped
        // and noted for the log.
        public static string[] Clean(IEnumerable<string> lines, StringBuilder removed)
        {
            var result = new List<string>();
            foreach (var raw in lines)
            {
                var line = Regex.Replace((raw ?? "").ToUpperInvariant(), @"\s+", " ").Trim();
                var sb = new StringBuilder();
                var e = StringInfo.GetTextElementEnumerator(line);
                while (e.MoveNext())
                {
                    var el = (string)e.Current;
                    if (el == " " || PosterRenderer.CanDraw(el))
                    {
                        sb.Append(el);
                        continue;
                    }
                    var plain = new string(el.Normalize(NormalizationForm.FormD)
                        .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark).ToArray());
                    if (plain.Length > 0 && PosterRenderer.CanDraw(plain))
                        sb.Append(plain);
                    else
                        removed.Append(el);
                }
                var cleaned = Regex.Replace(sb.ToString(), " {2,}", " ").Trim();
                if (cleaned.Length > 0)
                    result.Add(cleaned);
            }
            return result.ToArray();
        }

        static string[] DefaultLines(string name)
        {
            // At most two lines, split where the two halves are most even.
            var words = name.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length <= 1)
                return words;
            var best = Enumerable.Range(1, words.Length - 1)
                .OrderBy(k => Math.Max(string.Join(" ", words.Take(k)).Length, string.Join(" ", words.Skip(k)).Length)).First();
            return new[] { string.Join(" ", words.Take(best)), string.Join(" ", words.Skip(best)) };
        }

        static SKColor DefaultAccent(string name)
        {
            using (var sha = SHA1.Create())
                return Palette[sha.ComputeHash(Encoding.UTF8.GetBytes(name))[0] % Palette.Length];
        }

        public static SKColor ParseColor(string v, string key)
        {
            var p = v.Split(',');
            if (p.Length == 3 && p.All(x => byte.TryParse(x.Trim(), out _)))
                return new SKColor(byte.Parse(p[0].Trim()), byte.Parse(p[1].Trim()), byte.Parse(p[2].Trim()));
            throw new ConfigException($"{key} needs r,g,b (0-255), e.g. 150,205,255, got '{v}'");
        }

        // Everything in the style that changes the picture. It goes into the artwork key, so a style change makes the
        // poster and backdrop again.
        public string Key => string.Join("|", Lines) + ";" + Accent + ";" + Tint + ";" + Sepia;
    }

    // ---- renderer: draws the JPEG ----

    // The look, drawn with SkiaSharp: 3x3 mosaic of member images, (sepia), blurred, darkened, tinted, vertical
    // vignette; posters get a title band in Bebas Neue. The numbers below were tuned by eye on real collections.
    public static class PosterRenderer
    {
        public const string Version = "1";      // bump when the look changes: every poster is made again

        sealed class Mode
        {
            public int W, H, Band;
            public float Size, Blur, Bright, TintShare, Vignette, Shadow;
            public bool Title;
        }

        // Poster: 1000x1500, the usual 2:3 poster shape.
        static readonly Mode PosterMode = new Mode
            { W = 1000, H = 1500, Band = 450, Size = 210, Blur = 1.5f, Bright = 0.42f, TintShare = 0.35f, Vignette = 150, Shadow = 3, Title = true };

        // Backdrop: no title and brighter (Emby shows the poster over the right side of the backdrop, so a title there would be hidden).
        static readonly Mode BackdropMode = new Mode
            { W = 1920, H = 1080, Blur = 1, Bright = 0.68f, TintShare = 0.18f, Vignette = 90, Title = false };

        static SKTypeface _font;

        // True if the title font has a glyph for every character of the text (glyph 0 means missing).
        public static bool CanDraw(string text) => Font.GetGlyphs(text).All(g => g != 0);

        // Bebas Neue is embedded in the DLL as a resource (SIL OFL, Fonts/) and loaded once, on first use.
        static SKTypeface Font
        {
            get
            {
                if (_font == null)
                {
                    var asm = typeof(PosterRenderer).Assembly;
                    using (var s = asm.GetManifestResourceStream("UltimateCollections.Fonts.BebasNeue-Regular.ttf"))
                    {
                        var ms = new MemoryStream();
                        s.CopyTo(ms);
                        _font = SKTypeface.FromData(SKData.CreateCopy(ms.ToArray()));
                    }
                }
                return _font;
            }
        }

        // Returns the JPEG bytes. images = the member images to use, best first; with fewer than 9 they are repeated
        // to fill the grid. Images that cannot be decoded are skipped.
        public static byte[] Render(bool backdrop, IReadOnlyList<string> images, PosterStyle style)
        {
            var m = backdrop ? BackdropMode : PosterMode;
            var tiles = new List<SKBitmap>();
            try
            {
                foreach (var path in images.Take(9))
                {
                    var b = SKBitmap.Decode(path);
                    if (b != null)
                        tiles.Add(b);
                }
                if (tiles.Count == 0)
                    throw new InvalidOperationException("no member image could be read");
                var info = new SKImageInfo(m.W, m.H, SKColorType.Rgba8888, SKAlphaType.Premul);

                // 1. mosaic
                SKImage mosaic;
                using (var surface = SKSurface.Create(info))
                {
                    var c = surface.Canvas;
                    c.Clear(SKColors.Black);
                    using (var paint = new SKPaint { FilterQuality = SKFilterQuality.High, IsAntialias = true })
                        for (var i = 0; i < 9; i++)
                        {
                            int x0 = m.W * (i % 3) / 3, x1 = m.W * (i % 3 + 1) / 3, y0 = m.H * (i / 3) / 3, y1 = m.H * (i / 3 + 1) / 3;
                            var bmp = tiles[i % tiles.Count];
                            c.DrawBitmap(bmp, Fit(bmp.Width, bmp.Height, x1 - x0, y1 - y0), new SKRect(x0, y0, x1, y1), paint);
                        }
                    mosaic = surface.Snapshot();
                }

                using (mosaic)
                using (var surface = SKSurface.Create(info))
                {
                    var c = surface.Canvas;
                    c.Clear(SKColors.Black);
                    // 2. sepia + blur + brightness + tint in one pass (all linear per pixel)
                    using (var paint = new SKPaint
                           {
                               ColorFilter = SKColorFilter.CreateColorMatrix(ColorMatrix(style, m)),
                               ImageFilter = SKImageFilter.CreateBlur(m.Blur, m.Blur, SKShaderTileMode.Clamp),
                           })
                        c.DrawImage(mosaic, 0, 0, paint);

                    // 3. vignette: black, opacity vignette/255 * d^2 (d = distance from the middle row, 0..1)
                    var stops = Enumerable.Range(0, 21).Select(k => k / 20f).ToArray();
                    var colors = stops.Select(p =>
                    {
                        var d = Math.Abs(p - 0.5f) * 2;
                        return new SKColor(0, 0, 0, (byte)Math.Round(m.Vignette * d * d));
                    }).ToArray();
                    using (var shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(0, m.H), colors, stops, SKShaderTileMode.Clamp))
                    using (var paint = new SKPaint { Shader = shader })
                        c.DrawRect(0, 0, m.W, m.H, paint);

                    if (m.Title)
                        DrawTitle(c, m, style);

                    using (var img = surface.Snapshot())
                    using (var data = img.Encode(SKEncodedImageFormat.Jpeg, 90))
                        return data.ToArray();
                }
            }
            finally
            {
                foreach (var t in tiles)
                    t.Dispose();
            }
        }

        // Crop rectangle that fills w x h without stretching. Like PIL's ImageOps.fit with centering 0.5 / 0.4:
        // centred left/right, a bit above the middle top/bottom (faces tend to sit there).
        static SKRect Fit(int sw, int sh, int w, int h)
        {
            var target = (float)w / h;
            if ((float)sw / sh > target)
            {
                var cw = sh * target;
                var x = (sw - cw) * 0.5f;
                return new SKRect(x, 0, x + cw, sh);
            }
            var ch = sw / target;
            var y = (sh - ch) * 0.4f;
            return new SKRect(0, y, sw, y + ch);
        }

        // One colour matrix for sepia, brightness and tint: out = bright*(1-t) * [sepia](in) + t*tint. All of it is
        // linear per pixel, so it folds into a single pass.
        // NOTE: Skia's bias column (the 5th) is 0..1, not 0..255.
        static float[] ColorMatrix(PosterStyle s, Mode m)
        {
            float k = m.Bright * (1 - m.TintShare), t = m.TintShare;
            float[] tint = { s.Tint.Red / 255f, s.Tint.Green / 255f, s.Tint.Blue / 255f };
            var rows = new float[20];
            if (s.Sepia)
            {
                // PIL: grayscale (L = .299R + .587G + .114B), then colorize black->(25,18,8), white->(235,215,175)
                float[] dark = { 25 / 255f, 18 / 255f, 8 / 255f }, light = { 235 / 255f, 215 / 255f, 175 / 255f };
                for (var r = 0; r < 3; r++)
                {
                    var span = light[r] - dark[r];
                    rows[r * 5 + 0] = k * span * 0.299f;
                    rows[r * 5 + 1] = k * span * 0.587f;
                    rows[r * 5 + 2] = k * span * 0.114f;
                    rows[r * 5 + 4] = k * dark[r] + t * tint[r];
                }
            }
            else
                for (var r = 0; r < 3; r++)
                {
                    rows[r * 5 + r] = k;
                    rows[r * 5 + 4] = t * tint[r];
                }
            rows[18] = 1;   // alpha unchanged
            return rows;
        }

        // A dark band across the middle with an accent bar top and bottom, then the title lines centred in it: the
        // last line white, the ones above it in the accent colour, each with a small drop shadow.
        static void DrawTitle(SKCanvas c, Mode m, PosterStyle style)
        {
            var top = (m.H - m.Band) / 2f;
            var accentH = Math.Max(3, m.Band / 64);
            using (var band = new SKPaint { Color = new SKColor(0, 0, 0, 175) })
                c.DrawRect(0, top, m.W, m.Band, band);
            using (var bar = new SKPaint { Color = style.Accent })
            {
                c.DrawRect(0, top, m.W, accentH, bar);
                c.DrawRect(0, top + m.Band - accentH, m.W, accentH, bar);
            }

            using (var text = new SKPaint { Typeface = Font, IsAntialias = true, TextSize = m.Size })
            using (var shadow = new SKPaint { Typeface = Font, IsAntialias = true, Color = new SKColor(0, 0, 0, 200) })
            {
                var lines = style.Lines;
                if (lines.Length == 0)
                    return;
                // Shrink until the widest line fits the width and all lines fit the band.
                while ((lines.Max(l => text.MeasureText(l)) > m.W - 130 || text.TextSize * 0.92f * lines.Length > m.Band * 0.9f)
                       && text.TextSize > 20)
                    text.TextSize -= 5;
                var size = text.TextSize;
                shadow.TextSize = size;
                var lineH = size * 0.92f;
                var ascent = -text.FontMetrics.Ascent;      // PIL draws from the ascender line, Skia from the baseline
                var y = top + (m.Band - lineH * lines.Length) / 2 - size * 0.06f;
                for (var i = 0; i < lines.Length; i++)
                {
                    text.Color = i == lines.Length - 1 ? SKColors.White : style.Accent;
                    var w = text.MeasureText(lines[i]);
                    var x = (m.W - w) / 2;
                    c.DrawText(lines[i], x + m.Shadow, y + ascent + m.Shadow, shadow);
                    c.DrawText(lines[i], x, y + ascent, text);
                    y += lineH;
                }
            }
        }
    }

    // ---- sync: when to make and set the artwork ----

    // Where the member images come from and where the result goes.
    public interface IArtworkTarget
    {
        // Local file of an item's poster (backdrop=false) or first backdrop; null if it has none on disk.
        string GetImagePath(long itemId, bool backdrop);

        // Whether the collection currently has a poster / backdrop in Emby.
        bool HasImage(long collectionId, bool backdrop);

        // mimeType: "image/jpeg" for what we draw; a custom image may also be "image/png".
        Task SetImageAsync(long collectionId, bool backdrop, byte[] data, string mimeType, CancellationToken ct);
    }

    // Makes and sets a collection's poster and backdrop when they are new or when what they are made of changed
    // (style, the 9 best-rated members, renderer version). A custom image in images/ (see CustomImages) takes the place
    // of the generated one. In dry run it writes preview files instead.
    public class ArtworkSync
    {
        readonly IArtworkTarget _target;
        readonly string _previewDir;
        readonly ISyncLog _log;

        // "<collection id>" / "<collection id>:backdrop" -> key of what was set.
        public Dictionary<string, string> State { get; }

        // The user's own images; null = none (tests, or no images folder wired up).
        public CustomImages Custom { get; set; }

        public ArtworkSync(IArtworkTarget target, string previewDir, Dictionary<string, string> state, ISyncLog log)
        {
            _target = target;
            _previewDir = previewDir;
            State = state;
            _log = log;
        }

        static string Hash(string s)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(Encoding.UTF8.GetBytes(s)).Take(6).Select(b => b.ToString("x2")));
        }

        // collectionId null = dry run of a collection that does not exist yet. Poster first, then backdrop; each one is
        // only drawn when its key changed or the image is gone, so an unchanged nightly run uploads nothing.
        // generate=false (the collection has "posters no") still sets custom images: someone who uploaded one wants it
        // used, and "posters no" is about the generated look.
        public async Task SyncAsync(CollectionDefinition c, long? collectionId, IEnumerable<LibraryItem> members, bool dryRun,
            CancellationToken ct, bool generate = true)
        {
            PosterStyle style = null;
            var list = members.ToList();
            foreach (var backdrop in new[] { false, true })
            {
                var what = backdrop ? "backdrop" : "poster";
                var stateKey = collectionId + (backdrop ? ":backdrop" : "");

                // Custom image first. A file that is there but unusable is a warning and leaves the collection's
                // current image alone: falling back to a generated one would replace the user's picture just because
                // the file got damaged or swapped for a WebP.
                var custom = Custom?.Matches(c.Name, backdrop) ?? new List<string>();
                if (custom.Count > 0)
                {
                    if (custom.Count > 1)
                        _log.Warn($"[{c.Name}] {custom.Count} custom {what} files match, using {Path.GetFileName(custom[0])}: " +
                                  string.Join(", ", custom.Select(Path.GetFileName)));
                    CustomImageFile file;
                    byte[] data;
                    try
                    {
                        file = CustomImages.Inspect(custom[0], out data);
                    }
                    catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
                    {
                        _log.Warn($"[{c.Name}] custom {what} not used, {what} left as it is: {e.Message}");
                        continue;
                    }
                    // Keyed on the file's content, so replacing the file (same name or not) uploads it again.
                    await SetAsync(c, collectionId, backdrop, Hash("custom;" + what + ";" + Sha1(data)), data, file.MimeType,
                        "custom " + what, $" ({file.FileName}, {file.Width}x{file.Height})", dryRun, ct).ConfigureAwait(false);
                    continue;
                }

                if (!generate)
                    continue;
                if (style == null)
                {
                    style = PosterStyle.For(c);
                    if (style.Removed.Length > 0)
                        _log.Warn($"[{c.Name}] poster title: left out characters the font cannot draw: {style.Removed}");
                }
                var images = BestImages(_target, list, backdrop);
                if (images.Count == 0)
                {
                    _log.Warn($"[{c.Name}] no member has a {what} image, {what} not made");
                    continue;
                }
                var key = Hash($"{PosterRenderer.Version};{what};{style.Key};{string.Join(",", images.Select(i => i.id))}");
                // Unchanged and still there: skip before drawing, drawing is the slow part.
                if (Unchanged(collectionId, stateKey, key, backdrop, dryRun))
                    continue;
                var jpeg = PosterRenderer.Render(backdrop, images.Select(i => i.path).ToList(), style);
                await SetAsync(c, collectionId, backdrop, key, jpeg, "image/jpeg", what, $" ({images.Count} member images)", dryRun, ct)
                    .ConfigureAwait(false);
            }
        }

        // Unchanged and still there: nothing to do. A missing image (e.g. removed by hand) is set again.
        bool Unchanged(long? collectionId, string stateKey, string key, bool backdrop, bool dryRun) =>
            !dryRun && collectionId.HasValue && State.TryGetValue(stateKey, out var old) && old == key
            && _target.HasImage(collectionId.Value, backdrop);

        // Uploads the image (or writes the dry-run preview) unless it is already there, and remembers its key.
        async Task SetAsync(CollectionDefinition c, long? collectionId, bool backdrop, string key, byte[] data, string mimeType,
            string what, string detail, bool dryRun, CancellationToken ct)
        {
            var stateKey = collectionId + (backdrop ? ":backdrop" : "");
            if (Unchanged(collectionId, stateKey, key, backdrop, dryRun))
                return;
            if (dryRun || !collectionId.HasValue)
            {
                var dir = Path.Combine(_previewDir, backdrop ? "backdrops" : "posters");
                Directory.CreateDirectory(dir);
                var file = Path.Combine(dir, ListCache.FileName(c.Name) + (mimeType == "image/png" ? ".png" : ".jpg"));
                File.WriteAllBytes(file, data);
                _log.Info($"[{c.Name}] [dry-run] {what} preview written{detail}: {file}");
                return;
            }
            await _target.SetImageAsync(collectionId.Value, backdrop, data, mimeType, ct).ConfigureAwait(false);
            State[stateKey] = key;
            _log.Info($"[{c.Name}] {what} set{detail}");
        }

        static string Sha1(byte[] data)
        {
            using (var sha = SHA1.Create())
                return string.Concat(sha.ComputeHash(data).Select(b => b.ToString("x2")));
        }

        // The local image files of the 9 best-rated members that have one, best first (ties by name, so the pick is
        // stable). The settings page's poster preview uses this too, so it shows the same movies the sync would.
        public static List<(long id, string path)> BestImages(IArtworkTarget target, IEnumerable<LibraryItem> members, bool backdrop)
        {
            var images = new List<(long id, string path)>();
            foreach (var it in members.OrderByDescending(i => i.Rating ?? 0).ThenBy(i => i.Name, StringComparer.Ordinal))
            {
                var p = target.GetImagePath(it.Id, backdrop);
                if (p != null)
                    images.Add((it.Id, p));
                if (images.Count == 9)
                    break;
            }
            return images;
        }

        // Drops the keys of collections that no longer exist, so the state does not keep ids of deleted collections.
        public void Prune(IEnumerable<long> liveCollectionIds)
        {
            var live = new HashSet<string>(liveCollectionIds.Select(i => i.ToString(CultureInfo.InvariantCulture)));
            foreach (var k in State.Keys.Where(k => !live.Contains(k.Split(':')[0])).ToList())
                State.Remove(k);
        }
    }
}
