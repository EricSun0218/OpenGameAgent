using System.Globalization;
using System.Numerics;
using System.Text.Json;

namespace GameAgent.World;

public enum WorldNumericRoundingMode
{
    RejectIfInexact = 0,
    TowardZero = 1,
    Floor = 2,
    Ceiling = 3,
    HalfEven = 4
}

public static class WorldNumericReasonCodes
{
    public const string InvalidCanonicalValue = "numeric_invalid_canonical_value";

    public const string ScaleMismatch = "numeric_scale_mismatch";

    public const string UnitMismatch = "numeric_unit_mismatch";

    public const string OutOfBounds = "numeric_out_of_bounds";

    public const string Overflow = "numeric_overflow";

    public const string DivisionByZero = "numeric_division_by_zero";

    public const string Inexact = "numeric_inexact";

    public const string Insufficient = "numeric_insufficient";

    public const string NegativeAmount = "numeric_negative_amount";

    public const string BinaryFloatForbidden =
        "numeric_binary_float_forbidden";
}

/// <summary>
/// A portable authoritative value. <see cref="Units"/> is the signed,
/// unscaled Int64 and <see cref="Scale"/> declares the decimal scale.
/// Serialization uses <see cref="CanonicalUnits"/>, never a JSON number.
/// </summary>
public sealed class WorldFixedPointValue :
    IComparable<WorldFixedPointValue>,
    IEquatable<WorldFixedPointValue>
{
    public WorldFixedPointValue(long units, int scale)
    {
        if (scale is < 0 or > 18)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        Units = units;
        Scale = scale;
        CanonicalUnits = units.ToString(CultureInfo.InvariantCulture);
    }

    public long Units { get; }

    public int Scale { get; }

    public string CanonicalUnits { get; }

    public static WorldNumericParseResult TryParseCanonical(
        string? canonicalUnits,
        int scale)
    {
        if (scale is < 0 or > 18)
        {
            return WorldNumericParseResult.Failure(
                WorldNumericReasonCodes.ScaleMismatch);
        }

        if (!IsCanonicalInteger(canonicalUnits)
            || !long.TryParse(
                canonicalUnits,
                NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture,
                out var units))
        {
            return WorldNumericParseResult.Failure(
                WorldNumericReasonCodes.InvalidCanonicalValue);
        }

        return WorldNumericParseResult.Success(
            new WorldFixedPointValue(units, scale));
    }

    public int CompareTo(WorldFixedPointValue? other)
    {
        if (other is null)
        {
            return 1;
        }

        if (Scale != other.Scale)
        {
            throw new InvalidOperationException(
                "Fixed-point values require the same scale for comparison.");
        }

        return Units.CompareTo(other.Units);
    }

    public bool Equals(WorldFixedPointValue? other)
    {
        return other is not null
               && Units == other.Units
               && Scale == other.Scale;
    }

    public override bool Equals(object? obj)
    {
        return Equals(obj as WorldFixedPointValue);
    }

    public override int GetHashCode()
    {
        return HashCode.Combine(Units, Scale);
    }

    public override string ToString()
    {
        return CanonicalUnits;
    }

    private static bool IsCanonicalInteger(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 20)
        {
            return false;
        }

        var index = 0;
        if (value[0] == '-')
        {
            if (value.Length == 1 || value[1] == '0')
            {
                return false;
            }

            index = 1;
        }

        if (value[index] == '0')
        {
            return value.Length == 1;
        }

        if (value[index] is < '1' or > '9')
        {
            return false;
        }

        for (index++; index < value.Length; index++)
        {
            if (value[index] is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class WorldNumericParseResult
{
    private WorldNumericParseResult(
        bool succeeded,
        WorldFixedPointValue? value,
        string reasonCode)
    {
        Succeeded = succeeded;
        Value = value;
        ReasonCode = reasonCode;
    }

    public bool Succeeded { get; }

    public WorldFixedPointValue? Value { get; }

    public string ReasonCode { get; }

    internal static WorldNumericParseResult Success(
        WorldFixedPointValue value)
    {
        return new WorldNumericParseResult(true, value, string.Empty);
    }

    internal static WorldNumericParseResult Failure(string reasonCode)
    {
        return new WorldNumericParseResult(false, null, reasonCode);
    }
}

/// <summary>
/// Defines one authoritative numeric field. Bounds and defaults are supplied
/// as canonical unscaled Int64 strings.
/// </summary>
public sealed class WorldNumericSchema
{
    public WorldNumericSchema(
        string schemaId,
        int scale,
        string unitId,
        string minimum,
        string maximum,
        string defaultValue)
    {
        SchemaId = WorldValidation.Required(
            schemaId,
            nameof(schemaId));
        UnitId = WorldValidation.Required(unitId, nameof(unitId));
        if (scale is < 0 or > 18)
        {
            throw new ArgumentOutOfRangeException(nameof(scale));
        }

        Scale = scale;
        Minimum = ParseDefinitionValue(minimum, scale, nameof(minimum));
        Maximum = ParseDefinitionValue(maximum, scale, nameof(maximum));
        DefaultValue = ParseDefinitionValue(
            defaultValue,
            scale,
            nameof(defaultValue));
        if (Minimum.Units > Maximum.Units)
        {
            throw new ArgumentException(
                "The numeric minimum cannot exceed the maximum.");
        }

        if (DefaultValue.Units < Minimum.Units
            || DefaultValue.Units > Maximum.Units)
        {
            throw new ArgumentOutOfRangeException(
                nameof(defaultValue),
                "The numeric default must be within the declared bounds.");
        }
    }

    public string SchemaId { get; }

    public int Scale { get; }

    public string UnitId { get; }

    public WorldFixedPointValue Minimum { get; }

    public WorldFixedPointValue Maximum { get; }

    public WorldFixedPointValue DefaultValue { get; }

    public WorldNumericBindingResult TryBind(WorldFixedPointValue? value)
    {
        if (value is null || value.Scale != Scale)
        {
            return WorldNumericBindingResult.Failure(
                WorldNumericReasonCodes.ScaleMismatch);
        }

        if (value.Units < Minimum.Units || value.Units > Maximum.Units)
        {
            return WorldNumericBindingResult.Failure(
                WorldNumericReasonCodes.OutOfBounds);
        }

        return WorldNumericBindingResult.Success(
            new WorldNumericQuantity(this, value));
    }

    public WorldNumericBindingResult TryBindCanonical(
        string? canonicalUnits)
    {
        var parsed = WorldFixedPointValue.TryParseCanonical(
            canonicalUnits,
            Scale);
        return parsed.Succeeded
            ? TryBind(parsed.Value)
            : WorldNumericBindingResult.Failure(parsed.ReasonCode);
    }

    public WorldNumericBindingResult TryReadCanonical(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.String)
        {
            return WorldNumericBindingResult.Failure(
                value.ValueKind == JsonValueKind.Number
                    ? WorldNumericReasonCodes.BinaryFloatForbidden
                    : WorldNumericReasonCodes.InvalidCanonicalValue);
        }

        return TryBindCanonical(value.GetString());
    }

    private static WorldFixedPointValue ParseDefinitionValue(
        string value,
        int scale,
        string parameterName)
    {
        var parsed = WorldFixedPointValue.TryParseCanonical(value, scale);
        if (!parsed.Succeeded)
        {
            throw new ArgumentException(
                $"The value is not a canonical unscaled Int64: "
                + parsed.ReasonCode
                + ".",
                parameterName);
        }

        return parsed.Value!;
    }
}

public sealed class WorldNumericQuantity
{
    internal WorldNumericQuantity(
        WorldNumericSchema schema,
        WorldFixedPointValue value)
    {
        Schema = schema;
        Value = value;
    }

    public WorldNumericSchema Schema { get; }

    public WorldFixedPointValue Value { get; }
}

public sealed class WorldNumericBindingResult
{
    private WorldNumericBindingResult(
        bool succeeded,
        WorldNumericQuantity? quantity,
        string reasonCode)
    {
        Succeeded = succeeded;
        Quantity = quantity;
        ReasonCode = reasonCode;
    }

    public bool Succeeded { get; }

    public WorldNumericQuantity? Quantity { get; }

    public string ReasonCode { get; }

    internal static WorldNumericBindingResult Success(
        WorldNumericQuantity quantity)
    {
        return new WorldNumericBindingResult(true, quantity, string.Empty);
    }

    internal static WorldNumericBindingResult Failure(string reasonCode)
    {
        return new WorldNumericBindingResult(false, null, reasonCode);
    }
}

/// <summary>
/// Declares the unit signature of a multiplication or division. Unit
/// transformation is game-authored data, not inferred by the evaluator.
/// </summary>
public sealed class WorldNumericBinaryContract
{
    public WorldNumericBinaryContract(
        string leftUnitId,
        string rightUnitId,
        WorldNumericSchema resultSchema)
    {
        LeftUnitId = WorldValidation.Required(
            leftUnitId,
            nameof(leftUnitId));
        RightUnitId = WorldValidation.Required(
            rightUnitId,
            nameof(rightUnitId));
        ResultSchema = resultSchema
                       ?? throw new ArgumentNullException(
                           nameof(resultSchema));
    }

    public string LeftUnitId { get; }

    public string RightUnitId { get; }

    public WorldNumericSchema ResultSchema { get; }
}

public sealed class WorldNumericOperationResult
{
    private WorldNumericOperationResult(
        bool succeeded,
        WorldNumericQuantity? quantity,
        string reasonCode)
    {
        Succeeded = succeeded;
        Quantity = quantity;
        ReasonCode = reasonCode;
    }

    public bool Succeeded { get; }

    public WorldNumericQuantity? Quantity { get; }

    public string ReasonCode { get; }

    internal static WorldNumericOperationResult Success(
        WorldNumericQuantity quantity)
    {
        return new WorldNumericOperationResult(true, quantity, string.Empty);
    }

    internal static WorldNumericOperationResult Failure(string reasonCode)
    {
        return new WorldNumericOperationResult(false, null, reasonCode);
    }
}

public sealed class WorldNumericComparisonResult
{
    private WorldNumericComparisonResult(
        bool succeeded,
        int comparison,
        string reasonCode)
    {
        Succeeded = succeeded;
        Comparison = comparison;
        ReasonCode = reasonCode;
    }

    public bool Succeeded { get; }

    public int Comparison { get; }

    public string ReasonCode { get; }

    internal static WorldNumericComparisonResult Success(int comparison)
    {
        return new WorldNumericComparisonResult(
            true,
            Math.Sign(comparison),
            string.Empty);
    }

    internal static WorldNumericComparisonResult Failure(string reasonCode)
    {
        return new WorldNumericComparisonResult(false, 0, reasonCode);
    }
}

public static class WorldNumericMath
{
    public static WorldNumericComparisonResult Compare(
        WorldNumericQuantity left,
        WorldNumericQuantity right)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }
        if (!SameUnit(left, right))
        {
            return WorldNumericComparisonResult.Failure(
                WorldNumericReasonCodes.UnitMismatch);
        }

        var commonScale = Math.Max(
            left.Value.Scale,
            right.Value.Scale);
        var leftUnits = new BigInteger(left.Value.Units)
                        * Pow10(commonScale - left.Value.Scale);
        var rightUnits = new BigInteger(right.Value.Units)
                         * Pow10(commonScale - right.Value.Scale);
        return WorldNumericComparisonResult.Success(
            leftUnits.CompareTo(rightUnits));
    }

    public static WorldNumericOperationResult Add(
        WorldNumericQuantity left,
        WorldNumericQuantity right,
        WorldNumericSchema resultSchema,
        WorldNumericRoundingMode roundingMode =
            WorldNumericRoundingMode.RejectIfInexact)
    {
        return AddOrSubtract(
            left,
            right,
            resultSchema,
            roundingMode,
            subtract: false);
    }

    public static WorldNumericOperationResult Subtract(
        WorldNumericQuantity left,
        WorldNumericQuantity right,
        WorldNumericSchema resultSchema,
        WorldNumericRoundingMode roundingMode =
            WorldNumericRoundingMode.RejectIfInexact)
    {
        return AddOrSubtract(
            left,
            right,
            resultSchema,
            roundingMode,
            subtract: true);
    }

    public static WorldNumericOperationResult Multiply(
        WorldNumericQuantity left,
        WorldNumericQuantity right,
        WorldNumericBinaryContract contract,
        WorldNumericRoundingMode roundingMode)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (contract is null)
        {
            throw new ArgumentNullException(nameof(contract));
        }
        if (!string.Equals(
                left.Schema.UnitId,
                contract.LeftUnitId,
                StringComparison.Ordinal)
            || !string.Equals(
                right.Schema.UnitId,
                contract.RightUnitId,
                StringComparison.Ordinal))
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.UnitMismatch);
        }

        return FromFraction(
            new BigInteger(left.Value.Units) * right.Value.Units,
            Pow10(left.Value.Scale + right.Value.Scale),
            contract.ResultSchema,
            roundingMode);
    }

    public static WorldNumericOperationResult Divide(
        WorldNumericQuantity left,
        WorldNumericQuantity right,
        WorldNumericBinaryContract contract,
        WorldNumericRoundingMode roundingMode)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (contract is null)
        {
            throw new ArgumentNullException(nameof(contract));
        }
        if (right.Value.Units == 0)
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.DivisionByZero);
        }

        if (!string.Equals(
                left.Schema.UnitId,
                contract.LeftUnitId,
                StringComparison.Ordinal)
            || !string.Equals(
                right.Schema.UnitId,
                contract.RightUnitId,
                StringComparison.Ordinal))
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.UnitMismatch);
        }

        return FromFraction(
            new BigInteger(left.Value.Units)
            * Pow10(right.Value.Scale),
            new BigInteger(right.Value.Units)
            * Pow10(left.Value.Scale),
            contract.ResultSchema,
            roundingMode);
    }

    public static WorldNumericOperationResult Rescale(
        WorldNumericQuantity value,
        WorldNumericSchema resultSchema,
        WorldNumericRoundingMode roundingMode)
    {
        if (value is null)
        {
            throw new ArgumentNullException(nameof(value));
        }

        if (resultSchema is null)
        {
            throw new ArgumentNullException(nameof(resultSchema));
        }
        if (!string.Equals(
                value.Schema.UnitId,
                resultSchema.UnitId,
                StringComparison.Ordinal))
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.UnitMismatch);
        }

        return FromFraction(
            value.Value.Units,
            Pow10(value.Value.Scale),
            resultSchema,
            roundingMode);
    }

    public static WorldNumericOperationResult Clamp(
        WorldNumericQuantity value,
        WorldNumericSchema resultSchema,
        WorldNumericRoundingMode roundingMode =
            WorldNumericRoundingMode.RejectIfInexact)
    {
        var converted = Rescale(value, resultSchema, roundingMode);
        if (converted.Succeeded)
        {
            return converted;
        }

        if (!string.Equals(
                value.Schema.UnitId,
                resultSchema.UnitId,
                StringComparison.Ordinal))
        {
            return converted;
        }

        var minimum = BindUnchecked(
            resultSchema,
            resultSchema.Minimum);
        var maximum = BindUnchecked(
            resultSchema,
            resultSchema.Maximum);
        var below = Compare(value, minimum);
        if (below.Succeeded && below.Comparison < 0)
        {
            return WorldNumericOperationResult.Success(minimum);
        }

        var above = Compare(value, maximum);
        return above.Succeeded && above.Comparison > 0
            ? WorldNumericOperationResult.Success(maximum)
            : converted;
    }

    public static WorldNumericOperationResult Consume(
        WorldNumericQuantity available,
        WorldNumericQuantity amount)
    {
        if (available is null)
        {
            throw new ArgumentNullException(nameof(available));
        }

        if (amount is null)
        {
            throw new ArgumentNullException(nameof(amount));
        }
        if (amount.Value.Units < 0)
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.NegativeAmount);
        }

        var comparison = Compare(available, amount);
        if (!comparison.Succeeded)
        {
            return WorldNumericOperationResult.Failure(
                comparison.ReasonCode);
        }

        if (comparison.Comparison < 0)
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.Insufficient);
        }

        return Subtract(
            available,
            amount,
            available.Schema,
            WorldNumericRoundingMode.RejectIfInexact);
    }

    private static WorldNumericOperationResult AddOrSubtract(
        WorldNumericQuantity left,
        WorldNumericQuantity right,
        WorldNumericSchema resultSchema,
        WorldNumericRoundingMode roundingMode,
        bool subtract)
    {
        if (left is null)
        {
            throw new ArgumentNullException(nameof(left));
        }

        if (right is null)
        {
            throw new ArgumentNullException(nameof(right));
        }

        if (resultSchema is null)
        {
            throw new ArgumentNullException(nameof(resultSchema));
        }
        if (!SameUnit(left, right)
            || !string.Equals(
                left.Schema.UnitId,
                resultSchema.UnitId,
                StringComparison.Ordinal))
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.UnitMismatch);
        }

        var denominator = Pow10(
            left.Value.Scale + right.Value.Scale);
        var leftNumerator = new BigInteger(left.Value.Units)
                            * Pow10(right.Value.Scale);
        var rightNumerator = new BigInteger(right.Value.Units)
                             * Pow10(left.Value.Scale);
        return FromFraction(
            subtract
                ? leftNumerator - rightNumerator
                : leftNumerator + rightNumerator,
            denominator,
            resultSchema,
            roundingMode);
    }

    private static WorldNumericOperationResult FromFraction(
        BigInteger numerator,
        BigInteger denominator,
        WorldNumericSchema resultSchema,
        WorldNumericRoundingMode roundingMode)
    {
        if (!Enum.IsDefined(typeof(WorldNumericRoundingMode), roundingMode))
        {
            throw new ArgumentOutOfRangeException(nameof(roundingMode));
        }

        if (denominator.IsZero)
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.DivisionByZero);
        }

        numerator *= Pow10(resultSchema.Scale);
        var quotient = BigInteger.DivRem(
            numerator,
            denominator,
            out var remainder);
        if (!remainder.IsZero)
        {
            var direction = numerator.Sign * denominator.Sign;
            switch (roundingMode)
            {
                case WorldNumericRoundingMode.RejectIfInexact:
                    return WorldNumericOperationResult.Failure(
                        WorldNumericReasonCodes.Inexact);
                case WorldNumericRoundingMode.TowardZero:
                    break;
                case WorldNumericRoundingMode.Floor:
                    if (direction < 0)
                    {
                        quotient--;
                    }

                    break;
                case WorldNumericRoundingMode.Ceiling:
                    if (direction > 0)
                    {
                        quotient++;
                    }

                    break;
                case WorldNumericRoundingMode.HalfEven:
                    var doubledRemainder =
                        BigInteger.Abs(remainder) * 2;
                    var denominatorMagnitude =
                        BigInteger.Abs(denominator);
                    if (doubledRemainder > denominatorMagnitude
                        || (doubledRemainder == denominatorMagnitude
                            && !quotient.IsEven))
                    {
                        quotient += direction;
                    }

                    break;
            }
        }

        if (quotient < long.MinValue || quotient > long.MaxValue)
        {
            return WorldNumericOperationResult.Failure(
                WorldNumericReasonCodes.Overflow);
        }

        var value = new WorldFixedPointValue(
            (long)quotient,
            resultSchema.Scale);
        var binding = resultSchema.TryBind(value);
        return binding.Succeeded
            ? WorldNumericOperationResult.Success(binding.Quantity!)
            : WorldNumericOperationResult.Failure(binding.ReasonCode);
    }

    private static bool SameUnit(
        WorldNumericQuantity left,
        WorldNumericQuantity right)
    {
        return string.Equals(
            left.Schema.UnitId,
            right.Schema.UnitId,
            StringComparison.Ordinal);
    }

    private static WorldNumericQuantity BindUnchecked(
        WorldNumericSchema schema,
        WorldFixedPointValue value)
    {
        return schema.TryBind(value).Quantity!;
    }

    private static BigInteger Pow10(int exponent)
    {
        return BigInteger.Pow(10, exponent);
    }
}
