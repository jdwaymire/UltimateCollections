// Plugin.cs
//
// The plugin class Emby loads (name, GUID, settings page, picture on the Plugins page) and the settings it saves.
// Everything that does real work lives in Core/ and Emby/; this file only tells Emby what we are.
//
// NOTE: the GUID is also hard-coded in Web/configPage.js (pluginId). Change one and the settings page stops loading
// and saving the configuration. The page names in GetPages() must match the data-controller in configPage.html too.

using System;
using System.Collections.Generic;
using System.IO;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using UltimateCollections.Core;

namespace UltimateCollections
{
    public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages, IHasThumbImage
    {
        public static Plugin Instance { get; private set; }

        // Held by a sync run and by "Delete All Collections", so the two never run at the same time. A sync waits
        // for it; the delete button does not wait and is refused instead (see CollectionsApi).
        public static readonly System.Threading.SemaphoreSlim WorkLock = new System.Threading.SemaphoreSlim(1, 1);

        public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer) : base(applicationPaths, xmlSerializer)
        {
            Instance = this;
        }

        public override string Name => "Ultimate Collections";

        public override string Description =>
            "Builds and maintains collections from TMDb keywords, release years, public MDBList lists and library titles.";

        public override Guid Id => new Guid("ec206e22-423b-4c99-8507-75c7b6c119e5");

        // The picture on Emby's Plugins page: Images/thumb.png, an embedded resource.
        public Stream GetThumbImage() => GetType().Assembly.GetManifestResourceStream(GetType().Namespace + ".Images.thumb.png");

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        // The settings page is two embedded files. The HTML page loads the script as "__plugin/UltimateCollectionsJS",
        // which is the Name given here.
        public IEnumerable<PluginPageInfo> GetPages() => new[]
        {
            new PluginPageInfo
            {
                Name = "UltimateCollections",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
                IsMainConfigPage = true,
            },
            new PluginPageInfo
            {
                Name = "UltimateCollectionsJS",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.js",
            },
        };
    }

    // The settings. Emby saves them as XML in plugins/configurations/UltimateCollections.xml. The property names are
    // also the keys configPage.js reads and writes, so renaming one loses the saved value.
    public class PluginConfiguration : BasePluginConfiguration
    {
        // TMDb v4 read access token.
        public string TmdbToken { get; set; } = "";

        // The collection definitions (the collections.conf text from the settings page). A fresh install starts with
        // one small sample collection so the page is not an empty box; dry run is on, so it changes nothing until
        // the user turns that off. Existing installs keep their saved text: Emby only uses this default when there is
        // no saved configuration yet.
        public string CollectionsText { get; set; } = SampleCollections;

        // Both keywords checked on themoviedb.org 2026-10-09: "hacker" (2157) and "computer hacker" (303918).
        public const string SampleCollections =
            "# Ultimate Collections: one [collection NAME] block per collection.\n" +
            "# The guide with explained examples: https://github.com/jdwaymire/UltimateCollections\n" +
            "# This sample is here to get you started: change it, add your own, or delete it.\n" +
            "\n" +
            "[global]\n" +
            "min_items          2\n" +
            "max_pages          100\n" +
            "list_refresh_days  7\n" +
            "\n" +
            "# Every hacker movie you own, all year round. Try the Poster style button to change its look.\n" +
            "[collection Hacker Movies]\n" +
            "tmdb_keyword  hacker\n" +
            "tmdb_keyword  computer hacker\n" +
            "poster_title  HACKER|MOVIES\n" +
            "poster_accent 60,230,120\n" +
            "poster_tint   5,25,10\n";

        // Log what would change, change nothing. On by default until the plugin is trusted.
        public bool DryRun { get; set; } = true;

        // Log the matching titles of every collection.
        public bool ShowTitles { get; set; }

        // Treat every collection as in season (only honoured in dry run).
        public bool IgnoreSeason { get; set; }

        // Download all lists again on the next run; the sync task switches it off again after that run.
        public bool RefreshNextRun { get; set; }

        // Comma-separated collection names: sync only these (empty = all). Handy while building one collection.
        public string OnlyCollections { get; set; } = "";

        // ---- home screen rows (Core/HomeRows.cs) ----

        // Off by default, on fresh installs and upgrades alike: the server may already use another plugin for home
        // screens (decided 2026-10-12). Switching it off goes through HomeRowsApi, which removes our rows first.
        public bool ManageHomeRows { get; set; }

        // "all", or comma-separated Emby user ids.
        public string HomeRowUsers { get; set; } = "all";

        // cards | spotlight
        public string HomeRowStyle { get; set; } = "cards";

        // after (Continue Watching) | top | bottom; only where a new row goes.
        public string HomeRowPosition { get; set; } = "after";

        // Seasonal collections get a row without home_screen yes.
        public bool HomeRowSeasonal { get; set; } = true;

        // Move a seasonal spotlight to the top when its season starts (full-screen banner in the web app).
        public bool HomeRowSpotlightTop { get; set; }

        public HomeRowSettings GetHomeRowSettings() =>
            HomeRowSettings.From(HomeRowUsers, HomeRowStyle, HomeRowPosition, HomeRowSeasonal, HomeRowSpotlightTop);
    }
}
