// CustomImagesApi.cs
//
// The REST routes behind the settings page's "Custom images" popup: list which collections have their own poster or
// backdrop (with a small thumbnail), upload one, remove one. The files live in the plugin's images/ folder; the real
// work is in Core/CustomImages. Nothing is set on a collection here: the next sync picks the change up.
//
// NOTE: admin only, like the other routes. The page only ever sends a collection name, never a path or file name;
// the file name is made on this side (CustomImages.FileNameFor), so nothing can be written or deleted outside images/.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    [Route("/UltimateCollections/CustomImages/List", "POST", Summary = "Which of these collections have a custom poster or backdrop")]
    [Authenticated(Roles = "Admin")]
    public class CustomImagesListRequest : IReturn<List<CustomImageSlots>>
    {
        // Collection names, one per line (names cannot contain a line break, unlike a comma).
        public string Names { get; set; }
    }

    [Route("/UltimateCollections/CustomImages/Upload", "POST", Summary = "Save a custom poster or backdrop for a collection")]
    [Authenticated(Roles = "Admin")]
    public class CustomImageUploadRequest : IReturn<CustomImageSlots>
    {
        public string Name { get; set; }
        public bool Backdrop { get; set; }

        // The file, base64 (the way Emby's own image upload sends it).
        public string Data { get; set; }
    }

    [Route("/UltimateCollections/CustomImages/Delete", "POST", Summary = "Remove a collection's custom poster or backdrop")]
    [Authenticated(Roles = "Admin")]
    public class CustomImageDeleteRequest : IReturn<CustomImageSlots>
    {
        public string Name { get; set; }
        public bool Backdrop { get; set; }
    }

    public class CustomImageSlots
    {
        public string Name { get; set; }
        public CustomImageView Poster { get; set; }      // null = none, the generated one is used
        public CustomImageView Backdrop { get; set; }

        // Small pictures of what the collection has in Emby right now (usually the generated artwork), so a slot
        // without a custom image shows the real poster instead of an empty frame. Null when the collection is not
        // built yet (out of season, new) or has no image.
        public string CurrentPoster { get; set; }
        public string CurrentBackdrop { get; set; }
    }

    public class CustomImageView
    {
        public string FileName { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public long Bytes { get; set; }

        // "data:image/jpeg;base64,..." of the whole picture, small; null when the file is unusable.
        public string Thumb { get; set; }

        // Why the sync will not use the file (not an image, too big...); the popup shows it in place of the picture.
        public string Error { get; set; }

        // More files match the same slot; the first (this one) is used.
        public int Others { get; set; }
    }

    public class CustomImagesApi : IService
    {
        readonly ILogger _logger;
        readonly ILibraryManager _library;
        readonly IJsonSerializer _jsonSerializer;

        public CustomImagesApi(ILogManager logManager, ILibraryManager library, IJsonSerializer jsonSerializer)
        {
            _logger = logManager.GetLogger("UltimateCollections");
            _library = library;
            _jsonSerializer = jsonSerializer;
        }

        static CustomImages Images => new CustomImages(Path.Combine(Plugin.Instance.DataFolderPath, "images"));

        public object Post(CustomImagesListRequest request)
        {
            var state = SyncCollectionsTask.LoadJson<Dictionary<string, long>>(new EmbyJson(_jsonSerializer),
                Path.Combine(Plugin.Instance.DataFolderPath, "state.json"));
            return (request.Names ?? "").Split('\n').Select(n => n.Trim()).Where(n => n.Length > 0).Distinct()
                .Select(n => WithCurrent(Slots(n), state)).ToList();
        }

        // Adds the collection's current Emby images (ours only, found through state.json), for slots without a
        // custom image. A picture that cannot be read is just left out: it is decoration, not worth failing the
        // list for.
        CustomImageSlots WithCurrent(CustomImageSlots slots, Dictionary<string, long> state)
        {
            var item = state.TryGetValue(slots.Name, out var id) ? _library.GetItemById(id) : null;
            if (item == null)
                return slots;
            string Thumb(ImageType type)
            {
                try
                {
                    var info = item.GetImageInfo(type, 0);
                    if (info == null || !info.IsLocalFile || !File.Exists(info.Path))
                        return null;
                    return "data:image/jpeg;base64," + Convert.ToBase64String(CustomImages.Thumbnail(File.ReadAllBytes(info.Path)));
                }
                catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
                {
                    return null;
                }
            }
            if (slots.Poster == null)
                slots.CurrentPoster = Thumb(ImageType.Primary);
            if (slots.Backdrop == null)
                slots.CurrentBackdrop = Thumb(ImageType.Backdrop);
            return slots;
        }

        // Errors (bad image, too big) come back as an ArgumentException, which Emby turns into a 400 with the message
        // for the popup to show.
        public object Post(CustomImageUploadRequest request)
        {
            var name = Name(request.Name);
            byte[] data;
            try
            {
                data = Convert.FromBase64String(request.Data ?? "");
            }
            catch (FormatException)
            {
                throw new ArgumentException("The upload arrived damaged; try again.");
            }
            try
            {
                var f = Images.Save(name, request.Backdrop, data);
                _logger.Info("[{0}] custom {1} uploaded: {2} ({3}x{4})", name, request.Backdrop ? "backdrop" : "poster",
                    f.FileName, f.Width, f.Height);
            }
            catch (InvalidDataException e)
            {
                throw new ArgumentException(e.Message);
            }
            return Slots(name);
        }

        public object Post(CustomImageDeleteRequest request)
        {
            var name = Name(request.Name);
            var n = Images.Delete(name, request.Backdrop);
            _logger.Info("[{0}] custom {1} removed ({2} file(s))", name, request.Backdrop ? "backdrop" : "poster", n);
            return Slots(name);
        }

        static string Name(string name)
        {
            var n = (name ?? "").Trim();
            if (n.Length == 0)
                throw new ArgumentException("No collection given.");
            return n;
        }

        static CustomImageSlots Slots(string name) =>
            new CustomImageSlots { Name = name, Poster = View(name, false), Backdrop = View(name, true) };

        // One slot as the popup shows it. A file that the sync would refuse is still listed, with the reason, so the
        // user can see it is there and remove or replace it.
        static CustomImageView View(string name, bool backdrop)
        {
            var files = Images.Matches(name, backdrop);
            if (files.Count == 0)
                return null;
            var v = new CustomImageView { FileName = Path.GetFileName(files[0]), Others = files.Count - 1 };
            try
            {
                var f = CustomImages.Inspect(files[0], out var data);
                v.Width = f.Width;
                v.Height = f.Height;
                v.Bytes = f.Bytes;
                v.Thumb = "data:image/jpeg;base64," + Convert.ToBase64String(CustomImages.Thumbnail(data));
            }
            catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException)
            {
                v.Error = e.Message;
            }
            return v;
        }
    }
}
