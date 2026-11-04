namespace AutoAudioPan.Core;

public static class PanMath
{
    /// <summary>Maps a window's horizontal center across the virtual desktop to [-1, 1].</summary>
    public static float FromWindow(double left, double right, double desktopLeft, double desktopWidth)
    {
        if (desktopWidth <= 0) return 0;
        var center = (left + right) / 2.0;
        var p = ((center - desktopLeft) / desktopWidth) * 2.0 - 1.0;
        return (float)Math.Clamp(p, -1.0, 1.0);
    }

    /// <summary>Balance-style stereo pan that folds the attenuated channel into the other side.</summary>
    public static void Apply(float l, float r, float pan, out float outL, out float outR)
    {
        if (pan < 0)
        {
            outL = l + r * -pan;
            outR = r * (1 + pan);
        }
        else
        {
            outL = l * (1 - pan);
            outR = r + l * pan;
        }
    }
}

