// HomeRows.cs
//
// Collection rows on users' home screens. Emby 4.10+ keeps each user's home sections on the server; this decides
// which collections get a row, adds it once, keeps it pointing at the current collection across seasons, and removes
// it when it is no longer wanted. Off by default (the ManageHomeRows setting). Each rule below was checked on a real
// 4.10.1 server first (2026-10-12) and has a test in HomeRowsTests.
//
// NOTE: we only touch rows we track in home-rows.json: rows we added, and rows we adopted because they point at one
// of our collections (by hand, or copied with Emby's "Copy Data to Users" or another plugin). Every other row is left
// alone, except by the admin's "Clear orphaned rows" button (FindOrphans), which removes any row whose collection is
// gone. Found 2026-10-12: Emby makes a row's Id (a GUID) and refuses ours, the GUIDs are only unique per user (a copy
// keeps the source user's GUIDs), and an empty row (its collection deleted off season) is hidden by the web and TV
// apps. That last one is why rows stay all year: the user's chosen position survives until next season.

using System;
using System.Collections.Generic;
using System.Linq;

namespace UltimateCollections.Core
{
    // ---- settings, Emby view, state ----

    // The home-row settings from the settings page (see PluginConfiguration).
    public sealed class HomeRowSettings
    {
        public bool AllUsers { get; set; } = true;
        public HashSet<string> Users { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public string Style { get; set; } = "cards";        // default style for rows: cards | spotlight
        public string Position { get; set; } = "after";     // where a new row goes: after | top | bottom
        public bool Seasonal { get; set; } = true;          // seasonal collections get a row without home_screen yes
        public bool SpotlightTop { get; set; }              // move a seasonal spotlight to the top when its season starts

        // HomeRowUsers is "all" (or empty) or a comma-separated list of Emby user ids.
        public static HomeRowSettings From(string users, string style, string position, bool seasonal, bool spotlightTop)
        {
            var list = (users ?? "").Split(',').Select(u => u.Trim()).Where(u => u.Length > 0).ToList();
            var all = list.Count == 0 || list.Any(u => u.Equals("all", StringComparison.OrdinalIgnoreCase));
            return new HomeRowSettings
            {
                AllUsers = all,
                Users = new HashSet<string>(all ? new string[0] : list, StringComparer.OrdinalIgnoreCase),
                Style = (style ?? "").Trim().ToLowerInvariant() == "spotlight" ? "spotlight" : "cards",
                Position = new[] { "top", "bottom" }.Contains((position ?? "").Trim().ToLowerInvariant()) ? position.Trim().ToLowerInvariant() : "after",
                Seasonal = seasonal,
                SpotlightTop = spotlightTop,
            };
        }
    }

    public sealed class HomeUser
    {
        public string Id { get; set; }      // Emby user id (GUID, "N" format)
        public string Name { get; set; }
    }

    // One home section, cut down to what the rules need. A user's list is in home screen order.
    public sealed class HomeSection
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string SectionType { get; set; }     // "boxset" = Single Collection
        public string ParentId { get; set; }        // for boxset: the collection's Emby id
        public string ViewType { get; set; }        // "spotlight", or empty for cards
    }

    // What the rules need from Emby. Implemented by EmbyHomeScreens (IUserManager's home section calls).
    public interface IHomeScreens
    {
        List<HomeUser> GetUsers();
        List<HomeSection> Get(string userId);
        // Returns the Id Emby gave the new row (it is added at the bottom).
        string Add(string userId, HomeSection section);
        void Update(string userId, string sectionId, string parentId, string viewType);
        void Delete(string userId, string[] sectionIds);
        void Move(string userId, string sectionId, int newIndex);
    }

    // Our row for one collection on one user's home screen.
    public sealed class HomeRowState
    {
        public string Section { get; set; }     // the row's GUID; null = no row from us right now
        public bool Removed { get; set; }       // the user removed it: never add it again for them
        public bool Adopted { get; set; }       // it was not added by us (only for the log and the table)
        public string TopSeason { get; set; }   // set while moved to the top for this season (Season.StartKey)
    }

