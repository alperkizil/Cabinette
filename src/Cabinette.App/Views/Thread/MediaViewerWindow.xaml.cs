using System.IO;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Cabinette.App.Localization;
using Cabinette.App.ViewModels.Thread;

namespace Cabinette.App.Views.Thread;

/// <summary>
/// Simple built-in viewer for a thread's images and videos. Left/Right browse, Space plays/pauses, Esc closes.
/// Videos use a WPF <see cref="System.Windows.Controls.MediaElement"/>; "Open in default player" hands over to the shell.
/// </summary>
public partial class MediaViewerWindow : Window
{
    private const int PreviewSizePx = 660;

    private readonly IReadOnlyList<ThreadItemViewModel> _items;
    private readonly Action<ThreadItemViewModel> _openExternally;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _loadCts;
    private int _index;
    private bool _isVideo;
    private bool _playing;
    private bool _updatingSlider;
    private double _volumeBeforeMute = 0.8;

    public MediaViewerWindow(IReadOnlyList<ThreadItemViewModel> items, int index, Action<ThreadItemViewModel> openExternally)
    {
        ArgumentOutOfRangeException.ThrowIfZero(items.Count);
        InitializeComponent();
        _items = items;
        _index = Math.Clamp(index, 0, items.Count - 1);
        _openExternally = openExternally;
        VideoView.Volume = _volumeBeforeMute;
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += OnTick;
        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) => ShowCurrent();
        Closed += (_, _) =>
        {
            _timer.Stop();
            _loadCts?.Cancel();
            VideoView.Stop();
            VideoView.Close();
        };
    }

    public ThreadItemViewModel Current => _items[_index];

    private void ShowCurrent()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        StopVideo();

        var item = Current;
        Title = item.FileName;
        TitleText.Text = item.FileName;
        CounterText.Text = _items.Count > 1 ? L.Format("Thread_ViewerCounter", _index + 1, _items.Count) : "";
        var nav = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        PrevButton.Visibility = nav;
        NextButton.Visibility = nav;
        PrevButton.IsEnabled = _index > 0;
        NextButton.IsEnabled = _index < _items.Count - 1;
        MessagePanel.Visibility = Visibility.Collapsed;

        _isVideo = item.VisualKind == ItemVisualKind.Video;
        OpenExternalText.Text = L.Get(_isVideo ? "Thread_ViewerOpenInPlayer" : "Thread_ViewerOpenInApp");
        if (_isVideo)
        {
            ImageView.Source = null;
            ImageView.Visibility = Visibility.Collapsed;
            VideoView.Visibility = Visibility.Visible;
            VideoBar.Visibility = Visibility.Visible;
            PositionText.Text = ThreadFormat.Duration(0);
            DurationText.Text = item.DurationText.Length > 0 ? item.DurationText : ThreadFormat.Duration(0);
            try
            {
                VideoView.Source = ThreadFormat.FileUri(item.FullPath);
                VideoView.Play();
                SetPlaying(true);
                _timer.Start();
            }
            catch (Exception)
            {
                ShowMessage(L.Get("Thread_ViewerCannotPlay"));
            }
        }
        else
        {
            VideoView.Visibility = Visibility.Collapsed;
            VideoBar.Visibility = Visibility.Collapsed;
            ImageView.Visibility = Visibility.Visible;
            ImageView.Source = null;
            _ = LoadImageAsync(item, _loadCts.Token);
        }
    }

    private async Task LoadImageAsync(ThreadItemViewModel item, CancellationToken ct)
    {
        // The bubble's thumbnail is usually cached: show it at once, then swap in the full image.
        var preview = await item.Owner.Loader.GetThumbnailAsync(item.FullPath, item.ContentVersion, PreviewSizePx, ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (preview is not null)
        {
            ImageView.Source = preview;
        }

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var maxPx = (int)Math.Min(4096, Math.Max(ActualWidth, 1200) * scale);
        var path = item.FullPath;
        BitmapSource? full;
        try
        {
            full = await Task.Run(() => DecodeImage(path, maxPx), ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (ct.IsCancellationRequested)
        {
            return;
        }

        if (full is not null)
        {
            ImageView.Source = full;
        }
        else if (preview is null)
        {
            ShowMessage(L.Get("Thread_ViewerCannotShow"));
        }
    }

    /// <summary>Decodes an image no larger than <paramref name="maxPx"/> on its long side, honouring EXIF rotation.</summary>
    private static BitmapSource? DecodeImage(string path, int maxPx)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            int width = frame.PixelWidth, height = frame.PixelHeight;
            var rotation = Rotation.Rotate0;
            try
            {
                if (frame.Metadata is BitmapMetadata metadata && metadata.GetQuery("/app1/ifd/{ushort=274}") is ushort orientation)
                {
                    rotation = orientation switch
                    {
                        3 => Rotation.Rotate180,
                        6 => Rotation.Rotate90,
                        8 => Rotation.Rotate270,
                        _ => Rotation.Rotate0,
                    };
                }
            }
            catch (Exception)
            {
                // No EXIF block in this format.
            }

            stream.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.Rotation = rotation;
            if (width >= height && width > maxPx)
            {
                image.DecodePixelWidth = maxPx;
            }
            else if (height > width && height > maxPx)
            {
                image.DecodePixelHeight = maxPx;
            }

            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void ShowMessage(string text)
    {
        MessageText.Text = text;
        MessagePanel.Visibility = Visibility.Visible;
    }

    private void Navigate(int delta)
    {
        var next = _index + delta;
        if (next >= 0 && next < _items.Count)
        {
            _index = next;
            ShowCurrent();
        }
    }

    // ------------------------------------------------------------------ video

    private void StopVideo()
    {
        _timer.Stop();
        VideoView.Stop();
        VideoView.Source = null;
        SetPlaying(false);
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayIcon.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        PauseIcon.Visibility = playing ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TogglePlay()
    {
        if (!_isVideo)
        {
            return;
        }

        if (_playing)
        {
            VideoView.Pause();
            SetPlaying(false);
        }
        else
        {
            VideoView.Play();
            SetPlaying(true);
            _timer.Start();
        }
    }

    private void VideoView_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (VideoView.NaturalDuration.HasTimeSpan)
        {
            PositionSlider.Maximum = VideoView.NaturalDuration.TimeSpan.TotalSeconds;
            DurationText.Text = ThreadFormat.Duration(VideoView.NaturalDuration.TimeSpan);
        }
    }

    private void VideoView_MediaEnded(object sender, RoutedEventArgs e)
    {
        VideoView.Stop();
        SetPlaying(false);
        OnTick(this, EventArgs.Empty);
    }

    private void VideoView_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        SetPlaying(false);
        _timer.Stop();
        ShowMessage(L.Get("Thread_ViewerCannotPlay"));
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!_isVideo)
        {
            return;
        }

        _updatingSlider = true;
        PositionSlider.Value = VideoView.Position.TotalSeconds;
        _updatingSlider = false;
        PositionText.Text = ThreadFormat.Duration(VideoView.Position);
        var muted = VideoView.Volume <= 0.001;
        VolumeIcon.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        MutedIcon.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSlider || !_isVideo)
        {
            return;
        }

        VideoView.Position = TimeSpan.FromSeconds(e.NewValue);
        PositionText.Text = ThreadFormat.Duration(e.NewValue);
    }

    // ------------------------------------------------------------------ input

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left:
                Navigate(-1);
                e.Handled = true;
                break;
            case Key.Right:
                Navigate(1);
                e.Handled = true;
                break;
            case Key.Space:
                TogglePlay();
                e.Handled = true;
                break;
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
        }
    }

    private void Stage_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not ButtonBase && FindButton(e.OriginalSource as DependencyObject) is null)
        {
            TogglePlay();
        }
    }

    private static ButtonBase? FindButton(DependencyObject? d)
    {
        for (; d is not null; d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
        {
            if (d is ButtonBase button)
            {
                return button;
            }
        }

        return null;
    }

    private void Prev_Click(object sender, RoutedEventArgs e) => Navigate(-1);

    private void Next_Click(object sender, RoutedEventArgs e) => Navigate(1);

    private void PlayPause_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        if (VideoView.Volume > 0.001)
        {
            _volumeBeforeMute = VideoView.Volume;
            VideoView.Volume = 0;
        }
        else
        {
            VideoView.Volume = _volumeBeforeMute > 0.001 ? _volumeBeforeMute : 0.8;
        }

        OnTick(this, EventArgs.Empty);
    }

    private void OpenExternal_Click(object sender, RoutedEventArgs e)
    {
        if (_isVideo && _playing)
        {
            VideoView.Pause();
            SetPlaying(false);
        }

        _openExternally(Current);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
