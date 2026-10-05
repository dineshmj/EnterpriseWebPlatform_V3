-- ============================================================================
-- EnterpriseWebPlatform V3
-- Documents Management Database
--
-- Database: EwpDocumentsManagementDb
--
-- Bounded Context:
--     Documents Management
--
-- Ownership:
--     Documents Management Microservice
--
-- IMPORTANT:
--     This database must not be accessed directly by other microservices.
--     Cross-service communication will happen through APIs and Kafka events.
--
--     Document BLOB content is deliberately NOT stored in PostgreSQL.
--     The database stores document metadata and an opaque storage reference.
-- ============================================================================

-- ============================================================================
-- Connect to EwpDocumentsManagementDb before executing the remaining statements.
-- ============================================================================
--
-- In pgAdmin:
--     Select EwpDocumentsManagementDb and open Query Tool.
--
-- In psql:
--     \c EwpDocumentsManagementDb
--
-- ============================================================================

-- ============================================================================
-- DOCUMENTS
-- ============================================================================
--
-- Aggregate Root:
--     Document
--
-- This bounded context deliberately has no foreign keys to Customer,
-- OnboardingApplication, KycCase, Account, Payment, or any other entity
-- owned by another bounded context.
--
-- ============================================================================

-- ============================================================================
-- DROP EXISTING OBJECTS (CLEANUP)
-- ============================================================================

DROP TABLE IF EXISTS inbox_messages CASCADE;
DROP TABLE IF EXISTS documents CASCADE;

-- ============================================================================

CREATE TABLE documents
(
    id                  UUID          NOT NULL,

    file_name           VARCHAR(255)  NOT NULL,
    content_type        VARCHAR(255)  NOT NULL,
    size                BIGINT        NOT NULL,
    content_hash        VARCHAR(64)   NOT NULL,

    storage_reference   VARCHAR(1024) NOT NULL,
    document_type       VARCHAR(100)  NULL,
    business_reference  VARCHAR(255)  NULL,

    -- Branch scope of the uploading actor; used for object-level
    -- authorization. NULL (legacy rows) is accessible to nobody.
    resource_branch     VARCHAR(20)   NULL,

    created_at          TIMESTAMPTZ   NOT NULL,
    updated_at          TIMESTAMPTZ   NOT NULL,

    version             BIGINT        NOT NULL DEFAULT 1,

    -- Lifecycle: AVAILABLE; ATTACHED once submitted as evidence of an onboarding
    -- application; INVALIDATED by business compensation (e.g. the application was
    -- rejected). Attached and invalidated documents are retained (never deleted).
    status              VARCHAR(20)   NOT NULL DEFAULT 'AVAILABLE',
    invalidated_at      TIMESTAMPTZ   NULL,
    invalidation_reason VARCHAR(500)  NULL,
    attached_at         TIMESTAMPTZ   NULL,
    attached_to         VARCHAR(100)  NULL,

    CONSTRAINT pk_documents
        PRIMARY KEY (id),

    CONSTRAINT ck_documents_size
        CHECK (size >= 0),

    CONSTRAINT ck_documents_version
        CHECK (version > 0),

    CONSTRAINT ck_documents_status
        CHECK (status IN ('AVAILABLE', 'ATTACHED', 'INVALIDATED')),

    -- Invalidation metadata exists exactly when the document is invalidated.
    CONSTRAINT ck_documents_invalidation
        CHECK ((status = 'INVALIDATED') = (invalidated_at IS NOT NULL AND invalidation_reason IS NOT NULL)),

    -- An attached document records what it is evidence of (kept after invalidation).
    CONSTRAINT ck_documents_attachment
        CHECK ((attached_at IS NULL) = (attached_to IS NULL) AND (status <> 'ATTACHED' OR attached_at IS NOT NULL))
);

CREATE INDEX ix_documents_content_hash
    ON documents (content_hash);

CREATE INDEX ix_documents_resource_branch_business_reference_document_type
    ON documents (resource_branch, business_reference, document_type);


-- ============================================================================
-- INBOX MESSAGES (idempotent consumer)
-- ============================================================================
--
-- One row per Kafka message processed by a consumer (the Document Invalidation
-- Subscriber, through the internal API), written in the same transaction as
-- the change it caused. A redelivered message is recognised and changes nothing.
--
-- ============================================================================

CREATE TABLE inbox_messages
(
    id                  UUID          NOT NULL,
    message_id          UUID          NOT NULL,
    consumer            VARCHAR(200)  NOT NULL,
    received_at         TIMESTAMPTZ   NOT NULL,
    processed_at        TIMESTAMPTZ   NULL,

    CONSTRAINT pk_dm_inbox_messages
        PRIMARY KEY (id),

    CONSTRAINT uq_dm_inbox_messages_message_consumer
        UNIQUE (message_id, consumer)
);


-- ============================================================================
-- DESIGN NOTE
-- ============================================================================
--
-- storage_reference is intentionally opaque to the database. The configured
-- IDocumentStorage implementation interprets it. The initial implementation
-- uses the local file system; a future implementation can use S3, Documentum,
-- or another enterprise document store without changing the business model.
--
-- ============================================================================