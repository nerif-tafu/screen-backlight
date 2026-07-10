namespace BacklightStreamer.Services;

/// <summary>
/// CPU-side buffers holding only the four screen-edge bands of a frame.
/// LED sample regions only ever touch a thin border of the screen, so capture
/// backends can read back these bands instead of the full frame — at 4K this
/// cuts GPU→CPU transfer from ~33 MB to well under 1 MB per frame.
/// </summary>
public sealed class EdgeBandBuffers
{
    public int Width { get; private set; }
    public int Height { get; private set; }
    public int Thickness { get; private set; }

    /// <summary>Full frame width × Thickness rows.</summary>
    public byte[] Top { get; private set; } = [];
    public byte[] Bottom { get; private set; } = [];

    /// <summary>Thickness columns × full frame height.</summary>
    public byte[] Left { get; private set; } = [];
    public byte[] Right { get; private set; } = [];

    public bool IsConfigured => Width > 0 && Height > 0 && Thickness > 0;

    public void Configure(int width, int height, int thickness)
    {
        thickness = Math.Clamp(thickness, 1, Math.Min(width, height));
        if (width == Width && height == Height && thickness == Thickness)
            return;

        Width = width;
        Height = height;
        Thickness = thickness;
        Top = new byte[width * thickness * 4];
        Bottom = new byte[width * thickness * 4];
        Left = new byte[thickness * height * 4];
        Right = new byte[thickness * height * 4];
    }
}
