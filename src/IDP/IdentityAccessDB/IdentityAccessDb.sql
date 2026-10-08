-- Test message

-- Enterprise Web Platform V3 - IdentityAccessDb_Australian_Demo.sql
-- PostgreSQL schema and demonstration seed data.
-- This version is intentionally aligned with IdentityDbContext.cs.
-- Indexes are explicitly named so PostgreSQL and EF Core have the same model.
-- PoC/demo data only.

BEGIN;

-- =========================================================
-- RESET: reverse dependency order
-- =========================================================

DROP TABLE IF EXISTS user_mfa_recovery_codes;
DROP TABLE IF EXISTS user_mfa;
DROP TABLE IF EXISTS user_relationships;
DROP TABLE IF EXISTS role_permissions;
DROP TABLE IF EXISTS user_roles;
DROP TABLE IF EXISTS user_employment_profiles;
DROP TABLE IF EXISTS permissions;
DROP TABLE IF EXISTS roles;
DROP TABLE IF EXISTS departments;
DROP TABLE IF EXISTS branches;
DROP TABLE IF EXISTS users;

-- =========================================================
-- 1. USERS
-- =========================================================

CREATE TABLE users (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    subject_id UUID NOT NULL,
    first_name VARCHAR(100) NOT NULL,
    last_name VARCHAR(100) NOT NULL,
    email VARCHAR(200) NOT NULL,
    user_name VARCHAR(100) NOT NULL,
    hashed_password VARCHAR(500) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    -- Account lockout: consecutive failed sign-ins and the end of a lockout.
    access_failed_count INT NOT NULL DEFAULT 0,
    lockout_end TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- =========================================================
-- 2. ROLES
-- =========================================================

CREATE TABLE roles (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    code VARCHAR(100) NOT NULL,
    description VARCHAR(500),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- =========================================================
-- 3. PERMISSIONS
-- =========================================================

CREATE TABLE permissions (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    name VARCHAR(150) NOT NULL,
    code VARCHAR(150) NOT NULL,
    description VARCHAR(500),
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

-- =========================================================
-- 4. BRANCHES
-- =========================================================

CREATE TABLE branches (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code VARCHAR(20) NOT NULL,
    name VARCHAR(150) NOT NULL,
    region VARCHAR(100),
    city VARCHAR(100),
    country_code CHAR(2),
    is_active BOOLEAN NOT NULL DEFAULT TRUE
);

-- =========================================================
-- 5. DEPARTMENTS
-- =========================================================

CREATE TABLE departments (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    code VARCHAR(50) NOT NULL,
    name VARCHAR(150) NOT NULL,
    is_active BOOLEAN NOT NULL DEFAULT TRUE
);

-- =========================================================
-- 6. USER EMPLOYMENT / ABAC PROFILE
-- =========================================================

CREATE TABLE user_employment_profiles (
    user_id BIGINT PRIMARY KEY,
    employee_id VARCHAR(50) NOT NULL,
    -- The staff member's LAN ID ("filas": 2 letters of the first name + 3 of the last,
    -- lowercase; a digit is appended on a collision). Issued as the "lan_id" claim and
    -- shown on screens; records keep the subject ID as the identity. In production the
    -- LAN ID is the directory sign-in name; the demo signs in with role-named user names.
    lan_id VARCHAR(20) NOT NULL UNIQUE,
    department_id BIGINT NOT NULL REFERENCES departments(id),
    branch_id BIGINT NOT NULL REFERENCES branches(id),
    employment_type VARCHAR(50) NOT NULL,
    clearance_level INTEGER NOT NULL DEFAULT 1
        CHECK (clearance_level BETWEEN 1 AND 5),
    manager_user_id BIGINT NULL REFERENCES users(id)
);

-- =========================================================
-- 7. USER <-> ROLE
-- =========================================================

CREATE TABLE user_roles (
    user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    role_id BIGINT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, role_id)
);

-- =========================================================
-- 8. ROLE <-> PERMISSION
-- =========================================================

CREATE TABLE role_permissions (
    role_id BIGINT NOT NULL REFERENCES roles(id) ON DELETE CASCADE,
    permission_id BIGINT NOT NULL REFERENCES permissions(id) ON DELETE CASCADE,
    PRIMARY KEY (role_id, permission_id)
);

-- =========================================================
-- 8b. TWO-STEP SIGN-IN (TOTP - Google Authenticator)
-- =========================================================
-- One authenticator per user. The secret is stored ONLY encrypted with the IDP's Data
-- Protection key ring (schema identity_server), so a database reader cannot generate codes;
-- last_used_time_step stops a code being replayed. Recovery codes: SHA-256 hashes only.
-- Used only while "Mfa:Enabled" is true (IDP appsettings); enrolment happens at sign-in.

CREATE TABLE user_mfa (
    user_id BIGINT PRIMARY KEY REFERENCES users(id) ON DELETE CASCADE,
    secret_protected TEXT NOT NULL,
    enrolled_at TIMESTAMPTZ NOT NULL,
    last_used_time_step BIGINT NOT NULL
);

CREATE TABLE user_mfa_recovery_codes (
    id BIGINT GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    code_hash CHAR(64) NOT NULL,
    used_at TIMESTAMPTZ NULL
);

CREATE INDEX ix_user_mfa_recovery_codes_user_id ON user_mfa_recovery_codes (user_id);

-- =========================================================
-- 9. REBAC RELATIONSHIPS - not stored here
-- =========================================================
-- Relationships are owned by the bounded context that owns the resource
-- (Customer Onboarding: customers.managing_agent_user_id; Customer KYC:
-- kyc_cases.assigned_officer_user_id) and checked there at request time.
-- The former user_relationships table is dropped above for existing databases.

-- =========================================================
-- EXPLICIT INDEXES
-- Names and column sets match IdentityDbContext.cs exactly.
-- =========================================================

CREATE UNIQUE INDEX ux_users_subject_id
    ON users(subject_id);

CREATE UNIQUE INDEX ux_users_email
    ON users(email);

CREATE UNIQUE INDEX ux_users_user_name
    ON users(user_name);

CREATE INDEX ix_users_is_active
    ON users(is_active);

CREATE UNIQUE INDEX ux_roles_name
    ON roles(name);

CREATE UNIQUE INDEX ux_roles_code
    ON roles(code);

CREATE INDEX ix_roles_is_active
    ON roles(is_active);

CREATE UNIQUE INDEX ux_permissions_name
    ON permissions(name);

CREATE UNIQUE INDEX ux_permissions_code
    ON permissions(code);

CREATE INDEX ix_permissions_is_active
    ON permissions(is_active);

CREATE UNIQUE INDEX ux_branches_code
    ON branches(code);

CREATE INDEX ix_branches_is_active
    ON branches(is_active);

CREATE UNIQUE INDEX ux_departments_code
    ON departments(code);

CREATE INDEX ix_departments_is_active
    ON departments(is_active);

CREATE UNIQUE INDEX ux_user_employment_profiles_employee_id
    ON user_employment_profiles(employee_id);

CREATE INDEX ix_user_employment_profiles_department_id
    ON user_employment_profiles(department_id);

CREATE INDEX ix_user_employment_profiles_branch_id
    ON user_employment_profiles(branch_id);

CREATE INDEX ix_user_employment_profiles_manager_user_id
    ON user_employment_profiles(manager_user_id);

CREATE INDEX ix_user_employment_profiles_department_branch
    ON user_employment_profiles(department_id, branch_id);

CREATE INDEX ix_user_roles_role_id
    ON user_roles(role_id);

CREATE INDEX ix_role_permissions_permission_id
    ON role_permissions(permission_id);


-- =========================================================
-- 10. ROLES
-- =========================================================

INSERT INTO roles (name, code, description) VALUES
('Customer','customer','End customer using customer-facing banking services'),
('Customer Service Agent','customer_service_agent','Assists customers with onboarding and customer information'),
('KYC Officer','kyc_officer','Performs identity and document verification'),
('Compliance Officer','compliance_officer','Performs AML, compliance and risk-related decisions'),
('Account Officer','account_officer','Reviews and approves account-opening activities'),
('Payments Officer','payments_officer','Reviews and processes payment instructions'),
('Operations Administrator','operations_administrator','Performs operational monitoring and exception handling'),
('Auditor','auditor','Performs independent read-only audit and investigation activities'),
('Platform Administrator','platform_administrator','Administers the PoC platform and selected technical configuration');

-- =========================================================
-- 11. PERMISSIONS
-- =========================================================

INSERT INTO permissions (name, code, description) VALUES
('Create Customer Onboarding','customer.onboarding.create','Create a customer onboarding application'),
('View Own Customer Onboarding','customer.onboarding.view_own','View the customer''s own onboarding application'),
('Update Own Customer Onboarding','customer.onboarding.update_own','Update the customer''s own onboarding application'),
('Submit Customer Onboarding','customer.onboarding.submit','Submit a customer onboarding application'),
('View Own Account','customer.account.view_own','View an account owned by the customer'),
('Create Customer Payment','customer.payment.create','Create a payment instruction for the customer'),
('View Own Payment','customer.payment.view_own','View a payment owned by the customer'),
('View Customer Onboarding','customer.onboarding.view','View customer onboarding applications'),
('Update Customer Onboarding','customer.onboarding.update','Update customer onboarding applications'),
('Assist Customer Onboarding','customer.onboarding.assist','Assist a customer with onboarding'),
('View Customer Profile','customer.profile.view','View customer profile information'),
('Update Customer Profile','customer.profile.update','Update customer profile information'),
('View KYC Case','kyc.case.view','View KYC cases'),
('Update KYC Case','kyc.case.update','Update KYC case information'),
('Verify Identity','kyc.identity.verify','Perform identity verification'),
('Verify Document','kyc.document.verify','Perform document verification'),
('Request KYC Information','kyc.case.request_information','Request additional information for a KYC case'),
('Approve KYC Case','kyc.case.approve','Approve a KYC case subject to additional authorization rules'),
('Reject KYC Case','kyc.case.reject','Reject a KYC case'),
('Hold KYC Case','kyc.case.hold','Place a KYC case on hold'),
('View Compliance Case','compliance.case.view','View compliance cases'),
('Review Compliance Case','compliance.case.review','Review a compliance case'),
('Review AML','compliance.aml.review','Review AML screening results'),
('Assess Risk','compliance.risk.assess','Perform risk assessment'),
('Request Compliance Information','compliance.case.request_information','Request additional compliance information'),
('Approve Compliance Case','compliance.case.approve','Approve a compliance case subject to additional authorization rules'),
('Reject Compliance Case','compliance.case.reject','Reject a compliance case'),
('Hold Compliance Case','compliance.case.hold','Place a compliance case on hold'),
('Release Compliance Case','compliance.case.release','Release a compliance case from hold'),
('View Account Application','account.application.view','View account applications'),
('Review Account Application','account.application.review','Review account applications'),
('Approve Account Application','account.application.approve','Approve an account application subject to workflow and SoD rules'),
('Reject Account Application','account.application.reject','Reject an account application'),
('Hold Account Application','account.application.hold','Place an account application on hold'),
('View Account Lifecycle','account.lifecycle.view','View account lifecycle information'),
('Initiate Payment','payment.initiate','Capture and send a payment for a customer (assisted channel)'),
('View Payment','payment.view','View payment instructions'),
('Validate Payment','payment.validate','Validate payment instructions'),
('Approve Payment','payment.approve','Approve a payment subject to amount, risk, ABAC and SoD rules'),
('Reject Payment','payment.reject','Reject a payment'),
('Hold Payment','payment.hold','Place a payment on hold'),
('Release Payment','payment.release','Release a payment from hold'),
('Retry Payment','payment.retry','Retry eligible payment processing'),
('View Workflow Status','workflow.status.view','View business workflow status'),
('View Workflow','workflow.view','View operational workflow information'),
('Retry Workflow','workflow.retry','Retry an eligible workflow'),
('Pause Workflow','workflow.pause','Pause an eligible workflow'),
('Resume Workflow','workflow.resume','Resume a paused workflow'),
('Reprocess Workflow','workflow.reprocess','Reprocess an eligible workflow'),
('View Integration Status','integration.status.view','View integration status'),
('View Service Health','service.health.view','View service health information'),
('View Outbox','outbox.view','View Outbox operational information'),
('View Inbox','inbox.view','View Inbox operational information'),
('View Consumer Status','consumer.status.view','View Kafka consumer status'),
('View Audit','audit.view','View audit records'),
('Search Audit','audit.search','Search audit records'),
('View Workflow History','workflow.history.view','View workflow history'),
('View Customer History','customer.history.view','View customer history'),
('View KYC History','kyc.history.view','View KYC history'),
('View Account History','account.history.view','View account history'),
('View Payment History','payment.history.view','View payment history'),
('View Approval History','approval.history.view','View approval history'),
('View Platform Configuration','platform.configuration.view','View platform configuration'),
('Update Platform Configuration','platform.configuration.update','Update platform configuration'),
('View Platform Users','platform.user.view','View platform users'),
('Manage Platform Users','platform.user.manage','Manage platform users'),
('View Platform Roles','platform.role.view','View platform roles'),
('Manage Platform Roles','platform.role.manage','Manage platform roles'),
('View Platform Permissions','platform.permission.view','View platform permissions'),
('View Platform Health','platform.health.view','View platform health');

-- =========================================================
-- 12. BRANCHES
-- =========================================================

-- city / country_code are issued as the branch_city / branch_country_code claims.
-- Customer Onboarding scopes a Customer Service Agent to customers whose primary
-- residential address is in the agent's branch city and country.
INSERT INTO branches (code, name, region, city, country_code) VALUES
-- region = Australian state / territory.
('SYD001','Sydney CBD','NSW','Sydney','AU'),
('SYD002','Sydney North','NSW','Sydney','AU'),
('MEL001','Melbourne Central','VIC','Melbourne','AU'),
('BNE001','Brisbane City','QLD','Brisbane','AU'),
('ADL001','Adelaide City','SA','Adelaide','AU'),
('PER001','Perth City','WA','Perth','AU');

-- =========================================================
-- 13. DEPARTMENTS
-- =========================================================

INSERT INTO departments (code, name) VALUES
('CUSTOMER_SERVICE','Customer Service'),
('KYC','Know Your Customer'),
('COMPLIANCE','Compliance'),
('ACCOUNTS','Accounts'),
('PAYMENTS','Payments'),
('OPERATIONS','Operations'),
('AUDIT','Internal Audit'),
('IT','Information Technology');

-- =========================================================
-- 14. USERS
-- Australian demo identities are used for presentation purposes.
-- =========================================================
-- Demo password convention: <username>@bss
-- IMPORTANT: Do not hand-construct these hashes. PasswordHasher generates a random salt,
-- so hashes for the same password are expected to differ between runs.
-- Each value below was generated by ASP.NET Core Identity PasswordHasher<User> (Identity V3).
-- (grace.compliance: Identity V3 format, verified with PasswordHasher.VerifyHashedPassword.)
--
-- Separation of duties across clearance levels (Compliance):
-- olivia.compliance / olivia.compliance@bss - clearance 4: approves LOW and MEDIUM risk cases
-- grace.compliance  / grace.compliance@bss  - clearance 5 (senior): also approves HIGH risk (sanctions MATCH)
-- KYC work-queue demonstration users:
-- ethan.kyc / ethan.kyc@bss
-- noah.kyc  / noah.kyc@bss
-- Both are KYC officers in SYD001 with clearance level 3. They are intentionally
-- not assigned to a specific KYC case so the human-review queue can demonstrate
-- optimistic concurrency: the first authorized officer to complete the case wins.
--
-- Branch-scope (ABAC) demonstration users:
-- sophie.cs / sophie.cs@bss  - Customer Service Agent, SYD001 (Sydney)
-- mia.cs    / mia.cs@bss     - Customer Service Agent, MEL001 (Melbourne)
-- Each can onboard and see only customers whose primary residential address is
-- in their own branch's city; the Sydney KYC officers review Sophie's onboardings.

INSERT INTO users
(subject_id, first_name, last_name, email, user_name, hashed_password)
VALUES
('c7a8527b-6117-4ff0-a946-41c65775c43a','Liam','Taylor','customer.demo@ewp.local','customer.demo','AQAAAAIAAYagAAAAEIlPgp7TyCFnglFyGNtICXvPagDlh3e5iiHpgzdYDq13HNnTEz39YgoKfY30CtMX+A=='),
('756a2ead-62e2-49fa-986f-7d5c90c4b897','Sophie','Mitchell','sophie.cs@ewp.local','sophie.cs','AQAAAAIAAYagAAAAEPY4aOFXs5jKT6JQTz2NoI9lZ9PuueDVV+1Z8cjuBd3RD5C9mXsEdQZtyhRj2a7rmg=='),
('3e248f73-528a-460b-a052-ab56ccfa5e82','Liam','Anderson','liam.kyc@ewp.local','liam.kyc','AQAAAAIAAYagAAAAEBopMgsJC9hmTnnn49trJ/0pix6arpfqTsyKEKhIZFFh3Abv/4g0X3M+GF5JLurbTA=='),
('17fdde42-f3e7-4b7d-b41a-9812f4d89c97','Olivia','Bennett','olivia.compliance@ewp.local','olivia.compliance','AQAAAAIAAYagAAAAEGpSs2A8wnMhWNxds3d82NWpglQ+Xn3TpFZa2s3apE/FzCVFaOhVzVjDJacN3KSk2Q=='),
('12a2ce6b-2ff7-4f02-8c82-39001b2d98b3','Jack','Wilson','jack.accounts@ewp.local','jack.accounts','AQAAAAIAAYagAAAAEPLCTsqXUpWAbOKfsJqKp5SFc2QOzb/Ko1SH9VQXORu+r64z64jS1AjF5yX0DiqWDA=='),
('2e486365-900b-4f46-ba7a-da64c0db5def','Emily','Carter','emily.payments@ewp.local','emily.payments','AQAAAAIAAYagAAAAELIL6rQum0qOZEaRh2RC+4sHRWoSGJaOn8pmHW7TE7UB4nxI5FPfDvK0J8uzCB7wXQ=='),
('fe982b55-3396-4eaa-b1be-90e75a8111ba','Daniel','Cooper','daniel.ops@ewp.local','daniel.ops','AQAAAAIAAYagAAAAEKqRcAtU8ubQvN0M2kH1pVEFj7W+VEvTNO2zEQSD6ScH1y8kz4sM8HnOFWiMBL+CLA=='),
('bbf5ec6a-1903-41eb-98c7-ca76b1d5a415','Sarah','Collins','sarah.audit@ewp.local','sarah.audit','AQAAAAIAAYagAAAAED8RxCLXVADbAjiHytlh6xfOD3F2J+l3DguFIe42jd677n9a4wH4+HwRdjoOzChhNw=='),
('f77db41d-0eda-4291-a86e-7dd452254b1d','Michael','Turner','platform.admin@ewp.local','platform.admin','AQAAAAIAAYagAAAAECfATzS4UHKPpe6PWJ7voKLlapzMU22k1YdQpx7PayeDKBT/vSeYFJVI0O7nw7Pn1w=='),
('7d7a42e5-8250-45c7-ba92-6b6d449007a3','Ethan','Parker','ethan.kyc@ewp.local','ethan.kyc','AQAAAAIAAYagAAAAEKRsMC96qN6pkuEwNtKRnAxFQ8dS6Da0/+RBkXoL8jNKVRM/3GcQqxKB27ClDVvqWg=='),
('0b583994-3e74-4b4b-b807-efae632351ce','Noah','Hughes','noah.kyc@ewp.local','noah.kyc','AQAAAAIAAYagAAAAEKi316gXNJpixuDi0xLAI8oh6s/FIhmH+cqKU2ktAIB7AInf9Nb0Z6y130jVEAZyKA=='),
('14ea7069-72fc-4dda-b70f-0580ce03e518','Mia','Robinson','mia.cs@ewp.local','mia.cs','AQAAAAIAAYagAAAAEGfsNE93UJWybUddYUydvOF9uHg/AjFhOcjxV3tAza3cKkMzRB71EZHAnCnDWeFaiw=='),
('e41d2d3d-c803-4399-860a-3971342f1740','Grace','Walsh','grace.compliance@ewp.local','grace.compliance','AQAAAAIAAYagAAAAEGPpX+Mq624eR1rkU3B8Z0ryAM2JWOPMj+k/jCsPi0FoeaA/toS9m9854bxBhoQf0A==');

-- =========================================================
-- 15. EMPLOYMENT / ABAC PROFILES
-- =========================================================

INSERT INTO user_employment_profiles
(user_id, employee_id, lan_id, department_id, branch_id, employment_type, clearance_level)
SELECT
    u.id,
    x.employee_id,
    x.lan_id,
    d.id,
    b.id,
    x.employment_type,
    x.clearance_level
FROM (VALUES
    ('sophie.cs','EMP-10042','somit','CUSTOMER_SERVICE','SYD001','FULL_TIME',2),
    ('liam.kyc','EMP-10043','liand','KYC','SYD001','FULL_TIME',3),
    ('olivia.compliance','EMP-10044','olben','COMPLIANCE','SYD001','FULL_TIME',4),
    ('jack.accounts','EMP-10045','jawil','ACCOUNTS','SYD001','FULL_TIME',3),
    -- Payments officer of SYD001: approves the branch's payments above the tier (step 5b).
    ('emily.payments','EMP-10046','emcar','PAYMENTS','SYD001','FULL_TIME',4),
    ('daniel.ops','EMP-10047','dacoo','OPERATIONS','BNE001','FULL_TIME',4),
    ('sarah.audit','EMP-10048','sacol','AUDIT','ADL001','FULL_TIME',5),
    ('platform.admin','EMP-10049','mitur','IT','PER001','FULL_TIME',5),
    ('ethan.kyc','EMP-10050','etpar','KYC','SYD001','FULL_TIME',3),
    ('noah.kyc','EMP-10051','nohug','KYC','SYD001','FULL_TIME',3),
    ('mia.cs','EMP-10052','mirob','CUSTOMER_SERVICE','MEL001','FULL_TIME',2),
    -- Senior compliance officer: clearance 5 may approve HIGH-risk (sanctions MATCH) cases.
    ('grace.compliance','EMP-10053','grwal','COMPLIANCE','SYD001','FULL_TIME',5)
) AS x(user_name,employee_id,lan_id,department_code,branch_code,employment_type,clearance_level)
JOIN users u ON u.user_name = x.user_name
JOIN departments d ON d.code = x.department_code
JOIN branches b ON b.code = x.branch_code;

-- =========================================================
-- 16. USER ROLES
-- =========================================================

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'customer'
WHERE u.user_name = 'customer.demo';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'customer_service_agent'
WHERE u.user_name IN ('sophie.cs','mia.cs');

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'kyc_officer'
WHERE u.user_name = 'liam.kyc';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'kyc_officer'
WHERE u.user_name IN ('ethan.kyc','noah.kyc');

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'compliance_officer'
WHERE u.user_name IN ('olivia.compliance','grace.compliance');

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'account_officer'
WHERE u.user_name = 'jack.accounts';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'payments_officer'
WHERE u.user_name = 'emily.payments';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'operations_administrator'
WHERE u.user_name = 'daniel.ops';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'auditor'
WHERE u.user_name = 'sarah.audit';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code IN ('platform_administrator','operations_administrator')
WHERE u.user_name = 'platform.admin';

-- =========================================================
-- 17. ROLE PERMISSIONS
-- =========================================================

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'customer.onboarding.create',
    'customer.onboarding.view_own',
    'customer.onboarding.update_own',
    'customer.onboarding.submit',
    'customer.account.view_own',
    'customer.payment.create',
    'customer.payment.view_own'
)
WHERE r.code = 'customer';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'customer.onboarding.create',
    'customer.onboarding.view',
    'customer.onboarding.update',
    'customer.onboarding.submit',
    'customer.onboarding.assist',
    'customer.profile.view',
    'customer.profile.update',
    -- Assisted channel: capture a customer's payment and follow it (never approve it).
    'payment.initiate',
    'payment.view',
    'workflow.status.view'
)
WHERE r.code = 'customer_service_agent';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'kyc.case.view',
    'kyc.case.update',
    'kyc.identity.verify',
    'kyc.document.verify',
    'kyc.case.request_information',
    'kyc.case.approve',
    'kyc.case.reject',
    'kyc.case.hold',
    'workflow.status.view'
)
WHERE r.code = 'kyc_officer';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'compliance.case.view',
    'compliance.case.review',
    'compliance.aml.review',
    'compliance.risk.assess',
    'compliance.case.request_information',
    'compliance.case.approve',
    'compliance.case.reject',
    'compliance.case.hold',
    'compliance.case.release',
    'workflow.status.view'
)
WHERE r.code = 'compliance_officer';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'account.application.view',
    'account.application.review',
    'account.application.approve',
    'account.application.reject',
    'account.application.hold',
    'account.lifecycle.view',
    'workflow.status.view'
)
WHERE r.code = 'account_officer';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'payment.view',
    'payment.validate',
    'payment.approve',
    'payment.reject',
    'payment.hold',
    'payment.release',
    'payment.retry',
    'workflow.status.view'
)
WHERE r.code = 'payments_officer';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'workflow.view',
    'workflow.retry',
    'workflow.pause',
    'workflow.resume',
    'workflow.reprocess',
    'integration.status.view',
    'service.health.view',
    'outbox.view',
    'inbox.view',
    'consumer.status.view',
    'workflow.status.view'
)
WHERE r.code = 'operations_administrator';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'audit.view',
    'audit.search',
    'workflow.history.view',
    'customer.history.view',
    'kyc.history.view',
    'account.history.view',
    'payment.history.view',
    'approval.history.view',
    'workflow.status.view'
)
WHERE r.code = 'auditor';

