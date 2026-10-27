using System.Windows.Media;
using System.Windows.Threading;

namespace Cabinette.App.ViewModels.Thread;

/// <summary>Inline voice-note style audio playback. Only one item plays at a time. UI thread only.</summary>
public sealed class AudioPlayback : IDisposable
{
    private readonly Action<ThreadItemViewModel> _onFailed;
    private MediaPlayer? _player;
    private DispatcherTimer? _timer;
    private ThreadItemViewModel? _current;
    private bool _opened;

    public AudioPlayback(Action<ThreadItemViewModel> onFailed)
    {
        _onFailed = onFailed;
    }

    public ThreadItemViewModel? Current => _current;

    public void Toggle(ThreadItemViewModel item)
    {
        if (ReferenceEquals(item, _current) && _player is not null)
        {
            if (item.IsPlaying)
            {
                _player.Pause();
                item.IsPlaying = false;
                _timer?.Stop();
            }
            else
            {
                _player.Play();
                item.IsPlaying = true;
                _timer?.Start();
            }

            return;
        }

        Stop();
        if (item.IsMissing)
        {
            _onFailed(item);
            return;
        }

        var player = EnsurePlayer();
        _current = item;
        _opened = false;
        try
        {
            player.Open(ThreadFormat.FileUri(item.FullPath));
            player.Play();
        }
        catch (Exception)
        {
            _current = null;
            _onFailed(item);
            return;
        }

        item.IsPlaying = true;
        _timer!.Start();
    }

    public void Seek(ThreadItemViewModel item, double seconds)
    {
        if (ReferenceEquals(item, _current) && _player is not null && _opened)
        {
            _player.Position = TimeSpan.FromSeconds(Math.Max(0, seconds));
        }
    }

    public void Stop()
    {
        var current = _current;
        _current = null;
        _timer?.Stop();
        if (_player is not null)
        {
            _player.Stop();
            _player.Close();
        }

        if (current is not null)
        {
            current.IsPlaying = false;
            current.SetPositionFromPlayer(0);
        }
    }

    public void Dispose()
    {
        Stop();
        if (_player is not null)
        {
            _player.MediaOpened -= OnMediaOpened;
            _player.MediaEnded -= OnMediaEnded;
            _player.MediaFailed -= OnMediaFailed;
            _player = null;
        }

        if (_timer is not null)
        {
            _timer.Tick -= OnTick;
            _timer = null;
        }
    }

    private MediaPlayer EnsurePlayer()
    {
        if (_player is null)
        {
            _player = new MediaPlayer();
            _player.MediaOpened += OnMediaOpened;
            _player.MediaEnded += OnMediaEnded;
            _player.MediaFailed += OnMediaFailed;
            _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };
            _timer.Tick += OnTick;
        }

        return _player;
    }

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        _opened = true;
        if (_current is null || _player is null)
        {
            return;
        }

        if (_player.NaturalDuration.HasTimeSpan)
        {
            _current.SetPlayerDuration(_player.NaturalDuration.TimeSpan);
        }

        // Resume from where the user dragged the slider before pressing play.
        if (_current.PlaybackPosition > 0.2 && _current.PlaybackPosition < _current.PlaybackDurationSeconds - 0.2)
        {
            _player.Position = TimeSpan.FromSeconds(_current.PlaybackPosition);
        }
    }

    private void OnMediaEnded(object? sender, EventArgs e) => Stop();

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        var item = _current;
        Stop();
        if (item is not null)
        {
            _onFailed(item);
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (_current is not null && _player is not null && _opened)
        {
            _current.SetPositionFromPlayer(_player.Position.TotalSeconds);
        }
    }
}
