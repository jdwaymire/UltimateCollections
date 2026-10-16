// StatusApi.cs
//
// REST routes behind the top of the settings page and its History tab: the status cards (collections in Emby, the next
// seasonal one, home rows, the last run), the Collections popup list, the run history, and Run now.
//
// NOTE: everything here reads the SAVED settings and state files, like the next run sees them, never the unsaved page.
// A broken collections text is reported in Error instead of failing the page.
// NOTE: Run now queues the real scheduled task (it shows under Scheduled Tasks as running and takes WorkLock like a
// nightly run) rather than syncing inside this request.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using MediaBrowser.Common;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using MediaBrowser.Model.Tasks;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    [Route("/UltimateCollections/Status", "GET", Summary = "Status cards and the collections list for the settings page")]
    [Authenticated(Roles = "Admin")]
    public class StatusRequest : IReturn<StatusView>
    {
    }

    [Route("/UltimateCollections/History", "GET", Summary = "Every sync run, newest first")]
    [Authenticated(Roles = "Admin")]
    public class HistoryRequest : IReturn<List<HistoryView>>
    {
    }

    [Route("/UltimateCollections/RunNow", "POST", Summary = "Start the sync task now")]
    [Authenticated(Roles = "Admin")]
    public class RunNowRequest : IReturn<RunNowResult>
    {
    }

    public class StatusView
    {
        public string Error { get; set; }
        public bool DryRun { get; set; }
        public bool TokenSet { get; set; }
        public bool TokenRefused { get; set; }      // the latest run got a 401 from TMDb
        public int Configured { get; set; }
        public int Seasonal { get; set; }
        public int InEmby { get; set; }
        public string NextName { get; set; }
        public string NextDate { get; set; }        // "Nov 1"
        public List<CollectionStatus> Collections { get; set; } = new List<CollectionStatus>();
        public string Schedule { get; set; }        // "Daily 05:00", "No schedule"
        public bool Running { get; set; }
        public double? Progress { get; set; }
        public bool HomeRowsOn { get; set; }
        public string HomeRowStyle { get; set; }
        public int HomeUsers { get; set; }
        public List<string> HomeShowing { get; set; } = new List<string>();
        public HistoryView LastRun { get; set; }
    }

    public class HistoryView
    {
        public string Date { get; set; }            // local, "yyyy-MM-dd HH:mm"
        public string Kind { get; set; }
        public bool DryRun { get; set; }
        public int Created { get; set; }
        public int Updated { get; set; }
        public int Deleted { get; set; }
        public int Changed { get; set; }
        public int Errors { get; set; }
        public int Downloads { get; set; }
        public string Summary { get; set; }
        public List<string> Changes { get; set; }
        public List<string> Problems { get; set; }
        public string Failed { get; set; }

        public static HistoryView From(RunEntry e) => new HistoryView
        {
            Date = e.DateUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            Kind = e.Kind,
            DryRun = e.DryRun,
            Created = e.Created,
            Updated = e.Updated,
            Deleted = e.Deleted,
            Changed = e.ChangeCount,
            Errors = e.Errors + (e.Failed != null && e.Failed != "Cancelled" ? 1 : 0),
            Downloads = e.Downloads,
            Summary = RunHistory.Summary(e),
            Changes = e.Changes ?? new List<string>(),
            Problems = e.Problems ?? new List<string>(),
            Failed = e.Failed,
        };
    }

    public class RunNowResult
    {
        public bool Started { get; set; }
        public string Message { get; set; }
    }

    // Our task as Emby's task manager knows it: its triggers (for the schedule text and for telling a scheduled run
    // from a manual one) and whether it is running.
    public static class SyncTaskInfo
    {
        public static IScheduledTaskWorker Worker(ITaskManager tasks) =>
            tasks?.ScheduledTasks?.FirstOrDefault(w => w.ScheduledTask is SyncCollectionsTask);

        // Times of day of the daily and weekly triggers. Empty when they cannot be read: then every run counts as
        // manual, which only changes a label.
        public static List<TimeSpan> ScheduledTimes(IApplicationHost host)
        {
            try
            {
                return ScheduledTimes(Worker(host.Resolve<ITaskManager>()));
            }
            catch (Exception)
            {
                return new List<TimeSpan>();
            }
        }

        static List<TimeSpan> ScheduledTimes(IScheduledTaskWorker w) =>
            (w?.Triggers ?? Array.Empty<TaskTriggerInfo>())
                .Where(t => (t.Type == TaskTriggerInfo.TriggerDaily || t.Type == TaskTriggerInfo.TriggerWeekly) && t.TimeOfDayTicks.HasValue)
                .Select(t => TimeSpan.FromTicks(t.TimeOfDayTicks.Value)).ToList();

        // "Daily 05:00", "Daily 05:00 and 17:00", "Weekly", "No schedule".
        public static string Describe(IScheduledTaskWorker w)
        {
            var triggers = w?.Triggers ?? Array.Empty<TaskTriggerInfo>();
            var daily = triggers.Where(t => t.Type == TaskTriggerInfo.TriggerDaily && t.TimeOfDayTicks.HasValue)
                .Select(t => TimeSpan.FromTicks(t.TimeOfDayTicks.Value)).OrderBy(t => t).Select(t => t.ToString(@"hh\:mm")).ToList();
            if (daily.Count > 0)
                return "Daily " + string.Join(" and ", daily);
            if (triggers.Any(t => t.Type == TaskTriggerInfo.TriggerWeekly))
                return "Weekly";
            return triggers.Length > 0 ? "Custom schedule" : "No schedule";
        }
    }

    public class StatusApi : IService
    {
        readonly ILibraryManager _library;
        readonly ICollectionManager _collections;
        readonly IJsonSerializer _jsonSerializer;
        readonly ITaskManager _tasks;

        public StatusApi(ILibraryManager library, ICollectionManager collections, IJsonSerializer jsonSerializer, ITaskManager tasks)
        {
            _library = library;
            _collections = collections;
            _jsonSerializer = jsonSerializer;
            _tasks = tasks;
        }

        static string DataDir => Plugin.Instance.DataFolderPath;

        List<RunEntry> LoadHistory(EmbyJson json) =>
            SyncCollectionsTask.LoadJson<List<RunEntry>>(json, Path.Combine(DataDir, "history.json")).Where(e => e != null).ToList();

        public object Get(StatusRequest request)
        {
            var conf = Plugin.Instance.Configuration;
            var json = new EmbyJson(_jsonSerializer);
            var view = new StatusView
            {
                DryRun = conf.DryRun,
                TokenSet = !string.IsNullOrWhiteSpace(conf.TmdbToken),
                HomeRowsOn = conf.ManageHomeRows,
                HomeRowStyle = conf.HomeRowStyle,
            };
            var worker = SyncTaskInfo.Worker(_tasks);
            view.Schedule = SyncTaskInfo.Describe(worker);
            view.Running = worker != null && worker.State != TaskState.Idle;
            view.Progress = view.Running ? worker.CurrentProgress : null;

            var last = LoadHistory(json).FirstOrDefault();
            if (last != null)
            {
                view.LastRun = HistoryView.From(last);
                view.TokenRefused = (last.Problems ?? new List<string>()).Any(p => p.Contains("(401)"));
            }

            CollectionsConfig cfg;
            try
            {
                cfg = ConfigParser.Parse(conf.CollectionsText);
            }
            catch (ConfigException e)
            {
                view.Error = "The saved configuration text has an error: " + e.Message;
                return view;
            }
            var state = SyncCollectionsTask.LoadJson<Dictionary<string, long>>(json, Path.Combine(DataDir, "state.json"));
            var emby = new EmbyLibrary(_library, _collections);
            long? InEmby(string name) => state.TryGetValue(name, out var id) && emby.CollectionExists(id) ? id : (long?)null;
            view.Collections = CollectionStatusRules.List(cfg, InEmby, id => emby.GetMembers(id).Count, DateTime.Today);
            view.Configured = view.Collections.Count;
            view.Seasonal = cfg.Collections.Count(c => c.Get("season") != null);
            view.InEmby = view.Collections.Count(c => c.InEmby);
            var next = CollectionStatusRules.Next(view.Collections);
            if (next != null)
            {
                view.NextName = next.Name;
                view.NextDate = next.NextStart.Value.ToString("MMM d", CultureInfo.InvariantCulture);
            }

            var home = SyncCollectionsTask.LoadJson<HomeRowsState>(json, Path.Combine(DataDir, "home-rows.json")).Normalize();
            view.HomeUsers = home.Users.Count;
            if (conf.ManageHomeRows)
                view.HomeShowing = HomeRowRules.Table(cfg, conf.GetHomeRowSettings(), state, home, DateTime.Today)
                    .Where(r => r.Status == "Showing").Select(r => r.Name).ToList();
            return view;
        }

        public object Get(HistoryRequest request) =>
            LoadHistory(new EmbyJson(_jsonSerializer)).Select(HistoryView.From).ToList();

        public object Post(RunNowRequest request)
        {
            var worker = SyncTaskInfo.Worker(_tasks);
            if (worker == null)
                return new RunNowResult { Message = "The sync task was not found." };
            if (worker.State != TaskState.Idle)
                return new RunNowResult { Message = "A sync is already running." };
            Plugin.PageRunRequestedUtc = DateTime.UtcNow;
            _tasks.QueueScheduledTask<SyncCollectionsTask>();
            return new RunNowResult { Started = true, Message = "Sync started." };
        }
    }
}