INSERT INTO role_permissions (role_id, permission_id)
SELECT r.id, p.id
FROM roles r
JOIN permissions p ON p.code IN (
    'platform.configuration.view',
    'platform.configuration.update',
    'platform.user.view',
    'platform.user.manage',
    'platform.role.view',
    'platform.role.manage',
    'platform.permission.view',
    'platform.health.view',
    'service.health.view',
    'workflow.status.view'
)
WHERE r.code = 'platform_administrator';

-- =========================================================
-- 19. VERIFICATION QUERIES
-- =========================================================

-- Expected role assignments:
-- customer.demo      -> customer
-- sophie.cs          -> customer_service_agent
-- mia.cs             -> customer_service_agent
-- liam.kyc           -> kyc_officer
-- ethan.kyc          -> kyc_officer
-- noah.kyc           -> kyc_officer
-- olivia.compliance  -> compliance_officer
-- grace.compliance   -> compliance_officer (clearance 5)
-- jack.accounts      -> account_officer
-- emily.payments     -> payments_officer
-- daniel.ops         -> operations_administrator
-- sarah.audit        -> auditor
-- platform.admin     -> platform_administrator + operations_administrator

SELECT
    u.id,
    u.user_name,
    COALESCE(string_agg(r.code, ', ' ORDER BY r.code), '') AS roles
