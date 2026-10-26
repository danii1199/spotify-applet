#!/usr/bin/env python3
"""Spotify Web API helper for the spotify-applet applet.

Every command prints one JSON object on stdout.

  login CLIENT_ID          authorize in the browser (PKCE, no client secret)
  logout
  status
  state TRACK_URI          liked flag + editable playlists with "contains"
  add PLAYLIST_ID TRACK_URI
  remove PLAYLIST_ID TRACK_URI
  like TRACK_URI
  unlike TRACK_URI
  library                  Me gusta + every playlist in the library (to play them)
  search QUERY             tracks, albums, artists and playlists
  play DEVICE SHUFFLE URI  play URI on the device named DEVICE (or the active / a
                           computer one if ""); SHUFFLE 1 starts a list at a
                           random song and leaves shuffle on, 0 turns it off
"""
import base64
import hashlib
import http.server
import json
import os
import random
import secrets
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

CONFIG_DIR = os.path.expanduser('~/.config/spotify-applet')
TOKEN_FILE = os.path.join(CONFIG_DIR, 'token.json')
CACHE_FILE = os.path.expanduser('~/.cache/spotify-applet/playlists.json')
REDIRECT_PORT = 8898
REDIRECT_URI = f'http://127.0.0.1:{REDIRECT_PORT}/callback'
API = 'https://api.spotify.com/v1'
SCOPES = ' '.join([
    'playlist-read-private', 'playlist-read-collaborative',
    'playlist-modify-private', 'playlist-modify-public',
    'user-library-read', 'user-library-modify',
    'user-follow-read', 'user-follow-modify',
    'user-read-playback-state', 'user-modify-playback-state',
])
PLAY_SCOPE = 'user-modify-playback-state'


class ApiError(Exception):
    def __init__(self, status, message):
        super().__init__(message)
        self.status = status


def out(obj):
    print(json.dumps(obj, ensure_ascii=False))
    sys.exit(0)


def load_json(path, default):
    try:
        with open(path) as f:
            return json.load(f)
    except (OSError, ValueError):
        return default


def save_json(path, data, private=False):
    os.makedirs(os.path.dirname(path), exist_ok=True)
    tmp = path + '.tmp'
    with open(tmp, 'w') as f:
        json.dump(data, f)
    if private:
        os.chmod(tmp, 0o600)
    os.replace(tmp, path)


# ---------- auth ----------

def token_request(params):
    req = urllib.request.Request('https://accounts.spotify.com/api/token',
                                 data=urllib.parse.urlencode(params).encode(),
                                 headers={'Content-Type': 'application/x-www-form-urlencoded'})
    try:
        with urllib.request.urlopen(req, timeout=15) as r:
            return json.load(r)
    except urllib.error.HTTPError as e:
        raise ApiError(e.code, e.read().decode(errors='replace'))


def store_token(tok, client_id, old=None):
    data = {
        'client_id': client_id,
        'access_token': tok['access_token'],
        'refresh_token': tok.get('refresh_token') or (old or {}).get('refresh_token'),
        'expires_at': time.time() + tok.get('expires_in', 3600),
        'scope': tok.get('scope') or (old or {}).get('scope', ''),
    }
    save_json(TOKEN_FILE, data, private=True)
    return data


def access_token():
    tok = load_json(TOKEN_FILE, None)
    if not tok or not tok.get('refresh_token'):
        raise ApiError(401, 'not-logged-in')
    if tok['expires_at'] - 60 < time.time():
        new = token_request({'grant_type': 'refresh_token',
                             'refresh_token': tok['refresh_token'],
                             'client_id': tok['client_id']})
        tok = store_token(new, tok['client_id'], tok)
    return tok['access_token']


