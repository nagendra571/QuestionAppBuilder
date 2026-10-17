namespace QuestionAppBuilder.Reports.Time;

public static class ReportDateRange
{
    public static (DateTime? FromUtc, DateTime? ToUtcExclusive) FromLocalDates(
        DateOnly? fromDate, DateOnly? throughDate, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        if (fromDate is DateOnly from && throughDate is DateOnly through && from > through)
            throw new ArgumentException("The start date must be on or before the end date.");

        DateTime? fromUtc = fromDate is DateOnly fromValue
            ? ResolveBoundary(fromValue.ToDateTime(TimeOnly.MinValue), timeZone)
            : null;
        DateTime? toUtcExclusive = null;
        if (throughDate is DateOnly throughValue)
        {
            if (throughValue == DateOnly.MaxValue)
                throw new ArgumentOutOfRangeException(nameof(throughDate), "The end date is too late to form an exclusive boundary.");
            toUtcExclusive = ResolveBoundary(throughValue.AddDays(1).ToDateTime(TimeOnly.MinValue), timeZone);
        }

        return (fromUtc, toUtcExclusive);
    }

    public static DateTime ResolveBoundary(DateTime localBoundary, TimeZoneInfo timeZone)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        localBoundary = DateTime.SpecifyKind(localBoundary, DateTimeKind.Unspecified);
        if (timeZone.IsInvalidTime(localBoundary))
            throw new ArgumentException($"The local date boundary {localBoundary:O} does not exist in time zone '{timeZone.Id}'.", nameof(localBoundary));

        if (timeZone.IsAmbiguousTime(localBoundary))
        {
            // A greater UTC offset produces the earlier of the two possible UTC instants.
            var earlierOffset = timeZone.GetAmbiguousTimeOffsets(localBoundary).Max();
            return new DateTimeOffset(localBoundary, earlierOffset).UtcDateTime;
        }

        return TimeZoneInfo.ConvertTimeToUtc(localBoundary, timeZone);
    }
}