FROM users u
LEFT JOIN user_roles ur ON ur.user_id = u.id
LEFT JOIN roles r ON r.id = ur.role_id
GROUP BY u.id, u.user_name
ORDER BY u.id;

SELECT
    u.user_name,
    r.code AS role_code,
    r.name AS role_name
FROM users u
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id
ORDER BY u.id, r.id;

SELECT
    u.user_name,
    r.code AS role_code,
    p.code AS permission_code
FROM users u
JOIN user_roles ur ON ur.user_id = u.id
JOIN roles r ON r.id = ur.role_id
JOIN role_permissions rp ON rp.role_id = r.id
JOIN permissions p ON p.id = rp.permission_id
ORDER BY u.user_name, r.code, p.code;

SELECT
    u.user_name,
    e.employee_id,
    d.code AS department,
    b.code AS branch,
    b.region,
    e.employment_type,
    e.clearance_level
FROM users u
JOIN user_employment_profiles e ON e.user_id = u.id
JOIN departments d ON d.id = e.department_id
JOIN branches b ON b.id = e.branch_id
ORDER BY u.user_name;

-- =========================================================
-- DUENDE OPERATIONAL STORE + DATA PROTECTION KEYS (schema identity_server)
-- =========================================================
-- What the IDP must remember across restarts and share between instances:
--   PersistedGrants              refresh tokens, consents (stored by a HASH of the token
--                                handle - never the token itself; the details are encrypted)
--   PushedAuthorizationRequests  PAR requests between the BFF's push and the browser's visit
--   Keys                         signing keys managed by Duende (encrypted)
--   ServerSideSessions, DeviceCodes, Saml*  mapped by Duende's store; unused by this demo
--   data_protection_keys         the IDP's key ring (cookies, the encrypted columns above);
--                                encrypted with DPAPI in Development, a certificate elsewhere
-- Expired rows are removed by Duende's token clean-up (hourly). PascalCase names are Duende's.
-- Only ewp_idp may use the schema, for data access only (no DDL).

