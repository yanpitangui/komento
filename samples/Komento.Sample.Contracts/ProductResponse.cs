namespace Komento.Sample.Contracts;

/// <summary>A product as shown to one subject, with the outcome of the experiments they were evaluated in.</summary>
public sealed record ProductResponse(
    string  ProductId,
    string  Name,
    decimal Price,
    bool    PremiumPage,
    string  PriceVariant);
