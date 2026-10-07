namespace Komento.Internals;

/// <summary>
/// Structural comparison of two experiment configs. When two parts cannot be compared reliably (for example
/// two distinct instances of a variant value type without value equality), they count as different, so a
/// real change is never missed; at worst an unchanged config is reported as changed.
/// </summary>
internal static class ExperimentConfigComparer
{
    public static bool AreEqual(ExperimentConfig a, ExperimentConfig b)
        => ReferenceEquals(a, b)
           || (a.Id == b.Id
               && a.SubjectType == b.SubjectType
               && SequenceEqual(a.Variants, b.Variants, VariantEqual)
               && SequenceEqual(a.GlobalFilters, b.GlobalFilters, FilterEqual)
               && SequenceEqual(a.Overrides, b.Overrides, OverrideEqual));

    private static bool VariantEqual(VariantConfig x, VariantConfig y)
        => x.Name == y.Name && x.Allocation.Equals(y.Allocation) && Equals(x.Value, y.Value);

    private static bool FilterEqual(FilterConfig x, FilterConfig y) => (x, y) switch
    {
        (TraitEqualsFilter tx, TraitEqualsFilter ty)       => tx.Key == ty.Key && tx.Value == ty.Value,
        (SegmentIncludeFilter sx, SegmentIncludeFilter sy) => sx.Segment == sy.Segment,
        _                                                  => ReferenceEquals(x, y)
    };

    private static bool OverrideEqual(OverrideRule x, OverrideRule y) => (x, y) switch
    {
        (SubjectOverride sx, SubjectOverride sy) => sx.SubjectId == sy.SubjectId && sx.Variant == sy.Variant,
        (SegmentOverride sx, SegmentOverride sy) => sx.Segment == sy.Segment && sx.Variant == sy.Variant,
        _                                        => ReferenceEquals(x, y)
    };

    private static bool SequenceEqual<T>(IReadOnlyList<T> x, IReadOnlyList<T> y, Func<T, T, bool> equal)
    {
        if (x.Count != y.Count) return false;
        for (var i = 0; i < x.Count; i++)
            if (!equal(x[i], y[i])) return false;
        return true;
    }
}
