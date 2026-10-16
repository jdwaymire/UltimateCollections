/*
 * configPage.js
 *
 * Controller for the plugin's settings page (configPage.html, 1.3 layout): the status cards and the Collections popup,
 * the tabs (Settings, Images, Home Screen, Clean-up, History), Save with the "Unsaved changes" marker, Run now, the
 * "Poster style" popup (colour pickers with a server-drawn preview that write the poster_* lines into the text), the
 * Images tab (override pictures), the home screen rows (users, the rows list, the switch-off confirm box), the
 * clean-up buttons and the "Delete All Collections" danger zone.
 *
 * NOTE: pluginId must match the GUID in Plugin.cs, and the keys of `fields` must match the PluginConfiguration
 * property names exactly. A misspelt key fails quietly: that setting just never loads or saves.
 * NOTE: Emby keeps several copies of this page in the DOM; everything here works inside its own `view` only.
 */
define(['baseView', 'loading', 'emby-button'], function (BaseView, loading) {
    'use strict';

    var pluginId = 'ec206e22-423b-4c99-8507-75c7b6c119e5';

    // Setting name -> [element id, property to read/write]. Text inputs use value, checkboxes use checked.
    // HomeRowUsers, HomeRowStyle and HomeRowPosition are not in here: they are the user chips and two radio groups.
    var fields = {
        TmdbToken: ['ucTmdbToken', 'value'],
        CollectionsText: ['ucCollectionsText', 'value'],
        OnlyCollections: ['ucOnly', 'value'],
        DryRun: ['ucDryRun', 'checked'],
        ShowTitles: ['ucShowTitles', 'checked'],
        IgnoreSeason: ['ucIgnoreSeason', 'checked'],
        RefreshNextRun: ['ucRefreshNextRun', 'checked'],
        ManageHomeRows: ['ucManageHomeRows', 'checked'],
        HomeRowSeasonal: ['ucHomeRowSeasonal', 'checked'],
        HomeRowSpotlightTop: ['ucHomeRowSpotlightTop', 'checked']
    };

    var confirmWord = 'DELETE';
    var gridFirst = 8;                     // posters shown before "Show all"
    var maxUpload = 10 * 1024 * 1024;      // same limit as CustomImages.MaxBytes on the server

    // ---- small helpers ----

    function escapeHtml(s) {
        return String(s == null ? '' : s).replace(/[&<>"]/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]; });
    }

    function toast(text) {
        require(['toast'], function (t) { (t.default || t)(text); });
    }

    function getJson(path) {
        return ApiClient.getJSON(ApiClient.getUrl(path));
    }

    function postJson(path, body) {
        return ApiClient.ajax({
            type: 'POST',
            url: ApiClient.getUrl(path),
            data: JSON.stringify(body || {}),
            contentType: 'application/json',
            dataType: 'json'
        });
    }

    // Emby's ApiClient rejects with the fetch Response; the server's message (e.g. "x is not an image") is its body.
    function errorText(err) {
        if (err && typeof err.text === 'function') {
            return err.text().then(function (t) { return t || 'The request failed.'; }, function () { return 'The request failed.'; });
        }
        return Promise.resolve('The request failed.');
    }

    function plural(n, one, many) {
        return n + ' ' + (n === 1 ? one : (many || one + 's'));
    }

    var months = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];

    // "2026-10-15 05:00" (server local time) -> "Today, 05:00", "Yesterday, 05:00" or "Oct 13, 05:00".
    function friendlyDate(s) {
        var m = /^(\d{4})-(\d\d)-(\d\d) (\d\d:\d\d)$/.exec(s || '');
        if (!m) {
            return s || '';
        }
        var d = new Date(+m[1], +m[2] - 1, +m[3]);
        var today = new Date();
        today.setHours(0, 0, 0, 0);
        var days = Math.round((today - d) / 86400000);
        var day = days === 0 ? 'Today' : days === 1 ? 'Yesterday' : months[d.getMonth()] + ' ' + d.getDate();
        return day + ', ' + m[4];
    }

    var kindNames = { scheduled: 'Scheduled', page: 'Run now', manual: 'Manual' };

    var icons = {
        collections: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><rect x="3" y="7" width="13" height="14" rx="2"></rect><path d="M7 3h12a2 2 0 0 1 2 2v12"></path></svg>',
        home: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><path d="M3 11l9-7 9 7"></path><path d="M5 10v10h14V10"></path></svg>',
        clock: '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" aria-hidden="true"><circle cx="12" cy="12" r="9"></circle><path d="M12 7v5l3 2"></path></svg>'
    };

    function card(key, icon, name, pill, pillClass, big, unit, foot) {
        return '<button type="button" class="ucCard" data-card="' + key + '">' +
            '<div class="ucCardTop"><div class="ucCardName"><span class="ucIcon">' + icon + '</span>' + escapeHtml(name) + '</div>' +
            '<span class="ucPill ' + (pillClass || '') + '">' + escapeHtml(pill) + '</span></div>' +
            '<div class="ucBig"><b>' + escapeHtml(big) + '</b><span class="ucMuted ucSmall">' + escapeHtml(unit) + '</span></div>' +
            '<div class="ucCardFoot">' + foot.map(function (f) {
                return '<div class="ucFootLine"><span>' + escapeHtml(f[0]) + '</span><span class="ucFootValue">' + escapeHtml(f[1]) + '</span></div>';
            }).join('') + '</div></button>';
    }

    // ---- the collections text ----

    // The [collection ...] sections of the settings text, with the line number of each key. Mirrors the server's
    // parser closely enough for the page: a ' #' starts a comment, the first word is the key.
    function readSections(text) {
        var out = [], cur = null;
        (text || '').split('\n').forEach(function (line, i) {
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

    var styleKeys = ['poster_title', 'poster_accent', 'poster_tint', 'poster_sepia'];

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

    // The poster keys of one collection, as the PosterPreview request wants them.
    function styleOf(text, name) {
        var s = readSections(text).filter(function (x) { return x.name === name; })[0];
        var k = (s && s.keys) || {};
        var val = function (key) { return k[key] ? k[key].value : ''; };
        return { Name: name, Title: val('poster_title'), Accent: val('poster_accent'), Tint: val('poster_tint'), Sepia: val('poster_sepia') };
    }

    // ---- poster style popup ----
    //
    // A helper for the poster_* keys so nobody has to type r,g,b numbers: pick colours (or a ready-made style), see
    // the real poster, press Apply. The popup only edits the text box; Save still saves. The preview is drawn by the
    // server (UltimateCollections/PosterPreview) with the sync's own renderer and the collection's own movies, so what
    // you see is what the next sync makes.

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

    var dialogHtml =
        '<style>' +
        '.ucStyleDlg .formDialogContent{padding:1em 1.5em;overflow-y:auto}' +
        '.ucStyleGrid{display:flex;flex-wrap:wrap;gap:2em;align-items:flex-start}' +
        '.ucStyleControls{flex:1 1 22em;min-width:0;display:flex;flex-direction:column;gap:1.1em}' +
        '.ucStylePreview{flex:0 0 260px;margin:0 auto;text-align:center}' +
        '.ucStylePreview img{width:260px;max-width:100%;aspect-ratio:2/3;object-fit:cover;border-radius:4px;box-shadow:0 4px 18px rgba(0,0,0,.35);background:#222;transition:opacity .2s}' +
        '.ucStylePreview .ucBusy{opacity:.45}' +
        '.ucStyleLabel{display:block;margin-bottom:.4em;font-weight:500}' +
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
        '.ucStyleLink{font:inherit;font-size:1em;color:#52b54b;background:none;border:0;padding:0;cursor:pointer;text-decoration:underline}' +
        '.ucStyleWarn{color:#e09a2b;font-size:.9em;min-height:1.2em}' +
        '.ucStyleLines{font-family:monospace;font-size:.85em;white-space:pre;overflow-x:auto;padding:.6em .8em;border:1px solid rgba(128,128,128,.4);border-radius:4px}' +
        '.ucSource{font-size:.85em;opacity:.75;margin-top:.6em;line-height:1.4}' +
        '.ucStyleDlg .formDialogFooter{display:flex;justify-content:flex-end;gap:.8em;padding:1em 1.5em}' +
        '</style>' +
        '<div class="formDialogHeader">' +
        '  <button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1" title="Close"><i class="md-icon">close</i></button>' +
        '  <h3 class="formDialogHeaderTitle">Poster style: <span class="ucStyleName"></span></h3>' +
        '</div>' +
        '<div class="formDialogContent">' +
        '  <p class="ucHint" style="margin-top:0"><b>Apply</b> writes the lines into the configuration text; press <b>Save</b> on the page afterwards as usual.</p>' +
        '  <div class="ucStyleGrid">' +
        '    <div class="ucStyleControls">' +
        '      <div><span class="ucStyleLabel">Ready-made styles</span><div class="ucPresets"></div></div>' +
        '      <div class="ucColors">' +
        '        <div class="ucColor"><label class="ucStyleLabel" for="ucStyleAccent">Accent colour</label>' +
        '          <div class="ucColorRow"><input type="color" id="ucStyleAccent"><code class="ucAccentText"></code></div>' +
        '          <div class="ucHint">The bars and the top title line. Pick something bright.</div></div>' +
        '        <div class="ucColor"><label class="ucStyleLabel" for="ucStyleTint">Tint colour</label>' +
        '          <div class="ucColorRow"><input type="color" id="ucStyleTint"><code class="ucTintText"></code></div>' +
        '          <div class="ucHint">Washes over the whole picture. Dark works best. <button type="button" class="ucStyleLink ucMatchTint">Match the accent</button></div></div>' +
        '      </div>' +
        '      <div><label class="ucStyleLabel" for="ucStyleTitle">Title on the poster</label><input type="text" id="ucStyleTitle" class="ucStyleText" autocomplete="off">' +
        '        <div class="ucHint">Use | to start a new line, e.g. TIME|TRAVEL. Leave empty to use the collection name.</div></div>' +
        '      <label style="display:flex;align-items:center;gap:.6em"><input type="checkbox" id="ucStyleSepia"> Old-photo look (sepia)</label>' +
        '      <div class="ucStyleWarn" role="status"></div>' +
        '      <div><span class="ucStyleLabel">Lines that will be written</span><div class="ucStyleLines"></div></div>' +
        '    </div>' +
        '    <div class="ucStylePreview"><img alt="Poster preview"><div class="ucSource"></div></div>' +
        '  </div>' +
        '</div>' +
        '<div class="formDialogFooter">' +
        '  <button is="emby-button" type="button" class="raised button-cancel ucCancel"><span>Cancel</span></button>' +
        '  <button is="emby-button" type="button" class="raised button-submit ucApply"><span>Apply</span></button>' +
        '</div>';

    // Opens the popup for one collection; applied() runs after Apply changed the text.
    function openPosterStyle(textArea, name, applied) {
        require(['dialogHelper'], function (dialogHelper) {
            dialogHelper = dialogHelper.default || dialogHelper;

            var dlg = dialogHelper.createDialog({ size: 'medium', removeOnClose: true, scrollY: false });
            dlg.classList.add('formDialog', 'ucStyleDlg');
            dlg.innerHTML = dialogHtml;
            dlg.querySelector('.ucStyleName').textContent = name;

            var q = function (sel) { return dlg.querySelector(sel); };
            var accent = q('#ucStyleAccent'), tint = q('#ucStyleTint'), title = q('#ucStyleTitle'), sepia = q('#ucStyleSepia');
            var img = q('.ucStylePreview img');
            var state = { accent: '255,135,20', tint: '40,15,50' };
            var seq = 0, timer = null;

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
            function show() {
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
                q('.ucStyleWarn').textContent = warn.join(' ');
                q('.ucStyleLines').textContent = wantedLines().join('\n');
                clearTimeout(timer);
                timer = setTimeout(function () {
                    preview({ Name: name, Title: title.value, Accent: state.accent, Tint: state.tint, Sepia: sepia.checked ? 'yes' : '' });
                }, 300);
            }

            // Asks the server to draw the poster. With fill=true the answer's style (defaults filled in) is put into
            // the controls; that is how a collection without poster_accent shows the colour it gets today.
            function preview(body, fill) {
                var my = ++seq;
                img.classList.add('ucBusy');
                return postJson('UltimateCollections/PosterPreview', body).then(function (r) {
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
                        show();
                    }
                }, function () {
                    if (my === seq) {
                        img.classList.remove('ucBusy');
                        q('.ucSource').textContent = 'The preview could not be made; check the colours, or see the server log.';
                    }
                });
            }

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
                textArea.value = writeStyle(textArea.value, name, wantedLines());
                dialogHelper.close(dlg);
                toast(name + ' updated. Press Save to keep it.');
                applied();
            });

            dialogHelper.open(dlg);
            // Loads the collection's current poster lines from the text and lets the server fill in the defaults.
            var cur = styleOf(textArea.value, name);
            title.value = cur.Title;
            sepia.checked = !!cur.Sepia && !/^(no|false|off|0)$/i.test(cur.Sepia);
            preview(cur, true);
        });
    }

    // ---- images: how much of a picture Emby cuts off ----

    // As a sentence; '' when it fits.
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

    // ---- the page ----

    function View(view, params) {
        BaseView.apply(this, arguments);
        var self = this;

        var $ = function (sel) { return view.querySelector(sel); };
        var $$ = function (sel) { return Array.prototype.slice.call(view.querySelectorAll(sel)); };
        var textArea = $('#ucCollectionsText');

        // Everything the page knows besides the form itself.
        var st = {
            tab: 'settings',
            status: null,
            savedToken: '',
            tokenEditing: false,
            homeSaved: false,           // rows on in the SAVED settings: only then does switching off need the confirm box
            users: [],
            homeAll: true,
            homeIds: [],
            lastRows: null,             // the HomeRows answer, for the switch-off box
            snapshot: '',
            pollTimer: null,
            wasRunning: false,
            history: [],
            images: { slots: {}, names: [], selected: null, showAll: false, stale: true, previews: {}, queue: Promise.resolve() }
        };
        self.st = st;

        // ---- tabs ----

        function showTab(name) {
            st.tab = name;
            $$('.ucTab').forEach(function (t) { t.setAttribute('aria-selected', t.getAttribute('data-tab') === name ? 'true' : 'false'); });
            $$('.ucPanel').forEach(function (p) { p.hidden = p.getAttribute('data-panel') !== name; });
            if (name === 'images' && st.images.stale) {
                loadImages();
            } else if (name === 'cleanup') {
                loadUnused();
                loadOrphans();
                showCount();
            } else if (name === 'history') {
                loadHistory();
            } else if (name === 'home') {
                loadHomeTable();
            }
        }
        $$('.ucTab').forEach(function (t) {
            t.addEventListener('click', function () { showTab(t.getAttribute('data-tab')); });
        });

        // ---- unsaved changes ----

        function formState() {
            var o = {};
            Object.keys(fields).forEach(function (key) { o[key] = $('#' + fields[key][0])[fields[key][1]]; });
            o.HomeRowUsers = readUsers();
            o.HomeRowStyle = radio('ucHomeStyle');
            o.HomeRowPosition = radio('ucHomePosition');
            return JSON.stringify(o);
        }

        function markDirty() {
            $('.ucDirty').hidden = formState() === st.snapshot;
        }

        function radio(name) {
            var r = view.querySelector('input[name=' + name + ']:checked');
            return r ? r.value : '';
        }

        function setRadio(name, value) {
            var all = $$('input[name=' + name + ']');
            var hit = all.filter(function (r) { return r.value === value; })[0] || all[0];
            hit.checked = true;
        }

        view.querySelector('form').addEventListener('input', markDirty);
        view.querySelector('form').addEventListener('change', markDirty);

        // ---- status: header, cards, Collections popup ----

        function loadStatus() {
            clearTimeout(st.pollTimer);
            return getJson('UltimateCollections/Status').then(function (s) {
                st.status = s;
                renderStatus();
                updateToken();
                // While a run is going, check again every few seconds; when it ends, refresh what it may have changed.
                if (s.Running) {
                    st.wasRunning = true;
                    st.pollTimer = setTimeout(loadStatus, 3000);
                } else if (st.wasRunning) {
                    st.wasRunning = false;
                    loadHistory();
                    loadHomeTable();
                    st.images.stale = true;
                    if (st.tab === 'images') {
                        loadImages();
                    }
                }
            }, function () {
                $('.ucHealthText').textContent = 'Could not load the status.';
            });
        }

        function renderStatus() {
            var s = st.status;
            var last = s.LastRun;
            var health = $('.ucHealth'), dot = $('.ucHealthDot'), text = $('.ucHealthText');
            health.classList.remove('ucWarnText');
            dot.className = 'ucDot ucHealthDot';
            if (s.Running) {
                text.textContent = 'Sync running' + (s.Progress != null ? ', ' + Math.round(s.Progress) + '%' : '') + '...';
            } else if (s.Error) {
                dot.classList.add('ucBadDot');
                text.textContent = 'The saved configuration text has an error';
            } else if (s.DryRun) {
                health.classList.add('ucWarnText');
                dot.classList.add('ucWarnDot');
                text.textContent = 'Dry run on · nothing changes';
            } else if (!last) {
                dot.classList.add('ucOff');
                text.textContent = 'No run yet';
            } else {
                if (last.Errors) {
                    dot.classList.add('ucBadDot');
                }
                text.textContent = 'Last run ' + friendlyDate(last.Date).replace(', ', ' ') + ' · ' + (last.Errors ? plural(last.Errors, 'error') : 'no errors');
            }

            var next = s.NextName ? s.NextName + ', ' + s.NextDate : 'None waiting';
            var html = card('collections', icons.collections, 'Collections',
                s.Error ? 'Error' : s.InEmby ? 'Live' : 'None yet', s.Error ? 'ucBadPill' : s.InEmby ? '' : 'ucOff',
                String(s.InEmby), 'in Emby now', [['Configured', s.Error ? 'error' : String(s.Configured)], ['Next', next]]);
            html += card('home', icons.home, 'Home rows', s.HomeRowsOn ? 'On' : 'Off', s.HomeRowsOn ? '' : 'ucOff',
                s.HomeRowsOn ? String(s.HomeUsers) : 'Off', s.HomeRowsOn ? 'users' : '',
                [['Showing', s.HomeRowsOn ? (s.HomeShowing.length ? s.HomeShowing.join(', ') : 'Nothing in season') : '-'],
                 ['Style', s.HomeRowsOn ? (s.HomeRowStyle === 'spotlight' ? 'Spotlight' : 'Cards') : '-']]);
            var pill = s.Running ? 'Running' : s.DryRun ? 'Dry run' : s.Schedule;
            var pillClass = s.Running ? '' : s.DryRun ? 'ucWarnPill' : '';
            html += card('lastrun', icons.clock, 'Last run', pill, pillClass,
                last ? String(last.Changed) : '-', last ? (last.DryRun ? 'would change' : 'changes') : 'no run yet',
                [['When', last ? friendlyDate(last.Date) : '-'], ['Errors', last ? String(last.Errors) : '-']]);
            $('.ucCards').innerHTML = html;
            $('.ucCards [data-card=collections]').addEventListener('click', openCollections);
            $('.ucCards [data-card=home]').addEventListener('click', function () { showTab('home'); });
            $('.ucCards [data-card=lastrun]').addEventListener('click', function () { showTab('history'); });
            $('.ucSchedule').textContent = (s.Schedule || 'daily').toLowerCase();
            $('.ucRunNow').disabled = !!s.Running;
            $('.ucRunNow').textContent = s.Running ? 'Running...' : 'Run now';
        }

        // The Collections card: every collection in the SAVED configuration, what it uses and when it shows.
        function openCollections() {
            var s = st.status;
            if (!s) {
                return;
            }
            require(['dialogHelper'], function (dialogHelper) {
                dialogHelper = dialogHelper.default || dialogHelper;
                var dlg = dialogHelper.createDialog({ size: 'small', removeOnClose: true, scrollY: false });
                dlg.classList.add('formDialog');
                var waiting = s.Collections.filter(function (c) { return c.NextStart; }).length;
                var rows = s.Error ? '<div class="ucRow ucWarn">' + escapeHtml(s.Error) + '</div>' : s.Collections.map(function (c) {
                    return '<div class="ucRow"><div class="ucRowMain"><span style="font-weight:500">' + escapeHtml(c.Name) + '</span>' +
                        '<span class="ucMuted ucSmall ucEllipsis">' + escapeHtml(c.Source) + (c.Season ? ' · ' + escapeHtml(c.Season) : '') + '</span></div>' +
                        (c.InEmby ? '<span class="ucMuted ucSmall" style="white-space:nowrap">' + plural(c.Titles, 'title') + '</span>' : '') +
                        '<span class="ucPill ' + (c.Active ? '' : 'ucOff') + '">' + escapeHtml(c.Label) + '</span></div>';
                }).join('');
                dlg.innerHTML =
                    '<div class="formDialogHeader">' +
                    '  <button is="paper-icon-button-light" class="btnCancel autoSize" tabindex="-1" title="Close"><i class="md-icon">close</i></button>' +
                    '  <h3 class="formDialogHeaderTitle">Collections</h3>' +
                    '</div>' +
                    '<div class="formDialogContent ucPage" style="padding:1em 1.5em 6em;overflow-y:auto">' +
                    '  <p class="ucMuted" style="margin-top:0">' + s.InEmby + ' in Emby now' + (waiting ? ' · ' + waiting + ' waiting for their season' : '') +
                    '. From the saved configuration.</p>' +
                    '  <div class="ucList">' + rows + '</div>' +
                    '</div>';
                dlg.querySelector('.btnCancel').addEventListener('click', function () { dialogHelper.close(dlg); });
                dialogHelper.open(dlg);
            });
        }

        // ---- Settings tab ----

        // The token shows as one line once it is saved and working; the field opens for a new token, with no token,
        // after a 401, or when the text looks like the 32-character API Key (TMDb answers 401 to that; the Read Access
        // Token is a long JWT, so the shape is enough to tell them apart without asking TMDb).
        function updateToken() {
            var input = $('#ucTmdbToken');
            var hex = /^[0-9a-f]{32}$/i.test(input.value.trim());
            var refused = !!(st.status && st.status.TokenRefused) && input.value === st.savedToken;
            var open = st.tokenEditing || !st.savedToken || hex || refused;
            $('.ucTokenSaved').style.display = open ? 'none' : 'flex';
            $('.ucTokenEdit').style.display = open ? 'flex' : 'none';
            $('.ucTokenWarn').hidden = !hex;
            $('.ucTokenRefused').hidden = !refused || hex;
        }
        $('.ucTokenChange').addEventListener('click', function () {
            st.tokenEditing = true;
            updateToken();
            $('#ucTmdbToken').focus();
        });
        $('#ucTmdbToken').addEventListener('input', updateToken);

        function updateConfCount() {
            var sections = readSections(textArea.value);
            var seasonal = sections.filter(function (s) { return s.keys.season; }).length;
            $('.ucConfCount').textContent = plural(sections.length, 'collection') + (seasonal ? ' · ' + seasonal + ' seasonal' : '');
        }
        textArea.addEventListener('input', function () {
            updateConfCount();
            st.images.stale = true;
        });

        // "Ignore seasons" only counts in a dry run (the server ignores it otherwise), so it is greyed out without one.
        function updateIgnore() {
            var dry = $('#ucDryRun').checked;
            $('.ucIgnoreLine').classList.toggle('ucDim', !dry);
            $('#ucIgnoreSeason').disabled = !dry;
            $('.ucIgnoreHint').textContent = dry ? 'Preview out-of-season collections' : 'Only with Dry run on';
        }
        $('#ucDryRun').addEventListener('change', updateIgnore);

        $('.ucRunNow').addEventListener('click', function () {
            if (formState() !== st.snapshot) {
                toast('You have unsaved changes. Save first: Run now uses the saved settings.');
                return;
            }
            postJson('UltimateCollections/RunNow', {}).then(function (r) {
                toast(r.Message);
                if (r.Started) {
                    st.wasRunning = true;
                    setTimeout(loadStatus, 1500);
                }
            }, function (err) {
                errorText(err).then(toast);
            });
        });

        // ---- Images tab ----
        //
        // One poster per collection in the text box (saved or not). A tile shows the override if there is one, else
        // the collection's poster in Emby, else a preview drawn by the server (PosterPreview: same renderer and
        // colours the sync will use, nothing saved). Previews are drawn one at a time and only for tiles on screen,
        // so opening the tab does not make the server draw twenty posters at once. Overrides upload/remove at once
        // (UltimateCollections/CustomImages/*); the next sync puts them on the collections.

        var img = st.images;
        var picker = document.createElement('input');
        picker.type = 'file';
        picker.accept = 'image/jpeg,image/png';
        picker.style.display = 'none';
        view.appendChild(picker);
        var pickTarget = null;

        function loadImages() {
            img.stale = false;
            img.names = readSections(textArea.value).map(function (s) { return s.name; });
            var grid = $('.ucPosterGrid');
            if (!img.names.length) {
                grid.innerHTML = '<span class="ucMuted">Add a [collection NAME] section to the configuration text first.</span>';
                $('.ucGridMore').textContent = '';
                $('.ucSelected').hidden = true;
                return;
            }
            grid.innerHTML = '<span class="ucMuted">Loading...</span>';
            postJson('UltimateCollections/CustomImages/List', { Names: img.names.join('\n') }).then(function (list) {
                img.slots = {};
                (list || []).forEach(function (x) { img.slots[x.Name] = x; });
                if (img.names.indexOf(img.selected) < 0) {
                    img.selected = img.names[0];
                }
                renderGrid();
                renderSelected();
            }, function (err) {
                errorText(err).then(function (t) { grid.innerHTML = '<span class="ucWarn">' + escapeHtml('Could not load the images: ' + t) + '</span>'; });
            });
        }

        function hasOverride(slot) {
            return !!slot && ((slot.Poster && !slot.Poster.Error) || (slot.Backdrop && !slot.Backdrop.Error));
        }

        // The picture a slot shows without an override: Emby's current one, else a drawn preview (queued if missing).
        function current(name, backdrop) {
            var slot = img.slots[name] || {};
            var now = backdrop ? slot.CurrentBackdrop : slot.CurrentPoster;
            if (now) {
                return { src: now };
            }
            var p = img.previews[name + (backdrop ? '|b' : '|p')];
            return p ? { src: p.Image, source: p.Source } : null;
        }

        // NOTE: the tile and the selected panel can ask for the same preview; everyone waiting is told when it is
        // drawn (found 2026-10-15: the panel's poster stayed on "Drawing a preview..." when the tile asked first).
        function queuePreview(name, backdrop, done) {
            var key = name + (backdrop ? '|b' : '|p');
            if (img.previews[key]) {
                return;
            }
            var waiting = img.previews[key + '?'];
            if (waiting) {
                waiting.push(done);
                return;
            }
            waiting = img.previews[key + '?'] = [done];
            img.queue = img.queue.then(function () {
                var body = styleOf(textArea.value, name);
                body.Backdrop = backdrop;
                return postJson('UltimateCollections/PosterPreview', body).then(function (r) {
                    delete img.previews[key + '?'];
                    if (r.Image) {
                        img.previews[key] = r;
                        waiting.forEach(function (f) { f(); });
                    }
                }, function () { delete img.previews[key + '?']; });
            });
        }

        function renderGrid() {
            var names = img.showAll ? img.names : img.names.slice(0, gridFirst);
            var grid = $('.ucPosterGrid');
            grid.innerHTML = '';
            names.forEach(function (name) {
                var b = document.createElement('button');
                b.type = 'button';
                b.className = 'ucTile';
                b.setAttribute('aria-pressed', name === img.selected ? 'true' : 'false');
                b.addEventListener('click', function () {
                    img.selected = name;
                    $$('.ucTile').forEach(function (t) { t.setAttribute('aria-pressed', t === b ? 'true' : 'false'); });
                    renderSelected();
                });
                grid.appendChild(b);
                renderTile(b, name);
            });
            var more = $('.ucGridMore');
            if (img.names.length > gridFirst) {
                more.innerHTML = img.showAll ? '<button type="button" class="ucLinkBtn">Show fewer</button>'
                    : 'Showing ' + gridFirst + ' of ' + img.names.length + ' · <button type="button" class="ucLinkBtn">Show all</button>';
                more.querySelector('button').addEventListener('click', function () {
                    img.showAll = !img.showAll;
                    renderGrid();
                });
            } else {
                more.textContent = '';
            }
        }

        function renderTile(b, name) {
            var slot = img.slots[name] || {};
            var custom = slot.Poster && !slot.Poster.Error ? slot.Poster.Thumb : null;
            var pic = custom ? { src: custom } : current(name, false);
            b.innerHTML = '<div class="ucTileFrame">' + (pic ? '<img alt="">' : '<div class="ucTileEmpty">Drawing a preview...</div>') +
                '<span class="ucTileTag ' + (hasOverride(slot) ? 'ucCustomTag' : '') + '">' + (hasOverride(slot) ? 'Override' : 'Drawn') + '</span></div>' +
                '<span class="ucTileName"></span>';
            b.querySelector('.ucTileName').textContent = name;
            if (pic) {
                b.querySelector('img').src = pic.src;
            } else {
                queuePreview(name, false, function () { renderTile(b, name); });
            }
        }

        function renderSelected() {
            var box = $('.ucSelected');
            var name = img.selected;
            if (!name) {
                box.hidden = true;
                return;
            }
            box.hidden = false;
            box.innerHTML = '<div class="ucLabelRow"><span class="ucLabel"></span><button type="button" class="ucBtn ucStyleBtn">Poster style...</button></div>' +
                '<div class="ucSlots"><div class="ucSlot ucSlotPoster"></div><div class="ucSlot ucSlotBackdrop"></div></div>';
            box.querySelector('.ucLabel').textContent = 'Selected: ' + name;
            box.querySelector('.ucStyleBtn').addEventListener('click', function () {
                openPosterStyle(textArea, name, function () {
                    markDirty();
                    // The drawn preview follows the new colours; Emby's own poster changes on the next sync.
                    delete img.previews[name + '|p'];
                    delete img.previews[name + '|b'];
                    refreshName(name);
                });
            });
            renderSlot(box.querySelector('.ucSlotPoster'), name, false);
            renderSlot(box.querySelector('.ucSlotBackdrop'), name, true);
        }

        // After an upload, a removal or a style change: the tile and, if selected, the panel.
        function refreshName(name) {
            var i = (img.showAll ? img.names : img.names.slice(0, gridFirst)).indexOf(name);
            if (i >= 0) {
                renderTile($$('.ucTile')[i], name);
            }
            if (img.selected === name) {
                renderSelected();
            }
        }

        function renderSlot(el, name, backdrop) {
            var slot = img.slots[name] || {};
            var o = backdrop ? slot.Backdrop : slot.Poster;
            var what = backdrop ? 'Backdrop' : 'Poster';
            var now = current(name, backdrop);
            var html = '<span class="ucLabel" style="color:var(--uc-muted)">' + what + '</span>';
            var meta = '';
            if (o && o.Error) {
                html += '<div class="ucFrame"><div class="ucBad">' + escapeHtml(o.Error) + '. Not used.</div></div>';
                meta = escapeHtml(o.FileName);
            } else if (o) {
                html += '<div class="ucFrame ucHasImg" title="Hover or tap to see the whole picture"><span class="ucBadge ucCustomTag">Override</span><img alt=""></div>';
                var cut = cropNote(o.Width, o.Height, backdrop ? 16 : 2, backdrop ? 9 : 3);
                meta = escapeHtml(o.FileName) + '<br>' + o.Width + 'x' + o.Height + ', ' + sizeText(o.Bytes) +
                    (cut ? '<br><span class="ucWarn">' + cut + '</span>' : '');
            } else if (now) {
                html += '<div class="ucFrame ucHasImg" title="Hover or tap to see the whole picture"><span class="ucBadge">' + (now.source ? 'Preview' : 'Drawn') + '</span><img alt=""></div>';
                meta = now.source ? 'Not built yet. Made from ' + escapeHtml(now.source) + '.' : 'Drawn by the plugin.';
            } else {
                html += '<div class="ucFrame"><div class="ucEmpty">Drawing a preview...</div></div>';
                meta = 'Not built yet (out of season, or new).';
                queuePreview(name, backdrop, function () { if (img.selected === name) { renderSlot(el, name, backdrop); } });
            }
            if (o && o.Others) {
                meta += '<br><span class="ucWarn">' + o.Others + ' more file(s) match; this one is used.</span>';
            }
            html += '<div class="ucMeta">' + meta + '</div><div class="ucSlotButtons">' +
                '<button type="button" class="ucBtn ucUp">' + (o ? 'Replace...' : 'Override ' + what + '...') + '</button>' +
                (o ? '<button type="button" class="ucBtn ucRemove">Remove</button>' : '') + '</div>';
            el.innerHTML = html;
            var pic = o && !o.Error ? o.Thumb : now && now.src;
            if (pic && el.querySelector('img')) {
                el.querySelector('img').src = pic;
                // Tap toggles the whole picture on touch screens, where there is no hover.
                el.querySelector('.ucFrame').addEventListener('click', function () { this.classList.toggle('ucShowAll'); });
            }
            el.querySelector('.ucUp').addEventListener('click', function () {
                pickTarget = { el: el, name: name, backdrop: backdrop };
                picker.value = '';
                picker.click();
            });
            if (o) {
                el.querySelector('.ucRemove').addEventListener('click', function () {
                    busy(el, true);
                    postJson('UltimateCollections/CustomImages/Delete', { Name: name, Backdrop: backdrop }).then(function (r) {
                        keepSlots(name, r);
                        refreshName(name);
                        toast(name + ': ' + what.toLowerCase() + ' override removed. The drawn one comes back on the next sync.');
                    }, function (err) {
                        busy(el, false);
                        errorText(err).then(toast);
                    });
                });
            }
        }

        // The upload/delete answers carry the overrides only; Emby's current pictures are kept from the list.
        function keepSlots(name, r) {
            var old = img.slots[name] || {};
            r.CurrentPoster = r.CurrentPoster || old.CurrentPoster;
            r.CurrentBackdrop = r.CurrentBackdrop || old.CurrentBackdrop;
            img.slots[name] = r;
        }

        function busy(el, on) {
            Array.prototype.forEach.call(el.querySelectorAll('button'), function (b) { b.disabled = on; });
            el.style.opacity = on ? 0.5 : '';
        }

        // Upload: checked here for size first (no point sending 40 MB to be refused), then sent as base64; the
        // server checks it again properly (real format, dimensions).
        picker.addEventListener('change', function () {
            var file = picker.files && picker.files[0];
            var t = pickTarget;
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
                    keepSlots(t.name, r);
                    refreshName(t.name);
                    toast(t.name + ': ' + (t.backdrop ? 'backdrop' : 'poster') + ' override saved. It is put on the collection on the next sync.');
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

        // ---- Home Screen tab ----
        //
        // HomeRowUsers is "all" or a comma-separated list of user ids, from the chips: "All users" (includes users
        // added later) or picked names. The list comes from the server (saved settings and home-rows.json), so it
        // shows what the next run works from, not the unsaved page.
        // NOTE: switching rows off is not a plain Save: if they were on, the confirm box asks whether to remove our
        // rows and the server switches it off itself (HomeRowsApi), so the setting and the home screens never disagree.

        function showHome() {
            var on = $('#ucManageHomeRows').checked;
            $('.ucHomeOptions').style.display = on ? 'flex' : 'none';
            $('.ucHomeOffNote').style.display = on ? 'none' : '';
        }

        function readUsers() {
            return st.homeAll || !st.homeIds.length ? 'all' : st.homeIds.join(',');
        }

        function setUsers(saved) {
            var ids = (saved || 'all').split(',').map(function (x) { return x.trim(); }).filter(function (x) { return x; });
            st.homeAll = !ids.length || ids.some(function (x) { return x.toLowerCase() === 'all'; });
            st.homeIds = st.homeAll ? [] : ids;
        }

        function initials(name) {
            return (name || '?').split(/\s+/).map(function (w) { return w.charAt(0); }).join('').slice(0, 2).toUpperCase();
        }

        // In "All users" every name shows as included; clicking a name switches to picked names, starting with that
        // one. The last picked name cannot be unpicked (an empty list would mean "all" to the server anyway).
        function renderUsers() {
            var box = $('.ucUsers');
            box.innerHTML = '';
            var all = document.createElement('button');
            all.type = 'button';
            all.className = 'ucChip ucAll';
            all.textContent = 'All users';
            all.setAttribute('aria-pressed', st.homeAll ? 'true' : 'false');
            all.addEventListener('click', function () {
                st.homeAll = true;
                st.homeIds = [];
                renderUsers();
                markDirty();
            });
            box.appendChild(all);
            st.users.forEach(function (u) {
                var b = document.createElement('button');
                b.type = 'button';
                var picked = st.homeIds.indexOf(u.Id) >= 0;
                b.className = 'ucChip' + (st.homeAll ? ' ucImplied' : '');
                b.setAttribute('aria-pressed', !st.homeAll && picked ? 'true' : 'false');
                var av = document.createElement('span');
                av.className = 'ucAvatar';
                if (u.PrimaryImageTag) {
                    var im = document.createElement('img');
                    im.alt = '';
                    im.src = ApiClient.getUserImageUrl(u.Id, { type: 'Primary', tag: u.PrimaryImageTag, height: 48 });
                    av.appendChild(im);
                } else {
                    av.textContent = initials(u.Name);
                }
                b.appendChild(av);
                b.appendChild(document.createTextNode(u.Name));
                b.addEventListener('click', function () {
                    if (st.homeAll) {
                        st.homeAll = false;
                        st.homeIds = [u.Id];
                    } else if (picked) {
                        if (st.homeIds.length === 1) {
                            toast('At least one user stays picked. Choose All users to include everyone.');
                            return;
                        }
                        st.homeIds = st.homeIds.filter(function (x) { return x !== u.Id; });
                    } else {
                        st.homeIds.push(u.Id);
                    }
                    renderUsers();
                    markDirty();
                });
                box.appendChild(b);
            });
            $('.ucUsersNote').textContent = st.homeAll
                ? 'All users, including users added later. Pick names to limit it.'
                : plural(st.homeIds.length, 'user') + ' picked. New users are not added automatically.';
        }

        function loadUsers() {
            return ApiClient.getUsers().then(function (users) {
                st.users = users || [];
                renderUsers();
            }, function () {
                $('.ucUsers').textContent = 'Could not load the users.';
            });
        }

        function loadHomeTable() {
            var box = $('.ucHomeTable');
            var notes = $('.ucHomeNotes');
            getJson('UltimateCollections/HomeRows').then(function (r) {
                st.lastRows = r;
                notes.innerHTML = '';
                if (r.Error) {
                    box.innerHTML = '<span class="ucWarn">' + escapeHtml(r.Error) + '</span>';
                    return;
                }
                var rows = (r.Rows || []).map(function (x) {
                    var pill = x.Status === 'Showing' ? '' : 'ucOff';
                    var users = x.Status === 'No row' ? '' : plural(x.Users, 'user') + (x.Removed ? ' (' + x.Removed + ' removed it)' : '');
                    return '<div class="ucRow"><div class="ucRowMain"><span style="font-weight:500" class="ucEllipsis">' + escapeHtml(x.Name) + '</span>' +
                        '<span class="ucMuted ucSmall">' + escapeHtml(x.Season) + '</span></div>' +
                        '<span class="ucMuted ucSmall" style="width:5.5em">' + escapeHtml(x.Style) + '</span>' +
                        '<span class="ucMuted ucSmall" style="width:7em">' + escapeHtml(users) + '</span>' +
                        '<span class="ucPill ucRowPill ' + pill + '">' + escapeHtml(x.Status) + '</span></div>';
                });
                box.innerHTML = rows.length ? '<div class="ucList">' + rows.join('') + '</div>' : '<span class="ucMuted">No collection gets a row.</span>';
                (r.Notes || []).forEach(function (n) {
                    var p = document.createElement('div');
                    p.className = 'ucNote';
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
            '  <h3 class="formDialogHeaderTitle">Stop managing the home screen?</h3>' +
            '</div>' +
            '<div class="formDialogContent" style="padding:1em 1.5em">' +
            '  <p class="ucOffCount"></p>' +
            '  <label style="display:flex;gap:.6em;align-items:flex-start;margin-bottom:.3em"><input type="radio" name="ucOff" value="remove" checked><span><b>Remove the rows the plugin added</b></span></label>' +
            '  <div class="fieldDescription" style="margin:0 0 1em 1.8em">Rows users made themselves, or that another plugin added, are not touched. ' +
            '  A backup of each user\'s home screen is saved first (backups/ in the plugin\'s data folder).</div>' +
            '  <label style="display:flex;gap:.6em;align-items:flex-start;margin-bottom:.3em"><input type="radio" name="ucOff" value="leave"><span><b>Leave them as they are</b></span></label>' +
            '  <div class="fieldDescription" style="margin:0 0 1em 1.8em">They stop following the seasons and may point to old collections.</div>' +
            '</div>' +
            '<div class="formDialogFooter" style="display:flex;justify-content:flex-end;gap:.8em;padding:1em 1.5em">' +
            '  <button is="emby-button" type="button" class="raised button-cancel ucCancel"><span>Cancel</span></button>' +
            '  <button is="emby-button" type="button" class="raised button-submit ucOffBtn"><span>Switch off</span></button>' +
            '</div>';

        // Cancel (or closing the box) switches it on again. The server does the rest and saves the setting off.
        function openSwitchOff() {
            var box = $('#ucManageHomeRows');
            require(['dialogHelper'], function (dialogHelper) {
                dialogHelper = dialogHelper.default || dialogHelper;
                var dlg = dialogHelper.createDialog({ size: 'small', removeOnClose: true, scrollY: false });
                dlg.classList.add('formDialog');
                dlg.innerHTML = switchOffHtml;
                var rows = 0, users = st.lastRows ? st.lastRows.UsersWithRows : 0;
                ((st.lastRows && st.lastRows.Rows) || []).forEach(function (x) { rows += x.Users; });
                dlg.querySelector('.ucOffCount').textContent = rows
                    ? 'The plugin manages ' + plural(rows, 'row') + ' on ' + users + ' user' + (users === 1 ? "'s" : "s'") + ' home screens.'
                    : 'The plugin has no rows on any home screen right now.';
                var switched = false;
                function close() { dialogHelper.close(dlg); }
                dlg.querySelector('.btnCancel').addEventListener('click', close);
                dlg.querySelector('.ucCancel').addEventListener('click', close);
                dlg.addEventListener('close', function () {
                    if (!switched) {
                        box.checked = true;
                        showHome();
                        markDirty();
                    }
                });
                dlg.querySelector('.ucOffBtn').addEventListener('click', function () {
                    var remove = dlg.querySelector('input[name=ucOff]:checked').value === 'remove';
                    loading.show();
                    postJson('UltimateCollections/HomeRows/SwitchOff', { RemoveRows: remove }).then(function (r) {
                        loading.hide();
                        switched = true;
                        st.homeSaved = false;
                        close();
                        // Saved off by the server; the snapshot follows so this does not count as unsaved.
                        var snap = JSON.parse(st.snapshot || '{}');
                        snap.ManageHomeRows = false;
                        st.snapshot = JSON.stringify(snap);
                        markDirty();
                        toast(remove ? 'Home screen rows switched off; ' + plural(r.Removed, 'row') + ' removed.' : 'Home screen rows switched off; the rows were left as they are.');
                        loadHomeTable();
                        loadStatus();
                    }, function (err) {
                        loading.hide();
                        errorText(err).then(toast);
                    });
                });
                dialogHelper.open(dlg);
            });
        }

        $('#ucManageHomeRows').addEventListener('change', function () {
            showHome();
            if (!$('#ucManageHomeRows').checked && st.homeSaved) {
                openSwitchOff();
            }
        });

        // ---- Clean-up tab: each button acts at once ----

        // Files in images/ that no collection in the text box uses (left over from a renamed or removed collection,
        // or extra copies). The server works the list out again on delete, so it never deletes from a stale list.
        function loadUnused() {
            var names = readSections(textArea.value).map(function (s) { return s.name; }).join('\n');
            var count = $('.ucUnusedCount'), list = $('.ucUnusedList'), button = $('.ucDelUnused');
            button.disabled = true;
            postJson('UltimateCollections/CustomImages/Unused', { Names: names }).then(function (r) {
                r = r || [];
                list.innerHTML = r.map(function (u) { return '<li>' + escapeHtml(u.FileName) + ' <span class="ucMuted">(' + escapeHtml(u.Reason) + ')</span></li>'; }).join('');
                count.innerHTML = r.length ? '<b>' + plural(r.length, 'unused image') + '</b>' : okMark('None');
                button.textContent = r.length ? 'Delete ' + plural(r.length, 'unused image') : 'Delete unused images';
                button.disabled = !r.length;
            }, function (err) {
                list.innerHTML = '';
                errorText(err).then(function (t) { count.textContent = 'Could not count the unused images: ' + t; });
            });
        }

        function okMark(text) {
            return '<span class="ucOk"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2.5" aria-hidden="true"><path d="M5 12l5 5L20 7"></path></svg>' +
                escapeHtml(text) + '</span>';
        }

        $('.ucDelUnused').addEventListener('click', function () {
            var names = readSections(textArea.value).map(function (s) { return s.name; }).join('\n');
            this.disabled = true;
            postJson('UltimateCollections/CustomImages/DeleteUnused', { Names: names }).then(function (r) {
                toast(plural(r.Deleted, 'unused image') + ' deleted.');
                loadUnused();
            }, function (err) {
                errorText(err).then(toast);
                loadUnused();
            });
        });

        // A backup of each user's home screen is saved by the server before it changes one (backups/), like every
        // other home screen change.
        function loadOrphans() {
            var count = $('.ucOrphanCount'), list = $('.ucOrphanList'), button = $('.ucClearOrphans');
            button.disabled = true;
            getJson('UltimateCollections/HomeRows/Orphans').then(function (r) {
                list.innerHTML = (r.Rows || []).map(function (x) { return '<li>' + escapeHtml(x) + '</li>'; }).join('');
                count.innerHTML = r.Count
                    ? '<b>' + plural(r.Count, 'orphaned row') + '</b> on ' + r.Users + ' user' + (r.Users === 1 ? "'s" : "s'") + ' home screens'
                    : okMark('None');
                button.textContent = r.Count ? 'Clear ' + plural(r.Count, 'orphaned row') : 'Clear orphaned rows';
                button.disabled = !r.Count;
            }, function (err) {
                list.innerHTML = '';
                errorText(err).then(function (t) { count.textContent = 'Could not count the orphaned rows: ' + t; });
            });
        }

        $('.ucClearOrphans').addEventListener('click', function () {
            this.disabled = true;
            loading.show();
            postJson('UltimateCollections/HomeRows/ClearOrphans', {}).then(function (r) {
                loading.hide();
                toast(plural(r.Removed, 'orphaned row') + ' removed.');
                loadOrphans();
            }, function (err) {
                loading.hide();
                errorText(err).then(toast);
                loadOrphans();
            });
        });

        // How many collections a delete would remove, so the size of it shows before anyone types DELETE.
        function showCount() {
            var el = $('.ucCollectionCount');
            getJson('UltimateCollections/CollectionsSummary').then(function (s) {
                el.textContent = s.Total === 0
                    ? 'There are no collections in Emby right now.'
                    : 'This would delete ' + plural(s.Total, 'collection') + ' (' + s.MadeByPlugin + ' made by this plugin, ' +
                      (s.Total - s.MadeByPlugin) + ' made some other way).';
            }, function () {
                el.textContent = 'Could not count the collections.';
            });
        }

        var confirmInput = $('#ucDeleteConfirm');
        var deleteButton = $('.ucDeleteAll');
        // The button only unlocks for the exact word; the server checks it again.
        confirmInput.addEventListener('input', function (e) {
            e.stopPropagation();    // not a setting: keeps "Unsaved changes" out of it
            deleteButton.disabled = confirmInput.value !== confirmWord;
        });

        // The word box is cleared at once, so the button cannot be pressed twice by accident. Any failure (including
        // "a sync is running", which the server refuses) gets the same message; the plugin log has the details.
        deleteButton.addEventListener('click', function () {
            if (confirmInput.value !== confirmWord) {
                return;
            }
            var result = $('.ucDeleteResult');
            deleteButton.disabled = true;
            confirmInput.value = '';
            result.textContent = 'Deleting collections...';
            loading.show();
            postJson('UltimateCollections/DeleteAllCollections', { Confirm: confirmWord }).then(function (r) {
                loading.hide();
                result.textContent = 'Done: ' + r.Deleted + ' of ' + r.Found + ' collections deleted' +
                    (r.Failed ? ', ' + r.Failed + ' could not be deleted (see the plugin log)' : '') +
                    '. Backup of what existed: ' + r.BackupFile;
                showCount();
                loadStatus();
            }, function () {
                loading.hide();
                result.textContent = 'Nothing was deleted or the request failed (for example while a sync is running). ' +
                    'See the plugin log for details.';
                showCount();
            });
        });

        // ---- History tab ----

        function loadHistory() {
            getJson('UltimateCollections/History').then(function (list) {
                st.history = list || [];
                renderHistory();
            }, function () {
                $('.ucHistory').innerHTML = '<span class="ucWarn">Could not load the history.</span>';
            });
        }

        function renderHistory() {
            var onlyChanges = radio('ucHistFilter') === 'changes';
            var list = st.history.filter(function (h) { return !onlyChanges || h.Changed || h.Errors || h.Failed; });
            var box = $('.ucHistory');
            if (!st.history.length) {
                box.innerHTML = '<span class="ucMuted">No runs recorded yet. The history starts with the first run of version 1.3.</span>';
                return;
            }
            if (!list.length) {
                box.innerHTML = '<span class="ucMuted">No run changed anything yet.</span>';
                return;
            }
            box.innerHTML = list.map(function (h) {
                var tag = h.Failed === 'Cancelled' ? ['Cancelled', 'ucOff'] : h.Failed || h.Errors ? ['Error', 'ucBadPill'] : h.DryRun ? ['Dry run', 'ucWarnPill'] :
                    h.Changed ? ['Changed', ''] : ['Quiet', 'ucOff'];
                var lines = [];
                if (h.Failed) {
                    lines.push('<div class="ucWarn">' + escapeHtml(h.Failed) + '</div>');
                }
                (h.Changes || []).forEach(function (c) { lines.push('<div>' + escapeHtml(c) + '</div>'); });
                (h.Problems || []).forEach(function (p) { lines.push('<div class="ucWarn">' + escapeHtml(p) + '</div>'); });
                if (h.Downloads) {
                    lines.push('<div class="ucMuted">' + plural(h.Downloads, 'list') + ' downloaded</div>');
                }
                if (!lines.length) {
                    lines.push('<div class="ucMuted">Nothing changed.</div>');
                }
                if (h.DryRun) {
                    lines.push('<div class="ucMuted">Dry run: nothing was actually changed.</div>');
                }
                return '<details class="ucHist"><summary><span class="ucHistWhen">' + escapeHtml(friendlyDate(h.Date)) + '</span>' +
                    '<span class="ucHistHow">' + escapeHtml(kindNames[h.Kind] || h.Kind || '') + '</span>' +
                    '<span class="ucHistWhat">' + escapeHtml(h.Summary) + '</span>' +
                    '<span class="ucPill ucHistTag ' + tag[1] + '">' + tag[0] + '</span></summary>' +
                    '<div class="ucHistDetail">' + lines.join('') + '</div></details>';
            }).join('');
        }
        $$('input[name=ucHistFilter]').forEach(function (r) {
            r.addEventListener('change', function (e) {
                e.stopPropagation();    // a view filter, not a setting
                renderHistory();
            });
        });

        // ---- Save ----

        // Fetch the current configuration first and only overwrite our fields, so anything else Emby keeps in it
        // (or a setting the page does not show) survives.
        view.querySelector('form').addEventListener('submit', function (e) {
            e.preventDefault();
            loading.show();
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                Object.keys(fields).forEach(function (key) {
                    config[key] = $('#' + fields[key][0])[fields[key][1]];
                });
                config.HomeRowUsers = readUsers();
                config.HomeRowStyle = radio('ucHomeStyle') || 'cards';
                config.HomeRowPosition = radio('ucHomePosition') || 'after';
                return ApiClient.updatePluginConfiguration(pluginId, config);
            }).then(function (result) {
                st.homeSaved = $('#ucManageHomeRows').checked;
                st.savedToken = $('#ucTmdbToken').value;
                st.tokenEditing = false;
                st.snapshot = formState();
                markDirty();
                Dashboard.processPluginConfigurationUpdateResult(result);
                loadStatus();
                loadHomeTable();
            }, function () {
                loading.hide();
                toast('Saving failed.');
            });
            return false;
        });

        // Loads the saved settings into the form; used on every visit to the page.
        self.load = function () {
            loading.show();
            st.tokenEditing = false;
            confirmInput.value = '';
            deleteButton.disabled = true;
            ApiClient.getPluginConfiguration(pluginId).then(function (config) {
                Object.keys(fields).forEach(function (key) {
                    var v = config[key];
                    $('#' + fields[key][0])[fields[key][1]] = fields[key][1] === 'checked' ? !!v : (v || '');
                });
                setRadio('ucHomeStyle', config.HomeRowStyle);
                setRadio('ucHomePosition', config.HomeRowPosition);
                setUsers(config.HomeRowUsers);
                st.homeSaved = !!config.ManageHomeRows;
                st.savedToken = config.TmdbToken || '';
                st.images.stale = true;
                updateConfCount();
                updateIgnore();
                showHome();
                updateToken();
                st.snapshot = formState();
                markDirty();
                loading.hide();
                loadUsers().then(function () {
                    // Users the saved list names but who are gone do not count as a change.
                    st.snapshot = formState();
                    markDirty();
                });
                loadStatus();
                showTab(st.tab);
            }, function () {
                loading.hide();
                toast('Could not load the settings.');
            });
        };
    }

    Object.assign(View.prototype, BaseView.prototype);

    // Runs every time the page is shown.
    View.prototype.onResume = function (options) {
        BaseView.prototype.onResume.apply(this, arguments);
        this.load();
    };

    View.prototype.onPause = function () {
        BaseView.prototype.onPause.apply(this, arguments);
        clearTimeout(this.st.pollTimer);
    };

    return View;
});
