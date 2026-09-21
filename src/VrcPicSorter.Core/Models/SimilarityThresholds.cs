namespace VrcPicSorter.Core.Models;

public sealed record SimilarityThresholds(double MinimumPercent, double MaximumPercent)
{
    public bool IsValid() => double.IsFinite(MinimumPercent) && double.IsFinite(MaximumPercent)
        && MinimumPercent >= 0 && MaximumPercent <= 100 && MinimumPercent <= MaximumPercent;

    public void Validate()
    {
        if (!IsValid())
            throw new InvalidOperationException("Similarity limits must be numbers from 0 to 100, with minimum no greater than maximum.");
    }
}
