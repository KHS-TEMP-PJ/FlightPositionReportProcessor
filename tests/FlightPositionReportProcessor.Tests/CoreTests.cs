using FlightReportProcessor;
using Xunit;
namespace FlightPositionReportProcessor.Tests;

public sealed class CoreTests
{
    private static readonly DateTimeOffset Reference = new(2026, 9, 4, 12, 10, 0, TimeSpan.Zero);
    [Fact]
    public void Parse_ValidReport_ReturnsExpectedFields()
    {
        var x = PosReportParser.Parse("POS/UL204.FR RGN/TO BKK/041205/N1642.3/E09612.5/450/12500/2800", Reference);
        Assert.Equal("UL20420260904RGNBKK", x.FlightId);
        Assert.Equal(16.705, x.Latitude, 3);
        Assert.Equal(96.208333, x.Longitude, 5);
    }
    [Theory]
    [InlineData("N1642.3", 16.705)]
    [InlineData("S1642.3", -16.705)]
    public void Latitude_AllHemispheres(string input, double expected) => Assert.Equal(expected, CoordinateConverter.ToDecimalDegrees(input, true), 3);
    [Theory]
    [InlineData("E09612.5", 96.208333)]
    [InlineData("W09612.5", -96.208333)] 
    public void Longitude_AllHemispheres(string input, double expected) => Assert.Equal(expected, CoordinateConverter.ToDecimalDegrees(input, false), 5);
    [Theory]
    [InlineData("")]
    [InlineData("bad")]
    [InlineData("POS/UL204.FR RGN/TO BKK/041205/N1642.3E09612.5/x/12500/2800")] 
    public void Parse_Invalid_Throws(string raw) => Assert.Throws<PosReportValidationException>(() => PosReportParser.Parse(raw, Reference));
    [Fact]
    public void Timestamp_NearMonthBoundary_UsesClosestMonth()
    {
        var x = ReportTimestampResolver.Resolve("302359", new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(new DateTimeOffset(2026, 9, 30, 23, 59, 0, TimeSpan.Zero), x);
    }
    [Fact] 
    public void Haversine_SamePoint_IsZero() => Assert.Equal(0, FlightCalculator.HaversineNm(1, 2, 1, 2), 8);
    [Fact]
    public void Calculation_ProducesRoundedOutputs()
    {
        var p = PosReportParser.Parse("POS/UL204.FR RGN/TO BKK/041205/N1642.3/E09612.5/450/12500/2800", Reference);
        var r = FlightCalculator.Calculate(p, Reference);
        Assert.True(r.DistanceToDestinationNm > 200);
        Assert.False(r.LowFuelWarning);
        Assert.Equal(Math.Round(r.RemainingFlightTimeHours * 60, MidpointRounding.AwayFromZero), r.RemainingFlightTimeMinutes);
    }
    [Fact] 
    public void LowFuel_IsFlagged()
    { 
        var p = new ParsedPositionReport("id", "UL1", "RGN", "BKK", Reference, 0, 0, 100, 1, 1000); 
        Assert.True(FlightCalculator.Calculate(p, Reference).LowFuelWarning); 
    }
    [Fact] 
    public void UnknownAirport_Throws() 
    { var p = new ParsedPositionReport("id", "UL1", "RGN", "XXX", Reference, 0, 0, 10, 1, 1);
        Assert.Throws<PosReportValidationException>(() => FlightCalculator.Calculate(p, Reference)); 
    }
    [Fact] 
    public void ZeroSpeed_Throws()
    { var p = new ParsedPositionReport("id", "UL1", "RGN", "BKK", Reference, 0, 0, 0, 1, 1); 
        Assert.Throws<PosReportValidationException>(() => FlightCalculator.Calculate(p, Reference)); 
    }
    [Fact] 
    public void Status_ResultWinsWhenNewer() 
    { 
        var p = new ParsedMetadata("id", Reference, "a"); 
        var r = new ResultMetadata("id", Reference.AddMinutes(1), "r", 1, 60, 10, false); 
        Assert.Equal("CALCULATED", StatusSelector.Select("id", p, r)!.Status); 
    }
    [Fact] 
    public void FlightId_HasNoSeparators() => Assert.Equal("UL20420260904RGNBKK", FlightIdBuilder.Build("UL204", Reference, "RGN", "BKK"));
}
