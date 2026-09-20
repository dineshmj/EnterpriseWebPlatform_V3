0) Local Development Hostnames and HTTPS:

	This project uses dedicated hostnames for the individual tiers of the solution.

	This is important because browser cookies are scoped by hostname, not by port. When multiple applications use "localhost" on different ports, cookies created by the different applications can be sent with requests to other "localhost" applications. Authentication, correlation, nonce, session, and BFF cookies can therefore accumulate in the request's Cookie header.

	In our local development setup, this resulted in the request headers becoming large enough to cause:

		HTTP 400 - Request Too Long

	with the message:

		The size of the request headers is too long.

	Therefore, each major tier is given its own dedicated hostname, while all hostnames still resolve to 127.0.0.1. This gives each application an independent browser cookie namespace and prevents cookie collisions between the tiers.

	Please follow the steps mentioned below to accomplish this:

	a) Go to the folder "C:\Windows\System32\drivers\etc\", and open the "hosts" file in Notepad++.

	b) Ensure that you add the following hostname mappings to 127.0.0.1 for each tier in this solution at the end of the "hosts" file.

		127.0.0.1    idp.dev.localhost
		
		127.0.0.1    shell.dev.localhost
		
		127.0.0.1    customer.dev.localhost
		127.0.0.1    kyc.dev.localhost
		127.0.0.1    accounts.dev.localhost
		127.0.0.1    payments.dev.localhost

		127.0.0.1    customer-api.dev.localhost
		127.0.0.1    kyc-api.dev.localhost
		127.0.0.1    accounts-api.dev.localhost
		127.0.0.1    payments-api.dev.localhost

	c) Test them using ping commands. Each hostname should resolve to 127.0.0.1 and return a valid ping response.

		ping idp.dev.localhost
		ping shell.dev.localhost

		ping customer.dev.localhost
		ping customer-api.dev.localhost

		ping kyc.dev.localhost
		ping kyc-api.dev.localhost

		ping accounts.dev.localhost
		ping accounts-api.dev.localhost

		ping payments.dev.localhost
		ping payments-api.dev.localhost

	d) The local HTTPS URLs for the tiers are:

		Shell BFF				-	https://shell.dev.localhost:44367
		Shell SPA				-	Static export served by the Shell BFF

		IDP						-	https://idp.dev.localhost:44392

		Customer Onboarding BFF	-	https://customer.dev.localhost:44311
		Customer Onboarding SPA	-	Static export served by the Customer Onboarding BFF
		Customer Onboarding API	-	https://customer-api.dev.localhost:44363

		Customer KYC BFF		-	https://kyc.dev.localhost:33800
		Customer KYC SPA		-	Static export served by the Customer KYC BFF
		Customer KYC API		-	https://kyc-api.dev.localhost:44305

		Accounts BFF			-	https://accounts.dev.localhost:45456
		Accounts SPA			-	Static export served by the Accounts BFF
		Accounts API			-	https://accounts-api.dev.localhost:48486

		Payments BFF			-	https://payments.dev.localhost:44388
		Payments SPA			-	Next.js application; URL to be added when the local HTTPS configuration is finalized
		Payments API			-	https://payments-api.dev.localhost:44488

	e) For ASP.NET Core applications, use the Kestrel server for local development rather than IIS Express. Select the "https" Project profile in launchSettings.json for:

		1) Shell BFF
		2) Customer Onboarding BFF & API
		3) Customer KYC BFF & API
		4) Accounts BFF & API
		5) Payments API

		The "https" Project profile uses Kestrel and the ASP.NET Core HTTPS development certificate. The development certificate includes "*.dev.localhost" in its Subject Alternative Names (SANs).

		The Payments MFE and Payments BFF use Next.js and NestJS respectively, so their HTTPS configuration is separate from the ASP.NET Core applications.

		NOTE:
		The use of Kestrel for V3 is intentional. In the previous version, IIS Express was used for several applications mainly to avoid opening multiple console windows. V3 uses dedicated hostnames and Kestrel-based HTTPS for the ASP.NET Core tiers so that the local topology is explicit and consistent.

