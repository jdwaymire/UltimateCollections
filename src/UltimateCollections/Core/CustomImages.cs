// CustomImages.cs
//
// The user's own posters and backdrops: image files in the plugin's images/ folder that replace the generated
// artwork of a collection. "Halloween.jpg" is Halloween's poster, "Halloween backdrop.jpg" its backdrop. The settings
// page's "Custom images" popup uploads and removes them through CustomImagesApi; copying files in by hand works too.
//
// NOTE: names are matched loosely (case and everything but letters and digits ignored), so "Valentines Day.png",
// "valentine's_day.jpg" and "VALENTINES-DAY.jpg" all belong to [collection Valentine's Day]. People name files by
// hand, and Windows does not even allow some characters a collection name may have (: / ?).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SkiaSharp;

namespace UltimateCollections.Core
{
    // What we know about one image file, for the sync and for the popup.
    public sealed class CustomImageFile
    {
        public string Path { get; set; }
        public string FileName { get; set; }
        public string MimeType { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }
    }

    public class CustomImages
    {
        // Big enough for any sensible poster or backdrop, small enough that a mistake (a 60 MB photo) is refused.
        public const long MaxBytes = 10 * 1024 * 1024;

        // Smaller than this is not worth showing on a TV; most likely the wrong file.
        public const int MinSide = 100;

        // NOTE: JPEG and PNG only, on purpose. Emby's SkiaSharp (2.88.3) has the libwebp hole CVE-2023-4863, so we
        // never decode a WebP someone uploads. Emby shows JPEG and PNG on every client anyway.
        static readonly string[] Extensions = { ".jpg", ".jpeg", ".png" };

        const string BackdropSuffix = "backdrop";

        public string Dir { get; }

        public CustomImages(string dir) => Dir = dir;

        // ---- matching ----

        // Lower case, letters and digits only. "Top 80's" -> "top80s".
        public static string Normalize(string name) =>
            new string((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        // Every image file in the folder that belongs to this collection's poster (or backdrop), sorted by file name so
        // the pick is the same on every run. More than one is allowed (the sync uses the first and warns).
        // NOTE: a collection named "X Backdrop" would also match the backdrop of collection "X". Unlikely enough to live with.
        public List<string> Matches(string collection, bool backdrop)
        {
            if (!Directory.Exists(Dir))
                return new List<string>();
            var want = Normalize(collection) + (backdrop ? BackdropSuffix : "");
            if (want.Length == 0)
                return new List<string>();
            return Directory.GetFiles(Dir)
                .Where(f => Extensions.Contains(System.IO.Path.GetExtension(f).ToLowerInvariant()))
                .Where(f => Normalize(System.IO.Path.GetFileNameWithoutExtension(f)) == want)
                .OrderBy(f => System.IO.Path.GetFileName(f), StringComparer.Ordinal)
                .ToList();
        }

        // ---- checking ----

        // Reads and checks a file: size, real format (by its content, not the extension) and dimensions. Throws
        // InvalidDataException with a sentence fit for the log or the popup when it is not usable.
        public static CustomImageFile Inspect(string path, out byte[] data)
        {
            var info = new FileInfo(path);
            if (info.Length > MaxBytes)
                throw new InvalidDataException($"{info.Name} is {info.Length / 1024 / 1024} MB; the limit is {MaxBytes / 1024 / 1024} MB");
            data = File.ReadAllBytes(path);
            var f = Check(data, info.Name);
            f.Path = path;
            return f;
        }

        // The same checks on bytes (an upload before it is saved).
        public static CustomImageFile Check(byte[] data, string name)
        {
            if (data == null || data.Length == 0)
                throw new InvalidDataException($"{name} is empty");
            if (data.Length > MaxBytes)
                throw new InvalidDataException($"{name} is {data.Length / 1024 / 1024} MB; the limit is {MaxBytes / 1024 / 1024} MB");
            // SKCodec only reads the header, so this is cheap even for a large file.
            using (var codec = SKCodec.Create(new SKMemoryStream(data)))
            {
                if (codec == null)
                    throw new InvalidDataException($"{name} is not an image");
                string mime;
                switch (codec.EncodedFormat)
                {
                    case SKEncodedImageFormat.Jpeg: mime = "image/jpeg"; break;
                    case SKEncodedImageFormat.Png: mime = "image/png"; break;
                    default: throw new InvalidDataException($"{name} is a {codec.EncodedFormat} image; use a JPEG or PNG");
                }
                var w = codec.Info.Width;
                var h = codec.Info.Height;
                if (w < MinSide || h < MinSide)
                    throw new InvalidDataException($"{name} is only {w}x{h} pixels; use at least {MinSide}x{MinSide}");
                return new CustomImageFile { FileName = name, MimeType = mime, Width = w, Height = h, Bytes = data.Length };
            }
        }

        // ---- changing the folder (the popup) ----

        // The file name we save an upload under: the collection name as typed, minus characters some systems refuse,
        // plus " backdrop" for a backdrop. The loose matching above finds it again whatever was stripped.
        public static string FileNameFor(string collection, bool backdrop, string mimeType)
        {
            var bad = new HashSet<char>("<>:\"/\\|?*".Concat(System.IO.Path.GetInvalidFileNameChars()));
            var safe = new string(collection.Where(ch => !bad.Contains(ch) && !char.IsControl(ch)).ToArray()).Trim().TrimEnd('.');
            if (Normalize(safe).Length == 0)
                throw new InvalidDataException($"'{collection}' has no letters or digits to name a file after");
            return safe + (backdrop ? " " + BackdropSuffix : "") + (mimeType == "image/png" ? ".png" : ".jpg");
        }

        // Checks the upload, removes whatever image the collection had for that slot (a .png replacing a .jpg would
        // otherwise leave two), and writes the new one. Written to a temp file first and then moved into place, so a
        // sync running at the same moment never reads half a file.
        public CustomImageFile Save(string collection, bool backdrop, byte[] data)
        {
            var name = collection + (backdrop ? " backdrop" : " poster");
            var f = Check(data, name);
            Directory.CreateDirectory(Dir);
            var target = System.IO.Path.Combine(Dir, FileNameFor(collection, backdrop, f.MimeType));
            var tmp = target + ".upload";
            File.WriteAllBytes(tmp, data);
            Delete(collection, backdrop);
            File.Move(tmp, target);
            f.Path = target;
            f.FileName = System.IO.Path.GetFileName(target);
            return f;
        }

        // Removes every file of that slot. Returns how many went.
        public int Delete(string collection, bool backdrop)
        {
            var files = Matches(collection, backdrop);
            foreach (var f in files)
                File.Delete(f);
            return files.Count;
        }

        // ---- thumbnails (the popup) ----

        // A small JPEG of the whole image (longest side `max` px) for the popup. The popup crops it to the poster or
        // backdrop shape itself; the full picture is sent so it can also show what gets cut off.
        public static byte[] Thumbnail(byte[] data, int max = 480)
        {
            using (var src = SKBitmap.Decode(data))
            {
                if (src == null)
                    throw new InvalidDataException("not an image");
                var scale = Math.Min(1.0, (double)max / Math.Max(src.Width, src.Height));
                var info = new SKImageInfo(Math.Max(1, (int)(src.Width * scale)), Math.Max(1, (int)(src.Height * scale)));
                using (var small = src.Resize(info, SKFilterQuality.Medium))
                using (var img = SKImage.FromBitmap(small))
                using (var jpg = img.Encode(SKEncodedImageFormat.Jpeg, 82))
                    return jpg.ToArray();
            }
        }
    }
}
