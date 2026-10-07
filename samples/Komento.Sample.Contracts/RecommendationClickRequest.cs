namespace Komento.Sample.Contracts;

/// <summary>A click on a recommended item, recorded through OpenFeature's Track API.</summary>
public sealed record RecommendationClickRequest(string Item);
