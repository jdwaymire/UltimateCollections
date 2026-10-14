// HomeRowsApi.cs
//
// The REST routes behind the settings page's "Home screen rows" section: the "Rows right now" table, switching the
// feature off (remove our rows from every home screen, or leave them), and counting / clearing orphaned rows (rows
// whose collection is gone). The rules are in Core/HomeRows.cs.
//
// NOTE: all routes are admin only. SwitchOff also saves ManageHomeRows = false itself, so the setting and the
// home screens never disagree (a page left unsaved would otherwise let the next run add every row back).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;
using MediaBrowser.Model.Services;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    [Route("/UltimateCollections/HomeRows", "GET", Summary = "Which collections have home screen rows right now")]
    [Authenticated(Roles = "Admin")]
    public class HomeRowsRequest : IReturn<HomeRowsView>
    {
    }

    public class HomeRowsView
    {
        public List<HomeRowRules.TableRow> Rows { get; set; } = new List<HomeRowRules.TableRow>();
        public List<string> Notes { get; set; } = new List<string>();
        public string Error { get; set; }
        public int UsersWithRows { get; set; }
    }

    [Route("/UltimateCollections/HomeRows/SwitchOff", "POST", Summary = "Stop managing home screen rows, removing ours or leaving them")]
    [Authenticated(Roles = "Admin")]
    public class HomeRowsSwitchOffRequest : IReturn<HomeRowsSwitchOffResult>
    {
        public bool RemoveRows { get; set; }
    }

    public class HomeRowsSwitchOffResult
    {
        public int Removed { get; set; }
    }

    [Route("/UltimateCollections/HomeRows/Orphans", "GET", Summary = "Collection rows on home screens whose collection no longer exists")]
    [Authenticated(Roles = "Admin")]
    public class HomeRowsOrphansRequest : IReturn<HomeRowsOrphansView>
    {
    }

    [Route("/UltimateCollections/HomeRows/ClearOrphans", "POST", Summary = "Remove the rows whose collection no longer exists")]
    [Authenticated(Roles = "Admin")]
    public class HomeRowsClearOrphansRequest : IReturn<HomeRowsSwitchOffResult>
    {
    }

    public class HomeRowsOrphansView
    {
        public int Count { get; set; }
        public int Users { get; set; }
        // "Ronnie: Halloween" lines for the page, so the admin sees what goes before pressing the button.
        public List<string> Rows { get; set; } = new List<string>();
    }

    public class HomeRowsApi : IService
    {
        readonly IUserManager _users;
        readonly ILibraryManager _library;
        readonly ILogger _logger;
        readonly IJsonSerializer _jsonSerializer;
        readonly ILocalizationManager _localization;

        public HomeRowsApi(IUserManager users, ILibraryManager library, ILogManager logManager, IJsonSerializer jsonSerializer,
            ILocalizationManager localization)
        {
            _localization = localization;
            _users = users;
            _library = library;
            _logger = logManager.GetLogger("UltimateCollections");
            _jsonSerializer = jsonSerializer;
        }

        static string DataDir => Plugin.Instance.DataFolderPath;

        // From the SAVED settings and state, like the next run sees them. A broken collections text is reported in
        // Error instead of failing the page.
        public object Get(HomeRowsRequest request)
        {
            var conf = Plugin.Instance.Configuration;
            var json = new EmbyJson(_jsonSerializer);
            var view = new HomeRowsView();
            CollectionsConfig cfg;
            try
            {
                cfg = ConfigParser.Parse(conf.CollectionsText);
            }
            catch (ConfigException e)
            {
                view.Error = "The saved collections text has an error: " + e.Message;
                return view;
            }
            var settings = conf.GetHomeRowSettings();
            var collections = SyncCollectionsTask.LoadJson<Dictionary<string, long>>(json, Path.Combine(DataDir, "state.json"));
            var state = SyncCollectionsTask.LoadJson<HomeRowsState>(json, Path.Combine(DataDir, "home-rows.json")).Normalize();
            view.Rows = HomeRowRules.Table(cfg, settings, collections, state, DateTime.Today);
            view.Notes.AddRange(HomeRowRules.SpotlightOverlaps(cfg, settings));
            view.Notes.AddRange(HomeRowRules.Problems(cfg));
            view.UsersWithRows = state.Users.Count;
            return view;
        }

        // Like Delete All Collections: refused straight away while a sync runs, instead of waiting, so the page can
        // say so. The rows go first, then home-rows.json is saved, then the setting is switched off.
        public object Post(HomeRowsSwitchOffRequest request)
        {
            if (!Plugin.WorkLock.Wait(0))
                throw new InvalidOperationException("A collection sync is running right now. Try again when it has finished.");
            try
            {
                Directory.CreateDirectory(DataDir);
                var json = new EmbyJson(_jsonSerializer);
                var path = Path.Combine(DataDir, "home-rows.json");
                var state = SyncCollectionsTask.LoadJson<HomeRowsState>(json, path).Normalize();
                int removed;
                using (var log = new SyncLog(_logger, Path.Combine(DataDir, "logs")))
                {
                    var sync = new HomeRowSync(new EmbyHomeScreens(_users, _localization, json, Path.Combine(DataDir, "backups")), state, log);
                    removed = sync.RemoveAll(keepRows: !request.RemoveRows);
                }
                SyncCollectionsTask.SaveJson(json, path, state);
                var plugin = Plugin.Instance;
                plugin.Configuration.ManageHomeRows = false;
                plugin.SaveConfiguration();
                return new HomeRowsSwitchOffResult { Removed = removed };
            }
            finally
            {
                Plugin.WorkLock.Release();
            }
        }

        // ---- orphaned rows ----

        public object Get(HomeRowsOrphansRequest request)
        {
            var orphans = Orphans(new EmbyJson(_jsonSerializer), new ServerLog(_logger))
                .FindOrphans(Exists, Cfg(), Plugin.Instance.Configuration.GetHomeRowSettings());
            return new HomeRowsOrphansView
            {
                Count = orphans.Count,
                Users = orphans.Select(o => o.UserId).Distinct().Count(),
                Rows = orphans.Select(o => $"{o.User}: {o.Name ?? "(no name)"}").ToList(),
            };
        }

        // Under the work lock, like SwitchOff, so a sync never re-points a row while we decide it is dead. Nothing
        // is saved: only untracked rows are removed, so home-rows.json does not change.
        public object Post(HomeRowsClearOrphansRequest request)
        {
            if (!Plugin.WorkLock.Wait(0))
                throw new InvalidOperationException("A collection sync is running right now. Try again when it has finished.");
            try
            {
                var json = new EmbyJson(_jsonSerializer);
                using (var log = new SyncLog(_logger, Path.Combine(DataDir, "logs")))
                {
                    var removed = Orphans(json, log).ClearOrphans(Exists, Cfg(), Plugin.Instance.Configuration.GetHomeRowSettings());
                    return new HomeRowsSwitchOffResult { Removed = removed };
                }
            }
            finally
            {
                Plugin.WorkLock.Release();
            }
        }

        HomeRowSync Orphans(EmbyJson json, ISyncLog log)
        {
            var state = SyncCollectionsTask.LoadJson<HomeRowsState>(json, Path.Combine(DataDir, "home-rows.json")).Normalize();
            return new HomeRowSync(new EmbyHomeScreens(_users, _localization, json, Path.Combine(DataDir, "backups")), state, log);
        }

        // The config for FindOrphans' "keep what the next run needs" rules: only while home rows are on (null = off).
        // A broken saved text is refused rather than treated as off: that would clear rows a fixed text would keep.
        CollectionsConfig Cfg()
        {
            var conf = Plugin.Instance.Configuration;
            if (!conf.ManageHomeRows)
                return null;
            try
            {
                return ConfigParser.Parse(conf.CollectionsText);
            }
            catch (ConfigException e)
            {
                throw new InvalidOperationException("The saved collections text has an error, fix it first: " + e.Message);
            }
        }

        bool Exists(long id) => _library.GetItemById(id) != null;
    }
}