    // home-rows.json. History = every Emby id each collection has had, newest last, so a row copied in summer
    // (pointing at last season's deleted collection) is still recognised. Recorded even while home rows are off.
    public sealed class HomeRowsState
    {
        public Dictionary<string, Dictionary<string, HomeRowState>> Users { get; set; } = new Dictionary<string, Dictionary<string, HomeRowState>>();
        public Dictionary<string, List<long>> History { get; set; } = new Dictionary<string, List<long>>();

        public const int HistoryKeep = 10;

        // Emby's JSON reader leaves missing parts null; the rules want them empty.
        public HomeRowsState Normalize()
        {
            Users = Users ?? new Dictionary<string, Dictionary<string, HomeRowState>>();
            History = History ?? new Dictionary<string, List<long>>();
            foreach (var k in Users.Keys.Where(k => Users[k] == null).ToList())
                Users[k] = new Dictionary<string, HomeRowState>();
            return this;
        }

        // Adds each collection's current id to its history. Runs after every real sync, home rows on or off.
        public void RecordHistory(IReadOnlyDictionary<string, long> collections)
        {
            foreach (var kv in collections)
            {
                if (!History.TryGetValue(kv.Key, out var ids) || ids == null)
                    History[kv.Key] = ids = new List<long>();
                if (!ids.Contains(kv.Value))
                    ids.Add(kv.Value);
                if (ids.Count > HistoryKeep)
                    ids.RemoveRange(0, ids.Count - HistoryKeep);
            }
        }
    }

    // ---- rules ----

    public static class HomeRowRules
    {
        public const string Key = "home_screen";

        // home_screen yes|no|spotlight, normalised; null = not set (or a value we don't know, see Problems).
        public static string Setting(CollectionDefinition c)
        {
            var v = (c.Get(Key) ?? "").Trim().ToLowerInvariant();
            if (new[] { "yes", "true", "on", "1" }.Contains(v)) return "yes";
            if (new[] { "no", "false", "off", "0" }.Contains(v)) return "no";
            return v == "spotlight" ? "spotlight" : null;
        }

        // home_screen values we don't know. Only a warning, unlike other config mistakes: the worst a typo can do here
        // is leave out one row, so it should not stop the collections from syncing.
        public static IEnumerable<string> Problems(CollectionsConfig cfg) =>
            cfg.Collections.Where(c => c.Has(Key) && Setting(c) == null)
                .Select(c => $"[{c.Name}] unknown {Key} value '{c.Get(Key)}' (use yes, no or spotlight); ignored");

        // Seasonal collections get a row unless they say no (with the Seasonal setting on); others only with yes or
        // spotlight.
        public static bool Wanted(CollectionDefinition c, HomeRowSettings s)
        {
            var v = Setting(c);
            if (v == "yes" || v == "spotlight") return true;
            return v != "no" && s.Seasonal && c.Get("season") != null;
        }

        // The style the collection asks for. What it gets right now can still be cards, see Styles.
        public static string WantedStyle(CollectionDefinition c, HomeRowSettings s) =>
            Setting(c) == "spotlight" ? "spotlight" : s.Style;

        // One spotlight at a time (decided 2026-10-12: two stacked banners are too much): among the wanted spotlight
        // collections that exist right now, the first in the config keeps it, the others show as cards meanwhile.
        // Returns name -> style for every wanted collection, and name -> the collection holding the spotlight for
        // the ones that had to give it up.
        public static (Dictionary<string, string> styles, Dictionary<string, string> yielded) Styles(
            CollectionsConfig cfg, HomeRowSettings s, Func<string, bool> exists)
        {
            var styles = new Dictionary<string, string>();
            var yielded = new Dictionary<string, string>();
            string holder = null;
            foreach (var c in cfg.Collections.Where(c => Wanted(c, s)))
            {
                var st = WantedStyle(c, s);
                if (st == "spotlight" && exists(c.Name))
                {
                    if (holder == null)
                        holder = c.Name;
                    else
                    {
                        st = "cards";
                        yielded[c.Name] = holder;
                    }
                }
                styles[c.Name] = st;
            }
            return (styles, yielded);
        }

