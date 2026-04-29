namespace Komento;

public readonly struct VariantResult : IEquatable<VariantResult>
{
    public string  VariantName { get; init; }
    public object? Value       { get; init; }
    public bool    IsEligible  { get; init; }
    public bool    IsOutsider  { get; init; }

    /// <summary>Returned when the experiment does not exist. Value-equal to <see cref="Ineligible"/> by design — both result in control behavior.</summary>
    public static readonly VariantResult NotFound = new()
    {
        VariantName = "control",
        IsEligible  = false,
        IsOutsider  = false,
        Value       = null
    };

    /// <summary>Returned when the subject does not pass global filters. Value-equal to <see cref="NotFound"/> by design — both result in control behavior.</summary>
    public static readonly VariantResult Ineligible = new()
    {
        VariantName = "control",
        IsEligible  = false,
        IsOutsider  = false,
        Value       = null
    };

    public static VariantResult Outsider(object? controlValue = null) => new()
    {
        VariantName = "control",
        IsEligible  = true,
        IsOutsider  = true,
        Value       = controlValue
    };

    public static bool operator ==(VariantResult left, string right)
        => string.Equals(left.VariantName, right, StringComparison.Ordinal);

    public static bool operator !=(VariantResult left, string right)
        => !string.Equals(left.VariantName, right, StringComparison.Ordinal);

    /// <summary>Equality is determined by VariantName, IsEligible, and IsOutsider only. Value is intentionally excluded — variant identity does not depend on payload.</summary>
    public bool Equals(VariantResult other)
        => VariantName == other.VariantName && IsEligible == other.IsEligible && IsOutsider == other.IsOutsider;

    public override bool Equals(object? obj) => obj is VariantResult other && Equals(other);
    public override int  GetHashCode()        => HashCode.Combine(VariantName, IsEligible, IsOutsider);
}