DROP SCHEMA IF EXISTS identity_server CASCADE;
CREATE SCHEMA identity_server;

CREATE TABLE identity_server."DeviceCodes" (
    "UserCode" character varying(200) NOT NULL,
    "DeviceCode" character varying(200) NOT NULL,
    "SubjectId" character varying(200),
    "SessionId" character varying(100),
    "ClientId" character varying(200) NOT NULL,
    "Description" character varying(200),
    "CreationTime" timestamp with time zone NOT NULL,
    "Expiration" timestamp with time zone NOT NULL,
    "Data" character varying(50000) NOT NULL,
    CONSTRAINT "PK_DeviceCodes" PRIMARY KEY ("UserCode")
);

CREATE TABLE identity_server."Keys" (
    "Id" text NOT NULL,
    "Version" integer NOT NULL,
    "Created" timestamp with time zone NOT NULL,
    "Use" text,
    "Algorithm" character varying(100) NOT NULL,
    "IsX509Certificate" boolean NOT NULL,
    "DataProtected" boolean NOT NULL,
    "Data" text NOT NULL,
    CONSTRAINT "PK_Keys" PRIMARY KEY ("Id")
);

CREATE TABLE identity_server."PersistedGrants" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "Key" character varying(200),
    "Type" character varying(50) NOT NULL,
    "SubjectId" character varying(200),
    "SessionId" character varying(100),
    "ClientId" character varying(200) NOT NULL,
    "Description" character varying(200),
    "CreationTime" timestamp with time zone NOT NULL,
    "Expiration" timestamp with time zone,
    "ConsumedTime" timestamp with time zone,
    "Data" character varying(50000) NOT NULL,
    CONSTRAINT "PK_PersistedGrants" PRIMARY KEY ("Id")
);