        // For the settings page: spotlight collections whose seasons overlap (or that are permanent). The later one in
        // the config shows as cards while both are on. Checked day by day over a leap year, which is plenty fast.
        public static List<string> SpotlightOverlaps(CollectionsConfig cfg, HomeRowSettings s)
        {
            var spots = cfg.Collections.Where(c => Wanted(c, s) && WantedStyle(c, s) == "spotlight").ToList();
            var notes = new List<string>();
            for (var i = 0; i < spots.Count; i++)
                for (var j = i + 1; j < spots.Count; j++)
                {
                    var a = spots[i];
                    var b = spots[j];
                    bool On(CollectionDefinition c, DateTime d) => c.Get("season") == null || Season.InSeason(c.Get("season"), d);
                    var day = Enumerable.Range(0, 366).Select(n => new DateTime(2028, 1, 1).AddDays(n)).FirstOrDefault(d => On(a, d) && On(b, d));
                    if (day != default(DateTime))
                        notes.Add($"{a.Name} and {b.Name} are both spotlight when their seasons overlap: {b.Name} shows as cards then ({a.Name} is listed first).");
                }
            return notes;
        }

        // Where a new row goes (-1 = leave it at the bottom, where Emby adds it). "after" = right after Continue
        // Watching / Continue Listening (Next Up is part of Continue Watching in 4.10); top when there is neither.
        // Rows of ours already sitting at that spot are skipped, so rows added in one run keep the config order
        // instead of each new one pushing the last one down.
        public static int NewRowIndex(string position, List<HomeSection> sections, ICollection<string> ours)
        {
            if (position == "bottom")
                return -1;
            var i = position == "top" ? 0 : sections.FindLastIndex(x => x.SectionType == "resume" || x.SectionType == "resumeaudio") + 1;
            while (i < sections.Count && ours.Contains(sections[i].Id))
                i++;
            return i;
        }

        // ---- settings page table ----

        public sealed class TableRow
        {
            public string Name { get; set; }
            public string Season { get; set; }
            public string Status { get; set; }
            public string Style { get; set; }
            public int Users { get; set; }      // users with a row from us right now
            public int Removed { get; set; }    // users who removed it
        }

        // What the rows look like right now, from the saved settings and state (not the unsaved text box).
        public static List<TableRow> Table(CollectionsConfig cfg, HomeRowSettings s, IReadOnlyDictionary<string, long> collections,
            HomeRowsState state, DateTime today)
        {
            var (styles, yielded) = Styles(cfg, s, collections.ContainsKey);
            var rows = new List<TableRow>();
            foreach (var c in cfg.Collections)
            {
                var season = c.Get("season");
                var r = new TableRow { Name = c.Name, Season = season == null ? "All year" : SafeDescribe(season) };
                if (!Wanted(c, s))
                {
                    r.Status = "No row";
                    r.Style = "–";
                }
                else
                {
                    if (collections.ContainsKey(c.Name))
                        r.Status = "Showing";
                    else if (season != null && !SafeInSeason(season, today))
                        r.Status = "Hidden until " + Season.NextStart(season, today).ToString("MMM d", System.Globalization.CultureInfo.InvariantCulture);
                    else
                        r.Status = "Hidden (collection not built)";
                    var st = styles[c.Name] == "spotlight" ? "Spotlight" : "Cards";
                    r.Style = yielded.TryGetValue(c.Name, out var h) ? $"{st} ({h} has the spotlight)" : st;
                    foreach (var u in state.Users.Values)
                        if (u.TryGetValue(c.Name, out var rs) && rs != null)
                        {
                            if (rs.Section != null) r.Users++;
                            else if (rs.Removed) r.Removed++;
                        }
                }
                rows.Add(r);
            }
            return rows;
        }

