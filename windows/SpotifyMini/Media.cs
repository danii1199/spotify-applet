using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyMini
{
    // Spotify through Windows' media controls (the same ones the volume flyout uses).
    // Windows counterpart of MPRIS in the Linux applet.
    public class Media
    {
        GlobalSystemMediaTransportControlsSessionManager _manager;
        GlobalSystemMediaTransportControlsSession _session;

        public string Title { get; private set; } = "";
        public string Artist { get; private set; } = "";
        public string Album { get; private set; } = "";
        public byte[] Cover { get; private set; }
        public bool Playing { get; private set; }
        // null when Spotify doesn't report them through Windows
        public bool? Shuffle { get; private set; }
        public string Repeat { get; private set; }   // off | context | track
        public bool Running => _session != null;
        public TimeSpan Position { get; private set; }
        public TimeSpan Length { get; private set; }
        public DateTimeOffset PositionStamp { get; private set; }

        // raised on a background thread
        public event Action Changed;
        public event Action TrackChanged;

        public async Task InitAsync()
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            _manager.SessionsChanged += (s, e) => PickSession();
            PickSession();
        }

        static bool IsSpotify(GlobalSystemMediaTransportControlsSession s) =>
            s.SourceAppUserModelId.IndexOf("spotify", StringComparison.OrdinalIgnoreCase) >= 0;

        void PickSession()
        {
            var s = _manager.GetSessions().FirstOrDefault(IsSpotify);
            if (s?.SourceAppUserModelId == _session?.SourceAppUserModelId && s != null) return;
            if (_session != null)
            {
                _session.MediaPropertiesChanged -= OnProps;
                _session.PlaybackInfoChanged -= OnPlayback;
                _session.TimelinePropertiesChanged -= OnTimeline;
            }
            _session = s;
            if (s == null)
            {
                Title = Artist = Album = "";
                Cover = null;
                Playing = false;
                Length = Position = TimeSpan.Zero;
                TrackChanged?.Invoke();
                Changed?.Invoke();
                return;
            }
            s.MediaPropertiesChanged += OnProps;
            s.PlaybackInfoChanged += OnPlayback;
            s.TimelinePropertiesChanged += OnTimeline;
            OnPlayback(s, null);
            OnTimeline(s, null);
            OnProps(s, null);
        }

        async void OnProps(GlobalSystemMediaTransportControlsSession s, MediaPropertiesChangedEventArgs e)
        {
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                if (p == null) return;
                bool newTrack = p.Title != Title || p.Artist != Artist;
                Title = p.Title ?? "";
                Artist = p.Artist ?? "";
                Album = p.AlbumTitle ?? "";
                Cover = await ReadThumbnail(p.Thumbnail);
                if (newTrack) TrackChanged?.Invoke();
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        static async Task<byte[]> ReadThumbnail(IRandomAccessStreamReference thumb)
        {
            if (thumb == null) return null;
            using var stream = await thumb.OpenReadAsync();
            using var ms = new MemoryStream();
            await stream.AsStreamForRead().CopyToAsync(ms);
            return ms.ToArray();
        }

        void OnPlayback(GlobalSystemMediaTransportControlsSession s, PlaybackInfoChangedEventArgs e)
        {
            var info = s.GetPlaybackInfo();
            Playing = info?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Shuffle = info?.IsShuffleActive;
            Repeat = info?.AutoRepeatMode switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.List => "context",
                Windows.Media.MediaPlaybackAutoRepeatMode.Track => "track",
                Windows.Media.MediaPlaybackAutoRepeatMode.None => "off",
                _ => null,
            };
            Changed?.Invoke();
        }

        void OnTimeline(GlobalSystemMediaTransportControlsSession s, TimelinePropertiesChangedEventArgs e)
        {
            var t = s.GetTimelineProperties();
            if (t == null) return;
            Position = t.Position;
            Length = t.EndTime - t.StartTime;
            PositionStamp = t.LastUpdatedTime;
            Changed?.Invoke();
        }

        // position extrapolated since the last report, so the bar moves smoothly
        public TimeSpan CurrentPosition
        {
            get
            {
                var p = Position;
                if (Playing && PositionStamp != default)
                    p += DateTimeOffset.Now - PositionStamp;
                return Length > TimeSpan.Zero && p > Length ? Length : p;
            }
        }

        public void SetPosition(TimeSpan p)
        {
            Position = p;
            PositionStamp = DateTimeOffset.Now;
        }

        public async Task PlayPause()
        {
            if (_session != null) await _session.TryTogglePlayPauseAsync();
            else SpotifyApp.Start();
        }

        public async Task Next()
        {
            if (_session != null) await _session.TrySkipNextAsync();
        }

        public async Task Previous()
        {
            if (_session != null) await _session.TrySkipPreviousAsync();
        }

        public async Task<bool> SetShuffle(bool on)
        {
            return _session != null && Shuffle != null && await _session.TryChangeShuffleActiveAsync(on);
        }

        public async Task<bool> SetRepeat(string state)
        {
            var mode = state == "track" ? Windows.Media.MediaPlaybackAutoRepeatMode.Track
                     : state == "context" ? Windows.Media.MediaPlaybackAutoRepeatMode.List
                     : Windows.Media.MediaPlaybackAutoRepeatMode.None;
            return _session != null && Repeat != null && await _session.TryChangeAutoRepeatModeAsync(mode);
        }

        public async Task<bool> Seek(TimeSpan p)
        {
            return _session != null && await _session.TryChangePlaybackPositionAsync(p.Ticks);
        }
    }

    public static class SpotifyApp
    {
        // Classic installer lives in %APPDATA%\Spotify; the Microsoft Store one only
        // through its app id. --minimized starts it without showing the window.
        public static void Start(string uri = null)
        {
            string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                      "Spotify", "Spotify.exe");
            try
            {
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe, uri == null ? "--minimized" : "--minimized --uri=" + uri)
                    { UseShellExecute = true });
                else
                    Process.Start(new ProcessStartInfo(uri ?? "spotify:") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }

        public static void Show()
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Debug.WriteLine(ex);
            }
        }
    }
}
