# TEOS Opportunity Network

**by Elmahrosa**

An AI-powered opportunity, talent, software, and services network. Not a traditional job board.

```text
Discover opportunities
→ Normalize
→ Deduplicate
→ Match against user profiles
→ Persist evidence
→ Apply threshold
→ Notify users
```

## Opportunity types

`JOB` · `PROJECT` · `BOUNTY` · `HACKATHON` · `SOFTWARE` · `SERVICE`

## Architecture

```text
                    SOURCES
                       │
                       ▼
                  COLLECTOR          (Step 1)
                       │
                       ▼
                  NORMALIZER         (Step 2)
                       │
                       ▼
                    DEDUP            (Step 3)
                       │
                       ▼
                 POSTGRESQL
                       │
                       ▼
              HYBRID MATCHER         (Step 4)
             /      │       \
       HARD RULES  SCORE   SEMANTIC
             \      │       /
                    ▼
                MatchResult
                    │
                    ▼
             MATCH REPOSITORY        (Step 5/6)
                    │
                    ▼
              THRESHOLD GATE         (decision = ALERT)
                    │
                    ▼
            PENDING ALERTS
                    │
                    ▼
             TELEGRAM WORKER         (Step 9)
                    │
                    ▼
           NOTIFICATION DELIVERY
                    │
              ┌─────┴─────┐
              ▼           ▼
             SENT       FAILED
                          │
                          ▼
                        RETRY
```

### Architecture boundaries

| Layer | May depend on | Must NOT contain |
|---|---|---|
| Telegram (worker/formatter/adapter) | Core domain, `MatchRepository` (read), `NotificationRepository`, config | matching, scoring, LLM calls, dedup, benchmark |
| `MatchRepository` | PostgreSQL, domain | Telegram calls, notification formatting |
| Matcher | Core domain, LLM interface | Telegram, persistence |
| Replay/benchmark | Stored `input_snapshot` only | LLM calls, current mutable state |

Boundary tests in `tests/Teos.OpportunityNetwork.Tests/BoundaryTests.cs` enforce these rules.

## Steps 1–8

- **Step 1 — Collector.** `GitHubBountyCollector` queries `https://api.github.com/search/issues` (not the web site) with `Accept: application/vnd.github+json` and `Authorization: Bearer …` when `GITHUB_TOKEN` is set. Credentials are never hard-coded.
- **Step 2 — Normalizer.** Deterministic normalization of title, description, URL, company, skills, type, dates (UTC-aware), budgets, and currencies. Produces `content_hash` (SHA-256, 64 hex chars).
- **Step 3 — Deduplication.** Exact boundary is `UNIQUE(source, external_id)`. `content_hash` is a cross-source candidate *signal* only — provenance is never destroyed; `opportunity_sources` supports multi-source provenance.
- **Step 4 — Hybrid matching engine.** `Hard rules + deterministic scoring + LLM semantic evaluation → final score`. LLM-only matching is impossible: semantic is capped at its configured weight. Output validation: `LLM → JSON → schema validation → scoring`; invalid output fails safely. Score thresholds: `≥85 ALERT, ≥60 LOG, <60 IGNORE`. Action thresholds: `≥90 APPLY_NOW, ≥75 REVIEW, <75 PASS`. Invariants (`score ≥ 90` ⇒ never `IGNORE`, never `PASS`) are enforced in code **and** by PostgreSQL `CHECK` constraints.
- **Step 5/6 — Match persistence & MatchRepository.** `Save`, `AlreadyAlerted`, `PendingAlerts`, `HistoryForBenchmark`, `ScoreDistribution`, `SaveNewVersion`. Idempotency boundary: `(opportunity_id, user_id, weights_version, engine_version)` — re-saving never overwrites historical evidence.
- **Step 8 — Replay/benchmark.** `ReplayEngine.RescoreFromSnapshot` re-scores the immutable `input_snapshot` only; the stored semantic score is reused verbatim and the LLM is never called. Metrics: mean, stddev, ALERT rate, APPLY_NOW rate, P50, P90. `DecisionTransitions.Compute` reports `IGNORE→ALERT`, `ALERT→IGNORE`, etc. The benchmark provides *evidence*; choosing production configuration remains a human decision.

