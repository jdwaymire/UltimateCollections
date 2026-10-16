// RunHistory.cs
//
// What the settings page shows about past runs: the History tab (every run, quiet nights included, newest first) and
// the status cards at the top (last run, collections in Emby, the next seasonal collection to appear). Kept in
// history.json in the plugin's data folder; the sync task adds one entry per run.
//
// NOTE: the entry keeps short readable lines ("Halloween created with 112 titles"), not item lists, so the file stays
// small. The full detail of a run is in that day's log, as before.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace UltimateCollections.Core
{
    public sealed class RunEntry
    {
        public DateTime DateUtc { get; set; }
        public string Kind { get; set; }            // RunHistory.Scheduled / Page / Manual
        public bool DryRun { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Errors { get; set; }
        public int Downloads { get; set; }          // lists downloaded from TMDb/MDBList this run
        public List<string> Changes { get; set; } = new List<string>();
        public List<string> Problems { get; set; } = new List<string>();
        // Set when the run stopped before syncing anything (bad collections text, no token, ...).
        public string Failed { get; set; }

        public int ChangeCount => Created + Updated + Deleted;
    }

    public static class RunHistory
    {
        public const int Keep = 200;

        public const string Scheduled = "scheduled";    // the daily trigger
        public const string Page = "page";              // Run now on the settings page
        public const string Manual = "manual";          // anything else, e.g. Run now under Scheduled Tasks

        // The list with the new entry first, oldest entries dropped beyond Keep.
        public static List<RunEntry> Add(IEnumerable<RunEntry> list, RunEntry entry) =>
            new[] { entry }.Concat((list ?? Enumerable.Empty<RunEntry>()).Where(e => e != null))
                .OrderByDescending(e => e.DateUtc).Take(Keep).ToList();

        // One line for the History row and the status card: the failure, the first changes, or "No changes".
        public static string Summary(RunEntry e)
        {
            string text;
            if (e.Failed != null)
                text = e.Failed;
            else if (e.Changes.Count == 0 && e.Problems.Count == 0)
                text = "No changes";
            else
            {
                var lines = e.Changes.Count > 0 ? e.Changes : e.Problems;
                text = string.Join(", ", lines.Take(2)) + (lines.Count > 2 ? $", {lines.Count - 2} more" : "");
            }
            return e.DryRun && e.Failed == null && text != "No changes" ? "Dry run: " + text : text;
        }

        // How the run was started, from what we know at its start: the page sets a flag just before it queues the task
        // (pageRequestUtc); otherwise a start within a few minutes of a daily trigger time is the schedule. Emby does
        // not tell a task what started it.
        public static string KindOf(DateTime startLocal, DateTime? pageRequestUtc, DateTime nowUtc, IEnumerable<TimeSpan> dailyTimes)
        {
            if (pageRequestUtc.HasValue && (nowUtc - pageRequestUtc.Value).Duration() < TimeSpan.FromMinutes(2))
                return Page;
            foreach (var t in dailyTimes ?? Enumerable.Empty<TimeSpan>())
            {
                var diff = Math.Abs((startLocal.TimeOfDay - t).TotalMinutes);
                if (Math.Min(diff, 1440 - diff) <= 5)
                    return Scheduled;
            }
            return Manual;
        }
    }

    // One collection as the settings page lists it (the Collections card popup) and counts it (the status cards).
    public sealed class CollectionStatus
    {
        public string Name { get; set; }
        public string Source { get; set; }
        public string Season { get; set; }          // "Oct 1 – Oct 31", or null for all year
        public bool InEmby { get; set; }
        public string Label { get; set; }           // In season / All year / From Nov 1 / Not built
        public bool Active { get; set; }            // the label shows as a green pill
        public DateTime? NextStart { get; set; }    // seasonal and off season: the day it appears
        public int Titles { get; set; }             // members in Emby, when built
    }

    public static class CollectionStatusRules
    {
        static readonly CultureInfo En = CultureInfo.InvariantCulture;

        // inEmby: the collection's Emby id when it is ours and still exists, else null. titles: its member count.
        public static List<CollectionStatus> List(CollectionsConfig cfg, Func<string, long?> inEmby, Func<long, int> titles, DateTime today)
        {
            var list = new List<CollectionStatus>();
            foreach (var c in cfg.Collections)
            {
                var id = inEmby(c.Name);
                var s = new CollectionStatus { Name = c.Name, Source = Source(c), InEmby = id.HasValue, Titles = id.HasValue ? titles(id.Value) : 0 };
                var season = c.Get("season");
                if (season != null)
                {
                    s.Season = Season.Describe(season);
                    if (!Season.InSeason(season, today))
                    {
                        s.NextStart = Season.NextStart(season, today);
                        s.Label = "From " + s.NextStart.Value.ToString("MMM d", En);
                        list.Add(s);
                        continue;
                    }
                }
                s.Active = id.HasValue;
                s.Label = !id.HasValue ? "Not built" : season != null ? "In season" : "All year";
                list.Add(s);
            }
            return list;
        }

        // The seasonal collection that appears next ("Thanksgiving, Nov 1"), or null when none is waiting.
        public static CollectionStatus Next(IEnumerable<CollectionStatus> list) =>
            list.Where(s => s.NextStart.HasValue).OrderBy(s => s.NextStart).ThenBy(s => s.Name, StringComparer.Ordinal).FirstOrDefault();

        // "TMDb keyword: halloween", "3 TMDb keywords: spring, spring break, easter", "Released 1950–1959, best rated", ...
        // Several kinds of source are joined with " + ".
        public static string Source(CollectionDefinition c)
        {
            var parts = new List<string>();
            void Add(string key, string one, string many)
            {
                var v = c.GetList(key);
                if (v.Count == 1)
                    parts.Add(one + ": " + v[0]);
                else if (v.Count > 1)
                    parts.Add($"{v.Count} {many}: {string.Join(", ", v)}");
            }
            Add("tmdb_keyword", "TMDb keyword", "TMDb keywords");
            Add("tmdb_actor", "TMDb actor", "TMDb actors");
            Add("mdblist", "MDBList", "MDBLists");
            foreach (var y in c.GetList("release_years"))
            {
                var p = y.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                parts.Add(p.Length == 2 ? $"Released {p[0]}–{p[1]}" : "Released " + y);
            }
            var titles = c.GetList("title");
            if (titles.Count > 0)
                parts.Add("Library titles containing " + string.Join(" or ", titles.Select(t => "“" + t + "”")));
            if (c.Get("min_rating") != null || c.Get("min_votes") != null)
                parts.Add("best rated");
            return parts.Count == 0 ? "No source" : string.Join(" + ", parts).Replace(" + best rated", ", best rated");
        }
    }
}
