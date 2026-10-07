# Metrics and traces

Komento reports through `System.Diagnostics`, so any OpenTelemetry setup can pick it up. Both outputs need no extension point and no per-subject data in metrics.

## Metrics

A meter named `Komento` is always on:

| Instrument | Tags | Meaning |
|---|---|---|
| `komento.exposures` | `experiment`, `variant`, `outcome` (`assigned` / `outsider` / `ineligible`) | Exposures per variant |
| `komento.exposures.dropped` | `experiment` | Exposures dropped because the exposure stream was full |
| `komento.exposures.sink.dropped` | `sink`, `reason` (`queue_full` / `write_failed` / `write_timeout`) | Exposures a sink did not receive |
| `komento.track.dropped` | `reason` (`queue_full` / `no_subject` / `no_event_name`) | Conversion events dropped before reaching a sink |
| `komento.track.sink.dropped` | `sink`, `reason` (`queue_full` / `write_failed` / `write_timeout`) | Conversion events a sink did not receive |

Outsiders and ineligible subjects are reported with `variant="control"`, so filter on `outcome="assigned"` to count exposures to real variants. Tags carry only bounded values (experiment, variant, outcome), which keeps metric cardinality low; per-subject detail lives in the [exposure stream and sinks](exposures-and-conversions.md).

To export with OpenTelemetry:

```csharp
builder.Services.AddOpenTelemetry()
    .WithMetrics(m => m
        .AddMeter("Komento")
        .AddPrometheusExporter());   // or AddOtlpExporter(), etc.
```

## Traces

Each exposure is also recorded as a `feature_flag.evaluation` event on `Activity.Current` (for example the ASP.NET Core request span), following the [OpenTelemetry feature-flag convention](https://opentelemetry.io/docs/specs/semconv/feature-flags/feature-flags-events/). That ties "which variant did this request get" to the rest of the trace, with no extra setup. The event is recorded whenever a trace is active and sampled.

| Tag | Value |
|---|---|
| `feature_flag.key` | The experiment id |
| `feature_flag.result.variant` | The variant received (`control` for outsiders and ineligible subjects) |
| `feature_flag.result.reason` | `split` for an assigned variant, otherwise `default` |
| `feature_flag.provider.name` | `Komento` |
| `feature_flag.context.id` | The subject ID |
| `komento.outcome` | `assigned`, `outsider` or `ineligible` |
| `komento.subject_type` | The experiment's subject type |

The subject ID is recorded by default. If your subject IDs are personal data that must not appear in traces (traces are often retained and shared differently from application data), exclude it:

```csharp
services.AddKomento(o => o.IncludeSubjectIdInActivityEvents = false);
```

Turn the event off entirely with `EmitActivityEvents = false`. Using OpenFeature's own `TraceEnricherHook` instead is covered in [OpenFeature](openfeature.md#telemetry).
