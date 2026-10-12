// EmbyArtworkTarget.cs
//
// The Emby side of posters and backdrops: hands the renderer the members' local image files and saves the finished
// image (our JPEG, or the user's own JPEG/PNG from images/) on the collection with IProviderManager.SaveImage (the
// same call Emby uses when an image is uploaded in the web app).
//
// NOTE: SaveImage alone is not enough. It writes the file and the item in memory, but the database (and so every
// client) still shows no image until UpdateToRepository(ImageUpdate) runs. That was the v0.2.0 bug, fixed in 0.2.1.

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.IO;
using UltimateCollections.Core;

namespace UltimateCollections.Emby
{
    public class EmbyArtworkTarget : IArtworkTarget
    {
        readonly ILibraryManager _library;
        readonly IProviderManager _providers;
        readonly IFileSystem _fileSystem;

        public EmbyArtworkTarget(ILibraryManager library, IProviderManager providers, IFileSystem fileSystem)
        {
            _library = library;
            _providers = providers;
            _fileSystem = fileSystem;
        }

        // Only local files are used: the renderer reads the file straight from disk. A remote or missing image gives
        // null and that member is skipped.
        public string GetImagePath(long itemId, bool backdrop)
        {
            var info = _library.GetItemById(itemId)?.GetImageInfo(backdrop ? ImageType.Backdrop : ImageType.Primary, 0);
            return info != null && info.IsLocalFile && !string.IsNullOrEmpty(info.Path) && File.Exists(info.Path) ? info.Path : null;
        }

        public bool HasImage(long collectionId, bool backdrop) =>
            _library.GetItemById(collectionId)?.HasImage(backdrop ? ImageType.Backdrop : ImageType.Primary, 0) == true;

        // Index 0 replaces the existing image: for backdrops it overwrites fanart.jpg instead of adding a second
        // backdrop (verified on Emby 4.10.1).
        // We read the image back at the end so a save that silently did nothing shows up as a warning in the log.
        public async Task SetImageAsync(long collectionId, bool backdrop, byte[] data, string mimeType, CancellationToken ct)
        {
            var item = _library.GetItemById(collectionId) ?? throw new InvalidOperationException($"collection {collectionId} not found");
            var type = backdrop ? ImageType.Backdrop : ImageType.Primary;
            using (var ms = new MemoryStream(data))
                await _providers.SaveImage(item, _library.GetLibraryOptions(item), ms, mimeType.AsMemory(), type, 0,
                    Array.Empty<long>(), new DirectoryService(_fileSystem), true, ct).ConfigureAwait(false);
            // See the NOTE at the top (seen on 4.10.1, 2026-10-09). Emby's own upload endpoint does the same.
            item.UpdateToRepository(ItemUpdateType.ImageUpdate);
            if (!HasImage(collectionId, backdrop))
                throw new InvalidOperationException($"{type} image not on the collection after saving");
        }
    }
}