1) Technology Stack and V3 Architecture:

	a) Identity Provider (IDP):

		- Duende IdentityServer 8 running on ASP.NET Core 10.
		- Uses PostgreSQL for the Identity and Authorization database.
		- Database: EwpIdentityAccessDb.
		- Supports OpenID Connect and OAuth 2.x protocols.
		- Grant type: Authorization Code Flow with PKCE.
		- Supports refresh tokens where configured.
		- Supports role-based access control (R-BAC) using role claims.
		- Supports a consent page.
		- Uses a stable opaque SubjectId as the external OIDC "sub" identifier.
		- Identity data is kept separate from business microservice databases.

	b) Banking Services System (BSS):

		The BSS Shell is the master/composition application for the Banking Services System.

		- Shell SPA: Next.js.
		- Shell BFF: ASP.NET Core 10.
		- The Shell composes Microservice experiences and owns the application workspace.
		- The Shell does not directly own or query Microservice business databases.
		- Microservice URLs are obtained from the Shell Menu DB rather than hard-coded into the Shell application.
		- The Application Workspace exchanges opaque structured context between MFEs without requiring the Shell to understand business identifiers.

	c) Microservices / Bounded Contexts:

		1) Customer Onboarding:

			- Bounded Context: Customer Onboarding.
			- MFE: Next.js.
			- BFF: ASP.NET Core.
			- API: ASP.NET Core.
			- Database: CustomerDb.
			- Owns customer profile, contact/address information, onboarding applications and onboarding workflow state.

		2) Customer KYC:

			- Bounded Context: Customer KYC.
			- MFE: Next.js.
			- BFF: NestJS.
			- API: ASP.NET Core.
			- Database: KycDb.
			- Owns KYC cases, identity/document checks, AML screening, risk assessment and compliance decisions.

		3) Accounts:

			- Bounded Context: Accounts.
			- MFE: Next.js.
			- BFF: ASP.NET Core.
			- API: ASP.NET Core.
			- Database: AccountsDb.
			- Owns account applications, accounts, account holders and account lifecycle.
			- This PoC does not implement a real core-banking ledger.

		4) Payments:

			- Bounded Context: Payments.
			- MFE: Next.js.
			- BFF: NestJS.
			- API: ASP.NET Core.
			- Database: PaymentsDb.
			- Owns payment instructions, beneficiaries, payment attempts and payment-processing state.
			- This is an architectural PoC and not a real banking payment system.

	d) Messaging and distributed workflows:

		- Apache Kafka is used as the event/message backbone.
		- Producers use the Transactional Outbox pattern.
		- Consumers use the Inbox / Processed Messages pattern to support idempotent processing.
		- At-least-once delivery is assumed.
		- Customer Onboarding demonstrates a Saga workflow across the Customer, KYC and Accounts bounded contexts.
		- Saga compensation is demonstrated for failure scenarios.
		- Choreography is used where simple event reactions are more appropriate.
		- SignalR is used by the Shell to receive workflow-completion notifications.
		- CorrelationId, TraceId, SagaId and CausationId are propagated across the workflow.

	e) Application architecture:

		Each business microservice follows a layered / clean architecture structure:

			Service
			├── Domain
			│   ├── Entities / Aggregates
			│   ├── Value Objects
			│   ├── Domain Events
			│   └── Business Rules
			├── Application
			│   ├── Commands
			│   ├── Queries
			│   ├── Handlers
			│   └── DTOs
			├── Infrastructure
			│   ├── EF Core
			│   ├── Outbox
			│   ├── Inbox
			│   ├── Kafka
			│   └── External Services
			└── API
				├── Controllers / Endpoints
				└── Authorization

		Domain entities are owned by their bounded context. Business-domain entities are not shared between microservices.

	f) Data access:

		- PostgreSQL is the database platform.
		- EF Core is used for data access.
		- Each bounded context owns its own database.
		- There is no cross-service database access.
		- PostgreSQL table and column names use lowercase snake_case.
		- C# types and properties use normal .NET PascalCase naming.
		- EF Core migrations are used for database schema evolution.
		- Transactions are used where business state and Outbox records must be committed atomically.
		- Optimistic concurrency is used where appropriate.
		- Stored procedures are not used for ordinary CRUD operations.

	g) Security and authorization:

		- Human authentication is handled by the IDP.
		- BFFs represent the browser-facing security boundary.
		- Service-to-service communication can use M2M client-credentials tokens where appropriate.
		- Authorization is not based on RBAC alone.
		- The intended authorization pipeline is:

			Authenticated User
			        ↓
			      R-BAC
			        ↓
			      A-BAC
			        ↓
			      Re-BAC
			        ↓
			   Workflow State
			        ↓
			       SoD
			        ↓
			 Authorization Decision

		- R-BAC: Role-Based Access Control.
		- A-BAC: Attribute-Based Access Control.
		- Re-BAC: Relationship-Based Access Control.
		- SoD: Separation of Duties.
		- Menu visibility is not the authoritative authorization mechanism. BFF/API authorization remains authoritative.
		- Audit information is propagated with relevant business and security events.

	h) Resilience and observability:

		- Retry with appropriate limits.
		- Timeout.
		- Circuit breaker.
		- Bulkhead where appropriate.
		- Fallback / compensation for supported failure scenarios.
		- Idempotency.
		- Structured logging.
		- Correlation and distributed tracing.
		- Health checks.
		- Rate limiting.
		- Secure secret handling.
		- Input validation and appropriate HTTP security controls.
		- Simulated external KYC/AML providers are used to demonstrate failure, timeout and transient-error handling.

