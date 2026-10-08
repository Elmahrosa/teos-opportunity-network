-- Reverse of 0001_init.sql. Dropped in dependency order.

DROP TABLE IF EXISTS notification_deliveries;
DROP TABLE IF EXISTS opportunity_matches;
DROP TRIGGER IF EXISTS trg_input_snapshots_append_only ON input_snapshots;
DROP FUNCTION IF EXISTS input_snapshots_append_only();
DROP TABLE IF EXISTS input_snapshots;
DROP TABLE IF EXISTS telegram_chats;
DROP TABLE IF EXISTS user_profiles;
DROP TABLE IF EXISTS opportunities;
