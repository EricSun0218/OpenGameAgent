using System.Globalization;
using System.Text.Json;

namespace GameAgent.World.Tests;

public sealed class WorldPortableNumberTests
{
    [Theory]
    [InlineData("0", 0L)]
    [InlineData("1", 1L)]
    [InlineData("-1", -1L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("-9223372036854775808", long.MinValue)]
    public void CanonicalInt64RoundTrips(string text, long expected)
    {
        var parsed = WorldFixedPointValue.TryParseCanonical(text, 18);

        Assert.True(parsed.Succeeded);
        Assert.Equal(expected, parsed.Value!.Units);
        Assert.Equal(18, parsed.Value.Scale);
        Assert.Equal(text, parsed.Value.CanonicalUnits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("+1")]
    [InlineData("01")]
    [InlineData("-0")]
    [InlineData("-01")]
    [InlineData("1.0")]
    [InlineData("1e2")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("9223372036854775808")]
    [InlineData("-9223372036854775809")]
    public void NonCanonicalOrOutOfRangeInt64FailsClosed(string text)
    {
        var parsed = WorldFixedPointValue.TryParseCanonical(text, 0);

        Assert.False(parsed.Succeeded);
        Assert.Equal(
            WorldNumericReasonCodes.InvalidCanonicalValue,
            parsed.ReasonCode);
        Assert.Null(parsed.Value);
    }

    [Fact]
    public void CanonicalEncodingIsCultureIndependent()
    {
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            CultureInfo.CurrentUICulture =
                CultureInfo.GetCultureInfo("fr-FR");

            var value = new WorldFixedPointValue(-1234567890, 6);
            var parsed = WorldFixedPointValue.TryParseCanonical(
                value.CanonicalUnits,
                value.Scale);

            Assert.Equal("-1234567890", value.CanonicalUnits);
            Assert.True(parsed.Succeeded);
            Assert.Equal(value, parsed.Value);
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    [Fact]
    public void NumericSchemaRequiresCanonicalBoundedStrings()
    {
        var schema = Schema("field", 2, "unit", "-100", "100", "0");

        Assert.True(
            schema.TryBind(new WorldFixedPointValue(100, 2)).Succeeded);
        Assert.Equal(
            WorldNumericReasonCodes.OutOfBounds,
            schema.TryBind(new WorldFixedPointValue(101, 2)).ReasonCode);
        Assert.Equal(
            WorldNumericReasonCodes.ScaleMismatch,
            schema.TryBind(new WorldFixedPointValue(10, 1)).ReasonCode);
        Assert.Throws<ArgumentException>(
            () => Schema("bad", 2, "unit", "00", "100", "0"));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Schema("bad", 2, "unit", "0", "100", "101"));
    }

    [Fact]
    public void AuthoritativeNumericJsonRejectsJsonNumbers()
    {
        var schema = Schema(
            "field",
            2,
            "unit",
            long.MinValue.ToString(CultureInfo.InvariantCulture),
            long.MaxValue.ToString(CultureInfo.InvariantCulture),
            "0");

        var encoded = schema.TryReadCanonical(Json("\"125\""));
        var jsonNumber = schema.TryReadCanonical(Json("1.25"));

        Assert.True(encoded.Succeeded);
        Assert.Equal(125, encoded.Quantity!.Value.Units);
        Assert.False(jsonNumber.Succeeded);
        Assert.Equal(
            WorldNumericReasonCodes.BinaryFloatForbidden,
            jsonNumber.ReasonCode);
    }

    [Fact]
    public void ComparisonIsExactAcrossScalesAndChecksUnits()
    {
        var integer = Quantity(
            Schema("integer", 0, "same", "-10", "10", "0"),
            1);
        var scaled = Quantity(
            Schema("scaled", 2, "same", "-1000", "1000", "0"),
            100);
        var otherUnit = Quantity(
            Schema("other", 2, "other", "-1000", "1000", "0"),
            100);

        Assert.Equal(
            0,
            WorldNumericMath.Compare(integer, scaled).Comparison);
        Assert.Equal(
            WorldNumericReasonCodes.UnitMismatch,
            WorldNumericMath.Compare(integer, otherUnit).ReasonCode);
    }

    [Fact]
    public void AddSubtractOverflowAndBoundsFailClosed()
    {
        var full = Schema(
            "full",
            0,
            "unit",
            long.MinValue.ToString(CultureInfo.InvariantCulture),
            long.MaxValue.ToString(CultureInfo.InvariantCulture),
            "0");
        var bounded = Schema("bounded", 0, "unit", "0", "100", "0");
        var maximum = Quantity(full, long.MaxValue);
        var one = Quantity(full, 1);
        var ninety = Quantity(bounded, 90);
        var twenty = Quantity(bounded, 20);

        Assert.Equal(
            WorldNumericReasonCodes.Overflow,
            WorldNumericMath.Add(maximum, one, full).ReasonCode);
        Assert.Equal(
            WorldNumericReasonCodes.OutOfBounds,
            WorldNumericMath.Add(ninety, twenty, bounded).ReasonCode);
        Assert.Equal(
            70,
            WorldNumericMath.Subtract(
                    ninety,
                    twenty,
                    bounded)
                .Quantity!
                .Value
                .Units);
    }

    [Fact]
    public void MultiplyAndDivideUseExplicitUnitSignature()
    {
        var leftSchema = Schema(
            "left",
            2,
            "left-unit",
            "-100000",
            "100000",
            "0");
        var rightSchema = Schema(
            "right",
            2,
            "right-unit",
            "-100000",
            "100000",
            "0");
        var resultSchema = Schema(
            "result",
            2,
            "result-unit",
            "-100000",
            "100000",
            "0");
        var contract = new WorldNumericBinaryContract(
            "left-unit",
            "right-unit",
            resultSchema);

        var product = WorldNumericMath.Multiply(
            Quantity(leftSchema, 125),
            Quantity(rightSchema, 200),
            contract,
            WorldNumericRoundingMode.RejectIfInexact);
        var quotient = WorldNumericMath.Divide(
            Quantity(leftSchema, 125),
            Quantity(rightSchema, 200),
            contract,
            WorldNumericRoundingMode.HalfEven);

        Assert.True(product.Succeeded);
        Assert.Equal(250, product.Quantity!.Value.Units);
        Assert.True(quotient.Succeeded);
        Assert.Equal(62, quotient.Quantity!.Value.Units);
        Assert.Equal(
            WorldNumericReasonCodes.UnitMismatch,
            WorldNumericMath.Multiply(
                    Quantity(rightSchema, 125),
                    Quantity(leftSchema, 200),
                    contract,
                    WorldNumericRoundingMode.RejectIfInexact)
                .ReasonCode);
    }

    [Fact]
    public void RoundingModesAreDeterministicForSignsAndTies()
    {
        var source = Schema("source", 0, "source", "-100", "100", "0");
        var divisor = Schema("divisor", 0, "divisor", "-100", "100", "1");
        var result = Schema("result", 2, "result", "-1000", "1000", "0");
        var contract = new WorldNumericBinaryContract(
            "source",
            "divisor",
            result);
        var one = Quantity(source, 1);
        var three = Quantity(source, 3);
        var negativeOne = Quantity(source, -1);
        var eight = Quantity(divisor, 8);

        Assert.Equal(
            WorldNumericReasonCodes.Inexact,
            WorldNumericMath.Divide(
                    one,
                    eight,
                    contract,
                    WorldNumericRoundingMode.RejectIfInexact)
                .ReasonCode);
        Assert.Equal(
            12,
            WorldNumericMath.Divide(
                    one,
                    eight,
                    contract,
                    WorldNumericRoundingMode.HalfEven)
                .Quantity!
                .Value
                .Units);
        Assert.Equal(
            38,
            WorldNumericMath.Divide(
                    three,
                    eight,
                    contract,
                    WorldNumericRoundingMode.HalfEven)
                .Quantity!
                .Value
                .Units);
        Assert.Equal(
            -13,
            WorldNumericMath.Divide(
                    negativeOne,
                    eight,
                    contract,
                    WorldNumericRoundingMode.Floor)
                .Quantity!
                .Value
                .Units);
        Assert.Equal(
            -12,
            WorldNumericMath.Divide(
                    negativeOne,
                    eight,
                    contract,
                    WorldNumericRoundingMode.Ceiling)
                .Quantity!
                .Value
                .Units);
    }

    [Fact]
    public void RescaleClampConsumeAndDivisionByZeroAreExplicit()
    {
        var wide = Schema("wide", 2, "unit", "-10000", "10000", "0");
        var narrow = Schema("narrow", 1, "unit", "0", "100", "0");
        var divisor = Schema("divisor", 0, "divisor", "-10", "10", "1");
        var result = Schema("result", 0, "result", "-100", "100", "0");
        var contract = new WorldNumericBinaryContract(
            "unit",
            "divisor",
            result);

        Assert.Equal(
            WorldNumericReasonCodes.Inexact,
            WorldNumericMath.Rescale(
                    Quantity(wide, 125),
                    narrow,
                    WorldNumericRoundingMode.RejectIfInexact)
                .ReasonCode);
        Assert.Equal(
            12,
            WorldNumericMath.Rescale(
                    Quantity(wide, 125),
                    narrow,
                    WorldNumericRoundingMode.TowardZero)
                .Quantity!
                .Value
                .Units);
        Assert.Equal(
            100,
            WorldNumericMath.Clamp(
                    Quantity(wide, 2000),
                    narrow)
                .Quantity!
                .Value
                .Units);
        Assert.Equal(
            700,
            WorldNumericMath.Consume(
                    Quantity(wide, 1000),
                    Quantity(wide, 300))
                .Quantity!
                .Value
                .Units);
        Assert.Equal(
            WorldNumericReasonCodes.Insufficient,
            WorldNumericMath.Consume(
                    Quantity(wide, 100),
                    Quantity(wide, 300))
                .ReasonCode);
        Assert.Equal(
            WorldNumericReasonCodes.DivisionByZero,
            WorldNumericMath.Divide(
                    Quantity(wide, 100),
                    Quantity(divisor, 0),
                    contract,
                    WorldNumericRoundingMode.TowardZero)
                .ReasonCode);
    }

    private static WorldNumericSchema Schema(
        string id,
        int scale,
        string unit,
        string minimum,
        string maximum,
        string defaultValue)
    {
        return new WorldNumericSchema(
            id,
            scale,
            unit,
            minimum,
            maximum,
            defaultValue);
    }

    private static WorldNumericQuantity Quantity(
        WorldNumericSchema schema,
        long units)
    {
        return schema.TryBind(
                new WorldFixedPointValue(units, schema.Scale))
            .Quantity!;
    }

    private static JsonElement Json(string value)
    {
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }
}
