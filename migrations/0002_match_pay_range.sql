-- TEOS Opportunity Network — 0002: LLM-estimated pay range on matches.
-- Additive, nullable, idempotent. Existing rows keep NULL (no backfill, no rewrite).
-- The range is produced by the semantic provider and frozen into the input snapshot so replay
-- reproduces it without calling an LLM again.

ALTER TABLE opportunity_matches
    ADD COLUMN IF NOT EXISTS pay_range_min numeric,
    ADD COLUMN IF NOT EXISTS pay_range_max numeric,
    ADD COLUMN IF NOT EXISTS pay_currency  text;

-- Bound sanity, added once; NULL bounds (unknown) always pass.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_constraint
        WHERE conname = 'ck_matches_pay_range'
          AND conrelid = 'opportunity_matches'::regclass) THEN
        ALTER TABLE opportunity_matches
            ADD CONSTRAINT ck_matches_pay_range CHECK (
                pay_range_min IS NULL OR pay_range_max IS NULL OR pay_range_min <= pay_range_max);
    END IF;
END $$;