2) Databases and Initial Infrastructure:

	a) PostgreSQL is used as the primary relational database platform.

	b) The current databases are:

		- EwpIdentityAccessDb
			Identity, users, roles, permissions and authorization relationships.

		- EwpBssShellDb
			Shell navigation/menu metadata.

		The business databases will be:

		- CustomerDb
		- KycDb
		- AccountsDb
		- PaymentsDb

	c) The database-per-service rule applies to the business bounded contexts. A single local PostgreSQL server/instance may host the individual databases during development.

	d) Kafka and Kafka UI will be introduced as local Docker-based infrastructure as the messaging portion of V3 is implemented.

3) What to do after cloning the repository:

	a) Ensure that the required .NET 10 SDK, Node.js, pnpm and PostgreSQL installations are available.

	b) Configure the Windows hosts file as described in section 0.

	c) Ensure that the ASP.NET Core HTTPS development certificate is installed and trusted.

		To check the certificate:

			dotnet dev-certs https --check

		To trust it when required:

			dotnet dev-certs https --trust

	d) Ensure that PostgreSQL is running on:

			Host: localhost
			Port: 5432

	e) Create/restore the following databases before starting the applications:

			EwpIdentityAccessDb
			EwpBssShellDb

		The database scripts in the repository contain the schema and seed data for the current V3 foundation.

	f) IMPORTANT:
		The PostgreSQL credentials currently used by the local development configuration are development-only credentials. Do not use these credentials in a real environment. Production/deployment secrets must be supplied through an appropriate secret-management mechanism.

	g) Open the solution in Visual Studio and restore the NuGet packages.

	h) For ASP.NET Core applications, use the "https" Project profile rather than IIS Express.

	i) The IDP is self-hosted and therefore opens its own console window. The other ASP.NET Core applications are intended to run using their Kestrel "https" Project profiles.

	j) The Next.js / NestJS applications are started using their respective package-manager commands as their implementations become available.

