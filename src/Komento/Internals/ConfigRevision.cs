using System.Globalization;
using System.IO.Hashing;
using System.Text;

namespace Komento.Internals;

/// <summary>
/// A short, stable fingerprint of an experiment's definition (id, subject type, variants, filters and overrides).
/// Equal definitions give equal revisions in any process, so exposures can be grouped by the rules they ran under.
/// </summary>
internal static class ConfigRevision
{
    public static string Compute(ExperimentConfig config)
    {
        var text = new StringBuilder();

        Field(text, config.Id);
        Field(text, config.SubjectType);

        foreach (var variant in config.Variants)
        {
            Field(text, "variant");
            Field(text, variant.Name);
            Field(text, variant.Allocation.ToString("R", CultureInfo.InvariantCulture));
            Field(text, variant.Value is null
                ? ""
                : variant.Value.GetType().Name + "=" + Convert.ToString(variant.Value, CultureInfo.InvariantCulture));
        }

        foreach (var filter in config.GlobalFilters)
        {
            switch (filter)
            {
                case TraitEqualsFilter trait:
                    Field(text, "trait-equals");
                    Field(text, trait.Key);
                    Field(text, trait.Value);
                    break;
                case SegmentIncludeFilter segment:
                    Field(text, "segment-include");
                    Field(text, segment.Segment);
                    break;
            }
        }

        foreach (var rule in config.Overrides)
        {
            switch (rule)
            {
                case SubjectOverride subject:
                    Field(text, "subject");
                    Field(text, subject.SubjectId);
                    Field(text, subject.Variant);
                    break;
                case SegmentOverride segment:
                    Field(text, "segment");
                    Field(text, segment.Segment);
                    Field(text, segment.Variant);
                    break;
            }
        }

        var hash = XxHash64.HashToUInt64(Encoding.UTF8.GetBytes(text.ToString()));
        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    // Length-prefixed so that field boundaries are part of the hash: ("ab", "c") and ("a", "bc") differ.
    private static void Field(StringBuilder text, string value)
        => text.Append(value.Length).Append(':').Append(value).Append(';');
}
