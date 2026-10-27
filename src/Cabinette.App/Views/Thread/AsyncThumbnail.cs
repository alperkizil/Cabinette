using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Cabinette.App.ViewModels.Thread;

namespace Cabinette.App.Views.Thread;

public enum ThumbnailMode
{
    /// <summary>Shell thumbnail only (images, video). Reports failures to the item so the bubble can fall back.</summary>
    Thumbnail = 0,

    /// <summary>File-type icon only.</summary>
    Icon,

    /// <summary>Thumbnail when the shell has one (PDF, Office), otherwise the file-type icon.</summary>
    ThumbnailThenIcon,
}

/// <summary>
/// A <see cref="Border"/> that paints an item's thumbnail or icon as its (rounded) background.
/// Loads asynchronously through <see cref="ThumbnailLoader"/>, requests 2× the layout size for high-DPI screens,
/// and cancels when its container is recycled (Item changes) or unloaded.
/// </summary>
public sealed class AsyncThumbnail : Border
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(
        nameof(Item), typeof(ThreadItemViewModel), typeof(AsyncThumbnail), new PropertyMetadata(null, OnSourceChanged));

    public static readonly DependencyProperty ModeProperty = DependencyProperty.Register(
        nameof(Mode), typeof(ThumbnailMode), typeof(AsyncThumbnail), new PropertyMetadata(ThumbnailMode.Thumbnail, OnSourceChanged));

    public static readonly DependencyProperty RequestSizeProperty = DependencyProperty.Register(
        nameof(RequestSize), typeof(double), typeof(AsyncThumbnail), new PropertyMetadata(96d, OnSourceChanged));

    public static readonly DependencyProperty StretchProperty = DependencyProperty.Register(
        nameof(Stretch), typeof(Stretch), typeof(AsyncThumbnail), new PropertyMetadata(Stretch.UniformToFill, OnLookChanged));

    public static readonly DependencyProperty ImageAlignmentYProperty = DependencyProperty.Register(
        nameof(ImageAlignmentY), typeof(AlignmentY), typeof(AsyncThumbnail), new PropertyMetadata(AlignmentY.Center, OnLookChanged));

    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.Register(
        nameof(Placeholder), typeof(Brush), typeof(AsyncThumbnail), new PropertyMetadata(null, OnLookChanged));

    private static readonly DependencyPropertyKey HasImagePropertyKey = DependencyProperty.RegisterReadOnly(
        nameof(HasImage), typeof(bool), typeof(AsyncThumbnail), new PropertyMetadata(false));

    public static readonly DependencyProperty HasImageProperty = HasImagePropertyKey.DependencyProperty;

    private CancellationTokenSource? _cts;
    private string? _requestKey;
    private BitmapSource? _image;
    private bool _synchronous;

    public AsyncThumbnail()
    {
        Loaded += (_, _) => Reload();
        Unloaded += (_, _) =>
        {
            Cancel();
            _requestKey = null;
        };
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
    }

    public ThreadItemViewModel? Item
    {
        get => (ThreadItemViewModel?)GetValue(ItemProperty);
        set => SetValue(ItemProperty, value);
    }

    public ThumbnailMode Mode
    {
        get => (ThumbnailMode)GetValue(ModeProperty);
        set => SetValue(ModeProperty, value);
    }

    /// <summary>Displayed size in device-independent pixels (the request is twice this).</summary>
    public double RequestSize
    {
        get => (double)GetValue(RequestSizeProperty);
        set => SetValue(RequestSizeProperty, value);
    }

    public Stretch Stretch
    {
        get => (Stretch)GetValue(StretchProperty);
        set => SetValue(StretchProperty, value);
    }

    /// <summary>Where a cropped image is anchored (Top shows the title area of a document's first page).</summary>
    public AlignmentY ImageAlignmentY
    {
        get => (AlignmentY)GetValue(ImageAlignmentYProperty);
        set => SetValue(ImageAlignmentYProperty, value);
    }

    /// <summary>Background while nothing is loaded.</summary>
    public Brush? Placeholder
    {
        get => (Brush?)GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>True once an image is shown (templates hide their fallback glyph).</summary>
    public bool HasImage => (bool)GetValue(HasImageProperty);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => ((AsyncThumbnail)d).Reload();

    private static void OnLookChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var self = (AsyncThumbnail)d;
        self.Show(self._image, animate: false);
    }

    private void Reload()
    {
        if (!IsLoaded)
        {
            return;
        }

        var item = Item;
        var px = (int)Math.Ceiling(Math.Clamp(RequestSize, 8, 1024) * 2);
        var key = item is null ? null : $"{Mode}|{px}|{item.Id}|{item.FullPath}|{item.ContentVersion}|{item.IsMissing}";
        if (key is not null && key == _requestKey)
        {
            return; // already showing or loading exactly this
        }

        Cancel();
        _requestKey = key;
        _image = null;
        Show(null, animate: false);
        if (item is null)
        {
            return;
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(item.Owner.Lifetime);
        _cts = cts;
        _synchronous = true;
        _ = LoadAsync(item, Mode, px, cts);
        _synchronous = false;
    }

    private async Task LoadAsync(ThreadItemViewModel item, ThumbnailMode mode, int px, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        var loader = item.Owner.Loader;
        BitmapSource? image = null;
        if (mode != ThumbnailMode.Icon && !item.IsMissing)
        {
            image = await loader.GetThumbnailAsync(item.FullPath, item.ContentVersion, px, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            item.OnThumbnailLoaded(image);
        }

        if (image is null && mode != ThumbnailMode.Thumbnail)
        {
            image = await loader.GetIconAsync(item.FullPath, item.Extension, item.IsMissing, px, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }
        }

        if (!ReferenceEquals(Item, item) || !ReferenceEquals(_cts, cts))
        {
            return;
        }

        _image = image;
        Show(image, animate: !_synchronous);
    }

    private void Show(BitmapSource? image, bool animate)
    {
        if (image is null)
        {
            Background = Placeholder;
            SetValue(HasImagePropertyKey, false);
            BeginAnimation(OpacityProperty, null);
            return;
        }

        var brush = new ImageBrush(image)
        {
            Stretch = Stretch,
            AlignmentX = AlignmentX.Center,
            AlignmentY = ImageAlignmentY,
        };
        brush.Freeze();
        Background = brush;
        SetValue(HasImagePropertyKey, true);
        if (animate)
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)));
        }
    }

    private void Cancel()
    {
        if (_cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
        }
    }
}
