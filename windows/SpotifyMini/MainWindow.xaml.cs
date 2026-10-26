using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace SpotifyMini
{
    // The popup shown from the tray icon: same menu as the Cinnamon applet.
    public partial class MainWindow : Window
    {
        public Media Media { get; } = new Media();
        public event Action<string> TrackChanged;

        readonly SpotifyApi _api = new SpotifyApi();
        Config _cfg;
        string _trackUri;            // from the Web API: Windows' media info has no Spotify id
        bool _apiShuffle;
        string _apiRepeat = "off";
        TimeSpan _apiLength;
        List<Playlist> _library;
        string _lastPlayed;
        byte[] _shownCover;
        bool _seeking;
        DateTime _hiddenAt;
        CancellationTokenSource _login;
        readonly DispatcherTimer _timer;

        static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x1D, 0xB9, 0x54));
        static readonly Brush White = Brushes.White;

        public MainWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (s, e) => UpdateSeek();
            Deactivated += (s, e) => HidePopup();
            SizeChanged += (s, e) => Reposition();
        }

        public async Task InitAsync()
        {
            _cfg = Config.Load();
            Media.Changed += () => Dispatcher.BeginInvoke(new Action(UpdateUi));
            Media.TrackChanged += () => Dispatcher.BeginInvoke(new Action(async () => await OnTrackChanged()));
            UpdateAccountUi();
            try
            {
                await Media.InitAsync();
            }
            catch (Exception ex)
            {
                ShowStatus("No se pudo acceder a los controles multimedia de Windows: " + ex.Message);
            }
        }

        // ---------- popup ----------

        public void Toggle()
        {
            if (IsVisible) HidePopup();
            // the click on the tray icon already hid it (deactivate) a moment ago
            else if ((DateTime.Now - _hiddenAt).TotalMilliseconds > 300) ShowPopup();
        }

        public void ShowPopup()
        {
            MaxHeight = SystemParameters.WorkArea.Height - 24;
            Show();
            Activate();
            Reposition();
            _timer.Start();
            UpdateUi();
            _ = RefreshPlayerState();
            if (_api.LoggedIn && _library == null) _ = LoadLibrary();
        }

        void HidePopup()
        {
            if (!IsVisible) return;
            _hiddenAt = DateTime.Now;
            _timer.Stop();
            Hide();
            ResultsPanel.Children.Clear();
            SearchBox.Text = "";
        }

        // bottom-right, above the taskbar
        void Reposition()
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Right - ActualWidth - 12;
            Top = wa.Bottom - ActualHeight - 12;
        }

        // ---------- state ----------

        async Task OnTrackChanged()
        {
            TrackChanged?.Invoke(Media.Running && Media.Title != ""
                ? Media.Title + " — " + Media.Artist : "Spotify");
            AddListsPanel.Children.Clear();
            AddListsHeader.IsChecked = false;
            AddListsHeader.Content = "Añadir a listas";
            if (!Media.Running) _trackUri = null;
            await RefreshPlayerState();
        }

        async Task RefreshPlayerState()
        {
            if (!_api.LoggedIn || !Media.Running) return;
            try
            {
                var s = await _api.State();
                if (s == null) return;
                _trackUri = s.TrackUri;
                _apiShuffle = s.Shuffle;
                _apiRepeat = s.Repeat;
                _apiLength = TimeSpan.FromMilliseconds(s.DurationMs);
                Media.SetPosition(TimeSpan.FromMilliseconds(s.ProgressMs));
                if (s.Volume is int v && Mouse.LeftButton != MouseButtonState.Pressed) VolumeSlider.Value = v;
                if (_trackUri != null)
                    SetLiked(await _api.IsLiked(_trackUri));
                UpdateUi();
            }
            catch (ApiException e) when (e.Status == 401)
            {
                UpdateAccountUi();
            }
            catch (Exception)
            {
                // network hiccup: keep what Windows reports
            }
        }

        bool CurrentShuffle => Media.Shuffle ?? _apiShuffle;
        string CurrentRepeat => Media.Repeat ?? _apiRepeat;
        TimeSpan Length => Media.Length > TimeSpan.Zero ? Media.Length : _apiLength;

        void UpdateUi()
        {
            bool running = Media.Running;
            TitleText.Text = running ? (Media.Title != "" ? Media.Title : "Spotify") : "Spotify no está abierto";
            ArtistText.Text = running ? Media.Artist : "";
            AlbumText.Text = running ? Media.Album : "";
            if (!ReferenceEquals(Media.Cover, _shownCover))
            {
                _shownCover = Media.Cover;
                CoverImage.Source = LoadImage(Media.Cover);
            }
            PlayButton.Content = Media.Playing ? "" : "";
            ShuffleButton.Foreground = CurrentShuffle ? Green : White;
            RepeatButton.Foreground = CurrentRepeat != "off" ? Green : White;
            RepeatButton.Content = CurrentRepeat == "track" ? "" : "";
            RepeatButton.ToolTip = CurrentRepeat switch { "context" => "Repetir: lista", "track" => "Repetir: canción", _ => "Repetir: no" };
            ShowText.Text = running ? "Mostrar Spotify" : "Abrir Spotify";
            LikeButton.Visibility = _api.LoggedIn && _trackUri != null ? Visibility.Visible : Visibility.Collapsed;
            AddListsHeader.Visibility = _api.LoggedIn && _trackUri != null ? Visibility.Visible : Visibility.Collapsed;
            UpdateSeek();
        }

        static BitmapImage LoadImage(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0) return null;
            try
            {
                var img = new BitmapImage();
                img.BeginInit();
                img.CacheOption = BitmapCacheOption.OnLoad;
                img.StreamSource = new MemoryStream(bytes);
                img.EndInit();
                img.Freeze();
                return img;
            }
            catch
            {
                return null;
            }
        }

        void UpdateSeek()
        {
            var len = Length;
            SeekSlider.Visibility = Media.Running && len > TimeSpan.Zero ? Visibility.Visible : Visibility.Hidden;
            LengthText.Text = Fmt(len);
            if (_seeking) return;
            var pos = Media.CurrentPosition;
            if (len > TimeSpan.Zero && pos > len) pos = len;
            SeekSlider.Value = len > TimeSpan.Zero ? pos.TotalMilliseconds / len.TotalMilliseconds : 0;
            PositionText.Text = Fmt(pos);
        }

        static string Fmt(TimeSpan t) => $"{(int)t.TotalMinutes}:{t.Seconds:00}";

        void UpdateAccountUi()
        {
            bool on = _api.LoggedIn;
            ConnectButton.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
            PlayListsHeader.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            VolumeRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            if (!on)
            {
                PlayListsHeader.IsChecked = AddListsHeader.IsChecked = false;
                _library = null;
                _trackUri = null;
            }
            UpdateUi();
        }

        void ShowStatus(string text)
        {
            StatusText.Text = text ?? "";
            StatusText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        }

        static string ErrorText(Exception e) => e switch
        {
            ApiException { Status: 403 } => "Spotify ha denegado el permiso (403): revisa que la app tenga tu usuario y que tengas Premium",
            ApiException { Status: 429 } => "Demasiadas peticiones, prueba en un momento",
            ApiException { Status: 404, Message: "no-device" } => "Spotify no ha terminado de abrirse; prueba otra vez en unos segundos",
            ApiException { Status: 401 } => "La sesión ha caducado: vuelve a conectar la cuenta",
            _ => "Error: " + e.Message,
        };

        // ---------- transport ----------

        async void Play_Click(object sender, RoutedEventArgs e) => await Media.PlayPause();
        async void Next_Click(object sender, RoutedEventArgs e) => await Media.Next();
        async void Prev_Click(object sender, RoutedEventArgs e) => await Media.Previous();

        async void Shuffle_Click(object sender, RoutedEventArgs e)
        {
            bool on = !CurrentShuffle;
            if (await Media.SetShuffle(on)) return;
            await ApiAction(async () =>
            {
                await _api.SetShuffle(on);
                _apiShuffle = on;
            });
            UpdateUi();
        }

        async void Repeat_Click(object sender, RoutedEventArgs e)
        {
            string next = CurrentRepeat switch { "off" => "context", "context" => "track", _ => "off" };
            if (await Media.SetRepeat(next)) return;
            await ApiAction(async () =>
            {
                await _api.SetRepeat(next);
                _apiRepeat = next;
            });
            UpdateUi();
        }

        void Seek_Down(object sender, MouseButtonEventArgs e) => _seeking = true;

        async void Seek_Up(object sender, MouseButtonEventArgs e)
        {
            // let the slider move to the click point first
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            _seeking = false;
            var target = TimeSpan.FromMilliseconds(SeekSlider.Value * Length.TotalMilliseconds);
            Media.SetPosition(target);
            if (!await Media.Seek(target))
                await ApiAction(() => _api.Seek((long)target.TotalMilliseconds));
            UpdateSeek();
        }

        async void Volume_Up(object sender, MouseButtonEventArgs e)
        {
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            await ApiAction(() => _api.SetVolume((int)VolumeSlider.Value));
        }

        async Task ApiAction(Func<Task> action)
        {
            if (!_api.LoggedIn) return;
            try
            {
                await action();
                ShowStatus(null);
            }
            catch (Exception ex)
            {
                ShowStatus(ErrorText(ex));
            }
        }

        // ---------- like / lists ----------

        bool _liked;

        void SetLiked(bool liked)
        {
            _liked = liked;
            LikeButton.Content = liked ? "" : "";
            LikeButton.Foreground = liked ? Green : White;
        }

        async void Like_Click(object sender, RoutedEventArgs e)
        {
            if (_trackUri == null) return;
            bool want = !_liked;
            SetLiked(want);
            try
            {
                await _api.SetLiked(_trackUri, want);
            }
            catch (Exception ex)
            {
                SetLiked(!want);
                ShowStatus(ErrorText(ex));
            }
        }

        async Task LoadLibrary()
        {
            try
            {
                _library = await _api.Library();
            }
            catch (Exception ex)
            {
                if (ex is ApiException { Status: 401 }) UpdateAccountUi();
                else ShowStatus(ErrorText(ex));
                return;
            }
            PlayListsPanel.Children.Clear();
            foreach (var pl in _library)
                PlayListsPanel.Children.Add(PlayRow(pl.Liked ? "" : "", pl.Name, null, pl.Uri));
        }

        // a row that plays uri and stays in the popup, marked once chosen
        Button PlayRow(string glyph, string name, string sub, string uri)
        {
            var text = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            text.Inlines.Add(new System.Windows.Documents.Run(name));
            if (!string.IsNullOrEmpty(sub))
                text.Inlines.Add(new System.Windows.Documents.Run("  ·  " + sub) { Foreground = Brushes.Gray });
            var row = new DockPanel();
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = (FontFamily)FindResource("Icons"), Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            DockPanel.SetDock(icon, Dock.Left);
            row.Children.Add(icon);
            row.Children.Add(text);
            var b = new Button { Style = (Style)FindResource("RowButton"), Content = row, Tag = uri };
            b.Click += async (s, e) => await PlayUri(uri);
            MarkChosen(b);
            return b;
        }

        void MarkChosen(Button b)
        {
            bool chosen = (string)b.Tag == _lastPlayed;
            b.Foreground = chosen ? Green : White;
            b.FontWeight = chosen ? FontWeights.Bold : FontWeights.Normal;
        }

        void MarkAllChosen()
        {
            foreach (var b in PlayListsPanel.Children.OfType<Button>().Concat(ResultsPanel.Children.OfType<Button>()))
                MarkChosen(b);
        }

        // Plays through the Web API, opening Spotify (minimized) if needed.
        // Lists follow the shuffle button: on = start at a random song.
        async Task PlayUri(string uri)
        {
            if (!_api.LoggedIn)
            {
                SpotifyApp.Start(uri);
                return;
            }
            if (!Media.Running) SpotifyApp.Start();
            _lastPlayed = uri;
            MarkAllChosen();
            try
            {
                await _api.Play(uri, CurrentShuffle);
                ShowStatus(null);
            }
            catch (Exception ex)
            {
                _lastPlayed = null;
                MarkAllChosen();
                ShowStatus(ErrorText(ex));
            }
        }

        async void AddLists_Checked(object sender, RoutedEventArgs e)
        {
            if (_trackUri == null || AddListsPanel.Children.Count > 0) return;
            string track = _trackUri;
            ShowStatus("Cargando listas…");
            try
            {
                var (liked, lists) = await _api.TrackState(track);
                if (track != _trackUri) return;   // song changed meanwhile
                SetLiked(liked);
                AddListsPanel.Children.Clear();
                foreach (var (pl, contains) in lists)
                {
                    var cb = new CheckBox { Content = pl.Name, IsChecked = contains };
                    cb.Click += async (s, ev) =>
                    {
                        bool add = cb.IsChecked == true;
                        cb.IsEnabled = false;
                        try
                        {
                            await _api.SetInPlaylist(pl.Id, track, add);
                        }
                        catch (Exception ex)
                        {
                            cb.IsChecked = !add;
                            ShowStatus(ErrorText(ex));
                        }
                        cb.IsEnabled = true;
                        UpdateListsCount();
                    };
                    AddListsPanel.Children.Add(cb);
                }
                UpdateListsCount();
                ShowStatus(lists.Count == 0 ? "No tienes listas propias" : null);
            }
            catch (Exception ex)
            {
                ShowStatus(ErrorText(ex));
            }
        }

        void UpdateListsCount()
        {
            int n = AddListsPanel.Children.OfType<CheckBox>().Count(c => c.IsChecked == true);
            AddListsHeader.Content = n > 0 ? $"Listas (en {n})" : "Añadir a listas";
        }

        // ---------- search ----------

        void Search_TextChanged(object sender, TextChangedEventArgs e) =>
            SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        async void Search_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                HidePopup();
                return;
            }
            if (e.Key != Key.Enter) return;
            string text = SearchBox.Text.Trim();
            if (text == "") return;
            string uri = SpotifyApi.ToSpotifyUri(text);
            if (uri != null)
            {
                SearchBox.Text = "";
                await PlayUri(uri);
                return;
            }
            if (!_api.LoggedIn)
            {
                // no account: let the app search
                SpotifyApp.Start("spotify:search:" + Uri.EscapeDataString(text));
                SpotifyApp.Show();
                HidePopup();
                return;
            }
            ResultsPanel.Children.Clear();
            ResultsPanel.Children.Add(new TextBlock { Text = "Buscando…", Foreground = Brushes.Gray, Margin = new Thickness(8, 4, 8, 4) });
            try
            {
                var items = await _api.Search(text);
                ResultsPanel.Children.Clear();
                foreach (var it in items)
                {
                    string glyph = it.Kind switch { "track" => "", "album" => "", "artist" => "", _ => "" };
                    ResultsPanel.Children.Add(PlayRow(glyph, it.Name, it.Sub, it.Uri));
                }
                if (items.Count == 0)
                    ResultsPanel.Children.Add(new TextBlock { Text = "Sin resultados", Foreground = Brushes.Gray, Margin = new Thickness(8, 4, 8, 4) });
            }
            catch (Exception ex)
            {
                ResultsPanel.Children.Clear();
                ShowStatus(ErrorText(ex));
            }
        }

        // ---------- account / settings ----------

        async void Connect_Click(object sender, RoutedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(_cfg.ClientId))
            {
                OpenSettings(true);
                ClientIdBox.Focus();
                ShowStatus("Pega primero el Client ID de tu app de Spotify");
                return;
            }
            _login?.Cancel();
            _login = new CancellationTokenSource();
            ConnectText.Text = "Autoriza el acceso en el navegador…";
            try
            {
                string user = await _api.Login(_cfg.ClientId.Trim(), _login.Token);
                ShowStatus("Cuenta conectada: " + user);
                UpdateAccountUi();
                await LoadLibrary();
                await RefreshPlayerState();
            }
            catch (OperationCanceledException)
            {
                ShowStatus("No se completó la conexión a tiempo");
            }
            catch (Exception ex)
            {
                ShowStatus("No se pudo conectar: " + ex.Message);
            }
            ConnectText.Text = "Conectar cuenta de Spotify…";
        }

        void Logout_Click(object sender, RoutedEventArgs e)
        {
            _api.Logout();
            ShowStatus("Cuenta desconectada");
            UpdateAccountUi();
        }

        void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings(SettingsPanel.Visibility != Visibility.Visible);

        void OpenSettings(bool open)
        {
            SettingsPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            if (!open) return;
            ClientIdBox.Text = _cfg.ClientId;
            StartupCheck.IsChecked = Config.StartWithWindows;
        }

        void ClientId_LostFocus(object sender, RoutedEventArgs e)
        {
            _cfg.ClientId = ClientIdBox.Text.Trim();
            _cfg.Save();
        }

        void Startup_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Config.StartWithWindows = StartupCheck.IsChecked == true;
            }
            catch (Exception ex)
            {
                ShowStatus("No se pudo cambiar el inicio con Windows: " + ex.Message);
            }
        }

        void Show_Click(object sender, RoutedEventArgs e)
        {
            HidePopup();
            SpotifyApp.Show();
        }

        void Copy_Click(object sender, RoutedEventArgs e)
        {
            var parts = _trackUri?.Split(':');
            if (parts?.Length == 3)
                Clipboard.SetText($"https://open.spotify.com/{parts[1]}/{parts[2]}");
        }
    }
}
