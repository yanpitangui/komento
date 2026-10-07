using OpenFeature.Model;
using KomentoContext = Komento.EvaluationContext;
using OFContext = OpenFeature.Model.EvaluationContext;

namespace Komento.OpenFeature.AspNetCore;

/// <summary>Converts a Komento evaluation context into an OpenFeature one.</summary>
internal static class ContextMapper
{
    public static OFContext ToOpenFeature(string subjectId, KomentoContext context)
    {
        var builder = OFContext.Builder().SetTargetingKey(subjectId);
        foreach (var (key, raw) in context.Attributes)
            if (ToValue(raw) is { } value)
                builder.Set(key, value);
        return builder.Build();
    }

    private static Value? ToValue(object raw) => raw switch
    {
        string s  => new Value(s),
        bool b    => new Value(b),
        int i     => new Value(i),
        long l    => new Value((double)l),
        float f   => new Value((double)f),
        double d  => new Value(d),
        decimal m => new Value((double)m),
        _         => null   // other attribute types have no OpenFeature value
    };
}
