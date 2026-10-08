-- TEOS Opportunity Network — initial schema (PostgreSQL is the authoritative store).
-- Step 9 scope: opportunities, user_profiles, immutable input_snapshots, opportunity_matches,
-- notification_deliveries (with idempotent delivery state), telegram_chats.
--
-- Decision/action threshold invariants are enforced here as CHECK constraints so an invalid
-- row can never be persisted, regardless of caller:
--   score >= 85 -> ALERT   |  >= 60 -> LOG        |  < 60 -> IGNORE
--   score >= 90 -> APPLY_NOW | >= 75 -> REVIEW     |  < 75 -> PASS
--   score >= 90 can NEVER be IGNORE or PASS.

CREATE TABLE IF NOT EXISTS opportunities (
    id               uuid PRIMARY KEY,
    source           text NOT NULL,
    external_id      text NOT NULL,
    title            text NOT NULL,
    company          text,
    url              text,
    description      text,
    location         text,
    budget_min       numeric,
    budget_max       numeric,
    currency         text,
    budget_type      text NOT NULL DEFAULT 'unknown',
    opportunity_type text NOT NULL,
    skills_required  text[] NOT NULL DEFAULT '{}',
    posted_at        timestamptz,
    deadline         timestamptz,
    content_hash     text NOT NULL,
    first_seen_at    timestamptz NOT NULL,
    last_seen_at     timestamptz NOT NULL,
    status           text NOT NULL DEFAULT 'OPEN',
    CONSTRAINT uq_opportunities_source_external UNIQUE (source, external_id),
    CONSTRAINT ck_opportunities_content_hash CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_opportunities_budget CHECK (
        budget_min IS NULL OR budget_max IS NULL OR budget_min <= budget_max)
);

CREATE INDEX IF NOT EXISTS ix_opportunities_content_hash ON opportunities (content_hash);

CREATE TABLE IF NOT EXISTS user_profiles (
    id                   uuid PRIMARY KEY,
    display_name         text NOT NULL,
    skills               text[] NOT NULL DEFAULT '{}',
    years_experience     integer NOT NULL DEFAULT 0,
    location             text,
    preferred_types      text[] NOT NULL DEFAULT '{}',
    budget_min           numeric,
    budget_max           numeric,
    currency             text,
    keywords             text[] NOT NULL DEFAULT '{}',
    notifications_paused boolean NOT NULL DEFAULT FALSE,
    created_at           timestamptz NOT NULL DEFAULT now(),
    updated_at           timestamptz NOT NULL DEFAULT now()
);

-- Append-only evidence store. Both UPDATE and DELETE raise, so historical scoring inputs
-- can never be rewritten. Replay reads payload (jsonb) and re-uses the stored semantic score.
CREATE TABLE IF NOT EXISTS input_snapshots (
    snapshot_source_id uuid PRIMARY KEY,
    opportunity_id     uuid NOT NULL,
    user_id            uuid NOT NULL,
    payload            jsonb NOT NULL,
    weights_version    text NOT NULL,
    created_at         timestamptz NOT NULL DEFAULT now()
);

CREATE OR REPLACE FUNCTION input_snapshots_append_only() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'input_snapshots is append-only: UPDATE/DELETE is not allowed (immutable evidence)';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_input_snapshots_append_only ON input_snapshots;
CREATE TRIGGER trg_input_snapshots_append_only
    BEFORE UPDATE OR DELETE ON input_snapshots
    FOR EACH ROW EXECUTE FUNCTION input_snapshots_append_only();

