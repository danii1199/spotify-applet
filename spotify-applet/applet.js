const Applet = imports.ui.applet;
const Main = imports.ui.main;
const PopupMenu = imports.ui.popupMenu;
const Slider = imports.ui.slider;
const Settings = imports.ui.settings;
const Tooltips = imports.ui.tooltips;
const Util = imports.misc.util;
const Mainloop = imports.mainloop;
const St = imports.gi.St;
const Gio = imports.gi.Gio;
const GLib = imports.gi.GLib;
const Clutter = imports.gi.Clutter;
const Pango = imports.gi.Pango;

const BUS = 'org.mpris.MediaPlayer2.spotify';
const PATH = '/org/mpris/MediaPlayer2';
const IFACE_ROOT = 'org.mpris.MediaPlayer2';
const IFACE_PLAYER = 'org.mpris.MediaPlayer2.Player';
const IFACE_PROPS = 'org.freedesktop.DBus.Properties';
const CACHE_DIR = GLib.get_user_cache_dir() + '/spotify-applet';
let HELPER = null; // set from metadata.path in main()

function fmtTime(us) {
    let s = Math.max(0, Math.floor(us / 1000000));
    let m = Math.floor(s / 60);
    s = s % 60;
    return m + ':' + (s < 10 ? '0' : '') + s;
}

// open.spotify.com/playlist/ID?si=… → spotify:playlist:ID
function toSpotifyUri(text) {
    text = (text || '').trim();
    if (text.startsWith('spotify:'))
        return text;
    let m = text.match(/open\.spotify\.com\/(?:intl-[a-z-]+\/)?(track|album|playlist|artist|show|episode)\/([A-Za-z0-9]+)/);
    return m ? 'spotify:' + m[1] + ':' + m[2] : null;
}

