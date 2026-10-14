// SyncCollectionsTask.cs
//
// The scheduled task "Ultimate Collections > Sync collections" (Dashboard > Scheduled Tasks). Daily at 05:00 by
// default, a quiet hour on most servers; "Run now" there is the manual run. It wires the Core pieces to Emby, runs the Syncer once,
// then the home screen rows (when switched on), and saves state.json, artwork-state.json and home-rows.json. Also home of the
// small adapters: EmbyJson, SyncLog and ServerLog.
//
// NOTE: a dry run saves nothing (state, artwork state). The only setting it changes is "Download all lists again",
// which is a one-shot and is switched off after any run.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Tasks;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    public class SyncCollectionsTask : IScheduledTask
    {
        readonly ILibraryManager _library;
        readonly ICollectionManager _collections;
        readonly ILogger _logger;
        readonly IJsonSerializer _jsonSerializer;
        readonly IProviderManager _providers;
        readonly IFileSystem _fileSystem;
        readonly IUserManager _users;
        readonly ILocalizationManager _localization;

        public SyncCollectionsTask(ILibraryManager library, ICollectionManager collections, ILogManager logManager,
            IJsonSerializer jsonSerializer, IProviderManager providers, IFileSystem fileSystem, IUserManager users,
            ILocalizationManager localization)
        {
            _localization = localization;
            _library = library;
            _collections = collections;
            _logger = logManager.GetLogger("UltimateCollections");
            _jsonSerializer = jsonSerializer;
            _providers = providers;
            _fileSystem = fileSystem;
            _users = users;
        }

        // ---- small JSON file helpers (also used by CollectionsApi) ----

        // A missing file (first run) or a file holding "null" gives an empty object.
        internal static T LoadJson<T>(EmbyJson json, string path) where T : class, new() =>
            File.Exists(path) ? json.Deserialize<T>(File.ReadAllText(path)) ?? new T() : new T();

        // Write to a .tmp file and swap it in, so a crash halfway through never leaves a half-written state.json.
        // Losing state.json would mean the plugin no longer knows which collections are its own.
        internal static void SaveJson(EmbyJson json, string path, object value)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, json.Serialize(value));
            if (File.Exists(path))
                File.Replace(tmp, path, null);
            else
                File.Move(tmp, path);
        }

        public string Name => "Sync collections";
        public string Key => "UltimateCollectionsSync";
        public string Description => "Create, update and remove the collections defined in the Ultimate Collections settings.";
        public string Category => "Ultimate Collections";

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => new[]
        {
            new TaskTriggerInfo { Type = TaskTriggerInfo.TriggerDaily, TimeOfDayTicks = TimeSpan.FromHours(5).Ticks },
        };

        // ---- the run ----

        // WorkLock keeps a sync and "Delete All Collections" from running at the same time. A sync waits for it.
        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            await Plugin.WorkLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await RunAsync(cancellationToken, progress).ConfigureAwait(false);
            }
            finally
            {
                Plugin.WorkLock.Release();
            }
        }

        async Task RunAsync(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var plugin = Plugin.Instance;
            var conf = plugin.Configuration;
            var dataDir = plugin.DataFolderPath;
            Directory.CreateDirectory(dataDir);
            var json = new EmbyJson(_jsonSerializer);
            using (var log = new SyncLog(_logger, Path.Combine(dataDir, "logs")))
            {
                // Bad settings or a missing token stop the run before anything is read or changed. The error goes to
                // our log and is rethrown, so the task also shows as failed in the dashboard.
                CollectionsConfig cfg;
                try
                {
                    cfg = ConfigParser.Parse(conf.CollectionsText);
                }
                catch (ConfigException e)
                {
                    log.Error("ERROR in the collection settings: " + e.Message);
                    throw;
                }
                if (cfg.Collections.Any(c => c.NeedsDownload) && string.IsNullOrWhiteSpace(conf.TmdbToken))
                {
                    log.Error("ERROR: no TMDb token set in the plugin settings");
                    throw new InvalidOperationException("No TMDb token set");
                }

                var statePath = Path.Combine(dataDir, "state.json");
                var state = LoadJson<Dictionary<string, long>>(json, statePath);
                var artworkStatePath = Path.Combine(dataDir, "artwork-state.json");
                var artwork = new ArtworkSync(new EmbyArtworkTarget(_library, _providers, _fileSystem), Path.Combine(dataDir, "previews"),
                    LoadJson<Dictionary<string, string>>(json, artworkStatePath), log)
                {
                    Custom = new CustomImages(Path.Combine(dataDir, "images")),
                };

                // One TMDb client for the whole run, so tmdb.Calls is the request count for the summary line. Its
                // warnings are pointed at the collection being downloaded, so the log says which one they belong to.
                var http = new Http();
                var tmdb = new TmdbClient(http, json, conf.TmdbToken.Trim());
                var mdb = new MdbListClient(http, json);
                var cache = new ListCache(Path.Combine(dataDir, "lists"), json, log);
                var syncer = new Syncer(new EmbyLibrary(_library, _collections), cache,
                    (c, maxPages) =>
                    {
                        tmdb.Warn = m => log.Warn($"[{c.Name}] {m}");
                        return ListFetcher.FetchAsync(c, tmdb, mdb, maxPages, cancellationToken);
                    }, state, log)
                {
                    Artwork = artwork,
                };
                // "Ignore seasons" is for previewing seasonal collections, so it only counts in a dry run.
                var options = new SyncOptions
                {
                    DryRun = conf.DryRun,
                    ShowTitles = conf.ShowTitles,
                    IgnoreSeason = conf.DryRun && conf.IgnoreSeason,
                    Refresh = conf.RefreshNextRun,
                    Only = new HashSet<string>((conf.OnlyCollections ?? "").Split(',').Select(s => s.Trim()).Where(s => s.Length > 0)),
                };

                var result = await syncer.RunAsync(cfg, options, progress, cancellationToken).ConfigureAwait(false);

                // Artwork state is pruned to the collections we still own, so a deleted collection's entry goes too.
                if (!conf.DryRun)
                {
                    SaveJson(json, statePath, syncer.State);
                    artwork.Prune(syncer.State.Values);
                    SaveJson(json, artworkStatePath, artwork.State);
                }

                // Home screen rows come last, so they see this run's new collection ids. The id history is kept on
                // every real run, rows on or off, so switching rows on later can still adopt older rows. A failure
                // here is only a warning: the collections are done by now.
                var homePath = Path.Combine(dataDir, "home-rows.json");
                var home = LoadJson<HomeRowsState>(json, homePath).Normalize();
                if (!conf.DryRun)
                    home.RecordHistory(syncer.State);
                var homeNote = "";
                if (conf.ManageHomeRows)
                {
                    try
                    {
                        var rows = new HomeRowSync(new EmbyHomeScreens(_users, _localization, json, Path.Combine(dataDir, "backups")), home, log)
                            .Run(cfg, conf.GetHomeRowSettings(), syncer.State, options);
                        homeNote = $", home rows {rows.Added} added, {rows.Updated} updated, {rows.Adopted} adopted, {rows.Moved} moved, " +
                                   $"{rows.Removed} removed{(rows.Errors > 0 ? $", {rows.Errors} user(s) failed" : "")}";
                    }
                    catch (Exception e)
                    {
                        log.Warn($"home rows failed, left as they were: {e.GetType().Name}: {e.Message}");
                    }
                }
                if (!conf.DryRun)
                    SaveJson(json, homePath, home);
                // Repeated once at the end, so a refused token is not lost among the per-collection lines (collections
                // with a saved list keep working from it, which makes the problem easy to miss).
                if (tmdb.Refused != null)
                    log.Error("ERROR: " + tmdb.Refused);
                if (conf.RefreshNextRun)
                {
                    conf.RefreshNextRun = false;
                    plugin.SaveConfiguration();
                }
                log.Info($"{(conf.DryRun ? "[dry-run] " : "")}done: {result.Created} created, {result.Updated} changed, " +
                         $"{result.Deleted} deleted, {tmdb.Calls} TMDb request(s), {result.Errors} error(s){homeNote}");
            }
        }
    }

    // ---- adapters ----

    // IJson on Emby's own serializer, so the plugin ships no JSON library (deploy stays one DLL).
    public class EmbyJson : IJson
    {
        readonly IJsonSerializer _s;

        public EmbyJson(IJsonSerializer s) => _s = s;

        public T Deserialize<T>(string json) => _s.DeserializeFromString<T>(json);

        public string Serialize(object value) => _s.SerializeToString(value);
    }

    // Logs to Emby's server log and to a daily file in the plugin's data folder (kept 30 days).
    // Old files are cleared out when a log is opened, i.e. once per run.
    public sealed class SyncLog : ISyncLog, IDisposable
    {
        const int KeepDays = 30;
        readonly ILogger _logger;
        readonly StreamWriter _file;

        public SyncLog(ILogger logger, string dir)
        {
            _logger = logger;
            Directory.CreateDirectory(dir);
            foreach (var f in Directory.GetFiles(dir, "*.log"))
                if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddDays(-KeepDays))
                    File.Delete(f);
            _file = new StreamWriter(Path.Combine(dir, DateTime.Now.ToString("yyyy-MM-dd") + ".log"), append: true) { AutoFlush = true };
        }

        void Write(string level, string message) =>
            _file.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {(level == "INFO" ? "" : level + " ")}{message}");

        // NOTE: the message goes in as an argument to "{0}", never as the format string itself. Emby's ILogger
        // formats its first argument, so a title with { or } in it would break or garble the log line.
        public void Info(string message)
        {
            _logger.Info("{0}", message);
            Write("INFO", message);
        }

        public void Warn(string message)
        {
            _logger.Warn("{0}", message);
            Write("WARN", message);
        }

        public void Error(string message)
        {
            _logger.Error("{0}", message);
            Write("ERROR", message);
        }

        public void Dispose() => _file.Dispose();
    }

    // Server log only, for the page's read-only routes (a broken saved list in the poster preview, an unreadable home
    // screen while counting orphans). NOTE: not SyncLog: its file is held open by a running sync.
    public sealed class ServerLog : ISyncLog
    {
        readonly ILogger _logger;

        public ServerLog(ILogger logger) => _logger = logger;

        public void Info(string message) => _logger.Info("{0}", message);
        public void Warn(string message) => _logger.Warn("{0}", message);
        public void Error(string message) => _logger.Error("{0}", message);
    }
}
