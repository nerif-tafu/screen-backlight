namespace BacklightStreamer.Services;

public sealed class FrameInterpolator
{
    private float[]? _fromLinear;
    private float[]? _toLinear;
    private bool _hasKeyframe;

    public void Reset(int rgbByteLength)
    {
        _fromLinear = rgbByteLength > 0 ? new float[rgbByteLength] : [];
        _toLinear = rgbByteLength > 0 ? new float[rgbByteLength] : [];
        _hasKeyframe = false;
    }

    public bool HasKeyframe => _hasKeyframe;

    public void PushKeyframe(ReadOnlySpan<byte> capturedSrgb)
    {
        if (_toLinear == null || _toLinear.Length != capturedSrgb.Length)
            Reset(capturedSrgb.Length);

        if (!_hasKeyframe)
        {
            CopySrgbToLinear(capturedSrgb, _toLinear);
            CopySrgbToLinear(capturedSrgb, _fromLinear!);
            _hasKeyframe = true;
            return;
        }

        Array.Copy(_toLinear!, _fromLinear!, _toLinear.Length);
        CopySrgbToLinear(capturedSrgb, _toLinear);
    }

    public void WriteBlend(double t, Span<byte> outputSrgb)
    {
        if (_fromLinear == null || _toLinear == null || !_hasKeyframe)
            throw new InvalidOperationException("No keyframe available for interpolation.");

        t = Math.Clamp(t, 0, 1);
        var eased = SmoothStep(t);

        for (var i = 0; i < outputSrgb.Length; i++)
        {
            var blended = _fromLinear[i] + (_toLinear[i] - _fromLinear[i]) * (float)eased;
            outputSrgb[i] = LinearToSrgb(blended);
        }
    }

    private static double SmoothStep(double t) => t * t * (3 - 2 * t);

    private static readonly float[] SrgbToLinearLut = BuildSrgbToLinearLut();

    private static float[] BuildSrgbToLinearLut()
    {
        var lut = new float[256];
        for (var i = 0; i < 256; i++)
        {
            var s = i / 255f;
            lut[i] = s <= 0.04045f
                ? s / 12.92f
                : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        }
        return lut;
    }

    private static void CopySrgbToLinear(ReadOnlySpan<byte> srgb, Span<float> linear)
    {
        for (var i = 0; i < srgb.Length; i++)
            linear[i] = SrgbToLinearLut[srgb[i]];
    }

    private static byte LinearToSrgb(float linear)
    {
        var s = linear <= 0.0031308f
            ? linear * 12.92f
            : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        return (byte)Math.Clamp((int)MathF.Round(s * 255f), 0, 255);
    }
}
