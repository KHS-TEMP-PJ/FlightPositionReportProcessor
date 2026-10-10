namespace FlightReportProcessor;

public sealed record ParsedPositionReport(
    string FlightId,
    string FlightNumber,
    string Departure,
    string Destination,
    DateTimeOffset Timestamp,
    double Latitude,
    double Longitude,
    double GroundSpeedKnots,
    double FuelOnBoardKg,
    double FuelFlowKgPerHour);

public sealed record CalculationResult(
    string FlightId,
    string FlightNumber,
    string Departure,
    string Destination,
    DateTimeOffset ReportTimestamp,
    DateTimeOffset CalculationTimestamp,
    double Latitude,
    double Longitude,
    double GroundSpeedKnots,
    double FuelOnBoardKg,
    double FuelFlowKgPerHour,
    double DistanceToDestinationNm,
    double RemainingFlightTimeHours,
    int RemainingFlightTimeMinutes,
    double EstimatedFuelAtArrivalKg,
    bool LowFuelWarning);

public sealed record CalculationMessage(
    string FlightId,
    DateTimeOffset Timestamp);

public sealed record ParsedMetadata(
    string FlightId,
    DateTimeOffset Timestamp,
    string AttachmentKey);

public sealed record ResultMetadata(
    string FlightId,
    DateTimeOffset Timestamp,
    string ResultKey,
    double RemainingFlightTimeHours,
    int RemainingFlightTimeMinutes,
    double EstimatedFuelAtArrivalKg,
    bool LowFuelWarning);

public sealed record ProcessingStatus(
    string FlightId,
    string Status,
    DateTimeOffset Timestamp,
    double? RemainingFlightTimeHours = null,
    int? RemainingFlightTimeMinutes = null,
    double? EstimatedFuelAtArrivalKg = null,
    bool? LowFuelWarning = null);

public sealed record AirportCoordinate(
    double Latitude,
    double Longitude);

public sealed class PosReportValidationException(string message) : Exception(message);