        // The table is shown on the settings page even when a season line is broken (the run reports that error), so
        // these two never throw.
        static string SafeDescribe(string season)
        {
            try { return Season.Describe(season); } catch (Exception) { return season; }
        }

        static bool SafeInSeason(string season, DateTime today)
        {
            try { return Season.InSeason(season, today); } catch (Exception) { return false; }
        }
    }

    // ---- the sync ----

    public sealed class HomeRowResult
    {
        public int Added { get; set; }
        public int Updated { get; set; }
        public int Removed { get; set; }
        public int Adopted { get; set; }
        public int Moved { get; set; }
        public int Errors { get; set; }
    }

    // A row whose collection is gone (see HomeRowSync.FindOrphans).
    public sealed class OrphanRow
    {
        public string UserId { get; set; }
        public string User { get; set; }
        public string Section { get; set; }
        public string Name { get; set; }
    }

    // Runs after the collections are synced, so `collections` (name -> Emby id of our collections that exist now)
    // already has this run's new ids. Changes State in place; the task saves it after a real run.
    public sealed class HomeRowSync
    {
        readonly IHomeScreens _screens;
        readonly ISyncLog _log;

        public HomeRowsState State { get; }

        public HomeRowSync(IHomeScreens screens, HomeRowsState state, ISyncLog log)
        {
            _screens = screens;
            State = state;
            _log = log;
        }

        // The whole pass: styles are worked out once for everybody (the one-spotlight rule is the same for all users),
        // then each user on their own. A user whose home screen fails is logged and skipped; the others still sync.
        public HomeRowResult Run(CollectionsConfig cfg, HomeRowSettings s, IReadOnlyDictionary<string, long> collections, SyncOptions o)
        {
            var res = new HomeRowResult();
            foreach (var p in HomeRowRules.Problems(cfg))
                _log.Warn(p);
            var (styles, yielded) = HomeRowRules.Styles(cfg, s, collections.ContainsKey);
            foreach (var y in yielded)
                _log.Info($"[{y.Key}] home row shows as cards while {y.Value} has the spotlight");

            var users = _screens.GetUsers();
            // Users deleted in Emby: nothing left to clean up, just forget them.
            foreach (var gone in State.Users.Keys.Where(id => users.All(u => !u.Id.Equals(id, StringComparison.OrdinalIgnoreCase))).ToList())
                State.Users.Remove(gone);

            foreach (var u in users)
            {
                try
                {
                    SyncUser(u, cfg, s, collections, styles, o, res);
                }
                catch (Exception e)
                {
                    _log.Warn($"home rows for {u.Name} failed, left as they were: {e.Message}");
                    res.Errors++;
                }
            }
            return res;
        }

        // Every collection in config order for one user. The section list is read again after each change, so the
        // positions we compute are always against what Emby has now.
        void SyncUser(HomeUser u, CollectionsConfig cfg, HomeRowSettings s, IReadOnlyDictionary<string, long> collections,
            Dictionary<string, string> styles, SyncOptions o, HomeRowResult res)
        {
            var tag = o.DryRun ? "[dry-run] " : "";
            if (!State.Users.TryGetValue(u.Id, out var rows))
                rows = new Dictionary<string, HomeRowState>();
            var sections = _screens.Get(u.Id);

            // Not one of the chosen users (any more): take our rows off and forget them.
            if (!s.AllUsers && !s.Users.Contains(u.Id))
            {
                var ours = rows.Values.Where(r => r.Section != null && sections.Any(x => x.Id == r.Section)).Select(r => r.Section).ToArray();
                if (ours.Length > 0)
                {
                    _log.Info($"{tag}home rows: {u.Name} is not one of the chosen users, removing {ours.Length} row(s)");
                    if (!o.DryRun)
                        _screens.Delete(u.Id, ours);
                    res.Removed += ours.Length;
                }
                State.Users.Remove(u.Id);
                return;
            }

            foreach (var c in cfg.Collections)
            {
                if (o.Only.Count > 0 && !o.Only.Contains(c.Name))
                    continue;
                if (SyncRow(u, c, s, rows, sections, collections, styles, o, res) && !o.DryRun)
                    sections = _screens.Get(u.Id);
            }

            // Collections gone from the config (full runs only, like the collections themselves).
            if (o.Only.Count == 0)
            {
                var names = new HashSet<string>(cfg.Collections.Select(c => c.Name));
                foreach (var name in rows.Keys.Where(n => !names.Contains(n)).ToList())
                {
                    var r = rows[name];
                    if (r.Section != null && sections.Any(x => x.Id == r.Section))
                    {
                        _log.Info($"[{name}] {tag}no longer in config: home row removed for {u.Name}");
                        if (!o.DryRun)
                            _screens.Delete(u.Id, new[] { r.Section });
                        res.Removed++;
                    }
                    rows.Remove(name);
                }
            }

            if (rows.Count > 0)
                State.Users[u.Id] = rows;
            else
                State.Users.Remove(u.Id);
        }

