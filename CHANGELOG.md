# Changelog

## 1.3.0 (2026-10-15)

### A new settings page: status at a glance, tabs, run history

- **Status cards** at the top: *Collections* (how many are in Emby now, how many are configured, which seasonal one
  appears next; click for a list of every collection with its source, title count and season), *Home rows* (on or
  off, how many users, what is showing) and *Last run* (when, what it changed, errors). A line next to the title says
  how the last run went, and **Dry run** shows in orange there and on the card, so it is hard to leave on by accident.
- **Tabs** instead of one long form: **Settings** (opens first), **Images**, **Home Screen**, **Clean-up** and
  **History**. The page follows Emby's theme (light or dark) and fits Emby's settings page width.
- **Run now** on the Settings tab starts the sync task straight away (the same task as under Scheduled Tasks), and the
  page shows its progress and result when it finishes.
- **History** tab: every run (the last 200), quiet nights included, with when it ran, how it was started (scheduled,
  Run now, or by hand from Scheduled Tasks) and what it changed ("Halloween created with N titles", "Time Travel +2
  -0 titles"). Click a run for the details; *Changes only* hides the quiet ones. Kept in `history.json`.
- **Settings** tab: the TMDb token shows as one line (*Token saved* / *Change*) once it works, and opens again with a
  warning when TMDb refused it. The collections text is now called **Configuration text** and counts its collections.
  **Ignore seasons** is greyed out unless **Dry run** is on (it only ever worked in a dry run). *Log the matching
  titles* is now **Debug - Log Matching Titles**, at the bottom of the run options.
- **Images** tab replaces the *Custom images* popup: every collection as a poster tagged *Drawn* or *Override*; click
  one for its poster and backdrop, **Poster style...**, **Override Poster...** and **Override Backdrop...**. Previews
  of collections that are not built yet are drawn one at a time, only for the posters on screen.
- **Home Screen** tab (was the *Home screen rows* section): **Manage Home Screen** switch, users picked with name
  chips, Cards/Spotlight and where new rows go as buttons, and the rows list.
- **Clean-up** tab: *Delete unused images*, *Clear orphaned rows* and the Danger zone (*Delete All Collections*,
  folded closed) in one place.
- **Unsaved changes** shows next to Save while something on the Settings or Home Screen tab is not saved yet.
- Nothing changes in how collections are built; existing settings carry over as they are.

## 1.2.2 (2026-10-14)

### Fix: new users lost Emby's default home screen rows

Fixes [#1](https://github.com/jdwaymire/UltimateCollections/issues/1).

- A user who had never changed their home screen (for example a newly created user) ended up with **only the
  seasonal row** after the task ran: Emby's own rows (My Media, Continue Watching, Latest ...) disappeared. The plugin
  now saves Emby's default rows first, the way Emby's own home screen editor does, and adds its row after Continue
  Watching as intended.
- Users already affected are repaired on the next run: their home screen goes back to Emby's defaults and the
  collection rows are added again. Each user's home screen is backed up first.

## 1.2.1 (2026-10-14)

### Clean-up buttons and a clearer TMDb token error

- **Delete unused images** in the *Custom images* popup: shows how many image files in the plugin's `images/` folder
  no collection uses (left over from a renamed or removed collection, or extra copies of the same poster), lists
  them, and deletes them with one button.
- **Clear orphaned rows** in the *Home screen rows* section: counts and lists collection rows on your users' home
  screens whose collection no longer exists, and removes them with one button. Emby hides these empty rows, but they
  stay in each user's home screen settings and an empty first row stops a spotlight from showing full-screen. Works
  whether or not the plugin manages home screen rows; while it does, its own rows waiting for next season are kept.
  Each user's home screen is backed up first.
- **Clearer TMDb error:** a refused token now says *"TMDb refused the token (HTTP 401 Unauthorized)"* and explains
  to use the **API Read Access Token**, not the shorter API Key. After the first refusal the run stops asking TMDb
  and ends with one clear error line. The token field also warns when what you pasted looks like the API Key.

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
