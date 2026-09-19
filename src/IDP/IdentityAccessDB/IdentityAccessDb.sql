-- Enterprise Web Platform V3 - IdentityAccessDb_Australian_Demo.sql
-- PostgreSQL schema and demonstration seed data.
-- This version is intentionally aligned with IdentityDbContext.cs.
-- Indexes are explicitly named so PostgreSQL and EF Core have the same model.
-- PoC/demo data only.

BEGIN;

-- =========================================================
-- RESET: reverse dependency order
-- =========================================================

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
-- 9. REBAC RELATIONSHIPS
-- =========================================================

CREATE TABLE user_relationships (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    subject_user_id BIGINT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    relationship_type VARCHAR(100) NOT NULL,
    resource_type VARCHAR(100) NOT NULL,
    resource_id VARCHAR(100) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT CURRENT_TIMESTAMP
);

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

CREATE UNIQUE INDEX ux_user_relationships_subject_relationship_resource
    ON user_relationships(
        subject_user_id,
        relationship_type,
        resource_type,
        resource_id
    );

CREATE INDEX ix_user_relationships_resource
    ON user_relationships(resource_type, resource_id);

CREATE INDEX ix_user_relationships_subject_relationship
    ON user_relationships(subject_user_id, relationship_type);

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

INSERT INTO branches (code, name, region) VALUES
('BLR001','Bengaluru Central','SOUTH'),
('BLR002','Bengaluru East','SOUTH'),
('MUM001','Mumbai Central','WEST');

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

INSERT INTO users
(subject_id, first_name, last_name, email, user_name, hashed_password)
VALUES
('11111111-1111-4111-8111-111111111111','Liam','Taylor','customer.demo@ewp.local','customer.demo','AQAAAAIAAYagAAAAEIlPgp7TyCFnglFyGNtICXvPagDlh3e5iiHpgzdYDq13HNnTEz39YgoKfY30CtMX+A=='),
('22222222-2222-4222-8222-222222222222','Sophie','Mitchell','sophie.cs@ewp.local','sophie.cs','AQAAAAIAAYagAAAAEPY4aOFXs5jKT6JQTz2NoI9lZ9PuueDVV+1Z8cjuBd3RD5C9mXsEdQZtyhRj2a7rmg=='),
('33333333-3333-4333-8333-333333333333','Liam','Anderson','liam.kyc@ewp.local','liam.kyc','AQAAAAIAAYagAAAAEBopMgsJC9hmTnnn49trJ/0pix6arpfqTsyKEKhIZFFh3Abv/4g0X3M+GF5JLurbTA=='),
('44444444-4444-4444-8444-444444444444','Olivia','Bennett','olivia.compliance@ewp.local','olivia.compliance','AQAAAAIAAYagAAAAEGpSs2A8wnMhWNxds3d82NWpglQ+Xn3TpFZa2s3apE/FzCVFaOhVzVjDJacN3KSk2Q=='),
('55555555-5555-4555-8555-555555555555','Jack','Wilson','jack.accounts@ewp.local','jack.accounts','AQAAAAIAAYagAAAAEPLCTsqXUpWAbOKfsJqKp5SFc2QOzb/Ko1SH9VQXORu+r64z64jS1AjF5yX0DiqWDA=='),
('66666666-6666-4666-8666-666666666666','Emily','Carter','emily.payments@ewp.local','emily.payments','AQAAAAIAAYagAAAAELIL6rQum0qOZEaRh2RC+4sHRWoSGJaOn8pmHW7TE7UB4nxI5FPfDvK0J8uzCB7wXQ=='),
('77777777-7777-4777-8777-777777777777','Daniel','Cooper','daniel.ops@ewp.local','daniel.ops','AQAAAAIAAYagAAAAEKqRcAtU8ubQvN0M2kH1pVEFj7W+VEvTNO2zEQSD6ScH1y8kz4sM8HnOFWiMBL+CLA=='),
('88888888-8888-4888-8888-888888888888','Sarah','Collins','sarah.audit@ewp.local','sarah.audit','AQAAAAIAAYagAAAAED8RxCLXVADbAjiHytlh6xfOD3F2J+l3DguFIe42jd677n9a4wH4+HwRdjoOzChhNw=='),
('99999999-9999-4999-8999-999999999999','Michael','Turner','platform.admin@ewp.local','platform.admin','AQAAAAIAAYagAAAAECfATzS4UHKPpe6PWJ7voKLlapzMU22k1YdQpx7PayeDKBT/vSeYFJVI0O7nw7Pn1w==');

-- =========================================================
-- 15. EMPLOYMENT / ABAC PROFILES
-- =========================================================

INSERT INTO user_employment_profiles
(user_id, employee_id, department_id, branch_id, employment_type, clearance_level)
SELECT
    u.id,
    x.employee_id,
    d.id,
    b.id,
    x.employment_type,
    x.clearance_level
FROM (VALUES
    ('sophie.cs','EMP-10042','CUSTOMER_SERVICE','BLR001','FULL_TIME',2),
    ('liam.kyc','EMP-10043','KYC','BLR001','FULL_TIME',3),
    ('olivia.compliance','EMP-10044','COMPLIANCE','BLR001','FULL_TIME',4),
    ('jack.accounts','EMP-10045','ACCOUNTS','BLR002','FULL_TIME',3),
    ('emily.payments','EMP-10046','PAYMENTS','BLR002','FULL_TIME',4),
    ('daniel.ops','EMP-10047','OPERATIONS','BLR001','FULL_TIME',4),
    ('sarah.audit','EMP-10048','AUDIT','BLR001','FULL_TIME',5),
    ('platform.admin','EMP-10049','IT','BLR001','FULL_TIME',5)
) AS x(user_name,employee_id,department_code,branch_code,employment_type,clearance_level)
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
WHERE u.user_name = 'sophie.cs';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'kyc_officer'
WHERE u.user_name = 'liam.kyc';

INSERT INTO user_roles (user_id, role_id)
SELECT u.id, r.id
FROM users u
JOIN roles r ON r.code = 'compliance_officer'
WHERE u.user_name = 'olivia.compliance';

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
-- 18. ReBAC demonstration relationships
-- =========================================================

INSERT INTO user_relationships
(subject_user_id, relationship_type, resource_type, resource_id)
SELECT
    u.id,
    x.relationship_type,
    x.resource_type,
    x.resource_id
FROM users u
JOIN (VALUES
    ('sophie.cs','manages','Customer','CUST-10045'),
    ('liam.kyc','assigned_to','KYC_Case','KYC-10045'),
    ('olivia.compliance','assigned_to','KYC_Case','KYC-10045'),
    ('jack.accounts','assigned_to','Account_Application','ACCAPP-10045'),
    ('customer.demo','owns','Account','ACC-100001'),
    ('customer.demo','owns','Payment','PAY-100001')
) AS x(user_name, relationship_type, resource_type, resource_id)
ON u.user_name = x.user_name;

-- =========================================================
-- 19. VERIFICATION QUERIES
-- =========================================================

-- Expected role assignments:
-- customer.demo      -> customer
-- sophie.cs          -> customer_service_agent
-- liam.kyc           -> kyc_officer
-- olivia.compliance  -> compliance_officer
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

SELECT
    u.user_name,
    ur.relationship_type,
    ur.resource_type,
    ur.resource_id
FROM user_relationships ur
JOIN users u ON u.id = ur.subject_user_id
ORDER BY u.user_name, ur.resource_type, ur.resource_id;

COMMIT;