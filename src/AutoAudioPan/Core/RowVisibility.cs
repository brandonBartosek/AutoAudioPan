namespace AutoAudioPan.Core;

public static class RowVisibility
{
    public static bool ShouldDisplay(bool isActive, bool isPinnedByUser) =>
        isActive || isPinnedByUser;
}