        // One collection on one user's home screen. Returns true when something was changed in Emby.
        bool SyncRow(HomeUser u, CollectionDefinition c, HomeRowSettings s, Dictionary<string, HomeRowState> rows,
            List<HomeSection> sections, IReadOnlyDictionary<string, long> collections, Dictionary<string, string> styles,
            SyncOptions o, HomeRowResult res)
        {
            var tag = o.DryRun ? "[dry-run] " : "";
            var name = c.Name;
            var list = sections;
            rows.TryGetValue(name, out var r);
            var tracked = r?.Section;
            var present = tracked != null && list.Any(x => x.Id == tracked);
            var changed = false;

            // Not wanted (home_screen no, Seasonal off, ...): our row goes. A "removed" mark stays, so turning it back
            // on later does not bring back a row the user took off.
            if (!HomeRowRules.Wanted(c, s))
            {
                if (present)
                {
                    _log.Info($"[{name}] {tag}home row removed for {u.Name} (not wanted any more)");
                    if (!o.DryRun)
                        _screens.Delete(u.Id, new[] { tracked });
                    res.Removed++;
                    changed = true;
                }
                if (r != null)
                {
                    if (r.Removed)
                        r.Section = null;
                    else
                        rows.Remove(name);
                }
                return changed;
            }

            // Rows we don't track that point at this collection, now or in an earlier season: by hand, Emby's copy,
            // or another plugin. Rows we track for other collections are never candidates.
            var known = new HashSet<string>((State.History.TryGetValue(name, out var h) && h != null ? h : new List<long>())
                .Select(id => id.ToString()));
            long cid = 0;
            var exists = collections.TryGetValue(name, out cid);
            if (exists)
                known.Add(cid.ToString());
            var otherTracked = new HashSet<string>(rows.Where(kv => kv.Key != name && kv.Value.Section != null).Select(kv => kv.Value.Section));
            var candidates = list.Where(x => x.Id != tracked && !otherTracked.Contains(x.Id) && x.SectionType == "boxset" &&
                                             x.ParentId != null && known.Contains(x.ParentId)).ToList();

            // Ours is gone and nothing to adopt: the user removed it (or a copy replaced their rows). Respect that.
            if (tracked != null && !present && candidates.Count == 0)
            {
                _log.Info($"[{name}] home row gone for {u.Name} (removed, or replaced by a copied home screen); not adding it again");
                r.Section = null;
                r.Removed = true;
                r.Adopted = false;
                r.TopSeason = null;
                return false;
            }

            // Adopt / de-duplicate: every row for this collection is ours now. The one highest on the screen stays.
            if (candidates.Count > 0)
            {
                var all = list.Where(x => (present && x.Id == tracked) || candidates.Contains(x)).ToList();
                var keep = all[0];
                if (keep.Id != tracked)
                {
                    if (r == null)
                        rows[name] = r = new HomeRowState();
                    _log.Info($"[{name}] {tag}home row adopted on {u.Name}'s home screen");
                    r.Section = keep.Id;
                    r.Adopted = true;
                    r.Removed = false;
                    r.TopSeason = null;
                    tracked = keep.Id;
                    present = true;
                    res.Adopted++;
                }
                var extra = all.Where(x => x.Id != keep.Id).Select(x => x.Id).ToArray();
                if (extra.Length > 0)
                {
                    _log.Info($"[{name}] {tag}{extra.Length} extra row(s) for this collection removed on {u.Name}'s home screen");
                    if (!o.DryRun)
                        _screens.Delete(u.Id, extra);
                    list = list.Where(x => !extra.Contains(x.Id)).ToList();
                    res.Removed += extra.Length;
                    changed = true;
                }
            }

            if (r != null && r.Removed && !present)
                return changed;

            var season = c.Get("season");
            var style = styles.TryGetValue(name, out var st) ? st : "cards";
            var viewType = style == "spotlight" ? "spotlight" : null;
            var spotTop = s.SpotlightTop && season != null && style == "spotlight" && exists;

            // Collection not built (off season, or too few items): the row stays where it is, empty and hidden. With
            // "spotlight to the top" a row that went up for its season goes to the bottom now, because a hidden row at
            // the top still blocks the full-screen spotlight for the next row (tested 2026-10-12).
            if (!exists)
            {
                if (present && r.TopSeason != null)
                {
                    _log.Info($"[{name}] {tag}season over: home row moved to the bottom for {u.Name}");
                    if (!o.DryRun)
                        _screens.Move(u.Id, tracked, list.Count - 1);
                    r.TopSeason = null;
                    res.Moved++;
                    changed = true;
                }
                return changed;
            }

            var key = season != null ? Season.StartKey(season, o.Today) : null;
            if (!present)
            {
                var index = spotTop ? 0 : HomeRowRules.NewRowIndex(s.Position, list, otherTracked);
                var where = index < 0 ? "at the bottom" : spotTop || s.Position == "top" ? "at the top" : "after Continue Watching";
                _log.Info($"[{name}] {tag}home row added for {u.Name} ({style}, {where})");
                var id = "(dry-run)";
                if (!o.DryRun)
                {
                    id = _screens.Add(u.Id, new HomeSection { Name = name, SectionType = "boxset", ParentId = cid.ToString(), ViewType = viewType });
                    if (index >= 0)
                        _screens.Move(u.Id, id, index);
                }
                if (r == null)
                    rows[name] = r = new HomeRowState();
                r.Section = id;
                r.Removed = false;
                r.Adopted = false;
                r.TopSeason = spotTop ? key : null;
                res.Added++;
                return true;
            }

            // Ours and present: point it at this season's collection, with the right look. Updated in place, so the
            // user's position is kept.
            var cur = list.First(x => x.Id == tracked);
            if (cur.ParentId != cid.ToString() || (cur.ViewType ?? "") != (viewType ?? ""))
            {
                var why = cur.ParentId != cid.ToString() ? "now points at the current collection" : $"now {style}";
                _log.Info($"[{name}] {tag}home row updated for {u.Name} ({why})");
                if (!o.DryRun)
                    _screens.Update(u.Id, tracked, cid.ToString(), viewType);
                res.Updated++;
                changed = true;
            }
            if (spotTop && r.TopSeason != key)
            {
                _log.Info($"[{name}] {tag}season started: home row moved to the top for {u.Name}");
                if (!o.DryRun)
                    _screens.Move(u.Id, tracked, 0);
                r.TopSeason = key;
                res.Moved++;
                changed = true;
            }
            return changed;
        }