function decodeEntities(s) {
    return s.replace(/&#x([0-9a-f]+);/gi, (m, h) => String.fromCharCode(parseInt(h, 16)))
        .replace(/&#(\d+);/g, (m, d) => String.fromCharCode(parseInt(d, 10)))
        .replace(/&quot;/g, '"').replace(/&lt;/g, '<').replace(/&gt;/g, '>').replace(/&amp;/g, '&');
}

// A label clipped to a fixed width that scrolls back and forth when the text doesn't fit
class Marquee {
    constructor(styleClass, width) {
        this.actor = new St.Widget({ clip_to_allocation: true });
        this.label = new St.Label({ style_class: styleClass });
        this.label.clutter_text.ellipsize = Pango.EllipsizeMode.NONE;
        this.actor.add_child(this.label);
        this.actor.width = width;
        this._timer = 0;
        this._reset();
    }

    set text(t) {
        if (this.label.text === t) return;
        this.label.text = t;
        this._reset();
    }

    get text() {
        return this.label.text;
    }

    setWidth(w) {
        this.actor.width = w;
        this._reset();
    }

    _reset() {
        this._pos = 0;
        this._dir = 1;
        this._wait = 50;
        this.label.translation_x = 0;
    }

    start() {
        if (!this._timer)
            this._timer = Mainloop.timeout_add(30, () => this._tick());
    }

    stop() {
        if (this._timer) Mainloop.source_remove(this._timer);
        this._timer = 0;
        this._reset();
    }

    _tick() {
        let overflow = this.label.get_preferred_width(-1)[1] - this.actor.width;
        if (overflow <= 0) {
            this.label.translation_x = 0;
            return true;
        }
        if (this._wait > 0) {
            this._wait--;
            return true;
        }
        this._pos += this._dir;
        if (this._pos >= overflow || this._pos <= 0) {
            this._pos = Math.max(0, Math.min(this._pos, overflow));
            this._dir = -this._dir;
            this._wait = 50;
        }
        this.label.translation_x = -this._pos;
        return true;
    }

    destroy() {
        this.stop();
    }
}

class SpotifyApplet extends Applet.IconApplet {
    constructor(metadata, orientation, panelHeight, instanceId) {
        super(orientation, panelHeight, instanceId);
        this.setAllowedLayout(Applet.AllowedLayout.BOTH);

        this.settings = new Settings.AppletSettings(this, metadata.uuid, instanceId);
        this.settings.bind('cover-size', 'coverSize', () => this._onSizesChanged());
        this.settings.bind('preview-size', 'previewSize', () => this._onSizesChanged());
        this.settings.bind('client-id', 'clientId', () => this._refreshLists());

        this.set_applet_icon_name('spotify-client');
        this.set_applet_tooltip(''); // replaced by the cover preview

        this._owner = null;
        this._signalIds = [];
        this._status = 'Stopped';
        this._meta = {};
        this._shuffle = false;
        this._loop = 'None';
        this._volume = 1;
        this._position = 0;
        this._length = 0;
        this._coverUrl = null;
        this._coverPath = null;
        this._pollId = 0;
        this._seekDragging = false;
        this._artistsCache = {};
        this._fullArtists = null;
        this._previewTimer = 0;
        this._listsTrack = null;   // track the lists submenu currently reflects
        this._listsBusy = false;
        this._loggingIn = false;

        GLib.mkdir_with_parents(CACHE_DIR, 0o755);

        this.menuManager = new PopupMenu.PopupMenuManager(this);
        this.menu = new Applet.AppletPopupMenu(this, orientation);
        this.menuManager.addMenu(this.menu);
        this._buildMenu();
        this.menu.connect('open-state-changed', (m, open) => this._onMenuToggled(open));

        this.actor.connect('scroll-event', (a, e) => this._onScroll(e));
        this._buildPreview();
        this.actor.connect('enter-event', () => this._schedulePreview());
        this.actor.connect('leave-event', () => this._hidePreview());
        this.actor.connect('button-press-event', () => { this._hidePreview(); return Clutter.EVENT_PROPAGATE; });

        this._watchId = Gio.bus_watch_name(Gio.BusType.SESSION, BUS, Gio.BusNameWatcherFlags.NONE,
            (c, name, owner) => this._onAppeared(owner),
            () => this._onVanished());
        this._onVanished();
    }

    // ---------- DBus ----------

    _call(iface, method, params, cb) {
        Gio.DBus.session.call(BUS, PATH, iface, method, params || null, null,
            Gio.DBusCallFlags.NONE, -1, null, (conn, res) => {
                try {
                    let r = conn.call_finish(res);
                    if (cb) cb(r);
                } catch (e) {
                    global.logWarning('[spotify-applet] ' + method + ': ' + e.message);
                }
            });
    }

    _player(method, params) {
        this._call(IFACE_PLAYER, method, params);
    }

    _setProp(name, variant) {
        this._call(IFACE_PROPS, 'Set', new GLib.Variant('(ssv)', [IFACE_PLAYER, name, variant]));
    }

    _onAppeared(owner) {
        this._owner = owner;
        let sub = (iface, member, cb) => this._signalIds.push(Gio.DBus.session.signal_subscribe(
            owner, iface, member, PATH, null, Gio.DBusSignalFlags.NONE, cb));
        sub(IFACE_PROPS, 'PropertiesChanged', (c, s, p, i, sig, params) => {
            let [iface, changed] = params.recursiveUnpack();
            if (iface === IFACE_PLAYER)
                this._applyProps(changed);
        });
        sub(IFACE_PLAYER, 'Seeked', (c, s, p, i, sig, params) => {
            this._position = params.recursiveUnpack()[0];
            this._updateSeek();
        });
        this._call(IFACE_PROPS, 'GetAll', new GLib.Variant('(s)', [IFACE_PLAYER]),
            r => this._applyProps(r.recursiveUnpack()[0]));
        this._setRunning(true);
    }

    _onVanished() {
        this._owner = null;
        this._signalIds.forEach(id => Gio.DBus.session.signal_unsubscribe(id));
        this._signalIds = [];
        this._status = 'Stopped';
        this._meta = {};
        this._setRunning(false);
    }

    _applyProps(p) {
        if (p.PlaybackStatus !== undefined) this._status = p.PlaybackStatus;
        if (p.Shuffle !== undefined) this._shuffle = p.Shuffle;
        if (p.LoopStatus !== undefined) this._loop = p.LoopStatus;
        if (p.Volume !== undefined) this._volume = p.Volume;
        if (p.Position !== undefined) this._position = p.Position;
        if (p.Metadata !== undefined) {
            this._meta = p.Metadata;
            this._length = p.Metadata['mpris:length'] || 0;
            // Spotify emits no Seeked on track change; fetch the real position
            this._refreshPosition();
            this._loadCover(p.Metadata['mpris:artUrl']);
            this._fetchArtists(p.Metadata['xesam:url']);
            if (this.menu.isOpen) this._refreshLists();
        }
        this._updateAll();
    }

    _refreshPosition() {
        if (!this._owner) return;
        this._call(IFACE_PROPS, 'Get', new GLib.Variant('(ss)', [IFACE_PLAYER, 'Position']), r => {
            this._position = r.recursiveUnpack()[0];
            this._updateSeek();
        });
    }

    // ---------- Menu ----------

    _button(icon, tooltip, cb, extraClass) {
        let b = new St.Button({ style_class: 'spotify-button' + (extraClass ? ' ' + extraClass : ''), can_focus: true });
        b.set_child(new St.Icon({ icon_name: icon, icon_type: St.IconType.SYMBOLIC, icon_size: extraClass ? 24 : 18 }));
        b.connect('clicked', cb);
        b._tooltip = new Tooltips.Tooltip(b, tooltip);
        return b;
    }

    _buildMenu() {
        // Cover + track info
        let infoItem = new PopupMenu.PopupBaseMenuItem({ reactive: false });
        let box = new St.BoxLayout({ vertical: true, style_class: 'spotify-box', x_expand: true });
        infoItem.addActor(box, { expand: true, span: -1 });

        this._coverBin = new St.Bin({ x_align: St.Align.MIDDLE });
        box.add(this._coverBin, { x_fill: false, x_align: St.Align.MIDDLE });

        let w = this._menuTextWidth();
        this._titleLabel = new Marquee('spotify-title', w);
        this._artistLabel = new Marquee('spotify-artist', w);
        this._albumLabel = new Marquee('spotify-album', w);
        box.add(this._titleLabel.actor);
        box.add(this._artistLabel.actor);
        box.add(this._albumLabel.actor);

        // Seek bar
        this._seek = new Slider.Slider(0, true);
        this._seek.connect('drag-begin', () => this._seekDragging = true);
        this._seek.connect('drag-end', () => {
            this._seekDragging = false;
        this._artistsCache = {};
        this._fullArtists = null;
        this._previewTimer = 0;
            this._seekTo(this._seek._value);
        });
        this._seek.connect('value-changed', (s, v) => {
            if (!this._seekDragging) this._seekTo(v);
            else this._posLabel.text = fmtTime(v * this._length);
        });
        box.add(this._seek.actor, { expand: true });

        let times = new St.BoxLayout();
        this._posLabel = new St.Label({ style_class: 'spotify-time', text: '0:00' });
        this._lenLabel = new St.Label({ style_class: 'spotify-time', text: '0:00' });
        times.add(this._posLabel, { expand: true, x_fill: false, x_align: St.Align.START });
        times.add(this._lenLabel, { expand: false });
        box.add(times);

        // Transport controls
        let controls = new St.BoxLayout({ style_class: 'spotify-controls' });
        this._shuffleBtn = this._button('media-playlist-shuffle-symbolic', 'Aleatorio',
            () => this._setProp('Shuffle', GLib.Variant.new_boolean(!this._shuffle)));
        let prev = this._button('media-skip-backward-symbolic', 'Anterior', () => this._player('Previous'));
        this._playBtn = this._button('media-playback-start-symbolic', 'Reproducir / pausar',
            () => this._player('PlayPause'), 'spotify-play');
        let next = this._button('media-skip-forward-symbolic', 'Siguiente', () => this._player('Next'));
        this._loopBtn = this._button('media-playlist-repeat-symbolic', 'Repetir', () => this._cycleLoop());
        [this._shuffleBtn, prev, this._playBtn, next, this._loopBtn].forEach(b =>
            controls.add(b, { expand: true, x_fill: false, y_fill: false, y_align: St.Align.MIDDLE }));
        box.add(controls, { x_fill: true });

        this.menu.addMenuItem(infoItem);

        // Spotify's own volume (independent from the system volume)
        this._volItem = new PopupMenu.PopupSliderMenuItem(1);
        this._volIcon = new St.Icon({ icon_name: 'audio-volume-high-symbolic', icon_type: St.IconType.SYMBOLIC, icon_size: 16 });
        this._volItem.removeActor(this._volItem._slider);
        this._volItem.addActor(this._volIcon, { span: 0 });
        this._volItem.addActor(this._volItem._slider, { span: -1, expand: true });
        this._volItem.connect('value-changed', (s, v) => this._setVolume(v));
        this.menu.addMenuItem(this._volItem);

        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());
        this._buildListsSection();
        this.menu.addMenuItem(new PopupMenu.PopupSeparatorMenuItem());

        // Search / open link
        let entryItem = new PopupMenu.PopupBaseMenuItem({ reactive: false });
        this._entry = new St.Entry({ style_class: 'spotify-entry', hint_text: 'Buscar o pegar enlace de Spotify…', can_focus: true, x_expand: true });
        this._entry.clutter_text.connect('activate', () => this._onEntry());
        entryItem.addActor(this._entry, { expand: true, span: -1 });
        this.menu.addMenuItem(entryItem);

        this._copyItem = this.menu.addAction('Copiar enlace de la canción', () => {
            let url = this._meta['xesam:url'];
            if (url) St.Clipboard.get_default().set_text(St.ClipboardType.CLIPBOARD, url);
        });

        this._showItem = this.menu.addAction('Mostrar Spotify', () => this._raise());
        this._quitItem = this.menu.addAction('Cerrar Spotify', () => this._call(IFACE_ROOT, 'Quit'));
    }

    // ---------- Playlists / liked songs (Web API via spotify-lists.py) ----------

    _buildListsSection() {
        this._connectItem = this.menu.addAction('Conectar cuenta de Spotify…', () => this._login());

        this._likeItem = new PopupMenu.PopupSwitchMenuItem('Me gusta', false);
        this._likeItem.connect('toggled', (item, state) => this._toggleLike(state));
        this.menu.addMenuItem(this._likeItem);

        this._listsSub = new PopupMenu.PopupSubMenuMenuItem('Añadir a listas');
        this.menu.addMenuItem(this._listsSub);
        this._listsStatus = new PopupMenu.PopupMenuItem('', { reactive: false });
        this._listsSub.menu.addMenuItem(this._listsStatus);

        this._setListsMode('hidden');
    }

    _trackUri() {
        let m = (this._meta['mpris:trackid'] || '').match(/^\/com\/spotify\/(track|episode)\/([A-Za-z0-9]+)$/);
        return m ? 'spotify:' + m[1] + ':' + m[2] : null;
    }

    _runHelper(args, cb) {
        try {
            let proc = Gio.Subprocess.new(['python3', '-I', HELPER].concat(args),
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            proc.communicate_utf8_async(null, null, (pr, res) => {
                let r;
                try {
                    r = JSON.parse(pr.communicate_utf8_finish(res)[1]);
                } catch (e) {
                    r = { error: 'respuesta no válida' };
                }
                cb(r);
            });
        } catch (e) {
            cb({ error: e.message });
        }
    }

    // mode: hidden | connect | loading | ready | error
    _setListsMode(mode, message) {
        let running = !!this._owner;
        this._connectItem.actor.visible = running && mode === 'connect';
        this._likeItem.actor.visible = running && mode === 'ready';
        this._listsSub.actor.visible = running && ['loading', 'ready', 'error'].includes(mode);
        if (message !== undefined) this._listsStatus.label.text = message;
        this._listsStatus.actor.visible = mode !== 'ready' || !!message;
    }

    _errorText(r) {
        if (r.status === 403)
            return 'Spotify ha denegado el permiso (403): revisa que la app tenga tu usuario y que tengas Premium';
        if (r.status === 429)
            return 'Demasiadas peticiones, prueba en un momento';
        return 'Error: ' + r.error;
    }

    _refreshLists() {
        let uri = this._trackUri();
        if (!this._owner || !uri || this._loggingIn) {
            this._setListsMode('hidden');
            return;
        }
        if (!this.clientId) {
            this._setListsMode('connect');
            return;
        }
        if (this._listsBusy) return;
        this._listsBusy = true;
        this._listsTrack = uri;
        this._setListsMode('loading', 'Cargando listas…');
        this._likeItem.setSensitive(false);
        this._runHelper(['state', uri], r => {
            this._listsBusy = false;
            if (uri !== this._trackUri()) {   // track changed while loading
                if (this.menu.isOpen) this._refreshLists();
                return;
            }
            if (r.error) {
                if (r.status === 401) this._setListsMode('connect');
                else this._setListsMode('error', this._errorText(r));
                return;
            }
            this._fillLists(uri, r);
        });
    }

    _fillLists(uri, state) {
        this._likeItem.setSensitive(true);
        this._likeItem.setToggleState(state.liked);
        this._listsSub.menu.removeAll();
        this._listsStatus = new PopupMenu.PopupMenuItem('', { reactive: false });
        this._listsSub.menu.addMenuItem(this._listsStatus);

        let inCount = 0;
        for (let pl of state.playlists) {
            if (pl.contains) inCount++;
            let item = new PopupMenu.PopupSwitchMenuItem(pl.name, pl.contains);
            item.connect('toggled', (it, on) => this._togglePlaylist(it, pl, uri, on));
            this._listsSub.menu.addMenuItem(item);
        }
        this._listsSub.label.text = inCount ? 'Listas (en ' + inCount + ')' : 'Añadir a listas';
        this._setListsMode('ready', state.playlists.length ? '' : 'No tienes listas propias');
    }

    _togglePlaylist(item, pl, uri, on) {
        item.setSensitive(false);
        this._runHelper([on ? 'add' : 'remove', pl.id, uri], r => {
            item.setSensitive(true);
            if (r.error) {
                item.setToggleState(!on);
                this._setListsMode('ready', this._errorText(r));
            } else {
                pl.contains = on;
                this._listsStatus.actor.visible = false;
            }
        });
    }

    _toggleLike(on) {
        let uri = this._trackUri();
        if (!uri) return;
        this._likeItem.setSensitive(false);
        this._runHelper([on ? 'like' : 'unlike', uri], r => {
            this._likeItem.setSensitive(true);
            if (r.error) {
                this._likeItem.setToggleState(!on);
                this._setListsMode('error', this._errorText(r));
            }
        });
    }

    _login() {
        if (!this.clientId) {
            this.menu.close();
            Util.spawn(['xlet-settings', 'applet', this._uuid, '-i', String(this.instance_id)]);
            return;
        }
        this._loggingIn = true;
        this._connectItem.label.text = 'Autoriza el acceso en el navegador…';
        this._runHelper(['login', this.clientId.trim()], r => {
            this._loggingIn = false;
            this._connectItem.label.text = 'Conectar cuenta de Spotify…';
            if (r.error) {
                this._setListsMode('connect');
                this._connectItem.label.text = 'No se pudo conectar (' + r.error.slice(0, 60) + '). Reintentar…';
            } else {
                Main.notify('Spotify', 'Cuenta conectada' + (r.user ? ': ' + r.user : ''));
                if (this.menu.isOpen) this._refreshLists();
            }
        });
    }

    logout() {
        this._runHelper(['logout'], () => {
            this._listsTrack = null;
            this._setListsMode(this.clientId ? 'connect' : 'hidden');
        });
    }

    _onEntry() {
        let text = this._entry.get_text().trim();
        if (!text) return;
        let uri = toSpotifyUri(text) || 'spotify:search:' + encodeURIComponent(text);
        this._entry.set_text('');
        this.menu.close();
        if (this._owner) {
            this._player('OpenUri', new GLib.Variant('(s)', [uri]));
            if (uri.startsWith('spotify:search:')) this._raise();
        } else {
            Util.spawn(['spotify', '--uri=' + uri]);
        }
    }

    _raise() {
        if (this._owner)
            this._call(IFACE_ROOT, 'Raise');
        else
            Util.spawn(['spotify']);
    }

    _cycleLoop() {
        let next = { None: 'Playlist', Playlist: 'Track', Track: 'None' }[this._loop] || 'None';
        this._setProp('LoopStatus', GLib.Variant.new_string(next));
    }

    _setVolume(v) {
        this._volume = v;
        this._setProp('Volume', GLib.Variant.new_double(v));
        this._updateVolumeIcon();
    }

    _seekTo(fraction) {
        let trackid = this._meta['mpris:trackid'];
        if (!trackid || !this._length) return;
        this._position = Math.round(fraction * this._length);
        this._player('SetPosition', new GLib.Variant('(ox)', [trackid, this._position]));
        this._updateSeek();
    }

    // ---------- UI updates ----------

    _setRunning(running) {
        this._shuffleBtn.reactive = this._playBtn.reactive = this._loopBtn.reactive = running;
        this._volItem.actor.visible = running;
        this._copyItem.actor.visible = running;
        this._quitItem.actor.visible = running;
        this._showItem.label.text = running ? 'Mostrar Spotify' : 'Abrir Spotify';
        if (!running) this._setListsMode('hidden');
        this._updateAll();
    }

    _updateAll() {
        let running = !!this._owner;
        let title = this._meta['xesam:title'] || (running ? 'Spotify' : 'Spotify no está abierto');
        let artist = this._artists();
        let album = this._meta['xesam:album'] || '';
        for (let [m, t] of [[this._titleLabel, title], [this._artistLabel, artist], [this._albumLabel, album],
                            [this._pTitle, title], [this._pArtist, artist], [this._pAlbum, album]]) {
            m.text = t;
            m.actor.visible = !!t;
        }
        this._seek.actor.visible = running && this._length > 0;
        this._lenLabel.get_parent().visible = running && this._length > 0;

        this._playBtn.child.icon_name = this._status === 'Playing'
            ? 'media-playback-pause-symbolic' : 'media-playback-start-symbolic';
        this._shuffleBtn.change_style_pseudo_class('checked', this._shuffle);
        this._setActive(this._shuffleBtn, this._shuffle);
        this._loopBtn.child.icon_name = this._loop === 'Track'
            ? 'media-playlist-repeat-song-symbolic' : 'media-playlist-repeat-symbolic';
        this._setActive(this._loopBtn, this._loop !== 'None');
        this._loopBtn._tooltip.set_text({ None: 'Repetir: no', Playlist: 'Repetir: lista', Track: 'Repetir: canción' }[this._loop] || 'Repetir');

        if (!this._volItem._dragging) this._volItem.setValue(this._volume);
        this._updateVolumeIcon();
        this._updateSeek();
        this._updatePanel();
        if (!running) this._showCover(null);
    }

    _setActive(btn, active) {
        if (active) btn.add_style_class_name('spotify-button-active');
        else btn.remove_style_class_name('spotify-button-active');
    }

    _updateVolumeIcon() {
        let v = this._volume;
        this._volIcon.icon_name = v <= 0 ? 'audio-volume-muted-symbolic'
            : v < 0.34 ? 'audio-volume-low-symbolic'
            : v < 0.67 ? 'audio-volume-medium-symbolic' : 'audio-volume-high-symbolic';
    }

    _updateSeek() {
        if (!this._seekDragging) {
            this._seek.setValue(this._length ? this._position / this._length : 0);
            this._posLabel.text = fmtTime(this._position);
        }
        this._lenLabel.text = fmtTime(this._length);
    }

    _updatePanel() {
        this.actor.opacity = this._owner && this._status === 'Playing' ? 255 : 160;
    }

    // MPRIS only reports the main artist; the public track page lists them all
    _artists() {
        return this._fullArtists || (this._meta['xesam:artist'] || []).join(', ');
    }

    _fetchArtists(url) {
        this._fullArtists = null;
        if (!url || !/open\.spotify\.com\/track\//.test(url)) return;
        if (this._artistsCache[url]) {
            this._fullArtists = this._artistsCache[url];
            return;
        }
        try {
            let proc = Gio.Subprocess.new(['curl', '-sL', '-m', '10', '-A', 'Mozilla/5.0', url],
                Gio.SubprocessFlags.STDOUT_PIPE | Gio.SubprocessFlags.STDERR_SILENCE);
            proc.communicate_utf8_async(null, null, (pr, res) => {
                try {
                    let [, out] = pr.communicate_utf8_finish(res);
                    let m = (out || '').match(/<meta name="music:musician_description" content="([^"]*)"/)
                        || (out || '').match(/<meta property="og:description" content="([^"·]*)·/);
                    if (!m) return;
                    let artists = decodeEntities(m[1].trim());
                    this._artistsCache[url] = artists;
                    if (this._meta['xesam:url'] === url) {
                        this._fullArtists = artists;
                        this._updateAll();
                    }
                } catch (e) {
                    global.logWarning('[spotify-applet] artists: ' + e.message);
                }
            });
        } catch (e) {
            global.logWarning('[spotify-applet] curl: ' + e.message);
        }
    }

    // ---------- Hover preview ----------

    _menuTextWidth() {
        return Math.max(this.coverSize || 220, 240);
    }

    _previewTextWidth() {
        return 220;
    }

    _buildPreview() {
        this._preview = new St.BoxLayout({ style_class: 'spotify-preview', visible: false });
        this._pCoverBin = new St.Bin({ style_class: 'spotify-preview-cover', y_align: St.Align.MIDDLE });
        this._preview.add(this._pCoverBin, { y_fill: false, y_align: St.Align.MIDDLE });
        let text = new St.BoxLayout({ vertical: true, style_class: 'spotify-preview-text' });
        let w = this._previewTextWidth();
        this._pTitle = new Marquee('spotify-title', w);
        this._pArtist = new Marquee('spotify-artist', w);
        this._pAlbum = new Marquee('spotify-album', w);
        text.add(this._pTitle.actor);
        text.add(this._pArtist.actor);
        text.add(this._pAlbum.actor);
        this._preview.add(text, { y_fill: false, y_align: St.Align.MIDDLE });
        Main.uiGroup.add_actor(this._preview);
    }

    _schedulePreview() {
        if (this._previewTimer || this.menu.isOpen) return;
        this._previewTimer = Mainloop.timeout_add(350, () => {
            this._previewTimer = 0;
            this._showPreview();
            return false;
        });
    }

    _showPreview() {
        if (this.menu.isOpen) return;
        this._preview.show();
        this._preview.raise_top();
        [this._pTitle, this._pArtist, this._pAlbum].forEach(m => m.start());

        let [ax, ay] = this.actor.get_transformed_position();
        let [aw, ah] = this.actor.get_transformed_size();
        let pw = this._preview.get_preferred_width(-1)[1];
        let ph = this._preview.get_preferred_height(pw)[1];
        let mon = Main.layoutManager.findMonitorForActor(this.actor);
        let gap = 8, x, y;
        switch (this._orientation) {
            case St.Side.TOP: x = ax + aw / 2 - pw / 2; y = ay + ah + gap; break;
            case St.Side.LEFT: x = ax + aw + gap; y = ay + ah / 2 - ph / 2; break;
            case St.Side.RIGHT: x = ax - pw - gap; y = ay + ah / 2 - ph / 2; break;
            default: x = ax + aw / 2 - pw / 2; y = ay - ph - gap;
        }
        x = Math.max(mon.x + gap, Math.min(x, mon.x + mon.width - pw - gap));
        y = Math.max(mon.y + gap, Math.min(y, mon.y + mon.height - ph - gap));
        this._preview.set_position(Math.round(x), Math.round(y));
    }

    _hidePreview() {
        if (this._previewTimer) Mainloop.source_remove(this._previewTimer);
        this._previewTimer = 0;
        this._preview.hide();
        [this._pTitle, this._pArtist, this._pAlbum].forEach(m => m.stop());
    }

    _onSizesChanged() {
        let w = this._menuTextWidth();
        [this._titleLabel, this._artistLabel, this._albumLabel].forEach(m => m.setWidth(w));
        this._showCover();
    }

    _loadCover(url) {
        if (!url || url === this._coverUrl) return;
        this._coverUrl = url;
        let path = CACHE_DIR + '/' + url.split('/').pop() + '.jpg';
        if (GLib.file_test(path, GLib.FileTest.EXISTS)) {
            this._showCover(path);
            return;
        }
        Util.spawn_async(['wget', '-q', url, '-O', path], () => {
            if (this._coverUrl === url) this._showCover(path);
        });
        this._pruneCache();
    }

    _showCover(path) {
        if (path !== undefined) this._coverPath = path;
        let p = this._coverPath;
        let make = size => (p && GLib.file_test(p, GLib.FileTest.EXISTS))
            ? new St.Icon({ gicon: new Gio.FileIcon({ file: Gio.File.new_for_path(p) }), icon_size: size, icon_type: St.IconType.FULLCOLOR })
            : new St.Icon({ icon_name: 'spotify-client', icon_size: Math.round(size / 2), icon_type: St.IconType.FULLCOLOR });
        this._coverBin.set_child(make(this.coverSize || 220));
        this._pCoverBin.set_child(make(this.previewSize || 96));
    }

    _pruneCache() {
        // keep the cover cache small: drop it all past 200 files
        let dir = Gio.File.new_for_path(CACHE_DIR);
        let en = dir.enumerate_children('standard::name', Gio.FileQueryInfoFlags.NONE, null);
        let names = [], info;
        while ((info = en.next_file(null))) names.push(info.get_name());
        if (names.length > 200)
            names.forEach(n => { try { dir.get_child(n).delete(null); } catch (e) {} });
    }

    _onMenuToggled(open) {
        if (open) {
            this._hidePreview();
            if (this._listsTrack !== this._trackUri() || !this._likeItem.actor.visible) this._refreshLists();
        }
        [this._titleLabel, this._artistLabel, this._albumLabel].forEach(m => open ? m.start() : m.stop());
        if (this._pollId) {
            Mainloop.source_remove(this._pollId);
            this._pollId = 0;
        }
        if (open) {
            this._refreshPosition();
            this._pollId = Mainloop.timeout_add_seconds(1, () => {
                if (this._status === 'Playing') this._refreshPosition();
                return true;
            });
        }
    }

    _onScroll(event) {
        if (!this._owner) return Clutter.EVENT_PROPAGATE;
        let dir = event.get_scroll_direction();
        let step = 0.05;
        if (dir === Clutter.ScrollDirection.UP) this._setVolume(Math.min(1, this._volume + step));
        else if (dir === Clutter.ScrollDirection.DOWN) this._setVolume(Math.max(0, this._volume - step));
        else return Clutter.EVENT_PROPAGATE;
        this._volItem.setValue(this._volume);
        return Clutter.EVENT_STOP;
    }

    // ---------- Applet hooks ----------

    on_applet_clicked() {
        this.menu.toggle();
    }

    on_applet_middle_clicked() {
        if (this._owner) this._player('PlayPause');
        else this._raise();
    }

    on_applet_removed_from_panel() {
        if (this._pollId) Mainloop.source_remove(this._pollId);
        this._hidePreview();
        [this._titleLabel, this._artistLabel, this._albumLabel].forEach(m => m.destroy());
        this._preview.destroy();
        Gio.bus_unwatch_name(this._watchId);
        this._signalIds.forEach(id => Gio.DBus.session.signal_unsubscribe(id));
        this.settings.finalize();
    }
}

function main(metadata, orientation, panelHeight, instanceId) {
    HELPER = metadata.path + '/spotify-lists.py';
    return new SpotifyApplet(metadata, orientation, panelHeight, instanceId);
}
