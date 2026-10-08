-- =============================================================================
-- EwpNotificationsDb - Notifications bounded context
-- =============================================================================
-- Run while connected to EwpNotificationsDb (it drops and recreates the tables):
--   psql -h localhost -U postgres -d EwpNotificationsDb -v ON_ERROR_STOP=1 -f EwpNotificationsDb.sql
-- Holds what the workflow told whom (from Kafka events) and who has read it.
-- =============================================================================

DROP TABLE IF EXISTS notification_reads CASCADE;
DROP TABLE IF EXISTS notifications CASCADE;
DROP TABLE IF EXISTS inbox_messages CASCADE;

-- One notification for one audience: a person ("user:{sub}") or the staff of one role
-- in one branch ("staff:{role}:{branch}"). Immutable; read state is per person.
CREATE TABLE notifications (
    id BIGSERIAL PRIMARY KEY,
    audience VARCHAR(200) NOT NULL,
    category VARCHAR(20) NOT NULL,
    title VARCHAR(200) NOT NULL,
    body VARCHAR(1000) NOT NULL,

    -- The record it is about, e.g. {"mfe":"kyc","page":"cases/view-details","recordId":3}.
    -- Stored now; the Shell opens it in a later increment (deep links).
    target JSONB NULL,

    -- The workflow event it came from. One event tells each audience at most once.
    source_message_id UUID NOT NULL,
    source_event_type VARCHAR(100) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,

    CONSTRAINT ck_notifications_category CHECK (category IN ('PROGRESS', 'NEW_WORK')),
    CONSTRAINT ck_notifications_audience CHECK (audience LIKE 'user:%' OR audience LIKE 'staff:%'),
    CONSTRAINT uq_notifications_source_audience UNIQUE (source_message_id, audience)
);

-- The bell: newest first, per audience.
CREATE INDEX ix_notifications_audience_id ON notifications (audience, id DESC);

-- Read state per person (a branch-wide notification is read by each officer separately).
CREATE TABLE notification_reads (
    notification_id BIGINT NOT NULL REFERENCES notifications (id) ON DELETE CASCADE,
    user_id VARCHAR(200) NOT NULL,
    read_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_notification_reads PRIMARY KEY (notification_id, user_id)
);

CREATE INDEX ix_notification_reads_user_id ON notification_reads (user_id);

-- Inbox (idempotent consumer): a workflow event becomes notifications exactly once.
CREATE TABLE inbox_messages (
    id UUID NOT NULL,
    message_id UUID NOT NULL,
    consumer VARCHAR(200) NOT NULL,
    processed_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT pk_notifications_inbox_messages PRIMARY KEY (id),
    CONSTRAINT uq_notifications_inbox_messages_message_consumer UNIQUE (message_id, consumer)
);

-- The service's own least-privilege user (created by db/EwpServiceDbUsers.sql).
DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_notifications_api') THEN
        GRANT SELECT, INSERT, UPDATE, DELETE ON notifications, notification_reads, inbox_messages TO ewp_notifications_api;
        GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA public TO ewp_notifications_api;
    ELSE
        RAISE WARNING 'Role ewp_notifications_api does not exist yet: run ps\database\Apply-EwpServiceDbUsers.ps1, then this script again.';
    END IF;
END $$;