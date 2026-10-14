// PosterPreviewApi.cs
//
// The REST route behind the settings page's "Poster style" popup: draws a collection's poster with the colours being
// tried, using the same renderer and the same member images the nightly sync would use, and hands it back as a data
// URL. Nothing is saved or uploaded; the popup only writes lines into the settings text. The Custom images popup uses
// it too (Backdrop=true for backdrops), to show something for collections that are not built yet.
//
// NOTE: admin only, like the Danger zone routes. It reads the library, state.json and the saved lists, and never
// downloads anything, so trying colours costs no TMDb requests.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    [Route("/UltimateCollections/PosterPreview", "POST", Summary = "Draw a collection's poster with the given style, without saving it")]
    [Authenticated(Roles = "Admin")]
    public class PosterPreviewRequest : IReturn<PosterPreview>
    {
        // Collection name, as in [collection NAME].
        public string Name { get; set; }

        // The same values as the poster_* keys; empty = not set, so the plugin's default is used.
        public string Title { get; set; }
        public string Accent { get; set; }
        public string Tint { get; set; }
        public string Sepia { get; set; }

        // Draw the backdrop instead of the poster (the Custom images popup previews both for collections not built yet).
        public bool Backdrop { get; set; }
    }

    public class PosterPreview
    {
        // "data:image/jpeg;base64,..." or null when no member image could be found.
        public string Image { get; set; }

        // The style actually used, defaults filled in, so the popup can show what an unset colour turns into.
        public string Accent { get; set; }
        public string Tint { get; set; }
        public string Title { get; set; }

        // Where the movies came from, in words for the popup (e.g. "the collection's 121 titles").
        public string Source { get; set; }
    }

    public class PosterPreviewApi : IService
    {
        readonly ILibraryManager _library;
        readonly ICollectionManager _collections;
        readonly IProviderManager _providers;
        readonly IFileSystem _fileSystem;
        readonly ILogger _logger;
        readonly IJsonSerializer _jsonSerializer;

        public PosterPreviewApi(ILibraryManager library, ICollectionManager collections, IProviderManager providers,
            IFileSystem fileSystem, ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _library = library;
            _collections = collections;
            _providers = providers;
            _fileSystem = fileSystem;
            _logger = logManager.GetLogger("UltimateCollections");
            _jsonSerializer = jsonSerializer;
        }

        static string DataDir => Plugin.Instance.DataFolderPath;

        public object Post(PosterPreviewRequest request)
        {
            var name = (request.Name ?? "").Trim();
            if (name.Length == 0)
                throw new ArgumentException("Pick a collection first.");

            // Build the style the way the sync does, from a collection that has only the poster keys. A bad colour
            // comes back as a ConfigException, which the popup shows as it is.
            var def = new CollectionDefinition(name, 0);
            void Set(string key, string value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    def.Values[key] = value.Trim();
            }
            Set("poster_title", request.Title);
            Set("poster_accent", request.Accent);
            Set("poster_tint", request.Tint);
            Set("poster_sepia", request.Sepia);
            var style = PosterStyle.For(def);

            var (members, source) = Members(name);
            var images = ArtworkSync.BestImages(new EmbyArtworkTarget(_library, _providers, _fileSystem), members, request.Backdrop);
            return new PosterPreview
            {
                Image = images.Count == 0 ? null
                    : "data:image/jpeg;base64," + Convert.ToBase64String(PosterRenderer.Render(request.Backdrop, images.Select(i => i.path).ToList(), style)),
                Accent = $"{style.Accent.Red},{style.Accent.Green},{style.Accent.Blue}",
                Tint = $"{style.Tint.Red},{style.Tint.Green},{style.Tint.Blue}",
                Title = string.Join("|", style.Lines),
                Source = images.Count == 0 ? "none of its titles has a poster on disk" : source,
            };
        }

        // The movies a preview is made from, best match first:
        //   1. the collection as it is in Emby now (ours, from state.json),
        //   2. its saved list matched against the library (collections out of season, or not built yet),
        //   3. the whole movie library, so a brand-new collection still shows something.
        (List<LibraryItem> members, string source) Members(string name)
        {
            var lib = new EmbyLibrary(_library, _collections);
            var json = new EmbyJson(_jsonSerializer);
            var state = SyncCollectionsTask.LoadJson<Dictionary<string, long>>(json, Path.Combine(DataDir, "state.json"));
            if (state.TryGetValue(name, out var id) && lib.CollectionExists(id))
            {
                var inEmby = lib.GetMemberItems(id);
                if (inEmby.Count > 0)
                    return (inEmby, $"the collection's {inEmby.Count} titles");
            }

            var saved = new ListCache(Path.Combine(DataDir, "lists"), json, new ServerLog(_logger)).Peek(name);
            if (saved != null)
            {
                var def = new CollectionDefinition(name, 0);
                var movies = lib.GetMovies();
                var matched = Pick(movies, Matcher.ByIds(def, Set(saved.tmdb), new HashSet<string>(saved.imdb ?? new List<string>()), movies));
                if ((saved.tv_tmdb?.Count ?? 0) + (saved.tv_imdb?.Count ?? 0) > 0)
                {
                    var shows = lib.GetSeries();
                    matched.AddRange(Pick(shows, Matcher.ByIds(def, Set(saved.tv_tmdb), new HashSet<string>(saved.tv_imdb ?? new List<string>()), shows)));
                }
                if (matched.Count > 0)
                    return (matched, $"its saved list ({matched.Count} titles in your library); the collection is not in Emby right now");
            }

            // NOTE: out-of-season collections usually land here: the sync skips them before downloading, so they have
            // no saved list until their season starts (found 2026-10-09 with Christmas).
            return (lib.GetMovies(), "the best-rated movies in your library, because this collection is not built yet " +
                                     "(out of season, or new); the real poster will use its own titles");
        }

        static HashSet<long> Set(List<long> ids) => new HashSet<long>(ids ?? new List<long>());

        static List<LibraryItem> Pick(List<LibraryItem> items, HashSet<long> ids) => items.Where(i => ids.Contains(i.Id)).ToList();
    }
}
