# TEOS Opportunity Network

**by Elmahrosa**

[![TEOS Sovereign Repo Valuation](https://img.shields.io/badge/Action-TEOS%20Sovereign%20Repo%20Valuation-blue?logo=github)](https://github.com/Elmahrosa/teos-repo-valuation)

A deterministic opportunity scanner (`--once` / `--loop`) with a GitHub repo valuation action. Scores 0–100 with fixed thresholds (≥85 ALERT, ≥90 APPLY_NOW) and alerts via Telegram only when the bar is cleared. Matching is deterministic; an optional LLM semantic signal is capped at its configured weight and falls back to neutral-50 without an API key. Not a traditional job board.

```text
Discover opportunities
→ Normalize
→ Deduplicate
→ Match against user profiles
→ Persist evidence
→ Apply threshold
→ Notify users
```

## Use the GitHub Action

The **TEOS Sovereign Repo Valuation** action now lives in its own public repository:
[`Elmahrosa/teos-repo-valuation`](https://github.com/Elmahrosa/teos-repo-valuation). It scores a
repository **0–100** and estimates a value range. Deterministic; no LLM.

```yaml
# .github/workflows/valuations.yml
- uses: Elmahrosa/teos-repo-valuation@v1.2.1
  with:
    github-token: ${{ github.token }}
```

| Output | Description |
|---|---|
| `score` | TEOS Score 0–100 |
| `valuation` | Estimated range |
| `decision` | ALERT / LOG / IGNORE |

The rest of this README documents the opportunity-network runtime (scanner + Telegram bot).

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

## Semantic scoring with free LLMs (multi-agent ensemble)

The optional semantic signal can run on **several free OpenAI-compatible models at once** ("all free LLMs"), scored as a deterministic ensemble — no paid OpenAI key required.

- **One adapter, many providers**: `ChatCompletionsSemanticEvaluator` talks to any `/chat/completions` endpoint. Free-tier defaults ship for `groq`, `openrouter`, `mistral`, `gemini`, `github` (GitHub Models) — all via `TEOS_LLM_PROVIDERS=groq,openrouter…` plus `{NAME}_API_KEY`. `ollama` is keyless and runs locally.
- **Multi-agent fusion**: `EnsembleSemanticEvaluator` runs every configured provider in parallel, schema-validates each raw output, and fuses the valid judgments with a **deterministic median** (even counts use the mean of the two middle values). Provider order is normalized — identical inputs always give identical output. A canonical JSON carries the fused `relevance`, a per-provider rationale, and the union of `matched_keywords`.
- **Resilient**: a failing provider is skipped, never fails the pipeline. If *every* provider fails, the documented **neutral-50** signal is returned with an explicit `all providers failed` rationale. No key configured at all → the matcher's neutral-50 fallback, unchanged.
- **Replay stays honest**: the fused value is stored in the immutable `input_snapshot`; replay/benchmark reuses it verbatim and never re-calls any LLM (see `ForbiddenSemanticEvaluator`).
- **Legacy**: a lone `OPENAI_API_KEY` still configures single-provider OpenAI (`single:openai`).

```bash
# .env — run Groq + OpenRouter + a local Ollama as three scoring agents
TEOS_LLM_PROVIDERS=groq,openrouter,ollama
GROQ_API_KEY=gsk_...
OPENROUTER_API_KEY=sk-or-...
OLLAMA_BASE_URL=http://localhost:11434/v1
```

## Step 9 — Telegram notification layer

Telegram is a notification/UI layer only.

- **`notification_deliveries`** table: `id, match_id, user_id, channel, status, attempt_count, provider_message_id, last_error, next_attempt_at, created_at, sent_at, updated_at`. Statuses: `PENDING`, `SENDING`, `SENT`, `FAILED`. Match state and delivery state are separate tables — a successful `MatchResult` never implies delivery.
- **`NotificationRepository`**: create delivery, claim for send (`FOR UPDATE SKIP LOCKED`), mark sent, mark failed, retrieve retryable failures, reclaim stale `SENDING` rows.
- **`TelegramWorker`**: `pending_alerts → claim → format → send → mark_sent/mark_failed`. Reads persisted data only; never calculates scores, invokes the matcher, invokes the LLM, or deduplicates.
- **`TelegramFormatter`**: deterministic, pure, HTML-escaped, truncated below Telegram's 4096-char limit. Uses the stored score verbatim.
- **`TelegramBotClient`**: transport-only adapter. Reads `TELEGRAM_BOT_TOKEN` from the environment; missing token fails fast with a secret-free error. 429 honors `retry_after`; timeouts/5xx retry; 4xx are permanent. All error text passes a token redaction filter.
- **Delivery guarantee**: at most one `SENT` row per `(match_id, user_id, channel)` enforced by a partial unique index plus an `already_alerted` double-check. Exactly-once cannot be guaranteed across a crash between provider accept and `mark_sent`; application-level idempotency is what is guaranteed.

Bot commands (only what the profile model supports — implemented and tested): `/start`, `/pause`, `/resume`, `/help`, `/status`. `/status` reports the linked profile, notification state, and how many alerts were actually delivered (SENT) in the last 7 days.

## Non-stop scheduled scanning (free)

[`.github/workflows/scanner.yml`](.github/workflows/scanner.yml) runs the full pipeline **hourly** on GitHub-hosted runners — free for public repositories — plus on-demand via `workflow_dispatch`. (If the repository is private, scheduled runs count against the 2,000 free private Actions minutes/month; hourly is chosen to fit that budget at ~2 min/run. To restore 30-minute cadence, set `TEOS_SCAN_INTERVAL` lower **and** budget for paid minutes.)

1. Provision a **persistent** PostgreSQL on a free tier ([Neon](https://neon.tech), [Supabase](https://supabase.com)) and set the repo secret `DATABASE_URL`. Ephemeral databases break dedup/evidence between runs. The workflow runs `--migrate` (idempotent schema bootstrap) before every scan, so a fresh database needs no manual SQL.
2. Add secrets for the free LLM providers you want (`TEOS_LLM_PROVIDERS` + `{NAME}_API_KEY`), `TELEGRAM_BOT_TOKEN` for real alerts, and `TEOS_GITHUB_TOKEN` for collector rate limits.
3. Each scheduled pass behaves exactly like a local `--once`: collect → normalize → dedup → match (ensemble semantic) → persist → notify. A pass failure never blocks the next run.

Scheduled polling is near-continuous, not a literal daemon — for true 24/7 background operation, run `--loop` yourself on a server with Docker (`--loop --interval 600`).

## Monetization roadmap

The project is currently **open and free**; the tiers below are a proposal, not live billing. Nothing is for sale yet.

| Plan | Status | Includes |
|---|---|---|
| **Open** (free) | Available now | Self-hosted `--once` / `--loop` scanner, one profile, GitHub source, neutral semantic scoring, the repo valuation Action |
| **Pro — Hosted alerts** | Planned — not billable | Managed scan loop, LLM semantic scoring on a managed key, multiple profiles, priority Telegram delivery (proposed subscription) |
| **Network — API** | Planned — not billable | Matched-opportunity API feed, custom sources, replay/benchmark reports (proposed data/API tier) |

Revenue path: the free Open tier builds adoption (GitHub Marketplace Action + scanner); the paid tiers monetize the parts that actually cost money to operate — hosted scanning, managed semantic scoring, multi-profile coordination, and API/data access.

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
| `TELEGRAM_BOT_TOKEN` | Bot token from @BotFather (required for real sends; runtime dry-runs without it) |
| `TELEGRAM_CHAT_ID` | Fallback chat id for single-chat setups |
| `GITHUB_TOKEN` | Optional; Bearer auth for the GitHub search API |
| `OPENAI_API_KEY` | Optional; legacy single-provider semantic scoring |
| `TEOS_LLM_PROVIDERS` | Optional; comma-separated free providers for ensemble semantic scoring (`groq`, `openrouter`, `mistral`, `gemini`, `github`, `ollama`) |
| `{NAME}_BASE_URL` / `{NAME}_API_KEY` / `{NAME}_MODEL` | Per-provider OpenAI-compatible config; sane free-tier defaults exist for known providers; key optional for keyless local (Ollama) |
| `DATABASE_URL` / `TEOS_TEST_DB` | Application / test database connection strings |
| `TEOS_LOOP_SECONDS` | `--loop` interval in seconds (default 300) |
| `TEOS_CONFIDENCE` | Confidence stored with each match result (default 0.9) |

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

## Step 10 — notification runtime (`--once` / `--loop`)

The Runner now has a real runtime entry point that executes the full pipeline:

```text
Collect (GitHub search) → Normalize → Deduplicate → Match (HYBRID_V1) → Persist evidence → Notify (Telegram)
```

| Mode | Behavior |
|---|---|
| `--valuation` | Repo valuation CLI (existing behavior; `--repo-url`, `--output-json`, `--tier`, `--smoke`). Legacy alias `smoke` still works. |
| `--once` | One full pipeline pass, then exit. Prints an ingested/matched/delivered report. |
| `--loop [--interval SECS]` | Runs the pipeline on an interval (default `TEOS_LOOP_SECONDS`, 300 s) until Ctrl+C. |
| `--migrate` | Idempotently applies `migrations/0001_init.sql` to `DATABASE_URL` (safe on every run). |
| `--help` | Usage. Unknown flags exit 1 with help. |

```bash
# one pass (needs DATABASE_URL; dry-runs Telegram without TELEGRAM_BOT_TOKEN)
docker run -d --name teos-pg -p 54329:5432 \
  -e POSTGRES_USER=teos -e POSTGRES_PASSWORD=teos_dev -e POSTGRES_DB=teos \
  postgres:16

dotnet run --project src/Teos.OpportunityNetwork.Runner -- --migrate   # apply schema (idempotent)
dotnet run --project src/Teos.OpportunityNetwork.Runner -- --once
dotnet run --project src/Teos.OpportunityNetwork.Runner -- --loop --interval 600
```

**Secrets safety (public repo):** the runner redacts `DATABASE_URL`, the Telegram token, and every
`*_API_KEY` from logs and error messages before they reach stdout/stderr (`SecretRedactor`, incl.
Npgsql `Password=` segments). The scheduled scanner's first step checks that required secrets are
present and prints **set / not set only** — a value is never echoed into world-readable Actions logs.

Live Telegram smoke (first real bot message):

1. Create a bot with @BotFather and put its token in `TELEGRAM_BOT_TOKEN` and your chat id in `TELEGRAM_CHAT_ID`.
2. Seed one user profile and link the chat:
   ```sql
   INSERT INTO user_profiles (id, display_name, skills, years_experience, preferred_types)
   VALUES (gen_random_uuid(), 'You', '{csharp}', 5, '{BOUNTY}');
   INSERT INTO telegram_chats (user_id, chat_id)
   SELECT id, '<your-chat-id>' FROM user_profiles LIMIT 1;
   ```
3. `dotnet run --project src/Teos.OpportunityNetwork.Runner -- --once`
4. When a collected opportunity scores ≥ 85 (ALERT) you receive the Telegram notification. Without a token, the same pass runs end-to-end in dry-run and logs what it would have sent.