        // ---- orphaned rows (the settings page's "Clear orphaned rows") ----

        // Single Collection rows, on any user's home screen, whose collection no longer exists in Emby: left by a
        // switch-off with "Leave them", a collection deleted or renamed, or another plugin. Emby hides them, but they
        // are still listed in the user's home screen settings and an empty first row blocks the full-screen spotlight.
        // Any such row counts, not only ours: an empty row is no use to anyone.
        // While home rows are on (cfg given), two kinds of empty rows are kept, because the next run uses them: rows
        // we track (waiting for next season) and rows it would adopt (pointing at an old id of a collection that is
        // wanted for that user). A ParentId we cannot read as an Emby id is left alone.
        public List<OrphanRow> FindOrphans(Func<long, bool> itemExists, CollectionsConfig cfg, HomeRowSettings s)
        {
            var orphans = new List<OrphanRow>();
            var adoptable = new HashSet<string>();
            if (cfg != null)
                foreach (var c in cfg.Collections.Where(c => HomeRowRules.Wanted(c, s)))
                    if (State.History.TryGetValue(c.Name, out var ids) && ids != null)
                        adoptable.UnionWith(ids.Select(id => id.ToString()));
            foreach (var u in _screens.GetUsers())
            {
                var kept = new HashSet<string>();
                if (cfg != null && State.Users.TryGetValue(u.Id, out var rows))
                    kept.UnionWith(rows.Values.Where(r => r.Section != null).Select(r => r.Section));
                var inScope = cfg != null && (s.AllUsers || s.Users.Contains(u.Id));
                List<HomeSection> sections;
                try
                {
                    sections = _screens.Get(u.Id);
                }
                catch (Exception e)
                {
                    _log.Warn($"could not read {u.Name}'s home screen: {e.Message}");
                    continue;
                }
                foreach (var x in sections)
                {
                    if (x.SectionType != "boxset" || !long.TryParse(x.ParentId, out var pid) || itemExists(pid))
                        continue;
                    if (kept.Contains(x.Id) || (inScope && adoptable.Contains(x.ParentId)))
                        continue;
                    orphans.Add(new OrphanRow { UserId = u.Id, User = u.Name, Section = x.Id, Name = x.Name });
                }
            }
            return orphans;
        }

