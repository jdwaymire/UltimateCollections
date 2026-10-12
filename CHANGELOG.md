# Changelog

## 1.1.5 (2026-10-12)

### Custom images: use your own poster or backdrop for any collection

- New **Custom images...** button on the settings page: upload, replace or remove a poster and a backdrop for each
  collection (JPEG or PNG, up to 10 MB). The popup shows each image cropped the way Emby's apps show it, says how
  much is cut off, and shows the whole picture on hover or tap.
- Collections without a custom image show their current artwork, or a preview when they are not built yet (out of
  season, or new).
- Images live in the plugin's `images/` folder, named after the collection (for example `Halloween.jpg` and
  `Halloween backdrop.png`). Names are matched loosely, so files can also be copied in by hand.
- The sync uses a custom image instead of drawing one, uploads it again when the file changes, and keeps the current
  image if a file is unusable. `posters no` still applies custom images.

### Other changes

- Off-season collections now keep their list up to date, so their posters can be previewed and they appear from a
  ready list on the first day of their season.

## 1.0.0 (2026-10-09)

First public release.
