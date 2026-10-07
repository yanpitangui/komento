namespace Komento.Sample.Contracts;

/// <summary>Recommended items, and the algorithm variant that produced them.</summary>
public sealed record RecommendationsResponse(
    string                Algorithm,
    IReadOnlyList<string> Items);
