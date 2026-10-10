using System.Globalization;
using System.Text.RegularExpressions;

namespace FlightReportProcessor;

public interface IUtcClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemUtcClock : IUtcClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static partial class PosReportParser
{
    [GeneratedRegex(
        @"^POS/(?<flight>[A-Z]{2,3}\d{1,4})\.FR (?<dep>[A-Z]{3})/TO (?<dest>[A-Z]{3})/(?<dt>\d{6})/(?<lat>[NS]\d{4}\.\d{1,3})/(?<lon>[EW]\d{5}\.\d{1,3})/(?<speed>\d+(?:\.\d+)?)/(?<fuel>\d+(?:\.\d+)?)/(?<flow>\d+(?:\.\d+)?)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex FormatRegex();

    public static ParsedPositionReport Parse(string raw, DateTimeOffset referenceUtc)
    {
        if (string.IsNullOrWhiteSpace(raw))
            throw new PosReportValidationException("The POS report body is required.");
        if (raw.Length > 512)
            throw new PosReportValidationException("The POS report exceeds 512 characters.");

        var match = FormatRegex().Match(raw.Trim());
        if (!match.Success)
            throw new PosReportValidationException("The POS report format is invalid.");

        var timestamp = ReportTimestampResolver.Resolve(match.Groups["dt"].Value, referenceUtc);
        var latitude = CoordinateConverter.ToDecimalDegrees(match.Groups["lat"].Value, true);
        var longitude = CoordinateConverter.ToDecimalDegrees(match.Groups["lon"].Value, false);
        var speed = Positive(match, "speed");
        var fuel = NonNegative(match, "fuel");
        var flow = NonNegative(match, "flow");
        var flight = match.Groups["flight"].Value;
        var dep = match.Groups["dep"].Value;
        var dest = match.Groups["dest"].Value;
        var flightId = FlightIdBuilder.Build(flight, timestamp, dep, dest);

        return new(flightId, flight, dep, dest, timestamp, latitude, longitude, speed, fuel, flow);
    }

    private static double Positive(Match m, string name)
    {
        var value = Number(m, name);
        if (value <= 0)
            throw new PosReportValidationException($"{name} must be greater than zero.");
        return value;
    }

    private static double NonNegative(Match m, string name)
    {
        var value = Number(m, name);
        if (value < 0)
            throw new PosReportValidationException($"{name} must not be negative.");
        return value;
    }

    private static double Number(Match m, string name) =>
        double.TryParse(m.Groups[name].Value, NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
            ? value
            : throw new PosReportValidationException($"{name} is invalid.");
}

public static class CoordinateConverter
{
    public static double ToDecimalDegrees(string value, bool latitude)
    {
        var hemisphere = value[0];
        var degreeDigits = latitude ? 2 : 3;

        if ((latitude && hemisphere is not ('N' or 'S')) ||
            (!latitude && hemisphere is not ('E' or 'W')))
            throw new PosReportValidationException("Coordinate hemisphere is invalid.");

        if (!int.TryParse(value.AsSpan(1, degreeDigits), out var degrees) ||
            !double.TryParse(value.AsSpan(1 + degreeDigits), NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var minutes))
            throw new PosReportValidationException("Coordinate is invalid.");

        var max = latitude ? 90 : 180;
        if (minutes < 0 || minutes >= 60 || degrees > max ||
            (degrees == max && minutes != 0))
            throw new PosReportValidationException("Coordinate is outside its valid range.");

        var result = degrees + minutes / 60d;
        return hemisphere is 'S' or 'W' ? -result : result;
    }
}

public static class ReportTimestampResolver
{
    public static DateTimeOffset Resolve(string ddHHmm, DateTimeOffset referenceUtc)
    {
        if (ddHHmm.Length != 6 ||
            !int.TryParse(ddHHmm[..2], out var day) ||
            !int.TryParse(ddHHmm[2..4], out var hour) ||
            !int.TryParse(ddHHmm[4..], out var minute) ||
            hour > 23 || minute > 59)
            throw new PosReportValidationException("Report day/time is invalid.");

        referenceUtc = referenceUtc.ToUniversalTime();
        var candidates = new List<DateTimeOffset>();

        for (var offset = -1; offset <= 1; offset++)
        {
            var month = referenceUtc.AddMonths(offset);
            if (day <= DateTime.DaysInMonth(month.Year, month.Month))
                candidates.Add(new DateTimeOffset(month.Year, month.Month, day, hour, minute, 0, TimeSpan.Zero));
        }

        if (candidates.Count == 0)
            throw new PosReportValidationException("Report day does not exist in adjacent UTC months.");

        var resolved = candidates.MinBy(x => Math.Abs((x - referenceUtc).TotalMinutes));
        if (Math.Abs((resolved - referenceUtc).TotalDays) > 20)
            throw new PosReportValidationException("Report timestamp is more than 20 days from ingestion time.");

        return resolved;
    }
}

public static class FlightIdBuilder
{
    public static string Build(string flight, DateTimeOffset timestamp, string departure, string destination) =>
        $"{flight}{timestamp.UtcDateTime:yyyyMMdd}{departure}{destination}";
}

public static class FlightCalculator
{
    public const double EarthRadiusNm = 3440.065;

    private static readonly IReadOnlyDictionary<string, AirportCoordinate> Airports =
        new Dictionary<string, AirportCoordinate>(StringComparer.OrdinalIgnoreCase)
        {
            ["RGN"] = new(16.9073, 96.1332),
            ["BKK"] = new(13.6900, 100.7501),
            ["SIN"] = new(1.3644, 103.9915)
        };

    public static CalculationResult Calculate(ParsedPositionReport report, DateTimeOffset calculationTimestamp)
    {
        if (report.GroundSpeedKnots <= 0)
            throw new PosReportValidationException("Ground speed must be greater than zero.");
        if (!Airports.TryGetValue(report.Destination, out var airport))
            throw new PosReportValidationException($"Unknown destination airport: {report.Destination}.");

        var distance = HaversineNm(report.Latitude, report.Longitude, airport.Latitude, airport.Longitude);
        var hours = distance / report.GroundSpeedKnots;
        var minutes = (int)Math.Round(hours * 60, MidpointRounding.AwayFromZero);
        var fuel = report.FuelOnBoardKg - report.FuelFlowKgPerHour * hours;

        return new(report.FlightId, report.FlightNumber, report.Departure, report.Destination,
            report.Timestamp, calculationTimestamp, report.Latitude, report.Longitude,
            report.GroundSpeedKnots, report.FuelOnBoardKg, report.FuelFlowKgPerHour,
            Math.Round(distance, 2, MidpointRounding.AwayFromZero),
            Math.Round(hours, 4, MidpointRounding.AwayFromZero), minutes,
            Math.Round(fuel, 2, MidpointRounding.AwayFromZero), fuel < 0);
    }

    public static double HaversineNm(double lat1, double lon1, double lat2, double lon2)
    {
        static double R(double d) => d * Math.PI / 180;
        var dLat = R(lat2 - lat1);
        var dLon = R(lon2 - lon1);
        var a = Math.Pow(Math.Sin(dLat / 2), 2) +
                Math.Cos(R(lat1)) * Math.Cos(R(lat2)) * Math.Pow(Math.Sin(dLon / 2), 2);
        return EarthRadiusNm * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}

public static class StatusSelector
{
    public static ProcessingStatus? Select(string flightId, ParsedMetadata? parsed, ResultMetadata? result)
    {
        if (result is not null && (parsed is null || result.Timestamp >= parsed.Timestamp))
            return new(flightId, "CALCULATED", result.Timestamp, result.RemainingFlightTimeHours,
                result.RemainingFlightTimeMinutes, result.EstimatedFuelAtArrivalKg, result.LowFuelWarning);

        return parsed is null ? null : new(flightId, "PARSED", parsed.Timestamp);
    }
}
