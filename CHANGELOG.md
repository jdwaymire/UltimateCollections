# Changelog

## 1.2.0 (2026-10-12)

### Home screen rows: collections on your users' home screens

- New **Home screen rows** section on the settings page (Emby 4.10 or newer). **Off by default**, so nothing changes
  until you switch it on; if you already use another plugin for home screens, let only one of them manage collection
  rows.
- Seasonal collections get a row automatically: Halloween appears on the home screen when its season starts and
  hides when it ends. Other collections get one with the new key `home_screen yes`; `home_screen no` keeps one off.
- Choose which users get the rows (all users, or a list), the style (a row of posters, or a big **spotlight** banner;
  per collection with `home_screen spotlight`), and where new rows go (after Continue Watching, at the top, or at the
  bottom).
- Rows stay put all year: out of season they are empty and hidden, and next season they come back in the same spot,
  so users can arrange their home screen and keep it that way. A row a user removes stays removed.
- Only one spotlight shows at a time; when two spotlight collections overlap, the first one in the settings keeps it.
  Optionally, a seasonal spotlight moves to the top when its season starts (the full-screen banner in the web app).
- Rows already on a home screen for one of the plugin's collections (made by hand, or copied with Emby's *Copy Data
  to Users*) are taken over instead of doubled. Rows for anything else are never touched.
- A **Rows right now** table shows each collection's row (showing, hidden until a date, or none) and how many users
  have it.
- Each user's home screen is backed up before the plugin first changes it on a day. Switching the feature off asks
  whether to remove the plugin's rows or leave them.

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
