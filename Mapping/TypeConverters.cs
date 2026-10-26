using System.Globalization;

namespace AdsMigrator.Mapping;

// Thrown when a source row can't be migrated. The migrator catches it,
// writes the row to rejects.csv, and moves on (unless --fail-fast is set).
public class RowRejectedException : Exception
{
    public RowRejectedException(string reason) : base(reason) { }
}

// Small pure functions that turn Advantage-style values into clean MySQL values.
// No database access in here, so every one of them is easy to unit test.
public static class TypeConverters
{
    // Advantage CHAR(n) fields are space-padded. Trim the padding; blank becomes NULL.
    public static string? TrimChar(string? raw)
    {
        var trimmed = raw?.TrimEnd();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    // For columns that are NOT NULL in the target: blank after trimming is a reject, not a guess.
    public static string RequiredText(string? raw, string field) =>
        TrimChar(raw) ?? throw new RowRejectedException($"{field} is blank");

    // Advantage Logical values are stored as 'T'/'F'.
    public static bool ToBool(string? raw, string field) =>
        raw?.Trim().ToUpperInvariant() switch
        {
            "T" or "Y" or "1" => true,
            "F" or "N" or "0" => false,
            _ => throw new RowRejectedException($"{field}: '{raw}' is not a valid logical value")
        };

    // Legacy dates arrive as text. Only ISO yyyy-MM-dd is accepted; other formats are
    // reported, never guessed (is 03/04/2018 March or April?).
    // Blank and the 0000-00-00 "no date" sentinel return NULL without a warning.
    // Anything else that doesn't parse returns NULL and sets wasInvalid so it gets logged.
    public static DateTime? ParseLegacyDate(string? raw, out bool wasInvalid)
    {
        wasInvalid = false;
        var s = raw?.Trim();

        if (string.IsNullOrEmpty(s) || s == "0000-00-00")
            return null;

        if (DateTime.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                                   DateTimeStyles.None, out var date))
            return date;

        wasInvalid = true;
        return null;
    }

    // For NOT NULL date columns: no valid date means the row is rejected.
    public static DateTime RequiredDate(string? raw, string field) =>
        ParseLegacyDate(raw, out _) ?? throw new RowRejectedException($"{field}: '{raw}' is not a valid date");

    // Money arrives as a double (e.g. 149.97000000000003). Convert to decimal and round
    // to cents explicitly, instead of letting MySQL round silently on insert.
    public static decimal ToMoney(double value, string field)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            throw new RowRejectedException($"{field}: '{value}' is not a number");

        try
        {
            return Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
        }
        catch (OverflowException)
        {
            throw new RowRejectedException($"{field}: '{value}' is out of range for DECIMAL");
        }
    }
}