4) Current V3 Local Development URLs:

	Shell BFF:
		https://shell.dev.localhost:44367

	IDP:
		https://idp.dev.localhost:44392

	Customer Onboarding BFF:
		https://customer.dev.localhost:44311

	Customer Onboarding API:
		https://customer-api.dev.localhost:44363

	Customer KYC BFF:
		https://kyc.dev.localhost:33800

	Customer KYC API:
		https://kyc-api.dev.localhost:44305

	Accounts BFF:
		https://accounts.dev.localhost:45456

	Accounts API:
		https://accounts-api.dev.localhost:48486

	Payments BFF:
		https://payments.dev.localhost:44388

	Payments API:
		https://payments-api.dev.localhost:44488

	NOTE:
		Some of the above applications are reserved/future V3 endpoints. The URL list represents the intended local topology and will be updated as each application is implemented.

5) Current V3 Foundation:

	a) IDP:

		- PostgreSQL Identity/Authorization database.
		- Users, roles, permissions and authorization relationships.
		- OIDC Authorization Code Flow with PKCE.
		- Demo users representing the intended banking-services personas.
		- Stable opaque OIDC SubjectId values.
		- Role claims available to the BSS Shell.

	b) BSS Shell:

		- Banking Services System branding.
		- Shell BFF authentication.
		- PostgreSQL-backed menu repository.
		- Role-aware Microservice menu.
		- Application Workspace.
		- Structured context exchange between Shell and MFEs.
		- Dedicated local hostname.
		- Kestrel HTTPS development profile.

	c) Shell Menu DB:

		The Shell Menu database uses PostgreSQL lowercase snake_case identifiers:

			microservices
			management_areas
			menu_items
			menu_items_and_roles

		The Shell queries the menu metadata through the BSS Shell BFF. Menu visibility is based on the user's roles, but this visibility does not replace authorization enforcement in the downstream BFF/API.

6) Application Workspace:

	The Application Workspace is intentionally separate from the Microservice navigation menu.

	The Microservice menu is responsible for navigation.

	The Application Workspace is responsible for showing contextual information associated with the user's current work.

	The intended context model is:

		persistentContext
			Context that remains visible while the user moves between MFEs.

		currentContext
			The entity currently receiving the user's focus.

		retainedContext
			Previously established context that remains available for reuse.

	The Shell treats this context as opaque data. The MFE that owns the business semantics is responsible for creating and updating it.

7) Planned V3 Customer Onboarding Workflow:

	The flagship distributed workflow is Customer Onboarding:

		Customer MFE
		    ↓
		Customer BFF
		    ↓
		Customer API
		    ↓
		Business state + Outbox transaction
		    ↓
		Kafka
		    ↓
		KYC consumer
		    ↓
		KYC state + Outbox
		    ↓
		Kafka
		    ↓
		Compliance / AML processing
		    ↓
		Kafka
		    ↓
		Accounts consumer
		    ↓
		Account opening
		    ↓
		Workflow completion
		    ↓
		SignalR
		    ↓
		BSS Shell

	The workflow is intended to expose:

		- Application ID
		- Saga ID
		- Correlation ID
		- Current workflow status
		- Individual workflow steps
		- Event counts
		- Outbox activity
		- Retry activity
		- Compensation activity

8) Planned Authorization Demonstrations:

	The intended personas include:

		customer
		customer_service_agent
		kyc_officer
		compliance_officer
		account_officer
		payments_officer
		operations_administrator
		auditor
		platform_administrator

	Examples of authorization attributes include:

		User:
			employeeId
			department
			branch
			region
			employmentType
			clearanceLevel

		Resource:
			customerId
			branchId
			riskLevel
			classification
			paymentAmount
			workflowStatus
			assignedOfficerId

		Relationship:
			works_at
			assigned_to
			manages
			owns

		Separation-of-Duties examples will ensure that a user cannot perform prohibited combinations of actions merely because the user happens to possess multiple roles.

9) Logout:

	The BSS Shell owns the user-facing logout operation.

	The intended logout sequence is:

		- Clear the Shell authentication session.
		- Initiate OIDC logout with the IDP.
		- Perform the required back-channel/silent logout processing for participating Microservice BFFs.
		- Clear their local authentication sessions.
		- Allow the IDP to terminate its session.

	Authentication cookies must remain isolated between the dedicated local hostnames.