CREATE TABLE identity_server."PushedAuthorizationRequests" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "ReferenceValueHash" character varying(64) NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "Parameters" text NOT NULL,
    CONSTRAINT "PK_PushedAuthorizationRequests" PRIMARY KEY ("Id")
);

CREATE TABLE identity_server."SamlLogoutSessions" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "LogoutId" character varying(200) NOT NULL,
    "SerializedSession" text NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "Version" bigint NOT NULL,
    CONSTRAINT "PK_SamlLogoutSessions" PRIMARY KEY ("Id")
);

CREATE TABLE identity_server."SamlSigninStates" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "StateId" uuid NOT NULL,
    "SerializedState" text NOT NULL,
    "ExpiresAtUtc" timestamp with time zone NOT NULL,
    "ServiceProviderEntityId" character varying(200) NOT NULL,
    CONSTRAINT "PK_SamlSigninStates" PRIMARY KEY ("Id")
);

CREATE TABLE identity_server."ServerSideSessions" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "Key" character varying(100) NOT NULL,
    "Scheme" character varying(100) NOT NULL,
    "SubjectId" character varying(100) NOT NULL,
    "SessionId" character varying(100),
    "DisplayName" character varying(100),
    "Created" timestamp with time zone NOT NULL,
    "Renewed" timestamp with time zone NOT NULL,
    "Expires" timestamp with time zone,
    "Data" text NOT NULL,
    CONSTRAINT "PK_ServerSideSessions" PRIMARY KEY ("Id")
);

