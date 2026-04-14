using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Serilog;
using ShapeTraffic.App.ViewModels;

namespace ShapeTraffic.App;

public partial class MainWindow : Window
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeBefore20H1 = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private readonly MainViewModel _viewModel;

    public MainWindow(MainViewModel viewModel)
    {
        _viewModel = viewModel;
        DataContext = _viewModel;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int attributeValue, int attributeSize);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;

        try
        {
            await _viewModel.InitializeAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Log.Error(exception, "Main window initialization failed.");
            MessageBox.Show(
                $"ShapeTraffic could not finish initializing.\n\n{exception.Message}",
                "ShapeTraffic",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Close();
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _viewModel.Dispose();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        ApplyDarkCaption();
    }

    private void ProcessTableEditorElement_OnGotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _viewModel.SetProcessTableEditorActive(true);
    }

    private void ProcessTableEditorElement_OnLostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (IsProcessTableEditorElement(e.NewFocus as DependencyObject))
        {
            return;
        }

        _viewModel.SetProcessTableEditorActive(false);
    }

    private void ProcessLimitAction_OnClick(object sender, RoutedEventArgs e)
    {
        Dispatcher.BeginInvoke(
            () => _viewModel.SetProcessTableEditorActive(false),
            DispatcherPriority.Background);
    }

    private static bool IsProcessTableEditorElement(DependencyObject? element)
    {
        while (element is not null)
        {
            if (element is FrameworkElement { Tag: "ProcessTableEditorElement" })
            {
                return true;
            }

            element = element is Visual visual ? VisualTreeHelper.GetParent(visual) : null;
        }

        return false;
    }

    private void ApplyDarkCaption()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        var enabled = 1;
        if (DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(windowHandle, DwmwaUseImmersiveDarkModeBefore20H1, ref enabled, sizeof(int));
        }

        var captionColor = ToColorRef(Color.FromRgb(24, 24, 24));
        var borderColor = ToColorRef(Color.FromRgb(60, 60, 60));
        var textColor = ToColorRef(Color.FromRgb(212, 212, 212));

        DwmSetWindowAttribute(windowHandle, DwmwaCaptionColor, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(windowHandle, DwmwaBorderColor, ref borderColor, sizeof(int));
        DwmSetWindowAttribute(windowHandle, DwmwaTextColor, ref textColor, sizeof(int));
    }

    private static int ToColorRef(Color color)
    {
        return color.R | (color.G << 8) | (color.B << 16);
    }
}