CREATE TABLE IF NOT EXISTS opportunity_matches (
    id                 uuid PRIMARY KEY,
    opportunity_id     uuid NOT NULL REFERENCES opportunities (id),
    user_id            uuid NOT NULL REFERENCES user_profiles (id),
    match_score        double precision NOT NULL,
    decision           text NOT NULL,
    recommended_action text NOT NULL,
    skill_match        double precision NOT NULL DEFAULT 0,
    experience_match   double precision NOT NULL DEFAULT 0,
    budget_match       double precision NOT NULL DEFAULT 0,
    location_match     double precision NOT NULL DEFAULT 0,
    urgency_score      double precision NOT NULL DEFAULT 0,
    semantic_score     double precision NOT NULL DEFAULT 0,
    confidence         double precision NOT NULL DEFAULT 1,
    matched_skills     text[] NOT NULL DEFAULT '{}',
    missing_skills     text[] NOT NULL DEFAULT '{}',
    reasons            text NOT NULL DEFAULT '[]',
    warnings           text NOT NULL DEFAULT '[]',
    scoring_config     text NOT NULL,
    weights_version    text NOT NULL,
    engine_version     text NOT NULL,
    snapshot_source_id uuid NOT NULL REFERENCES input_snapshots (snapshot_source_id),
    created_at         timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT uq_matches_version UNIQUE (opportunity_id, user_id, weights_version, engine_version),
    CONSTRAINT ck_matches_decisions CHECK (decision IN ('ALERT', 'LOG', 'IGNORE')),
    CONSTRAINT ck_matches_actions CHECK (recommended_action IN ('APPLY_NOW', 'REVIEW', 'PASS')),
    CONSTRAINT ck_matches_decision_band CHECK (
        (decision = 'ALERT' AND match_score >= 85) OR
        (decision = 'LOG' AND match_score >= 60 AND match_score < 85) OR
        (decision = 'IGNORE' AND match_score < 60)),
    CONSTRAINT ck_matches_action_band CHECK (
        (recommended_action = 'APPLY_NOW' AND match_score >= 90) OR
        (recommended_action = 'REVIEW' AND match_score >= 75 AND match_score < 90) OR
        (recommended_action = 'PASS' AND match_score < 75)),
    CONSTRAINT ck_matches_no_ignore_above_90 CHECK (NOT (match_score >= 90 AND decision = 'IGNORE')),
    CONSTRAINT ck_matches_no_pass_above_90 CHECK (NOT (match_score >= 90 AND recommended_action = 'PASS'))
);

CREATE INDEX IF NOT EXISTS ix_matches_pending ON opportunity_matches (decision, created_at);

-- Delivery state lives ONLY here (never in opportunity_matches).
-- statuses: PENDING -> SENDING -> SENT | FAILED (FAILED rows are reused for retry).
CREATE TABLE IF NOT EXISTS notification_deliveries (
    id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    match_id            uuid NOT NULL,
    user_id             uuid NOT NULL,
    channel             text NOT NULL DEFAULT 'telegram',
    status              text NOT NULL DEFAULT 'PENDING',
    attempt_count       integer NOT NULL DEFAULT 0,
    provider_message_id text,
    last_error          text,
    next_attempt_at     timestamptz,
    created_at          timestamptz NOT NULL DEFAULT now(),
    sent_at             timestamptz,
    updated_at          timestamptz NOT NULL DEFAULT now(),
    CONSTRAINT ck_nd_status CHECK (status IN ('PENDING', 'SENDING', 'SENT', 'FAILED')),
    CONSTRAINT ck_nd_attempts CHECK (attempt_count >= 0)
);

-- At most ONE SENT delivery per (match, user, channel): application-level idempotency.
CREATE UNIQUE INDEX IF NOT EXISTS uq_nd_one_sent_per_match_user_channel
    ON notification_deliveries (match_id, user_id, channel)
    WHERE status = 'SENT';

CREATE INDEX IF NOT EXISTS ix_nd_claimable ON notification_deliveries (channel, status, created_at);

CREATE TABLE IF NOT EXISTS telegram_chats (
    user_id    uuid PRIMARY KEY,
    chat_id    text NOT NULL,
    paused     boolean NOT NULL DEFAULT FALSE,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now()
);