10) Troubleshooting:

	a) "HTTP 400 - Request Too Long" / "The size of the request headers is too long":

		- Confirm that the applications are being accessed using their dedicated *.dev.localhost hostnames rather than localhost.
		- Do not work around the problem by simply increasing server request-header limits.
		- Clear the cookies for the affected development host if stale OIDC correlation/nonce cookies are present.
		- Restart the affected application and repeat the authentication flow.

	b) HTTPS certificate warning for a *.dev.localhost URL:

		- Confirm that the ASP.NET Core application is running with the "https" Project profile.
		- Do not use the IIS Express profile for the ASP.NET Core V3 applications.
		- Run:

			dotnet dev-certs https --check

		- If necessary:

			dotnet dev-certs https --trust

		- Confirm that the requested hostname is under *.dev.localhost.

	c) A hostname does not resolve:

		- Check the Windows hosts file.
		- Run the relevant ping command.
		- Ensure that the hostname resolves to 127.0.0.1.

	d) Menu does not appear:

		- Confirm that PostgreSQL is running.
		- Confirm that EwpBssShellDb exists.
		- Confirm that the Shell Menu tables contain their expected snake_case names.
		- Confirm that the authenticated user has role claims.
		- Confirm that the BSS Shell BFF can connect to EwpBssShellDb.
		- Remember that menu visibility is based on role mappings; it is not a substitute for API authorization.

	e) Authentication succeeds but expected role-based menu items are missing:

		- Inspect the authenticated user's role claims.
		- Confirm the user-to-role mappings in EwpIdentityAccessDb.
		- Confirm that the role codes match the values used by the Shell Menu DB.
		- Sign out and sign in again after changing role assignments.

	f) Stale Visual Studio state:

		If Visual Studio behaves as though an old launch profile or project configuration is still being used:

		- Close Visual Studio.
		- Remove the solution's local ".vs" folder if necessary.
		- Reopen the solution.
		- Verify the configured startup profiles/projects.
		- Ensure that the ASP.NET Core applications use their "https" Project profiles.

11) Important Design Rules / Gotchas:

	a) PostgreSQL naming:

		- Use lowercase snake_case for PostgreSQL tables and columns.
		- Use PascalCase for C# classes and properties.
		- Avoid quoted PascalCase PostgreSQL identifiers.

	b) Database isolation:

		- Each business bounded context owns its own database.
		- Do not query another microservice's database directly.
		- Communicate across bounded contexts through APIs and/or Kafka events.

	c) Authorization:

		- Menu visibility is not security enforcement.
		- BFFs and APIs must enforce authorization.
		- Multiple roles do not automatically grant permission to violate Separation of Duties.

	d) Messaging:

		- Business state and Outbox messages must be committed atomically.
		- Consumers must be idempotent.
		- Assume at-least-once message delivery.

	e) Browser security:

		- Do not expose service access tokens or refresh tokens to the browser unnecessarily.
		- BFFs are the browser-facing security boundary.
		- Keep authentication cookies scoped to the appropriate application hostname.

	f) Secrets:

		- Development credentials are for local development only.
		- Do not commit production secrets, private signing keys or certificates to source control.
		- Production secrets must be supplied through an appropriate secret-management mechanism.

12) Final touches required (not urgent, only after the required V3 modules are implemented):

	- Complete OpenTelemetry distributed tracing.
	- Add centralized/structured audit persistence and audit-event processing.
	- Add Kafka monitoring and consumer-lag visibility.
	- Add comprehensive health/readiness endpoints.
	- Add rate limiting and production-grade security headers.
	- Add Terraform scripts to provision the required cloud infrastructure.
	- Add production secret-management integration.
	- Add deployment-specific configuration for Azure/AWS/GCP as appropriate.