def login(client_id):
    verifier = secrets.token_urlsafe(64)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(verifier.encode()).digest()).rstrip(b'=').decode()
    state = secrets.token_urlsafe(16)
    url = 'https://accounts.spotify.com/authorize?' + urllib.parse.urlencode({
        'client_id': client_id, 'response_type': 'code', 'redirect_uri': REDIRECT_URI,
        'scope': SCOPES, 'state': state, 'code_challenge_method': 'S256',
        'code_challenge': challenge,
    })
    result = {}

    class Handler(http.server.BaseHTTPRequestHandler):
        def do_GET(self):
            q = urllib.parse.parse_qs(urllib.parse.urlparse(self.path).query)
            if not self.path.startswith('/callback'):
                self.send_response(404)
                self.end_headers()
                return
            ok = q.get('state', [''])[0] == state and 'code' in q
            if ok:
                result['code'] = q['code'][0]
            else:
                result['error'] = q.get('error', ['bad-state'])[0]
            self.send_response(200)
            self.send_header('Content-Type', 'text/html; charset=utf-8')
            self.end_headers()
            msg = 'Cuenta conectada. Ya puedes cerrar esta pestaña.' if ok else 'No se pudo conectar: ' + result['error']
            self.wfile.write(f'<html><body style="font-family:sans-serif;background:#121212;color:#fff;'
                             f'display:flex;align-items:center;justify-content:center;height:100vh">'
                             f'<h2>{msg}</h2></body></html>'.encode())

        def log_message(self, *args):
            pass

    server = http.server.HTTPServer(('127.0.0.1', REDIRECT_PORT), Handler)
    server.timeout = 1
    subprocess.Popen(['xdg-open', url], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    deadline = time.time() + 300
    while not result and time.time() < deadline:
        server.handle_request()
    server.server_close()
    if 'code' not in result:
        out({'error': result.get('error', 'timeout')})
    tok = token_request({'grant_type': 'authorization_code', 'code': result['code'],
                         'redirect_uri': REDIRECT_URI, 'client_id': client_id,
                         'code_verifier': verifier})
    store_token(tok, client_id)
    out({'ok': True, 'user': api('GET', '/me').get('display_name')})


# ---------- API ----------

def api(method, path, params=None, body=None):
    url = path if path.startswith('http') else API + path
    if params:
        url += '?' + urllib.parse.urlencode(params)
    data = json.dumps(body).encode() if body is not None else (b'' if method != 'GET' else None)
    headers = {'Authorization': 'Bearer ' + access_token()}
    if body is not None:
        headers['Content-Type'] = 'application/json'
    for attempt in range(3):
        req = urllib.request.Request(url, data=data, method=method, headers=headers)
        try:
            with urllib.request.urlopen(req, timeout=20) as r:
                raw = r.read()
                try:
                    return json.loads(raw) if raw.strip() else {}
                except ValueError:   # some player endpoints answer with plain text
                    return {}
        except urllib.error.HTTPError as e:
            if e.code == 429 and attempt < 2:
                time.sleep(min(int(e.headers.get('Retry-After', '2')), 10))
                continue
            text = e.read().decode(errors='replace')
            try:
                text = json.loads(text)['error']['message']
            except (ValueError, KeyError, TypeError):
                pass
            raise ApiError(e.code, text)


def paged(path, params):
    url, items = path, []
    while url:
        page = api('GET', url, params if url == path else None)
        items += page.get('items') or []
        url = page.get('next')
    return items


def editable_playlists():
    me = api('GET', '/me')['id']
    return [p for p in paged('/me/playlists', {'limit': 50})
            if p and ((p.get('owner') or {}).get('id') == me or p.get('collaborative'))]


def playlist_uris(pl, cache):
    entry = cache.get(pl['id'])
    if entry and entry['snapshot'] == pl['snapshot_id']:
        return set(entry['uris'])
    uris = set()
    for it in paged(f"/playlists/{pl['id']}/items", {'limit': 50}):
        obj = it.get('item') or it.get('track') or {}
        if obj.get('uri'):
            uris.add(obj['uri'])
    cache[pl['id']] = {'snapshot': pl['snapshot_id'], 'uris': sorted(uris)}
    return uris


def cmd_state(track):
    cache = load_json(CACHE_FILE, {})
    result = []
    for pl in editable_playlists():
        result.append({
            'id': pl['id'],
            'name': pl['name'],
            'contains': track in playlist_uris(pl, cache),
        })
    save_json(CACHE_FILE, cache)
    liked = api('GET', '/me/library/contains', {'uris': track})
    out({'liked': bool(liked and liked[0]), 'playlists': result})


def names(objs):
    return ', '.join(o.get('name', '') for o in objs or [])


def cmd_library():
    me = api('GET', '/me')['id']
    lists = [{'name': 'Canciones que te gustan', 'uri': f'spotify:user:{me}:collection', 'liked': True}]
    for p in paged('/me/playlists', {'limit': 50}):
        if p:
            lists.append({'name': p['name'], 'uri': p['uri'],
                          'mine': (p.get('owner') or {}).get('id') == me})
    out({'playlists': lists})


def cmd_search(query):
    r = api('GET', '/search', {'q': query, 'type': 'track,album,artist,playlist', 'limit': 5})
    items = lambda k: [o for o in (r.get(k) or {}).get('items') or [] if o]
    out({
        'tracks': [{'name': t['name'], 'sub': names(t.get('artists')), 'uri': t['uri'],
                    'context': (t.get('album') or {}).get('uri')} for t in items('tracks')],
        'albums': [{'name': a['name'], 'sub': names(a.get('artists')), 'uri': a['uri']} for a in items('albums')],
        'artists': [{'name': a['name'], 'sub': '', 'uri': a['uri']} for a in items('artists')],
        'playlists': [{'name': p['name'], 'sub': (p.get('owner') or {}).get('display_name') or '', 'uri': p['uri']}
                      for p in items('playlists')],
    })


def pick_device(name):
    # a freshly started player takes a few seconds to show up in Spotify Connect
    for attempt in range(12):
        devices = api('GET', '/me/player/devices').get('devices') or []
        if name:
            found = [d for d in devices if d.get('name', '').lower() == name.lower()]
        else:
            found = sorted([d for d in devices if d.get('is_active') or d.get('type') == 'Computer'],
                           key=lambda d: not d.get('is_active'))
        if found:
            return found[0]['id']
        time.sleep(1)
    return None


def context_size(uri):
    kind, cid = uri.split(':')[1], uri.split(':')[-1]
    path = {'playlist': f'/playlists/{cid}/items', 'album': f'/albums/{cid}/tracks'}.get(kind)
    if uri.endswith(':collection'):
        path = '/me/tracks'
    return api('GET', path, {'limit': 1}).get('total') or 0 if path else 0


def cmd_play(device, shuffle, uri):
    tok = load_json(TOKEN_FILE, {})
    if PLAY_SCOPE not in (tok.get('scope') or '').split():
        out({'error': 'falta el permiso para reproducir', 'status': 'scope'})
    dev = pick_device(device)
    if not dev:
        out({'error': 'no-device', 'status': 'no-device'})
    if uri.split(':')[1] in ('track', 'episode'):
        body = {'uris': [uri]}   # like the app: the song, then similar ones (autoplay)
    else:
        body = {'context_uri': uri}
        size = context_size(uri) if shuffle == '1' else 0
        if size > 1:
            body['offset'] = {'position': random.randrange(size)}
    api('PUT', '/me/player/play', {'device_id': dev}, body)
    if shuffle in ('0', '1'):
        # the device may need a moment to take over before it accepts the setting
        for attempt in range(3):
            try:
                api('PUT', '/me/player/shuffle', {'state': 'true' if shuffle == '1' else 'false', 'device_id': dev})
                break
            except ApiError:
                time.sleep(0.7)
    out({'ok': True})


def update_cache(pid, snapshot, track, present):
    cache = load_json(CACHE_FILE, {})
    entry = cache.get(pid)
    if entry is not None:
        uris = set(entry['uris'])
        (uris.add if present else uris.discard)(track)
        cache[pid] = {'snapshot': snapshot, 'uris': sorted(uris)}
        save_json(CACHE_FILE, cache)


def main(argv):
    if not argv:
        out({'error': 'usage'})
    cmd, args = argv[0], argv[1:]
    try:
        if cmd == 'login':
            login(args[0])
        elif cmd == 'logout':
            for f in (TOKEN_FILE, CACHE_FILE):
                if os.path.exists(f):
                    os.remove(f)
            out({'ok': True})
        elif cmd == 'status':
            tok = load_json(TOKEN_FILE, None)
            out({'logged_in': bool(tok and tok.get('refresh_token'))})
        elif cmd == 'state':
            cmd_state(args[0])
        elif cmd == 'add':
            r = api('POST', f'/playlists/{args[0]}/items', body={'uris': [args[1]]})
            update_cache(args[0], r.get('snapshot_id'), args[1], True)
            out({'ok': True})
        elif cmd == 'remove':
            r = api('DELETE', f'/playlists/{args[0]}/items', body={'items': [{'uri': args[1]}]})
            update_cache(args[0], r.get('snapshot_id'), args[1], False)
            out({'ok': True})
        elif cmd == 'like':
            api('PUT', '/me/library', {'uris': args[0]})
            out({'ok': True})
        elif cmd == 'unlike':
            api('DELETE', '/me/library', {'uris': args[0]})
            out({'ok': True})
        elif cmd == 'library':
            cmd_library()
        elif cmd == 'search':
            cmd_search(args[0])
        elif cmd == 'play':
            cmd_play(*args[:3])
        else:
            out({'error': 'unknown command'})
    except ApiError as e:
        out({'error': str(e), 'status': e.status})
    except (urllib.error.URLError, OSError) as e:
        out({'error': 'network: ' + str(e)})


if __name__ == '__main__':
    main(sys.argv[1:])
