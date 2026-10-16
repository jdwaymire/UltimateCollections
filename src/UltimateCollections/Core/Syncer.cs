// Syncer.cs
//
// The nightly sync itself: for every collection in the settings, get its list (saved copy or download), match it
// against the library, then create, update or delete the Emby collection.
//
// NOTE: we only ever change or delete collections whose Emby ids are in State, i.e. the ones this plugin created.
// A collection with the same name made by somebody else (by hand, or by another plugin) is left alone with a
// warning, so the plugin never takes over collections it did not make.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace UltimateCollections.Core
{
    // A library movie or series, cut down to what matching (and the poster) needs.
    public sealed class LibraryItem
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public int? Year { get; set; }
        public string TmdbId { get; set; }
        public string ImdbId { get; set; }
        public string[] Genres { get; set; } = Array.Empty<string>();
        public bool IsSeries { get; set; }
        // Emby's community rating; the poster uses it to pick the 9 best-rated members.
        public float? Rating { get; set; }

        public string Label => $"{Name} ({Year}{(IsSeries ? ", TV" : "")})";

        // Label plus the ids, for the log when titles are shown.
        public string Debug => $"{Label} [id {Id}, tmdb {TmdbId ?? "-"}, imdb {ImdbId ?? "-"}]";
    }

    // Everything the sync needs from Emby. Implemented by EmbyLibrary (Emby's internal services).
    public interface IEmbyLibrary
    {
        List<LibraryItem> GetMovies();
        List<LibraryItem> GetSeries();
        // All collections (BoxSets) in Emby: name -> internal id.
        Dictionary<string, long> GetCollections();
        // Every collection with its id. Unlike GetCollections, names may repeat here.
        List<LibraryItem> GetAllCollections();
        // The items in a collection, with names (for the backup).
        List<LibraryItem> GetMemberItems(long collectionId);
        bool CollectionExists(long id);
        HashSet<long> GetMembers(long collectionId);
        Task<long> CreateCollectionAsync(string name, long[] itemIds);
        Task AddAsync(long collectionId, long[] itemIds);
        void Remove(long collectionId, long[] itemIds);
        void Delete(long collectionId);
    }

    public sealed class SyncOptions
    {
        public bool DryRun { get; set; } = true;
        public bool ShowTitles { get; set; }
        public bool IgnoreSeason { get; set; }
        public bool Refresh { get; set; }
        public HashSet<string> Only { get; set; } = new HashSet<string>();
        public DateTime Today { get; set; } = DateTime.Today;
    }

    public sealed class SyncResult
    {
        public int Errors { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        // Lists downloaded this run (saved copies do not count).
        public int Downloads { get; set; }
        // Short lines for the History tab: "Halloween created with 112 titles", "Time Travel +2 -0 titles", ...
        public List<string> Changes { get; } = new List<string>();
        // "Christmas: sources returned nothing", ...: one line per collection error.
        public List<string> Problems { get; } = new List<string>();
    }

    public static class Matcher
    {
        // Library items whose name contains any of the 'title' texts, ignoring case. No API calls at all.
        public static HashSet<long> ByTitle(CollectionDefinition col, IEnumerable<LibraryItem> items)
        {
            var texts = col.GetList("title");
            return new HashSet<long>(items.Where(i => texts.Any(t => (i.Name ?? "").IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0))
                .Select(i => i.Id));
        }

        // Library items that are on the list, by TMDb or IMDb id. exclude_genre is applied here a second time with
        // Emby's genres, because MDBList sources never go through TMDb's genre filter.
        public static HashSet<long> ByIds(CollectionDefinition col, HashSet<long> tmdb, HashSet<string> imdb, IEnumerable<LibraryItem> items)
        {
            var excl = new HashSet<string>(col.GetList("exclude_genre"), StringComparer.OrdinalIgnoreCase);
            var result = new HashSet<long>();
            foreach (var m in items)
            {
                var onList = (long.TryParse(m.TmdbId, out var t) && tmdb.Contains(t)) || (!string.IsNullOrEmpty(m.ImdbId) && imdb.Contains(m.ImdbId));
                if (onList && !m.Genres.Any(excl.Contains))
                    result.Add(m.Id);
            }
            return result;
        }
    }

    public class Syncer
    {
        readonly IEmbyLibrary _emby;
        readonly ListCache _cache;
        readonly Func<CollectionDefinition, int, Task<SourceList>> _download;
        readonly ISyncLog _log;

        // Posters and backdrops; null means no artwork at all.
        public ArtworkSync Artwork { get; set; }

        // Collection name -> Emby internal id, for the collections this plugin created. This is the ownership list.
        public Dictionary<string, long> State { get; }

        public Syncer(IEmbyLibrary emby, ListCache cache, Func<CollectionDefinition, int, Task<SourceList>> download,
            Dictionary<string, long> state, ISyncLog log)
        {
            _emby = emby;
            _cache = cache;
            _download = download;
            State = state;
            _log = log;
        }

        // ---- the whole run ----

        // Loads the library once, syncs each collection on its own (one failing collection is logged and the rest go
        // on), then on a full run deletes the collections that are gone from the settings.
        public async Task<SyncResult> RunAsync(CollectionsConfig cfg, SyncOptions o, IProgress<double> progress, CancellationToken ct)
        {
            var res = new SyncResult();
            var tag = o.DryRun ? "[dry-run] " : "";
            _log.Info($"{tag}start: {cfg.Collections.Count} collections in config");

            var lib = _emby.GetMovies();
            var shows = cfg.Collections.Any(c => c.WantsTv) ? _emby.GetSeries() : new List<LibraryItem>();
            // Found 2026-10-09 (first dry run on a real server): Emby handed back items without their ids (all 0), and every
            // match collapsed into one wrong title. The same id twice means half-loaded items, so stop before changing
            // anything.
            if (lib.Concat(shows).GroupBy(i => i.Id).Any(g => g.Count() > 1))
                throw new InvalidOperationException("library items with duplicate ids; nothing changed");
            var labels = lib.Concat(shows).GroupBy(i => i.Id)
                .ToDictionary(g => g.Key, g => o.ShowTitles ? g.First().Debug : g.First().Label);
            var boxsets = _emby.GetCollections();
            _log.Info($"library: {lib.Count} movies ({lib.Count(i => i.TmdbId != null)} with a TMDb id, " +
                      $"{lib.Count(i => i.ImdbId != null)} with an IMDb id, {lib.Select(i => i.Id).Distinct().Count()} distinct ids), " +
                      $"{shows.Count} series, {boxsets.Count} collections in Emby");
            if (o.ShowTitles)
                _log.Info("library sample: " + string.Join("; ", lib.Take(3).Select(i => i.Debug)));
            var maxPagesDefault = cfg.GlobalInt("max_pages", 100);
            var minItemsDefault = cfg.GlobalInt("min_items", 2);
            var refreshDays = cfg.GlobalDouble("list_refresh_days", 7);

            var done = 0;
            foreach (var c in cfg.Collections)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(100.0 * done++ / Math.Max(1, cfg.Collections.Count));
                if (o.Only.Count > 0 && !o.Only.Contains(c.Name))
                    continue;
                try
                {
                    await SyncOneAsync(cfg, c, o, tag, lib, shows, labels, boxsets, maxPagesDefault, minItemsDefault, refreshDays, res, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception e)
                {
                    _log.Error($"[{c.Name}] ERROR, collection left unchanged: {e.Message}");
                    res.Errors++;
                    res.Problems.Add($"{c.Name}: {e.Message}");
                }
            }

            // Clean-up only on a full run: with "Only these collections" the others merely look missing.
            if (o.Only.Count == 0)
            {
                _cache.Prune(cfg.Collections.Select(c => c.Name));
                var names = new HashSet<string>(cfg.Collections.Select(c => c.Name));
                foreach (var name in State.Keys.Where(n => !names.Contains(n)).ToList())
                {
                    _log.Info($"[{name}] {tag}no longer in config: deleting collection");
                    if (!o.DryRun)
                    {
                        if (_emby.CollectionExists(State[name]))
                            _emby.Delete(State[name]);
                        State.Remove(name);
                    }
                    res.Deleted++;
                    res.Changes.Add($"{name} deleted (no longer in the configuration)");
                }
            }
            progress?.Report(100);
            return res;
        }

        // ---- one collection ----

        // Order matters: ownership first, then the season window, then the list and the matching, then min_items,
        // and only then do we create or update. Anything that looks wrong leaves the collection as it is.
        async Task SyncOneAsync(CollectionsConfig cfg, CollectionDefinition c, SyncOptions o, string tag, List<LibraryItem> lib, List<LibraryItem> shows,
            Dictionary<long, string> labels, Dictionary<string, long> boxsets, int maxPagesDefault, int minItemsDefault,
            double refreshDays, SyncResult res, CancellationToken ct)
        {
            var name = c.Name;
            long? cid = State.TryGetValue(name, out var sid) ? sid : (long?)null;
            // Deleted by hand in Emby: drop the stale id and make it again, instead of keeping a dead id and
            // logging it every night.
            if (cid.HasValue && !_emby.CollectionExists(cid.Value))
            {
                _log.Info($"[{name}] collection {cid} was deleted in Emby, will recreate");
                if (!o.DryRun)
                    State.Remove(name);
                cid = null;
            }
            // Not ours but the name is taken: never adopt or touch somebody else's collection.
            if (!cid.HasValue && boxsets.ContainsKey(name))
            {
                _log.Warn($"[{name}] a collection with this name exists in Emby but was not made by this plugin; left alone");
                return;
            }

            void DeleteOwned(string why)
            {
                _log.Info($"[{name}] {tag}{why}: deleting collection");
                if (!o.DryRun)
                {
                    _emby.Delete(cid.Value);
                    State.Remove(name);
                }
                res.Deleted++;
                res.Changes.Add($"{name} deleted ({why})");
            }

            // Seasonal collections only exist inside their window; outside it ours is deleted.
            var season = c.Get("season");
            if (season != null && !o.IgnoreSeason && !Season.InSeason(season, o.Today))
            {
                if (cid.HasValue)
                    DeleteOwned($"off season ({season})");
                await KeepListReadyAsync(c, season, maxPagesDefault, refreshDays, o, res, ct).ConfigureAwait(false);
                return;
            }

            // Gather the wanted items from all sources (several sources = ANY). Downloaded lists go through the cache,
            // so most nights this costs no API requests.
            var want = new HashSet<long>();
            var listed = 0;
            var src = "library titles";
            if (c.NeedsDownload)
            {
                var maxPages = c.Get("max_pages") != null ? int.Parse(c.Get("max_pages")) : maxPagesDefault;
                var (r, how) = await _cache.GetAsync(c, () => _download(c, maxPages), refreshDays, o.Refresh, ct).ConfigureAwait(false);
                src = how;
                if (how.StartsWith("downloaded", StringComparison.Ordinal))
                    res.Downloads++;
                listed = r.Listed;
                want.UnionWith(Matcher.ByIds(c, r.Tmdb, r.Imdb, lib));
                if (c.WantsTv)
                    want.UnionWith(Matcher.ByIds(c, r.TvTmdb, r.TvImdb, shows));
            }
            if (c.GetList("title").Count > 0)
            {
                var byTitle = Matcher.ByTitle(c, c.WantsTv ? lib.Concat(shows) : lib);
                want.UnionWith(byTitle);
                listed += byTitle.Count;
            }
            // An empty source is more likely broken than empty, so we don't empty or delete the collection over it.
            if (listed == 0)
            {
                _log.Error($"[{name}] ERROR: sources returned nothing, collection left unchanged");
                res.Errors++;
                res.Problems.Add($"{name}: sources returned nothing");
                return;
            }
            if (o.ShowTitles)
                _log.Info($"[{name}] matches: {string.Join("; ", want.Select(i => labels[i]).OrderBy(x => x, StringComparer.Ordinal))}");

            var minItems = c.Get("min_items") != null ? int.Parse(c.Get("min_items")) : minItemsDefault;
            if (want.Count < minItems)
            {
                _log.Info($"[{name}] only {want.Count} of the {listed} listed items are in the library (< min_items {minItems})");
                if (cid.HasValue)
                    DeleteOwned("too few items");
                return;
            }

            if (!cid.HasValue)
            {
                _log.Info($"[{name}] {tag}create with {want.Count} items (list has {listed}, {src})");
                if (!o.DryRun)
                    State[name] = await _emby.CreateCollectionAsync(name, want.OrderBy(x => x).ToArray()).ConfigureAwait(false);
                res.Created++;
                res.Changes.Add($"{name} created with {want.Count} titles");
                await ArtworkAsync(cfg, c, o.DryRun ? (long?)null : State[name], want, lib, shows, o.DryRun, ct).ConfigureAwait(false);
                return;
            }

            // Existing collection: only the difference is sent to Emby.
            var have = _emby.GetMembers(cid.Value);
            var add = want.Except(have).OrderBy(x => x).ToArray();
            var rem = have.Except(want).OrderBy(x => x).ToArray();
            if (!o.DryRun)
            {
                if (add.Length > 0)
                    await _emby.AddAsync(cid.Value, add).ConfigureAwait(false);
                if (rem.Length > 0)
                    _emby.Remove(cid.Value, rem);
            }
            if (add.Length > 0 || rem.Length > 0)
            {
                res.Updated++;
                res.Changes.Add($"{name} +{add.Length} -{rem.Length} titles");
            }
            _log.Info($"[{name}] {tag}{want.Count} items (+{add.Length} -{rem.Length}, list has {listed}, {src})");
            await ArtworkAsync(cfg, c, cid, want, lib, shows, o.DryRun, ct).ConfigureAwait(false);
        }

        // Off-season collections still keep a saved list, through the same cache and weekly refresh as the rest. It
        // lets the settings page preview their poster from their own titles, and the collection appears from a ready
        // list on the first day of its season. Found 2026-10-12: without it, previews of Christmas & co. showed the
        // library's best-rated movies instead.
        // NOTE: costs a few requests a week (small keyword lists, MDBList is free). A failure here is only a warning:
        // nothing in Emby depends on it until the season starts, and then the normal path tries again.
        async Task KeepListReadyAsync(CollectionDefinition c, string season, int maxPagesDefault, double refreshDays, SyncOptions o,
            SyncResult res, CancellationToken ct)
        {
            if (!c.NeedsDownload)
                return;
            try
            {
                var maxPages = c.Get("max_pages") != null ? int.Parse(c.Get("max_pages")) : maxPagesDefault;
                var (r, how) = await _cache.GetAsync(c, () => _download(c, maxPages), refreshDays, o.Refresh, ct).ConfigureAwait(false);
                if (how.StartsWith("downloaded", StringComparison.Ordinal))
                    res.Downloads++;
                _log.Info($"[{c.Name}] off season ({season}), list kept ready: {r.Listed} listed ({how})");
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _log.Warn($"[{c.Name}] off season ({season}), list not refreshed: {e.Message}");
            }
        }

        // A failed poster or backdrop is only a warning: by this point the collection itself is already in order.
        async Task ArtworkAsync(CollectionsConfig cfg, CollectionDefinition c, long? cid, HashSet<long> want, List<LibraryItem> lib,
            List<LibraryItem> shows, bool dryRun, CancellationToken ct)
        {
            if (Artwork == null)
                return;
            // NOTE: everything that touches SkiaSharp stays inside the try. SkiaSharp comes from Emby, not from us; if
            // Emby ever ships a different version, loading it fails here as a warning and the collections still sync.
            try
            {
                // "posters no" only stops the generated artwork; custom images are still set (see ArtworkSync).
                var members = lib.Concat(shows).Where(i => want.Contains(i.Id));
                await Artwork.SyncAsync(c, cid, members, dryRun, ct, PosterStyle.Enabled(cfg, c)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception e)
            {
                _log.Warn($"[{c.Name}] poster/backdrop failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
