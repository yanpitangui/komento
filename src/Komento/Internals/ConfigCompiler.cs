namespace Komento.Internals;

internal static class ConfigCompiler
{
    private const int BucketCount = 1000;

    public static CompiledExperiment Compile(ExperimentConfig config)
    {
        var variants      = config.Variants;
        var compiled      = new CompiledVariant[variants.Count];
        var currentBucket = 1;

        for (var i = 0; i < variants.Count; i++)
        {
            var variant = variants[i];
            var count   = (int)Math.Round(variant.Allocation * BucketCount);
            var end     = Math.Min(currentBucket + count - 1, BucketCount);

            compiled[i] = new CompiledVariant
            {
                Name   = variant.Name,
                Value  = variant.Value,
                Ranges = [new BucketRange { Start = currentBucket, End = end }]
            };
            currentBucket = end + 1;
        }

        // When allocations sum to 1.0, clamp last variant to bucket 1000.
        // Needed because e.g. 3×0.33 rounds to 3×333 = 999, not 1000.
        if (compiled.Length > 0)
        {
            var total = 0.0;
            for (var i = 0; i < variants.Count; i++)
                total += variants[i].Allocation;

            if (Math.Abs(total - 1.0) < 0.001)
            {
                ref var last      = ref compiled[^1];
                var     lastRange = last.Ranges[0];
                if (lastRange.End < BucketCount)
                {
                    last = new CompiledVariant
                    {
                        Name   = last.Name,
                        Value  = last.Value,
                        Ranges = [new BucketRange { Start = lastRange.Start, End = BucketCount }]
                    };
                }
            }
        }

        return new CompiledExperiment
        {
            Id          = config.Id,
            SubjectType = config.SubjectType,
            Variants    = compiled,
            Filters     = config.GlobalFilters.Count > 0 ? [.. config.GlobalFilters] : [],
            Overrides   = config.Overrides.Count > 0    ? [.. config.Overrides]     : []
        };
    }
}