CREATE TABLE identity_server."SamlLogoutSessionRequestIndices" (
    "Id" bigint GENERATED BY DEFAULT AS IDENTITY,
    "RequestId" character varying(200) NOT NULL,
    "SamlLogoutSessionId" bigint NOT NULL,
    CONSTRAINT "PK_SamlLogoutSessionRequestIndices" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_SamlLogoutSessionRequestIndices_SamlLogoutSessions_SamlLogo~" FOREIGN KEY ("SamlLogoutSessionId") REFERENCES identity_server."SamlLogoutSessions" ("Id") ON DELETE CASCADE
);
CREATE UNIQUE INDEX "IX_DeviceCodes_DeviceCode" ON identity_server."DeviceCodes" ("DeviceCode");
CREATE INDEX "IX_DeviceCodes_Expiration" ON identity_server."DeviceCodes" ("Expiration");
CREATE INDEX "IX_Keys_Use" ON identity_server."Keys" ("Use");
CREATE INDEX "IX_PersistedGrants_ConsumedTime" ON identity_server."PersistedGrants" ("ConsumedTime");
CREATE INDEX "IX_PersistedGrants_Expiration" ON identity_server."PersistedGrants" ("Expiration");
CREATE UNIQUE INDEX "IX_PersistedGrants_Key" ON identity_server."PersistedGrants" ("Key");
CREATE INDEX "IX_PersistedGrants_SubjectId_ClientId_Type" ON identity_server."PersistedGrants" ("SubjectId", "ClientId", "Type");
CREATE INDEX "IX_PersistedGrants_SubjectId_SessionId_Type" ON identity_server."PersistedGrants" ("SubjectId", "SessionId", "Type");
CREATE INDEX "IX_PushedAuthorizationRequests_ExpiresAtUtc" ON identity_server."PushedAuthorizationRequests" ("ExpiresAtUtc");
CREATE UNIQUE INDEX "IX_PushedAuthorizationRequests_ReferenceValueHash" ON identity_server."PushedAuthorizationRequests" ("ReferenceValueHash");
CREATE UNIQUE INDEX "IX_SamlLogoutSessionRequestIndices_RequestId" ON identity_server."SamlLogoutSessionRequestIndices" ("RequestId");
CREATE INDEX "IX_SamlLogoutSessionRequestIndices_SamlLogoutSessionId" ON identity_server."SamlLogoutSessionRequestIndices" ("SamlLogoutSessionId");
CREATE INDEX "IX_SamlLogoutSessions_ExpiresAtUtc" ON identity_server."SamlLogoutSessions" ("ExpiresAtUtc");
CREATE UNIQUE INDEX "IX_SamlLogoutSessions_LogoutId" ON identity_server."SamlLogoutSessions" ("LogoutId");
CREATE INDEX "IX_SamlSigninStates_ExpiresAtUtc" ON identity_server."SamlSigninStates" ("ExpiresAtUtc");
CREATE UNIQUE INDEX "IX_SamlSigninStates_StateId" ON identity_server."SamlSigninStates" ("StateId");
CREATE INDEX "IX_ServerSideSessions_DisplayName" ON identity_server."ServerSideSessions" ("DisplayName");
CREATE INDEX "IX_ServerSideSessions_Expires" ON identity_server."ServerSideSessions" ("Expires");
CREATE UNIQUE INDEX "IX_ServerSideSessions_Key" ON identity_server."ServerSideSessions" ("Key");
CREATE INDEX "IX_ServerSideSessions_SessionId" ON identity_server."ServerSideSessions" ("SessionId");
CREATE INDEX "IX_ServerSideSessions_SubjectId" ON identity_server."ServerSideSessions" ("SubjectId");

CREATE TABLE identity_server.data_protection_keys (
    id integer GENERATED BY DEFAULT AS IDENTITY,
    friendly_name text,
    xml text,
    CONSTRAINT "PK_data_protection_keys" PRIMARY KEY (id)
);

DO $$
BEGIN
    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'ewp_idp') THEN
        GRANT USAGE ON SCHEMA identity_server TO ewp_idp;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA identity_server TO ewp_idp;
        GRANT USAGE, SELECT, UPDATE ON ALL SEQUENCES IN SCHEMA identity_server TO ewp_idp;
    ELSE
        RAISE WARNING 'Role ewp_idp does not exist yet: run ps\database\Apply-EwpServiceDbUsers.ps1 (it grants this schema too).';
    END IF;
END $$;

COMMIT;