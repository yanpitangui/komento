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
| Aspire orchestration | AppHost wires NATS + PostgreSQL + both APIs |

## Projects

```
samples/
  Komento.Sample.AppHost/        — Aspire orchestrator
  Komento.Sample.EcommerceApi/   — Customer-facing API
  Komento.Sample.AdminApi/       — Back-office API
  Komento.Sample.ServiceDefaults/— Shared health-checks / service discovery
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

```bash
# Enable the premium-product-page experiment (100 % treatment)
curl -X PUT http://localhost:<admin-port>/experiments/premium-product-page \
     -H "Content-Type: application/json" \
     -d '{
       "bucketCount": 1000,
       "variants": [
         { "name": "control",   "bucketRanges": [] },
         { "name": "treatment", "bucketRanges": [{ "start": 1, "end": 1000 }] }
       ],
       "filters": [],
       "overrides": []
     }'
```

The NATS watcher in EcommerceApi picks up the change within milliseconds. The next
`/products/{id}` call will return `"premiumPage": true` for users who hash into the
treatment bucket.

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

## Seeded data

On startup, `DataSeeder` seeds three experiments into NATS KV:

| Experiment | Buckets | Segments |
|---|---|---|
| `premium-product-page` | control 100 %, treatment 0 % | none |
| `price-display` | default 50 %, loyalty-price 30 %, vip-price 20 % | loyalty filter, VIP override |
| `recommendation-algorithm` | collaborative 70 %, content-based 30 % | premium-plan filter |

It also seeds three loyalty users (`alice`, `bob`, `carol`) into the `loyalty` NATS KV bucket.
