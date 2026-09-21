namespace VrcPicSorter.App.Services;

public static class ActionOutcomeText
{
    public static string Interrupted(bool cancelled) => cancelled
        ? "Stopped. Completed file changes remain. Check History and pending recovery for unfinished operations."
        : "Action failed. Some files may already have been moved or removed. Check History and pending recovery before retrying.";
}