        // Deletes what FindOrphans finds right now, one call per user. A user whose home screen fails is logged and
        // skipped. Returns how many rows were removed.
        public int ClearOrphans(Func<long, bool> itemExists, CollectionsConfig cfg, HomeRowSettings s)
        {
            var removed = 0;
            foreach (var g in FindOrphans(itemExists, cfg, s).GroupBy(o => o.UserId))
            {
                var user = g.First().User;
                try
                {
                    _screens.Delete(g.Key, g.Select(o => o.Section).ToArray());
                    removed += g.Count();
                    _log.Info($"orphaned home rows removed for {user}: {string.Join(", ", g.Select(o => o.Name ?? "(no name)"))}");
                }
                catch (Exception e)
                {
                    _log.Warn($"could not remove the orphaned home rows for {user}: {e.Message}");
                }
            }
            return removed;
        }

        // Switching the feature off: take every row we track off every home screen (unless keepRows), then forget
        // them all. History stays, so switching on again can still adopt old rows. Returns how many were removed.
        public int RemoveAll(bool keepRows)
        {
            var removed = 0;
            if (!keepRows)
            {
                var users = _screens.GetUsers();
                foreach (var kv in State.Users)
                {
                    var u = users.FirstOrDefault(x => x.Id.Equals(kv.Key, StringComparison.OrdinalIgnoreCase));
                    if (u == null)
                        continue;
                    try
                    {
                        var sections = _screens.Get(u.Id);
                        var ours = kv.Value.Values.Where(r => r.Section != null && sections.Any(x => x.Id == r.Section)).Select(r => r.Section).ToArray();
                        if (ours.Length == 0)
                            continue;
                        _screens.Delete(u.Id, ours);
                        removed += ours.Length;
                        _log.Info($"home rows switched off: {ours.Length} row(s) removed for {u.Name}");
                    }
                    catch (Exception e)
                    {
                        _log.Warn($"home rows switched off: could not remove the rows for {u.Name}: {e.Message}");
                    }
                }
            }
            else
                _log.Info("home rows switched off: rows left on the home screens as they are");
            State.Users.Clear();
            return removed;
        }
    }
}