### Scoring configuration

`HYBRID_V1` (`weights_version = hybrid-v1`, `engine_version = matcher-v1`) — historical baseline:

```json
{"skills":0.35,"experience":0.20,"budget":0.15,"type":0.10,"location":0.10,"urgency":0.05,"semantic":0.05}
```

`HYBRID_V2` (`weights_version = hybrid-v2`) — experimental only, does not replace V1:

```json
{"skills":0.30,"experience":0.20,"budget":0.15,"type":0.10,"location":0.10,"urgency":0.05,"semantic":0.10}
```

Weights are configuration-driven, validated (must total `1.0`), and versioned. `confidence` is metadata and never modifies `match_score`. Unknown budgets are valid and scored neutrally — never penalized.

## Step 9 — Telegram notification layer

Telegram is a notification/UI layer only.

- **`notification_deliveries`** table: `id, match_id, user_id, channel, status, attempt_count, provider_message_id, last_error, next_attempt_at, created_at, sent_at, updated_at`. Statuses: `PENDING`, `SENDING`, `SENT`, `FAILED`. Match state and delivery state are separate tables — a successful `MatchResult` never implies delivery.
- **`NotificationRepository`**: create delivery, claim for send (`FOR UPDATE SKIP LOCKED`), mark sent, mark failed, retrieve retryable failures, reclaim stale `SENDING` rows.
- **`TelegramWorker`**: `pending_alerts → claim → format → send → mark_sent/mark_failed`. Reads persisted data only; never calculates scores, invokes the matcher, invokes the LLM, or deduplicates.
- **`TelegramFormatter`**: deterministic, pure, HTML-escaped, truncated below Telegram's 4096-char limit. Uses the stored score verbatim.
- **`TelegramBotClient`**: transport-only adapter. Reads `TELEGRAM_BOT_TOKEN` from the environment; missing token fails fast with a secret-free error. 429 honors `retry_after`; timeouts/5xx retry; 4xx are permanent. All error text passes a token redaction filter.
- **Delivery guarantee**: at most one `SENT` row per `(match_id, user_id, channel)` enforced by a partial unique index plus an `already_alerted` double-check. Exactly-once cannot be guaranteed across a crash between provider accept and `mark_sent`; application-level idempotency is what is guaranteed.

Bot commands (only what the profile model supports): `/start`, `/pause`, `/resume`.

## Database setup

PostgreSQL is authoritative (UUID, JSONB, TIMESTAMPTZ, partial unique indexes). No SQLite or SQL Server fallback exists.

```bash
# local development
docker run -d --name teos-pg -p 54329:5432 \
  -e POSTGRES_USER=teos -e POSTGRES_PASSWORD=teos_dev -e POSTGRES_DB=teos \
  postgres:16

# apply schema
psql "$DATABASE_URL" -f migrations/0001_init.sql
# roll back
psql "$DATABASE_URL" -f migrations/0001_init.down.sql
```

Immutable snapshot integrity is enforced by a trigger: `input_snapshots` is append-only.

## Environment variables

See [.env.example](.env.example). Never commit real values.

| Variable | Purpose |
|---|---|
| `TELEGRAM_BOT_TOKEN` | Bot token from @BotFather (required for real sends) |
| `TELEGRAM_CHAT_ID` | Fallback chat id for single-chat setups |
| `GITHUB_TOKEN` | Optional; Bearer auth for the GitHub search API |
| `OPENAI_API_KEY` | Optional; semantic evaluation |
| `DATABASE_URL` / `TEOS_TEST_DB` | Application / test database connection strings |

## Test commands

```bash
# start the test database container
docker run -d --name teos-test-pg -p 54329:5432 \
  -e POSTGRES_USER=teos -e POSTGRES_PASSWORD=teos_test -e POSTGRES_DB=postgres \
  postgres:16

# run all tests (unit + real-PostgreSQL integration)
dotnet test
```

Tests use a real PostgreSQL database (never SQLite). `TEOS_TEST_DB` overrides the default connection string. No test opens a network connection or requires a real token.

## Run commands

```bash
dotnet build Teos.OpportunityNetwork.sln
dotnet test
```
