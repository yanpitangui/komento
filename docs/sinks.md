# Sinks

`Komento.Sinks` delivers exposures and conversions to a destination. A sink receives batches; Komento handles batching by size and time, a bounded buffer, timeouts, error isolation and a flush on shutdown. You write only the part specific to your destination.

```
dotnet add package Komento.Sinks
```

## Sending exposures somewhere

**1. Write a sink.** This one posts batches to an HTTP endpoint:

```csharp
public sealed class HttpExposureSink(IHttpClientFactory httpClientFactory) : IExposureSink
{
    public async ValueTask WriteAsync(IReadOnlyList<ExposureEvent> batch, CancellationToken ct)
    {
        var http     = httpClientFactory.CreateClient("exposures");
        var response = await http.PostAsJsonAsync("/v1/exposures", batch, ct);
        response.EnsureSuccessStatusCode();   // throwing drops this batch (it is logged and counted)
    }
}
```

Rules for sinks:

- `WriteAsync` calls are sequential per sink, so no locking is needed.
- Pass `ct` through. It is cancelled when `WriteTimeout` elapses, or when shutdown gives up on the sink after `ShutdownFlushTimeout`.
- If it throws or times out, that batch is dropped and later batches still arrive. Do any retrying inside the sink.
- The sink is created once as a singleton from the root service provider, so it depends on singleton or transient services.

**2. Register it.** Enable the stream and add the sink wherever you call `AddKomento`:

```csharp
builder.Services.AddHttpClient("exposures", c => c.BaseAddress = new Uri("https://events.example.com"));

builder.Services
    .AddKomento(o => o.EnableExposureStream = true)      // required: sinks read this stream
    .AddExposureSink<HttpExposureSink>(o =>
    {
        o.BatchSize     = 200;                            // flush when 200 are buffered...
        o.FlushInterval = TimeSpan.FromSeconds(5);        // ...or after 5 seconds, whichever is first
    });
```

Sinks run in a background service, so your app must run under a .NET generic host (ASP.NET Core and `Host.CreateApplicationBuilder` both do). If you build the container by hand, start the registered `IHostedService`s yourself. If `EnableExposureStream` is not set, the host fails at startup with a message saying so.

**3. Check that it works.** Add the built-in logging sink next to yours and evaluate an experiment. You should see one entry per exposure:

```csharp
.AddLoggingExposureSink()
// info: Komento.Sinks.Exposure
//       Exposure checkout-button subject user-42 -> treatment (eligible: True, outsider: False) at 2026-10-06T12:00:00.0000000+00:00
```

Remove it once your real sink works; it is useful for local development.

You can register as many sinks as you like (`AddExposureSink<A>().AddExposureSink<B>()`, or a delegate: `.AddExposureSink(async (batch, ct) => await ...)`). Each one gets every exposure.

## Conversion sinks

Track sinks work the same way for conversions: `ITrackSink.WriteAsync(IReadOnlyList<TrackEvent>, CancellationToken)`, one isolated queue per sink, the same options and the same failure behavior. Register with `AddTrackSink<T>()`, a delegate overload, or `AddLoggingTrackSink()`. They need `EnableTrackStream = true`; the host fails at startup with a clear message otherwise. Drops are counted in `komento.track.sink.dropped`.

## Options

| Option | Default | Meaning |
|---|---|---|
| `Name` | type name, or `sink-{index}` | The `sink` tag on metrics and the name in logs |
| `BatchSize` | 100 | Max events per `WriteAsync` call |
| `FlushInterval` | 5 s | Flush a partial batch after this long |
| `MaxPendingBatches` | 64 | Batches buffered while the sink is busy |
| `WriteTimeout` | 30 s | The write's token is cancelled after this; the batch is dropped |
| `ShutdownFlushTimeout` | 10 s | How long shutdown waits for this sink to flush |

## Behavior under load

Evaluation never waits on a sink. Komento writes each event to a bounded stream (`ExposureChannelCapacity` / `TrackChannelCapacity`, default 4096); if it is full, the event is dropped and counted in `komento.exposures.dropped` / `komento.track.dropped`.

Each sink then has its own queue, so a slow or stuck sink only affects itself: when its `MaxPendingBatches` fill, its new batches are dropped and counted in `komento.exposures.sink.dropped` / `komento.track.sink.dropped`, and the other sinks are unaffected. On shutdown each sink gets `ShutdownFlushTimeout` to write what is pending, then is abandoned. A sink that ignores its cancellation token and blocks forever cannot be killed, so it will fill its queue and then drop; watch the drop counters.

Alert on all four drop counters ([Metrics and traces](metrics-and-traces.md#metrics)). If any is non-zero, the data in your sink is incomplete and any analysis of it is biased.
