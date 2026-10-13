// HomeRowsApi.cs
//
// The two REST routes behind the settings page's "Home screen rows" section: the "Rows right now" table, and
// switching the feature off (remove our rows from every home screen, or leave them). The rules are in
// Core/HomeRows.cs.
//
// NOTE: both routes are admin only. SwitchOff also saves ManageHomeRows = false itself, so the setting and the
// home screens never disagree (a page left unsaved would otherwise let the next run add every row back).

using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
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

    public class HomeRowsApi : IService
    {
        readonly IUserManager _users;
        readonly ILogger _logger;
        readonly IJsonSerializer _jsonSerializer;

        public HomeRowsApi(IUserManager users, ILogManager logManager, IJsonSerializer jsonSerializer)
        {
            _users = users;
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
                    var sync = new HomeRowSync(new EmbyHomeScreens(_users, json, Path.Combine(DataDir, "backups")), state, log);
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
    }
}
