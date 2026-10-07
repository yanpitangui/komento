# Contributing

## Build and test

```bash
# Build
dotnet build

# Tests
dotnet test

# Single test
dotnet test --filter "FullyQualifiedName~Unknown_experiment_returns_NotFound"

# Benchmarks (Release mode required)
dotnet run --project benchmarks/Komento.Benchmarks/ -c Release
```

Tests use TUnit and AwesomeAssertions. The sample's integration tests start NATS and PostgreSQL containers through Aspire, so Docker must be running.

## Layout

| Path | Contents |
|---|---|
| `src/` | The packages |
| `tests/` | Unit and integration tests |
| `benchmarks/` | BenchmarkDotNet projects |
| `samples/` | The e-commerce sample ([walkthrough](samples/README.md)) |
| `docs/` | Documentation |
