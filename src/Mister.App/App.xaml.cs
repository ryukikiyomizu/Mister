using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;

namespace Mister.App;

public partial class App : Application
{
    private const double MouseWheelStep = 24d;

    static App()
    {
        if (string.IsNullOrWhiteSpace(
                Environment.GetEnvironmentVariable("WINDIR")))
        {
            string? systemRoot =
                Environment.GetEnvironmentVariable("SystemRoot");
            if (!string.IsNullOrWhiteSpace(systemRoot))
            {
                Environment.SetEnvironmentVariable(
                    "WINDIR",
                    systemRoot,
                    EnvironmentVariableTarget.Process);
            }
        }

        EventManager.RegisterClassHandler(
            typeof(ScrollViewer),
            UIElement.PreviewMouseWheelEvent,
            new MouseWheelEventHandler(OnScrollViewerPreviewMouseWheel),
            handledEventsToo: true);
    }

    private static void OnScrollViewerPreviewMouseWheel(
        object sender,
        MouseWheelEventArgs e)
    {
        if (e.Handled
            || e.Delta == 0
            || sender is not ScrollViewer current)
        {
            return;
        }

        bool horizontal =
            (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        ScrollViewer? target = FindScrollableViewer(
            e.OriginalSource as DependencyObject,
            e.Delta,
            horizontal);
        if (!ReferenceEquals(current, target)) return;

        double movement = WheelMovement(e.Delta);
        if (horizontal)
        {
            current.ScrollToHorizontalOffset(
                Math.Clamp(
                    current.HorizontalOffset - movement,
                    0,
                    current.ScrollableWidth));
        }
        else
        {
            current.ScrollToVerticalOffset(
                Math.Clamp(
                    current.VerticalOffset - movement,
                    0,
                    current.ScrollableHeight));
        }

        e.Handled = true;
    }

    internal static double WheelMovement(int delta) =>
        Math.Clamp(delta / 120d, -1d, 1d) * MouseWheelStep;

    private static ScrollViewer? FindScrollableViewer(
        DependencyObject? source,
        int delta,
        bool horizontal)
    {
        for (DependencyObject? current = source;
             current is not null;
             current = GetParent(current))
        {
            if (current is not ScrollViewer viewer) continue;

            double offset = horizontal
                ? viewer.HorizontalOffset
                : viewer.VerticalOffset;
            double extent = horizontal
                ? viewer.ScrollableWidth
                : viewer.ScrollableHeight;
            if ((delta > 0 && offset > 0)
                || (delta < 0 && offset < extent))
            {
                return viewer;
            }
        }

        return null;
    }

    private static DependencyObject? GetParent(DependencyObject child)
    {
        if (child is Visual or Visual3D)
        {
            return VisualTreeHelper.GetParent(child);
        }

        return LogicalTreeHelper.GetParent(child);
    }
}
