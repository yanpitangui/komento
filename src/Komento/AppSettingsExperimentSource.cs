using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Komento;

public sealed class AppSettingsExperimentSource : IExperimentSource
{
    private readonly IConfiguration _configuration;
    private readonly string         _sectionPath;

    public AppSettingsExperimentSource(IConfiguration configuration, string sectionPath = "Komento")
    {
        _configuration = configuration;
        _sectionPath   = sectionPath;
    }

    public ValueTask<IReadOnlyDictionary<string, ExperimentConfig>> LoadAsync(
        IReadOnlySet<string> experimentIds,
        CancellationToken ct = default)
    {
        var dtos   = _configuration.GetSection(_sectionPath).GetSection("Experiments")
                                   .Get<List<ExperimentConfigDto>>() ?? [];
        var result = new Dictionary<string, ExperimentConfig>(StringComparer.Ordinal);

        foreach (var dto in dtos)
            if (!string.IsNullOrEmpty(dto.Id) && experimentIds.Contains(dto.Id))
                result[dto.Id] = ToConfig(dto);

        return ValueTask.FromResult<IReadOnlyDictionary<string, ExperimentConfig>>(result);
    }

    private static ExperimentConfig ToConfig(ExperimentConfigDto dto)
    {
        var variants = new List<VariantConfig>(dto.Variants.Count);
        foreach (var v in dto.Variants)
            variants.Add(new VariantConfig { Name = v.Name, Allocation = v.Allocation, Value = CoerceValue(v.Value) });

        var filters = new List<FilterConfig>(dto.GlobalFilters.Count);
        foreach (var f in dto.GlobalFilters)
            filters.Add(ToFilter(f));

        var overrides = new List<OverrideRule>(dto.Overrides.Count);
        foreach (var o in dto.Overrides)
            overrides.Add(ToOverride(o));

        return new ExperimentConfig
        {
            Id            = dto.Id,
            SubjectType   = dto.SubjectType,
            Variants      = variants,
            GlobalFilters = filters,
            Overrides     = overrides
        };
    }

    private static FilterConfig ToFilter(FilterConfigDto dto) => dto.Type.ToLowerInvariant() switch
    {
        "trait-equals"    => new TraitEqualsFilter    { Key = dto.Key ?? "", Value = dto.Value ?? "" },
        "segment-include" => new SegmentIncludeFilter { Segment = dto.Segment ?? "" },
        _                 => throw new InvalidOperationException($"Unknown filter type: '{dto.Type}'")
    };

    private static OverrideRule ToOverride(OverrideRuleDto dto) => dto.Type.ToLowerInvariant() switch
    {
        "subject" => new SubjectOverride { SubjectId = dto.SubjectId ?? "", Variant = dto.Variant ?? "" },
        "segment" => new SegmentOverride { Segment = dto.Segment ?? "",    Variant = dto.Variant ?? "" },
        _         => throw new InvalidOperationException($"Unknown override type: '{dto.Type}'")
    };

    private static object? CoerceValue(string? raw)
    {
        if (raw is null) return null;
        if (bool.TryParse(raw, out var b)) return b;
        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return n;
        if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
        return raw;
    }

    // ── DTOs (IConfiguration binding — no polymorphism, flat structure) ───────

    private sealed class ExperimentConfigDto
    {
        public string                  Id            { get; set; } = "";
        public string                  SubjectType   { get; set; } = "";
        public List<VariantConfigDto>  Variants      { get; set; } = [];
        public List<FilterConfigDto>   GlobalFilters { get; set; } = [];
        public List<OverrideRuleDto>   Overrides     { get; set; } = [];
    }

    private sealed class VariantConfigDto
    {
        public string  Name       { get; set; } = "";
        public double  Allocation { get; set; }
        public string? Value      { get; set; }
    }

    private sealed class FilterConfigDto
    {
        public string  Type    { get; set; } = "";
        public string? Key     { get; set; }
        public string? Value   { get; set; }
        public string? Segment { get; set; }
    }

    private sealed class OverrideRuleDto
    {
        public string  Type      { get; set; } = "";
        public string? SubjectId { get; set; }
        public string? Segment   { get; set; }
        public string? Variant   { get; set; }
    }
}
