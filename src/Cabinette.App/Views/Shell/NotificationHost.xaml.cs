using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Cabinette.App.Services;

namespace Cabinette.App.Views.Shell;

/// <summary>Snackbar and progress cards. Slides each new notice in; pauses auto-dismiss while hovered.</summary>
public partial class NotificationHost : UserControl
{
    private NotificationService? _service;

    public NotificationHost()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_service is not null)
            {
                _service.PropertyChanged -= OnServicePropertyChanged;
            }

            _service = DataContext as NotificationService;
            if (_service is not null)
            {
                _service.PropertyChanged += OnServicePropertyChanged;
            }
        };
    }

    private void OnServicePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(NotificationService.Current) && _service?.Current is not null)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SnackShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
            Snackbar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

    private void OnSnackbarMouseEnter(object sender, MouseEventArgs e)
    {
        if (_service is not null)
        {
            _service.IsPaused = true;
        }
    }

    private void OnSnackbarMouseLeave(object sender, MouseEventArgs e)
    {
        if (_service is not null)
        {
            _service.IsPaused = false;
        }
    }
}
