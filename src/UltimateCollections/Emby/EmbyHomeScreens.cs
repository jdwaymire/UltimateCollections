// EmbyHomeScreens.cs
//
// IHomeScreens on Emby's IUserManager home section calls (Emby 4.10+). The only file that touches users' home
// screens, so an Emby change there should break here. Before the first change to a user's home screen each day, the
// whole section list is saved to backups/home-sections-<user>-<date>.json.
//
// NOTE: AddHomeSection returns nothing and Emby makes the new row's Id itself (ours is refused, tested 2026-10-12
// over REST), so Add reads the rows before and after and takes the one new Id. Sections are per user and keyed by
// the user's internal id; we keep the GUID string as our user key and look the internal id up here.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Querying;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    public class EmbyHomeScreens : IHomeScreens
    {
        readonly IUserManager _users;
        readonly IJson _json;
        readonly string _backupDir;
        readonly Dictionary<string, (long id, string name)> _known = new Dictionary<string, (long, string)>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _backedUp = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public EmbyHomeScreens(IUserManager users, IJson json, string backupDir)
        {
            _users = users;
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

        // The user's sections as Emby has them, in home screen order. A user who never changed their home screen still
        // gets Emby's defaults here; null is treated as none, to be safe.
        ContentSection[] Raw(string userId) =>
            _users.GetHomeSections(Internal(userId), CancellationToken.None)?.Sections ?? new ContentSection[0];

        public List<HomeSection> Get(string userId) =>
            Raw(userId).Select(x => new HomeSection
            {
                Id = x.Id,
                Name = x.Name,
                SectionType = x.SectionType,
                ParentId = x.ParentId,
                ViewType = x.ViewType,
            }).ToList();

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
            _users.MoveHomeSections(Internal(userId), new[] { sectionId }, newIndex, CancellationToken.None);
        }
    }
}
