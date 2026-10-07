# Komento Sample — E-Commerce Demo

A runnable end-to-end demo that exercises every Komento public extension point:

| Extension point | Where used |
|---|---|
| `IExperimentSource` | NATS JetStream KV → experiment configs |
| `IConfigUpdater` | Live config reload via `NatsExperimentWatcher` |
| `ISubjectProvider` | JWT `sub` claim → subject ID |
| `IEvaluationContextEnricher` | JWT claims → context attributes |
| `ISegmentProvider` | `NatsLoyaltyStore` + `VipBinSetStore` |
| `BinSet` | Compact in-memory VIP user set loaded from PostgreSQL |
| `IExperimentClient` | Direct usage in `/products/{id}` |
| `KomentoFeatureProvider` | OpenFeature bridge in `/recommendations` |
| `IExposureSink` | `PostgresExposureSink` batches every exposure into the `exposures` table |
| `IExperimentTracker` / `ITrackSink` | `/products/{id}/purchase` records a conversion natively; `PostgresTrackSink` stores it in the `conversions` table |
| OpenFeature `Track` | `/recommendations/clicks` records a conversion through `IFeatureClient.Track`, delivered by the same sink |
| `KomentoOptions.StaticContext` | Adds `service = ecommerce-api` to every evaluation and every recorded conversion |
| Aspire orchestration | AppHost wires NATS + PostgreSQL + both APIs |

## Projects

```
samples/
  Komento.Sample.AppHost/        — Aspire orchestrator
  Komento.Sample.EcommerceApi/   — Customer-facing API
  Komento.Sample.AdminApi/       — Back-office API
  Komento.Sample.ServiceDefaults/— Shared health-checks / service discovery
  Komento.Sample.Contracts/      — Response types shared by the APIs and the tests
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Docker](https://www.docker.com/) (for NATS and PostgreSQL containers)

## Running

```bash
dotnet run --project samples/Komento.Sample.AppHost
```

Aspire starts both APIs and launches the Aspire Dashboard at `http://localhost:15888`.

## Walkthrough

### 1. Get a token

```bash
# free-tier user
curl "http://localhost:<ecommerce-port>/token?userId=alice&plan=free"

# loyalty member
curl "http://localhost:<ecommerce-port>/token?userId=bob&plan=free"
```

Use the Aspire Dashboard to find the port for `ecommerce-api`.

### 2. View a product

```bash
TOKEN=<token from step 1>

curl -H "Authorization: Bearer $TOKEN" \
     http://localhost:<ecommerce-port>/products/42
```

Response shape:

```json
{
  "productId": "42",
  "name": "Komento Widget 42",
  "price": 99.99,
  "premiumPage": false,
  "priceVariant": "default"
}
```

### 3. Create an experiment (Admin API)

The body is an `ExperimentConfig`. The Admin API rejects a body that is not one (or whose `id`
differs from the one in the URL) with a `400`:

```bash
# Define the premium-product-page experiment: everyone on the premium plan gets the "on" variant
curl -X PUT http://localhost:<admin-port>/experiments/premium-product-page \
     -H "Content-Type: application/json" \
     -d '{
       "id": "premium-product-page",
       "subjectType": "user",
       "variants": [
         { "name": "on", "allocation": 1.0, "value": true }
       ],
       "globalFilters": [
         { "type": "trait-equals", "key": "plan", "value": "premium" }
       ],
       "overrides": []
     }'

# Read it back
curl http://localhost:<admin-port>/experiments/premium-product-page
```

The NATS watcher in EcommerceApi picks up the change within milliseconds. The next
`/products/{id}` call will return `"premiumPage": true` for users whose token has
`plan=premium`.

### 4. Add a loyalty member

```bash
# Mark bob as a loyalty member
curl -X PUT http://localhost:<admin-port>/loyalty/bob

# bob's token now gets a loyalty-price
curl -H "Authorization: Bearer $BOB_TOKEN" \
     http://localhost:<ecommerce-port>/products/42
# → "priceVariant": "loyalty-price", "price": 79.99
```

### 5. Add a VIP user

```bash
# Promote alice to VIP (persisted in PostgreSQL, loaded into BinSet)
curl -X POST http://localhost:<admin-port>/vip/alice

# After the 5-minute BinSet refresh (or restart), alice gets vip-price
curl -H "Authorization: Bearer $ALICE_TOKEN" \
     http://localhost:<ecommerce-port>/products/42
# → "priceVariant": "vip-price", "price": 89.99
```

### 6. Get recommendations (OpenFeature)

```bash
curl -H "Authorization: Bearer $TOKEN" \
     http://localhost:<ecommerce-port>/recommendations
```

The `recommendation-algorithm` experiment is evaluated through the OpenFeature
`KomentoFeatureProvider` bridge, returning `collaborative` or `content-based`
depending on the experiment assignment.

### 7. See the recorded exposures

Every evaluation above was recorded as an exposure. `PostgresExposureSink` batches them
(flushing every second in this sample) into the `exposures` table, and the Admin API reads
them back:

```bash
# after viewing a product with a token for the user "nobody"
curl "http://localhost:<admin-port>/exposures?subjectId=nobody"
```

`/products/{id}` evaluates two experiments, so you get two rows (newest first). "nobody" is
neither a loyalty member nor VIP and is on the free plan, so both are ineligible:

```json
[
  { "experiment": "price-display", "subjectId": "nobody", "subjectType": "user",
    "variant": "control", "isEligible": false, "isOutsider": false, "exposedAt": "..." },
  { "experiment": "premium-product-page", "subjectId": "nobody", "subjectType": "user",
    "variant": "control", "isEligible": false, "isOutsider": false, "exposedAt": "..." }
]
```

Outsiders and ineligible subjects are recorded too (with variant `control`); filter them out
when analyzing. Every evaluation is recorded (nothing is de-duplicated), so the endpoint returns
at most the latest 100 exposures for the subject. The same exposures are also written to the
EcommerceApi log by `AddLoggingExposureSink()`.

### 8. Record a conversion

Conversions are recorded two ways, and both end up in the `conversions` table through the same sink.

```bash
# Native: IExperimentTracker.Track
curl -X POST http://localhost:<ecommerce-port>/products/42/purchase \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{ "amount": 49.90 }'

# OpenFeature: featureClient.Track
curl -X POST http://localhost:<ecommerce-port>/recommendations/clicks \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -d '{ "item": "Widget A" }'

# Read them back
curl "http://localhost:<admin-port>/conversions?subjectId=alice"
```

Each conversion carries the evaluation context: the per-call attributes (here the user's `plan`)
plus the static context configured once on `KomentoOptions` (here `service`). The conversions endpoint
returns at most the latest 100 for the subject, newest first.

## Seeded data

On startup, `DataSeeder` seeds three experiments into NATS KV:

| Experiment | Buckets | Segments |
|---|---|---|
| `premium-product-page` | control 100 %, treatment 0 % | none |
| `price-display` | default 50 %, loyalty-price 30 %, vip-price 20 % | loyalty filter, VIP override |
| `recommendation-algorithm` | collaborative 70 %, content-based 30 % | premium-plan filter |

It also seeds three loyalty users (`alice`, `bob`, `carol`) into the `loyalty` NATS KV bucket.
