# Performance

All read operations (variant lookup, filter evaluation, segment membership) are allocation-free on the hot path. BenchmarkDotNet (`MemoryDiagnoser`) shows **0 bytes allocated** per `GetVariantAsync` call across all paths, including the async segment filter path.

Key mechanisms:

- `FrozenDictionary<string, CompiledExperiment>`: the experiment map, with lock-free reads.
- `ArrayPool<byte>`: the XxHash64 input buffer, so no heap allocation per hash.
- `ValueTask<T>`: synchronous results wrapped without a `Task` allocation.
- `in EvaluationContext`: the struct is passed by reference, never copied.
- No LINQ and no closures in any hot-path method.

Config updates (`UpdateAsync`) are the only write operation. They compare each incoming config with the one held, compile what changed into a new `FrozenDictionary` and atomically swap the reference. In-flight reads are unaffected.

Run the benchmarks in Release mode:

```bash
dotnet run --project benchmarks/Komento.Benchmarks/ -c Release
```
