using System.Drawing;
using System.Windows.Forms;
using Vortice.DXGI;

namespace BacklightStreamer.Services;

internal static class MonitorCaptureHelper
{
    public static IDXGIOutput1? FindOutputForScreen(
        int screenIndex,
        IDXGIFactory1 factory,
        out IDXGIAdapter1? matchedAdapter)
    {
        matchedAdapter = null;
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return null;

        var screen = screens[Math.Clamp(screenIndex, 0, screens.Length - 1)];
        var target = screen.Bounds;

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out var adapter).Failure || adapter == null)
                break;

            for (uint outputIndex = 0; ; outputIndex++)
            {
                if (adapter.EnumOutputs(outputIndex, out var output).Failure || output == null)
                    break;

                using (output)
                {
                    var desc = output.Description;
                    var bounds = desc.DesktopCoordinates;
                    var outputRect = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
                    if (outputRect != target) continue;

                    matchedAdapter = adapter;
                    return output.QueryInterface<IDXGIOutput1>();
                }
            }

            adapter.Dispose();
        }

        return null;
    }
}
