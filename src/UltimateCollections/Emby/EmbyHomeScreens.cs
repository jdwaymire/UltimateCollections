// EmbyHomeScreens.cs
//
// IHomeScreens on Emby's IUserManager home section calls (Emby 4.10+). The only file that touches users' home
// screens, so an Emby change there should break here. Before the first change to a user's home screen each day, the
// whole section list is saved to backups/home-sections-<user>-<date>.json.
//
// NOTE: AddHomeSection returns nothing and Emby makes the new row's Id itself (ours is refused, tested 2026-10-12
// over REST), so Add reads the rows before and after and takes the one new Id. Sections are per user and keyed by
// the user's internal id; we keep the GUID string as our user key and look the internal id up here.
//
// NOTE: a user who never saved a home screen has NO stored sections (GetHomeSections gives an empty list). Emby's
// REST layer (Emby.Api ContentService, 4.10.1) shows them its "legacy" defaults instead, and before its first save it
// stores those defaults (EnsureSavedSections). IUserManager.AddHomeSection does not, so adding to such a user left
// only our row (found 2026-10-14 on a new user). Legacy() below copies Emby's edit-mode defaults: Get shows them for
// such a user and EnsureSaved stores them before our first change, the way Emby's own editor does.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.Querying;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    public class EmbyHomeScreens : IHomeScreens
    {
        readonly IUserManager _users;
        readonly ILocalizationManager _localization;
        readonly IJson _json;
        readonly string _backupDir;
        readonly Dictionary<string, (long id, string name)> _known = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public EmbyHomeScreens(IUserManager users, ILocalizationManager localization, IJson json, string backupDir)
        {
            _users = users;
            _localization = localization;
            _json = json;
            _backupDir = backupDir;
        }

        // Every user, disabled ones too (a disabled user can be enabled again and should find their rows). The internal
        // ids are remembered here, so the calls below don't look each user up again.
        public List<HomeUser> GetUsers()
        {
            var list = new List<HomeUser>();
            foreach (var u in _users.GetUserList(new UserQuery()))
            {
                var id = u.Id.ToString("N");
                _known[id] = (u.InternalId, u.Name);
                list.Add(new HomeUser { Id = id, Name = u.Name });
            }
            return list;
        }

        // Our user key (GUID string) -> the internal id the home section calls want.
        long Internal(string userId)
        {
            if (_known.TryGetValue(userId, out var k))
                return k.id;
            var u = _users.GetUserById(userId) ?? throw new InvalidOperationException($"user {userId} not found");
            _known[userId] = (u.InternalId, u.Name);
            return u.InternalId;
        }

        // The user's STORED sections, in home screen order: empty for a user who never saved a home screen (see the
        // NOTE at the top); null is treated as none, to be safe.
        ContentSection[] Raw(string userId) =>
            _users.GetHomeSections(Internal(userId), CancellationToken.None)?.Sections ?? new ContentSection[0];

        // What the user sees: the stored sections, or Emby's defaults when nothing is stored.
        public List<HomeSection> Get(string userId)
        {
            var raw = Raw(userId);
            return (raw.Length > 0 ? raw : Legacy(userId).ToArray()).Select(x => new HomeSection
            {
                Id = x.Id,
                Name = x.Name,
                SectionType = x.SectionType,
                ParentId = x.ParentId,
                ViewType = x.ViewType,
            }).ToList();
        }

        // Emby's defaults for a user with no stored sections, as Emby.Api's GetLegacyHomeSections + ExpandLegacySection
        // build them in edit mode (what EnsureSavedSections stores): eight slots from the user's settings
        // homesection0..7, falling back to My Media, Continue Watching, Continue Listening, On Now, -, Latest Media.
        List<ContentSection> Legacy(string userId)
        {
            var user = _users.GetUserById(userId) ?? throw new InvalidOperationException($"user {userId} not found");
            var settings = _users.GetUserSettings(user.InternalId, CancellationToken.None);
            var cfg = user.Configuration;
            var types = new List<string>();
            for (var i = 0; i <= 7; i++)
            {
                if (settings == null || !settings.TryGetValue("homesection" + i, out var t))
                    t = i == 0 ? "smalllibrarytiles" : i == 1 ? "resume" : i == 2 ? "resumeaudio" : i == 3 ? "livetv" : i == 5 ? "latestmediablock" : "none";
                types.Add(t == "latestmedia" ? "latestmediablock" : t == "livetv" ? "onnow" : t);
            }
            var list = new List<ContentSection>();
            foreach (var type in types)
            {
                var sec = new ContentSection { Id = type, SectionType = type, ExcludedFolders = new string[0], ItemTypes = new string[0], IncludeNextUpInResume = true };
                string name = null;
                switch (type)
                {
                    case "latestmediablock":
                        sec.ExcludedFolders = cfg.LatestItemsExcludes;
                        if (cfg.HidePlayedInLatest)
                            sec.Query.IsPlayed = false;
                        name = "HeaderLatestMedia";
                        break;
                    case "smalllibrarytiles":
                    case "librarybuttons":
                        sec.SectionType = "userviews";
                        sec.CardSizeOffset = type == "smalllibrarytiles" ? -1 : 0;
                        sec.ViewType = type == "librarybuttons" ? "buttons" : null;
                        sec.ExcludedFolders = cfg.MyMediaExcludes;
                        name = "HeaderMyMedia";
                        break;
                    case "resume":
                        sec.ExcludedFolders = cfg.LatestItemsExcludes;
                        sec.IncludeNextUpInResume = !types.Contains("nextup", StringComparer.OrdinalIgnoreCase);
                        name = "HeaderContinueWatching";
                        break;
                    case "resumeaudio":
                        sec.ExcludedFolders = cfg.LatestItemsExcludes;
                        name = "HeaderContinueListening";
                        break;
                    case "latestmoviereleases":
                        sec.ExcludedFolders = cfg.LatestItemsExcludes;
                        sec.ItemTypes = new[] { "Movie" };
                        name = "RecentlyReleasedMovies";
                        break;
                    case "latestepisodereleases":
                        sec.ExcludedFolders = cfg.LatestItemsExcludes;
                        sec.ItemTypes = new[] { "Episode" };
                        name = "RecentlyReleasedEpisodes";
                        break;
                    case "onnow":
                        if (!user.Policy.EnableLiveTvAccess)
                            continue;
                        name = "HeaderOnNow";
                        break;
                    case "nextup":
                        sec.SectionType = "resume";
                        sec.CustomName = _localization.GetLocalizedString("HeaderNextUp");
                        sec.ItemTypes = new[] { "Episode" };
                        break;
                    case "playlists":
                        sec.ItemTypes = new[] { "Playlist" };
                        name = "Playlists";
                        break;
                    case "collections":
                        sec.ItemTypes = new[] { "BoxSet" };
                        name = "Collections";
                        break;
                    case "activerecordings":
                        name = "HeaderActiveRecordings";
                        break;
                    case "studio": case "boxset": case "container": case "genre": case "items": case "playlist": case "tag":
                        break;
                    default:    // "none" and anything Emby does not know
                        continue;
                }
                sec.Name = sec.CustomName ?? (name == null ? null : _localization.GetLocalizedString(name));
                list.Add(sec);
            }
            return list;
        }

        // Stores Emby's defaults for a user who has none stored yet, before our first change (see the NOTE at the top).
        void EnsureSaved(string userId)
        {
            if (Raw(userId).Length > 0)
                return;
            foreach (var sec in Legacy(userId))
                _users.AddHomeSection(Internal(userId), sec, CancellationToken.None);
        }

        // Once per user per run, and only one file per user per day (the first state of the day is the useful one).
        void Backup(string userId)
        {
            if (!_backedUp.Add(userId))
                return;
            Directory.CreateDirectory(_backupDir);
            var name = _known.TryGetValue(userId, out var k) ? k.name : userId;
            var safe = new string(name.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) || ch == ' ' ? '_' : ch).ToArray());
            var path = Path.Combine(_backupDir, $"home-sections-{safe}-{DateTime.Now:yyyy-MM-dd}.json");
            if (!File.Exists(path))
                File.WriteAllText(path, _json.Serialize(Raw(userId)));
        }

        // Emby adds the row at the bottom and gives back nothing, so the new Id is the one that was not there before
        // (see the NOTE at the top; works on 4.10.1, first live run 2026-10-12). The empty arrays match what Emby's own
        // editor saves for a new row.
        public string Add(string userId, HomeSection section)
        {
            Backup(userId);
            EnsureSaved(userId);
            var before = new HashSet<string>(Raw(userId).Select(x => x.Id));
            _users.AddHomeSection(Internal(userId), new ContentSection
            {
                Name = section.Name,
                SectionType = section.SectionType,
                ParentId = section.ParentId,
                ViewType = section.ViewType,
                Monitor = new string[0],
                ItemTypes = new string[0],
                ExcludedFolders = new string[0],
            }, CancellationToken.None);
            var added = Raw(userId).Where(x => !before.Contains(x.Id)).Select(x => x.Id).ToList();
            if (added.Count != 1)
                throw new InvalidOperationException($"could not tell which row Emby added ({added.Count} new rows)");
            return added[0];
        }

        // Emby's defaults again for a user whose stored list is only our rows (left before 1.2.2, see the NOTE at the
        // top): with nothing stored Emby shows its defaults (checked live 2026-10-14). Returns the list afterwards.
        public List<HomeSection> ResetToDefaults(string userId)
        {
            Backup(userId);
            var ids = Raw(userId).Select(x => x.Id).ToArray();
            if (ids.Length > 0)
                _users.DeleteHomeSections(Internal(userId), ids, CancellationToken.None);
            return Get(userId);
        }

        // Emby wants the whole section back, so we change our two fields on the one it has and send that.
        public void Update(string userId, string sectionId, string parentId, string viewType)
        {
            Backup(userId);
            var sec = Raw(userId).FirstOrDefault(x => x.Id == sectionId) ?? throw new InvalidOperationException($"row {sectionId} not found");
            sec.ParentId = parentId;
            sec.ViewType = viewType;
            sec.ParentItem = null;
            _users.UpdateHomeSection(Internal(userId), sec, CancellationToken.None);
        }

        public void Delete(string userId, string[] sectionIds)
        {
            Backup(userId);
            _users.DeleteHomeSections(Internal(userId), sectionIds, CancellationToken.None);
        }

        public void Move(string userId, string sectionId, int newIndex)
        {
            Backup(userId);
            EnsureSaved(userId);
            _users.MoveHomeSections(Internal(userId), new[] { sectionId }, newIndex, CancellationToken.None);
        }
    }
}
