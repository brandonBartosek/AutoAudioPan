namespace AutoAudioPan.Core;

public static class RowVisibility
{
    public static bool ShouldDisplay(
        bool isActive,
        bool producedSoundThisSession,
        bool hidden,
        bool showHiddenApps) =>
        showHiddenApps ? producedSoundThisSession : producedSoundThisSession && !hidden;
}
