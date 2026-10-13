/*
 * configPage.js
 *
 * Controller for the plugin's settings page (configPage.html). Loads and saves PluginConfiguration, runs the
 * "Poster style" popup (colour pickers with a server-drawn preview that write the poster_* lines into the text), the
 * "Custom images" popup (upload/remove the user's own posters and backdrops), the "Home screen rows" section (users,
 * the rows table, the switch-off confirm box) and drives the "Delete All Collections" danger zone.
 *
 * NOTE: pluginId must match the GUID in Plugin.cs, and the keys of `fields` must match the PluginConfiguration
 * property names exactly. A misspelt key fails quietly: that setting just never loads or saves.
 */
define(['baseView', 'loading', 'emby-input', 'emby-button', 'emby-checkbox', 'emby-textarea', 'emby-select', 'emby-radio'], function (BaseView, loading) {
    'use strict';

    var pluginId = 'ec206e22-423b-4c99-8507-75c7b6c119e5';

    // Setting name -> [element id, property to read/write]. Text inputs use value, checkboxes use checked.
    var fields = {
        TmdbToken: ['ucTmdbToken', 'value'],
        CollectionsText: ['ucCollectionsText', 'value'],
        OnlyCollections: ['ucOnly', 'value'],
        DryRun: ['ucDryRun', 'checked'],
        ShowTitles: ['ucShowTitles', 'checked'],
        IgnoreSeason: ['ucIgnoreSeason', 'checked'],
        RefreshNextRun: ['ucRefreshNextRun', 'checked'],
        ManageHomeRows: ['ucManageHomeRows', 'checked'],
        HomeRowStyle: ['ucHomeRowStyle', 'value'],
        HomeRowPosition: ['ucHomeRowPosition', 'value'],
        HomeRowSeasonal: ['ucHomeRowSeasonal', 'checked'],
        HomeRowSpotlightTop: ['ucHomeRowSpotlightTop', 'checked']
    };
    // HomeRowUsers is not in `fields`: it is the radio pair plus the user checklist (see readUsers / showUsers).

    var confirmWord = 'DELETE';

    // Fills in how many collections a delete would remove, so the user sees the size of it before typing DELETE.
    function showCount(view) {
        var el = view.querySelector('.ucCollectionCount');
        ApiClient.getJSON(ApiClient.getUrl('UltimateCollections/CollectionsSummary')).then(function (s) {
            el.textContent = s.Total === 0
                ? 'There are no collections in Emby right now.'
                : 'This would delete ' + s.Total + ' collection' + (s.Total === 1 ? '' : 's') +
                  ' (' + s.MadeByPlugin + ' made by this plugin, ' + (s.Total - s.MadeByPlugin) + ' made some other way).';
        }, function () {
            el.textContent = 'Could not count the collections.';
        });
    }

    // ---- poster style popup ----
    //
    // A helper for the poster_* keys so nobody has to type r,g,b numbers: pick a collection, pick colours (or a
    // ready-made style), see the real poster, press Apply. The popup only edits the text box; Save still saves.
    // The preview is drawn by the server (UltimateCollections/PosterPreview) with the sync's own renderer and the
    // collection's own movies, so what you see is what the next sync makes.

    // Same colours as the collections the plugin ships with.
    var presets = [
        { name: 'Pumpkin', accent: '255,135,20', tint: '40,15,50' },
        { name: 'Christmas', accent: '230,60,60', tint: '20,70,35' },
        { name: 'Ice', accent: '150,205,255', tint: '20,45,80' },
        { name: 'Neon', accent: '255,60,200', tint: '30,10,70' },
        { name: 'Sci-fi', accent: '90,220,255', tint: '10,30,70' },
        { name: 'Jungle', accent: '170,210,70', tint: '35,50,15' },
        { name: 'Valentine', accent: '255,90,140', tint: '80,10,35' },
        { name: 'Gold', accent: '240,195,40', tint: '10,20,60' },
        { name: 'Old film', accent: '225,190,120', tint: '60,45,20', sepia: true }
    ];

    var styleKeys = ['poster_title', 'poster_accent', 'poster_tint', 'poster_sepia'];

    function rgbToHex(rgb) {
        return '#' + rgb.split(',').map(function (v) {
            return ('0' + Math.max(0, Math.min(255, parseInt(v, 10) || 0)).toString(16)).slice(-2);
        }).join('');
    }

    function hexToRgb(hex) {
        return [1, 3, 5].map(function (i) { return parseInt(hex.substr(i, 2), 16); }).join(',');
    }

    // Rough brightness (0-1) of an r,g,b string, for the "hard to read" warnings.
    function brightness(rgb) {
        var c = rgb.split(',').map(Number);
        return (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]) / 255;
    }

    // The [collection ...] sections of the settings text, with the line number of each poster key. Mirrors the
    // server's parser closely enough for this: a ' #' starts a comment, the first word is the key.
    function readSections(text) {
        var out = [], cur = null;
        text.split('\n').forEach(function (line, i) {
            var m = line.match(/^\s*\[(\w+)(?:\s+(.+?))?\]\s*$/);
            if (m) {
                cur = m[1] === 'collection' && m[2] ? { name: m[2].trim(), end: i, keys: {} } : null;
                if (cur) {
                    out.push(cur);
                }
                return;
            }
            var t = line.replace(/\s+#.*$/, '').trim();
            if (!cur || !t || t.charAt(0) === '#') {
                return;
            }
            cur.end = i;
            var sp = t.search(/\s/);
            cur.keys[sp < 0 ? t : t.slice(0, sp)] = { value: sp < 0 ? '' : t.slice(sp + 1).trim(), line: i };
        });
        return out;
    }

    // Replaces the section's poster lines with the new ones, placed after its last setting. Comments and every
    // other line are left exactly as they were.
    function writeStyle(text, name, lines) {
        var section = readSections(text).filter(function (s) { return s.name === name; })[0];
        if (!section) {
            return text;
        }
        var drop = {};
        styleKeys.forEach(function (k) {
            if (section.keys[k]) {
                drop[section.keys[k].line] = true;
            }
        });
        var out = [];
        text.split('\n').forEach(function (line, i) {
            if (!drop[i]) {
                out.push(line);
            }
            if (i === section.end) {
                lines.forEach(function (l) { out.push(l); });
            }
        });
        return out.join('\n');
    }

    function keyLine(key, value) {
        return (key + '              ').slice(0, 14) + value;
    }

    function escapeHtml(s) {
        return String(s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
    }

    var dialogHtml =
        '<style>' +
        '.ucStyleDlg .formDialogContent{padding:1em 1.5em;overflow-y:auto}' +
        '.ucStyleGrid{display:flex;flex-wrap:wrap;gap:2em;align-items:flex-start}' +
        '.ucStyleControls{flex:1 1 22em;min-width:0;display:flex;flex-direction:column;gap:1.1em}' +
        '.ucStylePreview{flex:0 0 260px;margin:0 auto;text-align:center}' +
        '.ucStylePreview img{width:260px;max-width:100%;aspect-ratio:2/3;object-fit:cover;border-radius:4px;box-shadow:0 4px 18px rgba(0,0,0,.35);background:#222;transition:opacity .2s}' +
        '.ucStylePreview .ucBusy{opacity:.45}' +
        '.ucStyleLabel{display:block;margin-bottom:.4em;font-weight:500}' +
        '.ucStyleSelect{width:100%;padding:.5em;font:inherit;color:inherit;background:transparent;border:1px solid rgba(128,128,128,.5);border-radius:4px}' +
        '.ucStyleSelect option{color:#000}' +
        '.ucStyleText{width:100%;box-sizing:border-box;padding:.5em;font:inherit;color:inherit;background:transparent;border:1px solid rgba(128,128,128,.5);border-radius:4px}' +
        '.ucPresets{display:flex;flex-wrap:wrap;gap:.5em}' +
        '.ucPreset{display:inline-flex;align-items:center;gap:.5em;font:inherit;font-size:.9em;color:inherit;background:transparent;border:1px solid rgba(128,128,128,.5);border-radius:2em;padding:.3em .8em .3em .3em;cursor:pointer}' +
        '.ucPreset:hover{border-color:#52b54b}' +
        '.ucSwatch{width:1.4em;height:1.4em;border-radius:50%;box-shadow:inset 0 0 0 1px rgba(0,0,0,.2)}' +
        '.ucColors{display:flex;flex-wrap:wrap;gap:1.5em}' +
        '.ucColor{flex:1 1 12em}' +
        '.ucColorRow{display:flex;align-items:center;gap:.7em}' +
        '.ucColorRow input[type=color]{width:3em;height:2.2em;padding:2px;border:1px solid rgba(128,128,128,.5);border-radius:4px;background:transparent;cursor:pointer}' +
        '.ucColorRow code{opacity:.75}' +
        '.ucHint{font-size:.85em;opacity:.75;margin-top:.3em;line-height:1.4}' +
        '.ucLink{font:inherit;font-size:1em;color:#52b54b;background:none;border:0;padding:0;cursor:pointer;text-decoration:underline}' +
        '.ucWarn{color:#e09a2b;font-size:.9em;min-height:1.2em}' +
        '.ucLines{font-family:monospace;font-size:.85em;white-space:pre;overflow-x:auto;padding:.6em .8em;border:1px solid rgba(128,128,128,.4);border-radius:4px}' +
        '.ucSource{font-size:.85em;opacity:.75;margin-top:.6em;line-height:1.4}' +
        '.ucStyleDlg .formDialogFooter{display:flex;justify-content:flex-end;gap:.8em;padding:1em 1.5em}' +
        '</style>' +
        '<div class="formDialogHeader">' +
        '  <button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1" title="Close"><i class="md-icon">close</i></button>' +
        '  <h3 class="formDialogHeaderTitle">Poster style</h3>' +
        '</div>' +
        '<div class="formDialogContent">' +
        '  <p class="ucHint" style="margin-top:0">Pick a collection and its colours. <b>Apply</b> writes the lines into the collections text; press <b>Save</b> on the page afterwards as usual.</p>' +
        '  <div class="ucStyleGrid">' +
        '    <div class="ucStyleControls">' +
        '      <div><label class="ucStyleLabel" for="ucStyleCollection">Collection</label><select id="ucStyleCollection" class="ucStyleSelect"></select></div>' +
        '      <div><span class="ucStyleLabel">Ready-made styles</span><div class="ucPresets"></div></div>' +
        '      <div class="ucColors">' +
        '        <div class="ucColor"><label class="ucStyleLabel" for="ucStyleAccent">Accent colour</label>' +
        '          <div class="ucColorRow"><input type="color" id="ucStyleAccent"><code class="ucAccentText"></code></div>' +
        '          <div class="ucHint">The bars and the top title line. Pick something bright.</div></div>' +
        '        <div class="ucColor"><label class="ucStyleLabel" for="ucStyleTint">Tint colour</label>' +
        '          <div class="ucColorRow"><input type="color" id="ucStyleTint"><code class="ucTintText"></code></div>' +
        '          <div class="ucHint">Washes over the whole picture. Dark works best. <button type="button" class="ucLink ucMatchTint">Match the accent</button></div></div>' +
        '      </div>' +
        '      <div><label class="ucStyleLabel" for="ucStyleTitle">Title on the poster</label><input type="text" id="ucStyleTitle" class="ucStyleText" autocomplete="off">' +
        '        <div class="ucHint">Use | to start a new line, e.g. TIME|TRAVEL. Leave empty to use the collection name.</div></div>' +
        '      <label style="display:flex;align-items:center;gap:.6em"><input type="checkbox" id="ucStyleSepia"> Old-photo look (sepia)</label>' +
        '      <div class="ucWarn" role="status"></div>' +
        '      <div><span class="ucStyleLabel">Lines that will be written</span><div class="ucLines"></div></div>' +
        '    </div>' +
        '    <div class="ucStylePreview"><img alt="Poster preview"><div class="ucSource"></div></div>' +
        '  </div>' +
        '</div>' +
        '<div class="formDialogFooter">' +
        '  <button is="emby-button" type="button" class="raised button-cancel ucCancel"><span>Cancel</span></button>' +
        '  <button is="emby-button" type="button" class="raised button-submit ucApply"><span>Apply</span></button>' +
        '</div>';

    // Opens the popup on the collection the text cursor is in (or the first one). Emby's dialogHelper and toast are
    // loaded only here, so the settings page itself does not depend on them.
    function openPosterStyle(view) {
        var textArea = view.querySelector('#ucCollectionsText');
        var sections = readSections(textArea.value);
        if (!sections.length) {
            require(['toast'], function (toast) {
                (toast.default || toast)('Add a [collection NAME] section to the collections text first.');
            });
            return;
        }
        require(['dialogHelper', 'toast'], function (dialogHelper, toast) {
            dialogHelper = dialogHelper.default || dialogHelper;
            toast = toast.default || toast;

            var dlg = dialogHelper.createDialog({ size: 'medium', removeOnClose: true, scrollY: false });
            dlg.classList.add('formDialog', 'ucStyleDlg');
            dlg.innerHTML = dialogHtml;

            var q = function (sel) { return dlg.querySelector(sel); };
            var select = q('#ucStyleCollection');
            var accent = q('#ucStyleAccent'), tint = q('#ucStyleTint'), title = q('#ucStyleTitle'), sepia = q('#ucStyleSepia');
            var img = q('.ucStylePreview img');
            var state = { accent: '255,135,20', tint: '40,15,50' };
            var seq = 0, timer = null;

            sections.forEach(function (s) {
                var o = document.createElement('option');
                o.value = o.textContent = s.name;
                select.appendChild(o);
            });
            // Up to the end of the cursor's line, so a cursor on the [collection ...] line itself counts too.
            var lineEnd = textArea.value.indexOf('\n', textArea.selectionStart);
            var before = textArea.value.slice(0, lineEnd < 0 ? textArea.value.length : lineEnd).split('\n').reverse()
                .filter(function (l) { return /^\s*\[collection\s+.+\]\s*$/.test(l); })[0];
            if (before) {
                select.value = before.trim().replace(/^\[collection\s+/, '').replace(/\]$/, '').trim();
            }
            if (!select.value) {
                select.selectedIndex = 0;
            }

            presets.forEach(function (p) {
                var b = document.createElement('button');
                b.type = 'button';
                b.className = 'ucPreset';
                b.innerHTML = '<span class="ucSwatch" style="background:linear-gradient(135deg,' + rgbToHex(p.accent) + ' 50%,' +
                    rgbToHex(p.tint) + ' 50%)"></span>' + escapeHtml(p.name);
                b.addEventListener('click', function () {
                    state.accent = p.accent;
                    state.tint = p.tint;
                    sepia.checked = !!p.sepia;
                    show();
                });
                q('.ucPresets').appendChild(b);
            });

            function wantedLines() {
                var lines = [];
                if (title.value.trim()) {
                    lines.push(keyLine('poster_title', title.value.trim()));
                }
                lines.push(keyLine('poster_accent', state.accent));
                lines.push(keyLine('poster_tint', state.tint));
                if (sepia.checked) {
                    lines.push(keyLine('poster_sepia', 'yes'));
                }
                return lines;
            }

            // Puts the current values into the controls, then asks for a new preview (debounced, so dragging a
            // colour around does not fire a request per pixel; only the newest answer is shown).
            function show(noPreview) {
                accent.value = rgbToHex(state.accent);
                tint.value = rgbToHex(state.tint);
                q('.ucAccentText').textContent = state.accent;
                q('.ucTintText').textContent = state.tint;
                var warn = [];
                if (brightness(state.accent) < 0.25) {
                    warn.push('The accent is quite dark; the bars and title may be hard to see.');
                }
                if (brightness(state.tint) > 0.55) {
                    warn.push('The tint is quite light; the picture will look washed out behind the title.');
                }
                q('.ucWarn').textContent = warn.join(' ');
                q('.ucLines').textContent = wantedLines().join('\n');
                clearTimeout(timer);
                timer = setTimeout(function () {
                    preview({ Name: select.value, Title: title.value, Accent: state.accent, Tint: state.tint, Sepia: sepia.checked ? 'yes' : '' });
                }, 300);
            }

            // Asks the server to draw the poster. With fill=true the answer's style (defaults filled in) is put into
            // the controls; that is how a collection without poster_accent shows the colour it gets today.
            function preview(body, fill) {
                var my = ++seq;
                img.classList.add('ucBusy');
                return ApiClient.ajax({
                    type: 'POST',
                    url: ApiClient.getUrl('UltimateCollections/PosterPreview'),
                    data: JSON.stringify(body),
                    contentType: 'application/json',
                    dataType: 'json'
                }).then(function (r) {
                    if (my !== seq) {
                        return;
                    }
                    img.classList.remove('ucBusy');
                    if (r.Image) {
                        img.src = r.Image;
                    } else {
                        img.removeAttribute('src');
                    }
                    q('.ucSource').textContent = 'Made from ' + r.Source + '.';
                    title.placeholder = r.Title;
                    if (fill) {
                        state.accent = r.Accent;
                        state.tint = r.Tint;
                        show(true);
                    }
                }, function () {
                    if (my === seq) {
                        img.classList.remove('ucBusy');
                        q('.ucSource').textContent = 'The preview could not be made; check the colours, or see the server log.';
                    }
                });
            }

            // Loads a collection's current poster lines from the text and lets the server fill in the defaults.
            function load() {
                var s = readSections(textArea.value).filter(function (x) { return x.name === select.value; })[0];
                var k = (s && s.keys) || {};
                var val = function (key) { return k[key] ? k[key].value : ''; };
                title.value = val('poster_title');
                sepia.checked = !!k.poster_sepia && !/^(no|false|off|0)$/i.test(val('poster_sepia'));
                preview({ Name: select.value, Title: val('poster_title'), Accent: val('poster_accent'), Tint: val('poster_tint'), Sepia: val('poster_sepia') }, true);
            }

            select.addEventListener('change', load);
            accent.addEventListener('input', function () { state.accent = hexToRgb(accent.value); show(); });
            tint.addEventListener('input', function () { state.tint = hexToRgb(tint.value); show(); });
            title.addEventListener('input', show);
            sepia.addEventListener('change', show);
            // The plugin's own default tint: the accent at a quarter brightness.
            q('.ucMatchTint').addEventListener('click', function () {
                state.tint = state.accent.split(',').map(function (v) { return Math.floor(v * 0.25); }).join(',');
                show();
            });
            q('.btnCancel').addEventListener('click', function () { dialogHelper.close(dlg); });
            q('.ucCancel').addEventListener('click', function () { dialogHelper.close(dlg); });
            q('.ucApply').addEventListener('click', function () {
                textArea.value = writeStyle(textArea.value, select.value, wantedLines());
                dialogHelper.close(dlg);
                toast(select.value + ' updated. Press Save to keep it.');
            });

            dialogHelper.open(dlg);
            load();
        });
    }

    // ---- custom images popup ----
    //
    // The user's own poster or backdrop for a collection, instead of the generated one. One row per collection in
    // the text box (saved or not), each with a poster slot (2:3) and a backdrop slot (16:9). A slot shows the image
    // cropped the way Emby's apps fill that shape, says how much gets cut off, and shows the whole picture on hover
    // or tap. Upload/Remove go straight to the server (UltimateCollections/CustomImages/*); the images are put on
    // the collections by the next sync, so nothing needs saving here.

    var maxUpload = 10 * 1024 * 1024;   // same limit as CustomImages.MaxBytes on the server

    var customHtml =
        '<style>' +
        // NOTE: Emby's dialog footer sits over the bottom of the content, so the content gets room below the last row
        // (found 2026-10-12: the last collection's Upload button was hidden behind Close).
        '.ucImgDlg .formDialogContent{padding:1em 1.5em 6em;overflow-y:auto}' +
        '.ucImgRow{display:flex;flex-wrap:wrap;gap:1.5em;align-items:flex-start;padding:1.1em 0;border-bottom:1px solid rgba(128,128,128,.25)}' +
        '.ucImgRow:last-child{border-bottom:0}' +
        '.ucImgName{flex:1 0 100%;font-weight:500;font-size:1.05em}' +
        '.ucSlot{display:flex;flex-direction:column;gap:.45em}' +
        '.ucSlotPoster{width:140px}' +
        '.ucSlotBackdrop{width:280px;max-width:100%}' +
        '.ucSlotLabel{font-size:.8em;text-transform:uppercase;letter-spacing:.06em;opacity:.65}' +
        '.ucFrame{position:relative;width:100%;border-radius:4px;overflow:hidden;background:#1c1c1c;cursor:default}' +
        '.ucSlotPoster .ucFrame{aspect-ratio:2/3}' +
        '.ucSlotBackdrop .ucFrame{aspect-ratio:16/9}' +
        '.ucFrame img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}' +
        '.ucFrame.ucHasImg:hover img,.ucFrame.ucShowAll img{object-fit:contain}' +
        '.ucFrame.ucHasImg{cursor:zoom-in}' +
        '.ucBadge{position:absolute;left:.4em;top:.4em;z-index:1;font-size:.68em;letter-spacing:.06em;text-transform:uppercase;padding:.15em .55em;border-radius:3px;background:rgba(0,0,0,.7);color:#fff}' +
        '.ucBadgeCustom{background:#52b54b}' +
        '.ucEmpty{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;text-align:center;padding:.6em;font-size:.85em;color:#bbb;border:2px dashed rgba(160,160,160,.5);border-radius:4px}' +
        '.ucBad{position:absolute;inset:0;display:flex;align-items:center;justify-content:center;text-align:center;padding:.6em;font-size:.82em;color:#e09a2b;border:2px solid #e09a2b;border-radius:4px}' +
        '.ucMeta{font-size:.8em;opacity:.75;line-height:1.35;min-height:2.7em}' +
        '.ucCut{color:#e09a2b;opacity:1}' +
        '.ucSlotButtons{display:flex;gap:.5em;flex-wrap:wrap}' +
        '.ucSmallBtn{font:inherit;font-size:.85em;color:inherit;background:transparent;border:1px solid rgba(128,128,128,.55);border-radius:3px;padding:.3em .8em;cursor:pointer}' +
        '.ucSmallBtn:hover{border-color:#52b54b}' +
        '.ucSmallBtn[disabled]{opacity:.4;cursor:default}' +
        '.ucImgDlg .formDialogFooter{display:flex;justify-content:flex-end;gap:.8em;padding:1em 1.5em}' +
        '</style>' +
        '<div class="formDialogHeader">' +
        '  <button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1" title="Close"><i class="md-icon">close</i></button>' +
        '  <h3 class="formDialogHeaderTitle">Custom images</h3>' +
        '</div>' +
        '<div class="formDialogContent">' +
        '  <p class="ucHint" style="margin-top:0">Your own poster or backdrop replaces the generated one for that collection. ' +
        '  JPEG or PNG, up to 10 MB; best at 1000x1500 for posters and 1920x1080 for backdrops. The frames show how Emby\'s apps ' +
        '  crop the picture; hover or tap one to see the whole image. Changes reach the collections on the next sync ' +
        '  (Scheduled Tasks &gt; Sync collections &gt; Run now). Remove a custom image to go back to the generated one.</p>' +
        '  <div class="ucImgRows">Loading...</div>' +
        '</div>' +
        '<div class="formDialogFooter">' +
        '  <button is="emby-button" type="button" class="raised button-cancel ucCancel"><span>Close</span></button>' +
        '</div>';

    // How much of the picture Emby cuts off to fill the frame (centre crop), as a sentence; '' when it fits.
    function cropNote(w, h, frameW, frameH) {
        var a = w / h, r = frameW / frameH;
        if (Math.abs(a - r) / r < 0.02) {
            return '';
        }
        var cut = a > r ? 1 - r / a : 1 - a / r;
        var pct = Math.round(cut * 50);
        if (pct < 1) {
            return '';
        }
        return a > r ? 'About ' + pct + '% is cut off on the left and right.' : 'About ' + pct + '% is cut off at the top and bottom.';
    }

    function sizeText(bytes) {
        return bytes >= 1024 * 1024 ? (bytes / 1024 / 1024).toFixed(1) + ' MB' : Math.max(1, Math.round(bytes / 1024)) + ' KB';
    }

    // Emby's ApiClient rejects with the fetch Response; the server's message (e.g. "x is not an image") is its body.
    function errorText(err) {
        if (err && typeof err.text === 'function') {
            return err.text().then(function (t) { return t || 'The request failed.'; }, function () { return 'The request failed.'; });
        }
        return Promise.resolve('The request failed.');
    }

    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(path),
            data: JSON.stringify(body),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    function openCustomImages(view) {
        var textArea = view.querySelector('#ucCollectionsText');
        var sections = readSections(textArea.value);
        if (!sections.length) {
            require(['toast'], function (toast) {
                (toast.default || toast)('Add a [collection NAME] section to the collections text first.');
            });
            return;
        }
        require(['dialogHelper', 'toast'], function (dialogHelper, toast) {
            dialogHelper = dialogHelper.default || dialogHelper;
            toast = toast.default || toast;

            var dlg = dialogHelper.createDialog({ size: 'medium', removeOnClose: true, scrollY: false });
            dlg.classList.add('formDialog', 'ucImgDlg');
            dlg.innerHTML = customHtml;
            var rows = dlg.querySelector('.ucImgRows');

            // One hidden file picker for every slot; `target` says which slot it is for.
            var picker = document.createElement('input');
            picker.type = 'file';
            picker.accept = 'image/jpeg,image/png';
            picker.style.display = 'none';
            dlg.appendChild(picker);
            var target = null;

            // What each collection has in Emby now (CurrentPoster/CurrentBackdrop from the list), kept so a slot can show
            // it again after its custom image is removed.
            var current = {};

            // Collections that are not built yet (out of season, or new) have no image in Emby, so we ask the Poster
            // style preview to draw one: same renderer and colours the sync will use, nothing saved. One request at a
            // time, so opening the popup does not make the server draw a dozen posters at once.
            var previews = Promise.resolve();
            function queuePreview(slotEl, name, backdrop) {
                var s = readSections(textArea.value).filter(function (x) { return x.name === name; })[0];
                var k = (s && s.keys) || {};
                var val = function (key) { return k[key] ? k[key].value : ''; };
                previews = previews.then(function () {
                    return postJson('UltimateCollections/PosterPreview', {
                        Name: name, Title: val('poster_title'), Accent: val('poster_accent'), Tint: val('poster_tint'),
                        Sepia: val('poster_sepia'), Backdrop: backdrop
                    }).then(function (r) {
                        var empty = slotEl.querySelector('.ucEmpty');
                        if (!r.Image || !empty) {
                            return;     // nothing to draw from, or the slot changed meanwhile (an upload)
                        }
                        current[name + (backdrop ? '|b' : '|p')] = r.Image;
                        current[name + (backdrop ? '|b' : '|p') + 'src'] = r.Source;
                        renderSlot(slotEl, name, backdrop, null);
                    }, function () { /* keep the empty frame */ });
                });
            }

            // Draws one slot from the server's answer (img = CustomImageView or null). Without a custom image the slot
            // shows the collection's current poster/backdrop in Emby, so it is never just an empty frame.
            function renderSlot(slotEl, name, backdrop, img) {
                var now = current[name + (backdrop ? '|b' : '|p')];
                var previewOf = current[name + (backdrop ? '|b' : '|p') + 'src'];
                var frameW = backdrop ? 16 : 2, frameH = backdrop ? 9 : 3;
                var html = '<span class="ucSlotLabel">' + (backdrop ? 'Backdrop' : 'Poster') + '</span>';
                var meta = '';
                if (!img && now && previewOf) {
                    html += '<div class="ucFrame ucHasImg" title="Hover or tap to see the whole picture"><span class="ucBadge">Preview</span><img alt=""></div>';
                    meta = 'Not built yet. Made from ' + escapeHtml(previewOf) + '.';
                } else if (!img && now) {
                    html += '<div class="ucFrame ucHasImg" title="Hover or tap to see the whole picture"><span class="ucBadge">Generated</span><img alt=""></div>';
                    meta = 'Made by the plugin. Upload your own to replace it.';
                } else if (!img) {
                    html += '<div class="ucFrame"><div class="ucEmpty">Making a preview...</div></div>';
                    meta = 'The collection is not built yet (out of season, or new).';
                    queuePreview(slotEl, name, backdrop);
                } else if (img.Error) {
                    html += '<div class="ucFrame"><div class="ucBad">' + escapeHtml(img.Error) + '. Not used.</div></div>';
                    meta = escapeHtml(img.FileName);
                } else {
                    html += '<div class="ucFrame ucHasImg" title="Hover or tap to see the whole picture"><span class="ucBadge ucBadgeCustom">Custom</span><img alt=""></div>';
                    var cut = cropNote(img.Width, img.Height, frameW, frameH);
                    meta = escapeHtml(img.FileName) + '<br>' + img.Width + 'x' + img.Height + ', ' + sizeText(img.Bytes) +
                        (cut ? '<br><span class="ucCut">' + cut + '</span>' : '');
                }
                if (img && img.Others) {
                    meta += '<br><span class="ucCut">' + img.Others + ' more file(s) match; this one is used.</span>';
                }
                html += '<div class="ucMeta">' + meta + '</div><div class="ucSlotButtons">' +
                    '<button type="button" class="ucSmallBtn ucUp">' + (img ? 'Replace' : 'Upload') + '</button>' +
                    (img ? '<button type="button" class="ucSmallBtn ucRemove">Remove</button>' : '') + '</div>';
                slotEl.innerHTML = html;
                var thumb = img ? img.Thumb : now;
                if (thumb) {
                    slotEl.querySelector('img').src = thumb;
                    // Tap toggles the whole picture on touch screens, where there is no hover.
                    slotEl.querySelector('.ucFrame').addEventListener('click', function () { this.classList.toggle('ucShowAll'); });
                }
                slotEl.querySelector('.ucUp').addEventListener('click', function () {
                    target = { el: slotEl, name: name, backdrop: backdrop };
                    picker.value = '';
                    picker.click();
                });
                if (img) {
                    slotEl.querySelector('.ucRemove').addEventListener('click', function () {
                        busy(slotEl, true);
                        postJson('UltimateCollections/CustomImages/Delete', { Name: name, Backdrop: backdrop }).then(function (r) {
                            renderSlot(slotEl, name, backdrop, backdrop ? r.Backdrop : r.Poster);
                            toast(name + ': custom ' + (backdrop ? 'backdrop' : 'poster') + ' removed. The generated one comes back on the next sync.');
                        }, function (err) {
                            busy(slotEl, false);
                            errorText(err).then(toast);
                        });
                    });
                }
            }

            function busy(slotEl, on) {
                Array.prototype.forEach.call(slotEl.querySelectorAll('button'), function (b) { b.disabled = on; });
                slotEl.style.opacity = on ? 0.5 : '';
            }

            // Upload: checked here for size first (no point sending 40 MB to be refused), then sent as base64; the
            // server checks it again properly (real format, dimensions).
            picker.addEventListener('change', function () {
                var file = picker.files && picker.files[0];
                var t = target;
                if (!file || !t) {
                    return;
                }
                if (file.size > maxUpload) {
                    toast(file.name + ' is ' + sizeText(file.size) + '; the limit is 10 MB.');
                    return;
                }
                busy(t.el, true);
                var reader = new FileReader();
                reader.onload = function () {
                    var data = String(reader.result).replace(/^data:[^,]*,/, '');
                    postJson('UltimateCollections/CustomImages/Upload', { Name: t.name, Backdrop: t.backdrop, Data: data }).then(function (r) {
                        renderSlot(t.el, t.name, t.backdrop, t.backdrop ? r.Backdrop : r.Poster);
                        toast(t.name + ': custom ' + (t.backdrop ? 'backdrop' : 'poster') + ' saved. It is put on the collection on the next sync.');
                    }, function (err) {
                        busy(t.el, false);
                        errorText(err).then(toast);
                    });
                };
                reader.onerror = function () {
                    busy(t.el, false);
                    toast('Could not read ' + file.name + '.');
                };
                reader.readAsDataURL(file);
            });

            dlg.querySelector('.btnCancel').addEventListener('click', function () { dialogHelper.close(dlg); });
            dlg.querySelector('.ucCancel').addEventListener('click', function () { dialogHelper.close(dlg); });
            dialogHelper.open(dlg);

            var names = sections.map(function (s) { return s.name; });
            postJson('UltimateCollections/CustomImages/List', { Names: names.join('\n') }).then(function (list) {
                var byName = {};
                (list || []).forEach(function (x) { byName[x.Name] = x; });
                rows.innerHTML = '';
                names.forEach(function (name) {
                    var row = document.createElement('div');
                    row.className = 'ucImgRow';
                    row.innerHTML = '<div class="ucImgName"></div><div class="ucSlot ucSlotPoster"></div><div class="ucSlot ucSlotBackdrop"></div>';
                    row.querySelector('.ucImgName').textContent = name;
                    var x = byName[name] || {};
                    current[name + '|p'] = x.CurrentPoster;
                    current[name + '|b'] = x.CurrentBackdrop;
                    renderSlot(row.querySelector('.ucSlotPoster'), name, false, x.Poster);
                    renderSlot(row.querySelector('.ucSlotBackdrop'), name, true, x.Backdrop);
                    rows.appendChild(row);
                });
            }, function (err) {
                errorText(err).then(function (t) { rows.textContent = 'Could not load the custom images: ' + t; });
            });
        });
    }

    // ---- home screen rows ----
    //
    // The options only show while "Manage home screen rows" is ticked. HomeRowUsers is "all" or a comma-separated
    // list of user ids, from the radio pair and the checklist. The table comes from the server (saved settings and
    // home-rows.json), so it shows what the next run works from, not the unsaved page.
    // NOTE: switching it off is not a plain Save: if it was on, the confirm box asks whether to remove our rows and
    // the server switches it off itself (HomeRowsApi), so the setting and the home screens never disagree.

    function showHomeOptions(view) {
        var on = view.querySelector('#ucManageHomeRows').checked;
        view.querySelector('.ucHomeOptions').style.display = on ? '' : 'none';
        var chosen = view.querySelector('#ucHomeUsersChosen').checked;
        view.querySelector('.ucUserPick').style.display = chosen ? '' : 'none';
    }

    // Fills the user checklist and the radio pair from the saved HomeRowUsers value.
    function showUsers(view, saved) {
        var list = view.querySelector('.ucUserList');
        var ids = (saved || 'all').split(',').map(function (x) { return x.trim(); }).filter(function (x) { return x; });
        var all = !ids.length || ids.some(function (x) { return x.toLowerCase() === 'all'; });
        view.querySelector('#ucHomeUsersAll').checked = all;
        view.querySelector('#ucHomeUsersChosen').checked = !all;
        showHomeOptions(view);
        return ApiClient.getUsers().then(function (users) {
            list.innerHTML = '';
            users.forEach(function (u) {
                var label = document.createElement('label');
                label.style.display = 'block';
                label.innerHTML = '<input is="emby-checkbox" type="checkbox" class="ucUserBox"><span></span>';
                label.querySelector('input').value = u.Id;
                label.querySelector('input').checked = !all && ids.indexOf(u.Id) >= 0;
                label.querySelector('span').textContent = u.Name + (u.Policy && u.Policy.IsAdministrator ? ' (admin)' : '');
                list.appendChild(label);
            });
        }, function () {
            list.textContent = 'Could not load the users.';
        });
    }

    function readUsers(view) {
        if (view.querySelector('#ucHomeUsersAll').checked) {
            return 'all';
        }
        return Array.prototype.filter.call(view.querySelectorAll('.ucUserBox'), function (b) { return b.checked; })
            .map(function (b) { return b.value; }).join(',');
    }

    // The "Rows right now" table. lastRows keeps the answer for the switch-off box (how many rows it would remove).
    var lastRows = null;
    function loadHomeTable(view) {
        var box = view.querySelector('.ucHomeTable');
        var notes = view.querySelector('.ucHomeNotes');
        ApiClient.getJSON(ApiClient.getUrl('UltimateCollections/HomeRows')).then(function (r) {
            lastRows = r;
            notes.innerHTML = '';
            if (r.Error) {
                box.textContent = r.Error;
                return;
            }
            var th = 'style="text-align:left;padding:.45em .8em;font-weight:500;border-bottom:1px solid rgba(128,128,128,.35)"';
            var td = 'style="padding:.45em .8em;border-bottom:1px solid rgba(128,128,128,.15)"';
            var html = '<table style="width:100%;min-width:36em;border-collapse:collapse;font-size:.92em"><thead><tr>' +
                '<th ' + th + '>Collection</th><th ' + th + '>Season</th><th ' + th + '>Row</th><th ' + th + '>Style</th><th ' + th + '>Users</th>' +
                '</tr></thead><tbody>';
            (r.Rows || []).forEach(function (x) {
                var pill = x.Status === 'Showing' ? 'background:#52b54b;color:#fff' :
                    x.Status === 'No row' ? 'opacity:.6' : 'background:rgba(128,128,128,.25)';
                var users = x.Status === 'No row' ? '' : x.Users + (x.Removed ? ' (' + x.Removed + ' removed it)' : '');
                html += '<tr><td ' + td + '><b>' + escapeHtml(x.Name) + '</b></td><td ' + td + '>' + escapeHtml(x.Season) + '</td>' +
                    '<td ' + td + '><span style="display:inline-block;padding:.1em .7em;border-radius:1em;' + pill + '">' + escapeHtml(x.Status) + '</span></td>' +
                    '<td ' + td + '>' + escapeHtml(x.Style) + '</td><td ' + td + '>' + escapeHtml(users) + '</td></tr>';
            });
            box.innerHTML = html + '</tbody></table>';
            (r.Notes || []).forEach(function (n) {
                var p = document.createElement('p');
                p.style.cssText = 'margin:.6em 0 0;padding:.5em .8em;border-radius:4px;background:rgba(224,154,43,.15);color:#e09a2b;font-size:.9em';
                p.textContent = n;
                notes.appendChild(p);
            });
        }, function () {
            box.textContent = 'Could not load the home screen rows.';
        });
    }

    var switchOffHtml =
        '<div class="formDialogHeader">' +
        '  <button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1" title="Close"><i class="md-icon">close</i></button>' +
        '  <h3 class="formDialogHeaderTitle">Stop managing home screen rows?</h3>' +
        '</div>' +
        '<div class="formDialogContent" style="padding:1em 1.5em">' +
        '  <p class="ucOffCount"></p>' +
        '  <label class="radio-label-block"><input type="radio" is="emby-radio" name="ucOff" value="remove" checked><span><b>Remove the rows the plugin added</b></span></label>' +
        '  <div class="fieldDescription" style="margin:0 0 1em 2.2em">Rows users made themselves, or that another plugin added, are not touched. ' +
        '  A backup of each user\'s home screen is saved first (backups/ in the plugin\'s data folder).</div>' +
        '  <label class="radio-label-block"><input type="radio" is="emby-radio" name="ucOff" value="leave"><span><b>Leave them as they are</b></span></label>' +
        '  <div class="fieldDescription" style="margin:0 0 1em 2.2em">They stop following the seasons and may point to old collections.</div>' +
        '</div>' +
        '<div class="formDialogFooter" style="display:flex;justify-content:flex-end;gap:.8em;padding:1em 1.5em">' +
        '  <button is="emby-button" type="button" class="raised button-cancel ucCancel"><span>Cancel</span></button>' +
        '  <button is="emby-button" type="button" class="raised button-submit ucOff"><span>Switch off</span></button>' +
        '</div>';

    // Cancel (or closing the box) ticks the setting again. The server does the rest and saves the setting off.
    function openSwitchOff(view, done) {
        var box = view.querySelector('#ucManageHomeRows');
        require(['dialogHelper', 'toast'], function (dialogHelper, toast) {
            dialogHelper = dialogHelper.default || dialogHelper;
            toast = toast.default || toast;
            var dlg = dialogHelper.createDialog({ size: 'small', removeOnClose: true, scrollY: false });
            dlg.classList.add('formDialog');
            dlg.innerHTML = switchOffHtml;
            var rows = 0, users = lastRows ? lastRows.UsersWithRows : 0;
            ((lastRows && lastRows.Rows) || []).forEach(function (x) { rows += x.Users; });
            dlg.querySelector('.ucOffCount').textContent = rows
                ? 'The plugin manages ' + rows + ' row' + (rows === 1 ? '' : 's') + ' on ' + users + ' user' + (users === 1 ? "'s" : "s'") + ' home screens.'
                : 'The plugin has no rows on any home screen right now.';
            var switched = false;
            function close() { dialogHelper.close(dlg); }
            dlg.querySelector('.btnCancel').addEventListener('click', close);
            dlg.querySelector('.ucCancel').addEventListener('click', close);
            dlg.addEventListener('close', function () {
                if (!switched) {
                    box.checked = true;
                    showHomeOptions(view);
                }
            });
            dlg.querySelector('.ucOff').addEventListener('click', function () {
                var remove = dlg.querySelector('input[name=ucOff]:checked').value === 'remove';
                loading.show();
                postJson('UltimateCollections/HomeRows/SwitchOff', { RemoveRows: remove }).then(function (r) {
                    loading.hide();
                    switched = true;
                    close();
                    done();
                    toast(remove ? 'Home screen rows switched off; ' + r.Removed + ' row(s) removed.' : 'Home screen rows switched off; the rows were left as they are.');
                    loadHomeTable(view);
                }, function (err) {
                    loading.hide();
                    errorText(err).then(toast);
                });
            });
            dialogHelper.open(dlg);
        });
    }


    function View(view, params) {
        BaseView.apply(this, arguments);

        view.querySelector('#ucPosterStyle').addEventListener('click', function () {
            openPosterStyle(view);
        });
        view.querySelector('#ucCustomImages').addEventListener('click', function () {
            openCustomImages(view);
        });

        // homeSaved = whether rows are on in the SAVED settings; only then does unticking need the confirm box.
        var self = this;
        self.homeSaved = false;
        var homeBox = view.querySelector('#ucManageHomeRows');
        homeBox.addEventListener('change', function () {
            showHomeOptions(view);
            if (!homeBox.checked && self.homeSaved) {
                openSwitchOff(view, function () { self.homeSaved = false; });
            }
        });
        view.querySelector('#ucHomeUsersAll').addEventListener('change', function () { showHomeOptions(view); });
        view.querySelector('#ucHomeUsersChosen').addEventListener('change', function () { showHomeOptions(view); });
        view.querySelector('.ucUsersAll').addEventListener('click', function () {
            Array.prototype.forEach.call(view.querySelectorAll('.ucUserBox'), function (b) { b.checked = true; });
        });
        view.querySelector('.ucUsersNone').addEventListener('click', function () {
            Array.prototype.forEach.call(view.querySelectorAll('.ucUserBox'), function (b) { b.checked = false; });
        });

        var confirmInput = view.querySelector('#ucDeleteConfirm');
        var deleteButton = view.querySelector('#ucDeleteAll');
        var result = view.querySelector('.ucDeleteResult');

        // The button only unlocks for the exact word; the server checks it again.
        confirmInput.addEventListener('input', function () {
            deleteButton.disabled = confirmInput.value !== confirmWord;
        });

        // The word box is cleared at once, so the button cannot be pressed twice by accident. Any failure (including
        // "a sync is running", which the server refuses) gets the same message; the plugin log has the details.
        deleteButton.addEventListener('click', function () {
            if (confirmInput.value !== confirmWord) {
                return;
            }
            deleteButton.disabled = true;
            confirmInput.value = '';
            result.textContent = 'Deleting collections...';
            loading.show();
            ApiClient.ajax({
                type: 'POST',
                url: ApiClient.getUrl('UltimateCollections/DeleteAllCollections'),
                data: JSON.stringify({ Confirm: confirmWord }),
                contentType: 'application/json',
                dataType: 'json'
            }).then(function (r) {
                loading.hide();
                result.textContent = 'Done: ' + r.Deleted + ' of ' + r.Found + ' collections deleted' +
                    (r.Failed ? ', ' + r.Failed + ' could not be deleted (see the plugin log)' : '') +
                    '. Backup of what existed: ' + r.BackupFile;
                showCount(view);
            }, function () {
                loading.hide();
                result.textContent = 'Nothing was deleted or the request failed (for example while a sync is running). ' +
                    'See the plugin log for details.';
                showCount(view);
            });
        });

        // Save: fetch the current configuration first and only overwrite our fields, so anything else Emby keeps in it
        // (or a setting the page does not show) survives.
        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                Object.keys(fields).forEach(function (key) {
                    config[key] = view.querySelector('#' + fields[key][0])[fields[key][1]];
                });
                config.HomeRowUsers = readUsers(view);
                return ApiClient.updatePluginConfiguration(pluginId, config);
            }).then(function (result) {
                self.homeSaved = view.querySelector('#ucManageHomeRows').checked;
                Dashboard.processPluginConfigurationUpdateResult(result);
                loadHomeTable(view);
            });
            return false;
        });
    }

    Object.assign(View.prototype, BaseView.prototype);

    // Runs every time the page is shown: refresh the count, lock the delete button again, load the settings.
    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        var view = this.view;
        showCount(view);
        view.querySelector('#ucDeleteConfirm').value = '';
        view.querySelector('#ucDeleteAll').disabled = true;
        loading.show();
        var self = this;
        ApiClient.getPluginConfiguration(pluginId).then(function (config) {
            Object.keys(fields).forEach(function (key) {
                var v = config[key];
                view.querySelector('#' + fields[key][0])[fields[key][1]] = fields[key][1] === 'checked' ? !!v : (v || '');
            });
            // Selects need a value they have; an old or empty setting falls back to the first option.
            ['#ucHomeRowStyle', '#ucHomeRowPosition'].forEach(function (sel) {
                var el = view.querySelector(sel);
                if (!el.value) {
                    el.selectedIndex = 0;
                }
            });
            self.homeSaved = !!config.ManageHomeRows;
            showUsers(view, config.HomeRowUsers);
            loadHomeTable(view);
            loading.hide();
        });
    };

    return View;
});
