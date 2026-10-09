namespace VrcPicSorter.Core.Scanning;

/// <summary>Storage can recover without a new event; invalid data and unsafe paths need attention.</summary>
public static class ScanRetry
{
    public static bool IsTransient(Exception error) => error switch
    {
        InvalidDataException or PathTooLongException => false,
        IOException or UnauthorizedAccessException => true,
        AggregateException aggregate => aggregate.InnerExceptions.Any(IsTransient),
        _ => false,
    };
}
