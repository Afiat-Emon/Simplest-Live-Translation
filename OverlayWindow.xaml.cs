using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace LiveTranslator;

public partial class OverlayWindow : Window
{
    private readonly Action? _onCloseCallback;

    public OverlayWindow(Action onCloseCallback, double opacity, string textColorHex)
    {
        InitializeComponent();
        _onCloseCallback = onCloseCallback;

        // Apply Transparency
        BackgroundBorder.Opacity = opacity;

        // Apply Subtitle Color
        if (ColorConverter.ConvertFromString(textColorHex) is Color color)
        {
            SubtitleTextBlock.Foreground = new SolidColorBrush(color);
        }

        Loaded += (s, e) =>
        {
            double screenWidth = SystemParameters.WorkArea.Width;
            double screenHeight = SystemParameters.WorkArea.Height;
            Left = (screenWidth - Width) / 2;
            Top = SystemParameters.WorkArea.Top + screenHeight - (screenHeight * 0.15);
        };
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        try
        {
            DragMove();
        }
        catch { }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _onCloseCallback?.Invoke();
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _onCloseCallback?.Invoke();
            Close();
        }
    }

    public void UpdateSubtitle(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => UpdateSubtitle(text));
            return;
        }

        if (IsLoaded)
        {
            SubtitleTextBlock.Text = text;
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        CloseButton.Opacity = 1;
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        CloseButton.Opacity = 0;
    }
}