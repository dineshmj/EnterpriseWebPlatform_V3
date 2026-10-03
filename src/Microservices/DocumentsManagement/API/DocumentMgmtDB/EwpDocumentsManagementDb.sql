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

    CONSTRAINT pk_documents
        PRIMARY KEY (id),

    CONSTRAINT ck_documents_size
        CHECK (size >= 0),

    CONSTRAINT ck_documents_version
        CHECK (version > 0)
);

CREATE INDEX ix_documents_content_hash
    ON documents (content_hash);

CREATE INDEX ix_documents_resource_branch_business_reference_document_type
    ON documents (resource_branch, business_reference, document_type);


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