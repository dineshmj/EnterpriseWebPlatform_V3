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

13) Bruno API Testing
    ------------------

    This section explains how to configure Bruno for testing the Microservice
    API endpoints using the OAuth 2.0 Authorization Code + PKCE flow provided
    by EnterpriseWebPlatform.IdentityServer.


    a) Register a dedicated Bruno client in IdentityServer

       Ensure that IdentityServer's Config.cs has one dedicated public client
       registered for Bruno.

       Client ID:

           BSS.ApiTesting.Bruno.ClientID

       The client should be configured as follows:

           AllowedGrantTypes:
               Authorization Code

           RequirePkce:
               true

           RequireClientSecret:
               false

           RedirectUris:
               http://127.0.0.1:3000/callback

       The client should be allowed to request the required identity scopes:

           openid
           profile
           email
           roles

       and the Microservice API scopes that are required for testing.

       For Customer Onboarding:

           customer-onboarding.read
           customer-onboarding.write

       WHY:
       Bruno is treated as a public OAuth client. Authorization Code + PKCE
       avoids the need to store a client secret in the developer workstation.

       IF NOT:
       IdentityServer will reject the OAuth authorization request because the
       client is unknown, disabled, or not permitted to use the requested
       grant/scopes.


    b) Configure OAuth 2.0 in Bruno

       In Bruno, configure the request/collection to use:

           Grant Type:
               Authorization Code

           Authorization URL:
               https://idp.dev.localhost:44392/connect/authorize

           Access Token URL:
               https://idp.dev.localhost:44392/connect/token

           Client ID:
               BSS.ApiTesting.Bruno.ClientID

           Client Secret:
               Leave empty

           Use PKCE:
               Enabled

           Callback URL:
               http://127.0.0.1:3000/callback

       The access token should be added to the request as:

           Authorization: Bearer <access-token>


    c) Configure the OAuth Scope in Bruno

       This is an important Bruno/IdentityServer distinction.

       IdentityServer's AllowedScopes determine which scopes the Bruno client
       IS ALLOWED TO REQUEST.

       Bruno's OAuth "Scope" field determines which scopes Bruno ACTUALLY
       REQUESTS during the authorization flow.

       For example, for testing the Customer Onboarding GET endpoints, the
       Bruno Scope must explicitly contain:

           openid profile email roles customer-onboarding.read

       For a write operation, request the corresponding write scope:

           openid profile email roles customer-onboarding.write

       Do not assume that adding a scope to IdentityServer's AllowedScopes
       automatically causes that scope to appear in the access token.

       During testing, the initial token contained only:

           openid
           profile
           email
           roles

       because Bruno was requesting only those scopes.

       The token therefore did not contain:

           customer-onboarding.read

       After adding customer-onboarding.read to Bruno's OAuth Scope and
       obtaining a new token, the access token contained the expected API
       scope.


    d) Clear Bruno's OAuth token cache after configuration changes

       Bruno caches OAuth access tokens.

       Whenever the OAuth configuration, requested scopes, IdentityServer
       client configuration, or related IdentityServer configuration changes,
       use Bruno's:

           Clear Cache

       option before obtaining a new token.

       Then authenticate again and obtain a fresh access token.

       WHY:
       An already-issued access token does not change when IdentityServer
       configuration changes.


    e) Verify the access token before troubleshooting the API

       Decode the JWT access token and verify the important claims.

       For Customer Onboarding API testing, the token should contain:

           iss:
               https://idp.dev.localhost:44392

           aud:
               customer-onboarding-api

           scope:
               customer-onboarding.read

       When testing with the Sophie user, the token should also contain the
       expected identity information, including the user's role.

       This makes it possible to distinguish an OAuth/IdentityServer/Bruno
       problem from an API authentication or authorization problem.


    f) OAuth callback - Bruno hosted callback

       Bruno can use its hosted OAuth callback:

           https://oauth.usebruno.com/callback

       During our V3 testing, the browser successfully completed the
       IdentityServer login and redirected to the hosted callback, but remained
       on:

           Redirecting to Bruno...

       and did not return control to Bruno.

       Browser developer tools did not show useful network activity while the
       page remained in this state.

       Therefore, for local development we moved to Bruno's local callback
       server.


    g) Local OAuth callback server

       Start Bruno's OAuth callback server from a terminal:

           npx @usebruno/oauth2-callback-server --port 3000

       The installed callback server reported:

           OAuth2 callback server running at
           http://127.0.0.1:3000/callback

       Therefore, the IdentityServer client's RedirectUris must contain the
       exact URI reported by the callback server:

           http://127.0.0.1:3000/callback

       IMPORTANT:
       The callback path can differ between Bruno documentation and the
       installed callback-server package/version.

       Always use the endpoint reported by the callback server that is
       actually running.

       During this testing session, the actual endpoint was:

           /callback

       NOT:

           /oauth/callback


    h) Do not use Bruno's custom protocol as the IdentityServer Redirect URI

       Bruno uses a custom protocol internally:

           bruno://app/oauth2/callback

       Manual testing confirmed that this protocol was correctly registered
       with Windows. Opening the URI caused Edge to ask whether Bruno should
       be opened, and selecting "Open" launched Bruno.

       However, this does NOT mean that the IdentityServer client RedirectUri
       should simply be changed to:

           bruno://app/oauth2/callback

       The hosted/local callback mechanism is responsible for forwarding the
       OAuth result back to Bruno.

       For our local setup, use:

           http://127.0.0.1:3000/callback


    i) Bruno system browser vs embedded browser

       Bruno provides an option:

           Use system browser for OAuth

       During testing, the system-browser flow reached the hosted callback but
       remained at:

           Redirecting to Bruno...

       For local troubleshooting, disable:

           Use system browser for OAuth

       This allows Bruno's embedded browser to perform the complete OAuth flow:

           Bruno
             |
             v
           IdentityServer Login
             |
             v
           Authentication
             |
             v
           OAuth Consent (if enabled)
             |
             v
           Callback
             |
             v
           Bruno
             |
             v
           API Request

       This successfully completed the OAuth flow during V3 development.


    j) IdentityServer consent

       If the Bruno client has:

           RequireConsent = true

       the IdentityServer consent page is displayed during authentication.

       This is useful for demonstrating the OAuth consent process.

       For troubleshooting, RequireConsent can temporarily be set to false so
       that successful authentication proceeds directly to the callback.

       Consent configuration does not replace or bypass API authorization.


    k) Use the correct test user

       For Customer Onboarding Customer Read testing, use the demo user:

           Username:
               sophie.cs

           Password:
               sophie.cs@bss

       Sophie represents the:

           customer_service_agent

       role.

       When testing a specific Microservice endpoint, make sure that the
       selected demo user's role is appropriate for the operation being tested.


    l) Expected troubleshooting progression

       During the initial Bruno setup, the following problems were encountered:

       1. IdentityServer returned:

              unauthorized_client
              Unknown client or client not enabled

          Cause:
              The Bruno client had not yet been registered in IdentityServer.

          Resolution:
              Register BSS.ApiTesting.Bruno.ClientID.


       2. Bruno/browser remained at:

              Redirecting to Bruno...

          Cause:
              The hosted/system-browser callback did not return control to
              Bruno during local development.

          Resolution:
              Use the local OAuth callback server and/or Bruno's embedded
              browser.


       3. The JWT did not contain:

              customer-onboarding.read

          Cause:
              Bruno's OAuth Scope field did not request the API scope.

          Resolution:
              Explicitly add the required API scope to Bruno's Scope field and
              clear Bruno's OAuth cache before obtaining a new token.


       4. The API subsequently returned:

              401 Unauthorized

          At this point the API was receiving a token, but the token did not
          yet represent the required Customer Onboarding API access.


       5. After requesting customer-onboarding.read, the JWT contained:

              aud:
                  customer-onboarding-api

              scope:
                  customer-onboarding.read

          The API authentication/scope portion of the flow was then working.


       6. The API subsequently returned:

              403 Forbidden

          This indicated that authentication had succeeded but the authenticated
          user did not satisfy the API's authorization requirements.

          Testing with the appropriate Customer Onboarding demo user then
          allowed the request to reach the controller.


    m) Successful end-to-end Bruno flow

       The final successful flow is:

           Bruno
             |
             | Authorization Code + PKCE
             v
           IdentityServer
             |
             | Login as sophie.cs
             v
           OAuth authorization
             |
             | customer-onboarding.read
             v
           Access Token
             |
             +--> aud = customer-onboarding-api
             |
             +--> scope = customer-onboarding.read
             |
             +--> role = customer_service_agent
             |
             v
           Customer Onboarding API
             |
             v
           GET /v1/customers
             |
             v
           CustomerController


    n) General rule for future Microservice APIs

       The same Bruno client can be used to test the other V3 Microservice APIs.

       Only the requested API scope needs to change.

       Examples:

           Customer Onboarding:
               customer-onboarding.read
               customer-onboarding.write

           Customer KYC:
               customer-kyc.read
               customer-kyc.write

           Accounts:
               accounts.read
               accounts.write

           Payments:
               payments.read
               payments.write

       The corresponding API resource/audience must also be present in the
       issued access token.

       Therefore, when troubleshooting a new Microservice API, always verify:

           1. Bruno client is registered.
           2. Redirect URI matches exactly.
           3. PKCE is enabled.
           4. Bruno requests the required scope.
           5. Bruno OAuth cache is cleared after configuration changes.
           6. The access token contains the expected scope.
           7. The access token contains the expected audience.
           8. The appropriate demo user is being used.