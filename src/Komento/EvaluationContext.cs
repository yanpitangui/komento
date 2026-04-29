using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace Komento;

public readonly struct EvaluationContext
{
    private readonly FrozenDictionary<string, object>? _attributes;

    internal EvaluationContext(FrozenDictionary<string, object> attributes)
        => _attributes = attributes;

    public bool TryGetValue(string key, [NotNullWhen(true)] out object? value)
    {
        if (_attributes is null) { value = null; return false; }
        return _attributes.TryGetValue(key, out value);
    }

    internal void CopyTo(Dictionary<string, object> target)
    {
        if (_attributes is null) return;
        foreach (var kvp in _attributes)
            target[kvp.Key] = kvp.Value;
    }

    public static EvaluationContextBuilder Create() => new();

    public static readonly EvaluationContext Empty = default;
}

public sealed class EvaluationContextBuilder
{
    private readonly Dictionary<string, object> _attributes = new(StringComparer.Ordinal);

    public EvaluationContextBuilder Set(string key, object value)
    {
        _attributes[key] = value;
        return this;
    }

    public EvaluationContext Build()
        => new(_attributes.ToFrozenDictionary(StringComparer.Ordinal));

    public static EvaluationContextBuilder CreateFrom(in EvaluationContext context)
    {
        var builder = new EvaluationContextBuilder();
        context.CopyTo(builder._attributes);
        return builder;
    }
}
