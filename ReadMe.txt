Enterprise Web Platform V3 - Local Development Guide
====================================================

This file is ONLY the local-development guide: host names, HTTPS, databases, Kafka, building, starting, troubleshooting and API testing.

What the platform is, how it is designed and what each component must do are documented elsewhere - see the "Documentation map" in README.md. Nothing in this file repeats those documents.


0) Contents:

	1) Local Development Hostnames and HTTPS
	2) Local URLs
	3) First-Time Setup
	4) Starting the Platform
	5) Troubleshooting
	6) Bruno API Testing


1) Local Development Hostnames and HTTPS:

	Each tier uses its own host name. Browser cookies are scoped by host name, not by port. When several applications share "localhost", their authentication, correlation, nonce, session and BFF cookies are all sent to each other, and the request headers grow until the server answers:

		HTTP 400 - Request Too Long
		The size of the request headers is too long.

	Giving each tier its own host name (all resolving to 127.0.0.1) gives each application an independent cookie namespace.

	a) Open "C:\Windows\System32\drivers\etc\hosts" as Administrator and append:

		127.0.0.1    idp.dev.localhost
		127.0.0.1    shell.dev.localhost

		127.0.0.1    customer.dev.localhost
		127.0.0.1    customer-api.dev.localhost

		127.0.0.1    kyc.dev.localhost
		127.0.0.1    kyc-api.dev.localhost

		127.0.0.1    documents-management-api.dev.localhost

		127.0.0.1    compliance.dev.localhost

		127.0.0.1    accounts.dev.localhost
		127.0.0.1    accounts-api.dev.localhost
		127.0.0.1    payments.dev.localhost
		127.0.0.1    payments-api.dev.localhost

	b) Check each name with "ping <hostname>"; each must resolve to 127.0.0.1.

	c) ASP.NET Core applications run on Kestrel using the "https" launch profile (not IIS Express). The ASP.NET Core development certificate covers "*.dev.localhost".

		This applies to: Shell BFF, Customer Onboarding BFF and API, Customer KYC API, Documents Management API, and later the Accounts BFF/API and Payments API.

	d) Node.js applications need the development certificate exported as a PFX file:

		- Customer KYC BFF (NestJS)  - see src\Microservices\CustomerKyc\BFF.Web\README.md
		- Payments BFF (NestJS)      - when implemented

	NOTE:
		Kestrel is used deliberately in V3 so that the local topology is explicit and consistent. The IDP is self-hosted and opens its own console window.


2) Local URLs:

	Live:

		IDP								https://idp.dev.localhost:44392
		Shell BFF (serves Shell SPA)	https://shell.dev.localhost:44367
		Customer Onboarding BFF (MFE)	https://customer.dev.localhost:44311
		Customer Onboarding API			https://customer-api.dev.localhost:44363
		Customer KYC BFF (MFE)			https://kyc.dev.localhost:33800
		Customer KYC API				https://kyc-api.dev.localhost:44305
		Documents Management API		https://documents-management-api.dev.localhost:49486
		Kafka UI						http://localhost:8080

	Reserved (not implemented yet):

		Compliance BFF (MFE)			https://compliance.dev.localhost:44399
		Accounts BFF					https://accounts.dev.localhost:45456
		Accounts API					https://accounts-api.dev.localhost:48486
		Payments BFF					https://payments.dev.localhost:44388
		Payments API					https://payments-api.dev.localhost:44488

	The Shell Menu DB seed registers these same URLs.


3) First-Time Setup:

	a) Install: .NET 10 SDK, Node.js 18+, pnpm, Docker Desktop.

	b) Configure the hosts file (section 1).

	c) Trust the ASP.NET Core development certificate:

			dotnet dev-certs https --check
			dotnet dev-certs https --trust

	d) Start PostgreSQL, Kafka and Kafka UI:

			docker compose up -d

		PostgreSQL listens on localhost:5432 (user "postgres", password "admin").
		Kafka listens on localhost:9092.

		IMPORTANT:
			These are development-only credentials. Real environments must use a secret store.

	e) Create the databases and run their scripts.

		Each database has ONE complete script. It drops and recreates that database's tables and seeds its reference/demo data:

			EwpIdentityAccessDb			src\IDP\IdentityAccessDB\IdentityAccessDb.sql
			EwpBssShellDb				src\Shell\MenuDB\EwpBssShellDb.sql
			EwpCustomerDb				src\Microservices\CustomerOnboarding\API\CustomerDB\EwpCustomerDb.sql
			EwpKycDb					src\Microservices\CustomerKyc\API\KycDb\EwpKycDb.sql
			EwpDocumentsManagementDb	src\Microservices\DocumentsManagement\API\DocumentMgmtDB\EwpDocumentsManagementDb.sql

		WARNING:
			Running a script erases that database's data. Each script must run while connected to ITS OWN database.

		With PostgreSQL installed locally (Windows service), from the repository root in PowerShell:

			$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
			$env:PGPASSWORD = 'admin'

			# Create any database that does not exist yet (the quotes keep the mixed-case names used by the connection strings):
			& $psql -h localhost -U postgres -c 'CREATE DATABASE "EwpBssShellDb";'

			# Run a script against its database:
			& $psql -h localhost -U postgres -d EwpBssShellDb -v ON_ERROR_STOP=1 -f .\src\Shell\MenuDB\EwpBssShellDb.sql

		With the docker-compose PostgreSQL container instead, prefix the same psql commands with "docker exec -i ewp-postgres" and pipe the script in, e.g.:

			Get-Content .\src\Shell\MenuDB\EwpBssShellDb.sql -Raw | docker exec -i ewp-postgres psql -U postgres -d EwpBssShellDb -v ON_ERROR_STOP=1

		(The container creates EwpIdentityAccessDb automatically.) pgAdmin's Query Tool, opened on the right database, works as well.

		After recreating the IDP database, sign out and sign in again so that new claims are issued.

	f) Create the Kafka topics.

		docker-compose.yml disables automatic topic creation, so every topic must be created explicitly (PowerShell):

			$topics = @(
				"customer.created",
				"onboarding.application.submitted",
				"onboarding.application.status.changed",
				"kyc.case.created",
				"kyc.case.approved",
				"kyc.case.rejected",
				"kyc.identity.verification.approved",
				"kyc.identity.verification.rejected",
				"kyc.document.verification.approved",
				"kyc.document.verification.rejected",
				"customer-onboarding.kyc-subscriber.dlq"
			)

			foreach ($t in $topics) {
				docker exec ewp-kafka /opt/kafka/bin/kafka-topics.sh --bootstrap-server localhost:9092 --create --if-not-exists --topic $t --partitions 1 --replication-factor 1
			}

		The authoritative topic list is doc\Integration-Event-Catalogue.md.

		IMPORTANT - recreating databases means recreating topics:
			Database IDs restart at 1 when a database is recreated, but Kafka keeps the old messages, and consumer groups would replay
			them against the new data (e.g. an old "application 1" event applied to a new application 1). Whenever you recreate the
			Customer Onboarding or KYC database, delete and recreate the topics above (Kafka UI, or kafka-topics.sh --delete followed by the
			creation loop above). Deleting a topic also discards the consumer groups' offsets for it.

	g) Build and export the front ends:

			.\CompileAndExportBFFClients_V3.ps1

		This builds the Shell SPA, the Customer Onboarding MFE, the Customer KYC MFE and the KYC NestJS BFF, and copies each static export to where its BFF serves it.
		(CompileAndExportBFFClients.ps1 is the older V2 script; it refers to V2 projects and is not used in V3.)

	h) Configure the Customer KYC BFF: its environment variables and PFX certificate are described in src\Microservices\CustomerKyc\BFF.Web\README.md (runnow.bat sets them and starts the BFF).

	NOTE - secrets:
		Each component reads its own client secrets from its own configuration; nothing is compiled into Common.Landscape any more.
		Development values: appsettings.Development.json of the IDP, Shell BFF, Customer Onboarding BFF and CustomerKycSubscriber, and runnow.bat of the KYC BFF.
		A component refuses to start when a secret is missing. Outside Development, supply them as environment variables or from a secret store.

	i) Open EnterpriseWebPlatform.BSS.sln in Visual Studio and restore the NuGet packages.


4) Starting the Platform:

	a) Visual Studio: use the multi-project launch profile in EnterpriseWebPlatform.BSS.slnLaunch. It starts:

		IDP, Documents Management API, Customer Onboarding API, Customer KYC API, CustomerOutboxPublisher, CustomerKycSubscriber, CustomerOnboardingKycSubscriber, Shell BFF and Customer Onboarding BFF.

		Every publisher and subscriber is a console (generic host) application. Several instances of each may run in parallel:
		publishers claim Outbox rows with FOR UPDATE SKIP LOCKED, subscribers share one Kafka consumer group per subscriber (one
		partition = one instance), and consumers are idempotent. Topics are created with 1 partition, so extra subscriber instances
		are hot standbys until the partition count is raised.

	b) The Customer KYC BFF is NestJS and is NOT in that profile. Start it separately:

			cd src\Microservices\CustomerKyc\BFF.Web
			pnpm run start

	c) Browse to https://shell.dev.localhost:44367 and sign in. The demo users, their roles and the password convention are listed in src\IDP\doc\IDP-Requirements.md (section 6).

	d) A typical end-to-end check:

		- Sign in as sophie.cs (branch SYD001, Sydney), open Onboarding Applications, create a customer with a residential address in Sydney, AU, attach both PDFs and submit.
		- Optional ABAC check: sign in as mia.cs (MEL001, Melbourne) - Sophie's customer is not visible, and a Sydney address is refused with 403.
		- In Kafka UI, check that customer.created and the onboarding.application.* topics received messages.
		- Sign out, sign in as ethan.kyc or noah.kyc, open KYC Cases, and decide the identity and document stages.
		- In Kafka UI, check the kyc.* topics.
		- Back as sophie.cs, the application's status has moved SUBMITTED -> KYC_IN_PROGRESS (when the KYC case opened) -> KYC_COMPLETED
		  (when KYC approved), recorded by CustomerOnboardingKycSubscriber. EwpCustomerDb.inbox_messages holds one row per KYC event processed.


5) Troubleshooting:

	a) "HTTP 400 - Request Too Long" / "The size of the request headers is too long":

		- Use the *.dev.localhost host names, never localhost.
		- Do not work around this by raising the server header limits.
		- Clear the cookies of the affected host if stale OIDC correlation or nonce cookies remain.
		- Restart the application and sign in again.

	b) HTTPS certificate warning for a *.dev.localhost URL:

		- ASP.NET Core: make sure the "https" launch profile is used, not IIS Express.
		- Run "dotnet dev-certs https --check", and "--trust" if needed.
		- Node.js (KYC BFF): re-export the PFX and check the KYC_BFF_TLS_PFX_* settings.

	c) A host name does not resolve: check the hosts file and ping it (section 1).

	d) The menu does not appear:

		- Check that PostgreSQL is running and EwpBssShellDb exists with its snake_case tables.
		- Check that the signed-in user has role claims, and that the role codes match the menu_items_and_roles rows.
		- Remember that menu visibility is not authorization.

	e) Expected menu items are missing after a role change: sign out and in again, so new role claims are issued.

	f) Nothing appears in KYC after an onboarding submission:

		- Check that the Kafka topics exist (3f); auto-creation is disabled.
		- Check that CustomerOutboxPublisher is running, and look at outbox_messages.published_at and last_error in EwpCustomerDb.
		- Check that CustomerKycSubscriber is running. A message it cannot process currently stops the worker; its console shows the cause.

	g) The KYC BFF shows "documents cannot be shown" or 403 for evidence:

		- Documents are branch-scoped. The officer's branch (IDP employment profile) must match the branch of the agent who uploaded them.
		- Documents uploaded before branch scoping existed have no branch and are inaccessible; re-submit the onboarding.
		- Sign out and in again after IDP changes, so the "organization" claims are issued.

	h) Visual Studio uses a stale launch profile: close VS, delete the solution's ".vs" folder, reopen, and check the start-up profile.


6) Bruno API Testing:

	Bruno can call the Microservice APIs directly using OAuth 2.0 Authorization Code + PKCE against the IDP.

	a) IDP client:

		Client ID:			BSS.ApiTesting.Bruno.ClientID
		Grant:				Authorization Code, PKCE required, no client secret (public client)
		Identity scopes:	openid profile email roles
		API scopes:			customer-onboarding.read/.write, customer-kyc.read/.write, documents-management.read/.write, accounts.*, payments.*

		Redirect URIs:		http://127.0.0.1:3000/callback (local callback server, recommended - see c)
							https://oauth.usebruno.com/callback

		The Bruno client is registered ONLY when the IDP runs in the Development environment.

	b) Bruno OAuth settings:

		Authorization URL:	https://idp.dev.localhost:44392/connect/authorize
		Access Token URL:	https://idp.dev.localhost:44392/connect/token
		Client ID:			BSS.ApiTesting.Bruno.ClientID
		Client Secret:		(empty)
		Use PKCE:			enabled
		Callback URL:		must exactly match a registered redirect URI

	c) Callback: the hosted Bruno callback can get stuck at "Redirecting to Bruno...". Use the local callback server instead:

			npx @usebruno/oauth2-callback-server --port 3000

		Use the exact URL it reports (it was "/callback", not "/oauth/callback").
		Do not register Bruno's custom protocol (bruno://app/oauth2/callback) as a redirect URI.
		If the system browser still hangs, turn off "Use system browser for OAuth" so that Bruno's embedded browser completes the flow.

	d) Scope: the IDP's AllowedScopes only permit scopes; Bruno's "Scope" field decides what is actually requested. Request the API scope explicitly, for example:

			openid profile email roles customer-onboarding.read

	e) Clear Bruno's OAuth token cache ("Clear Cache") after any change to the client, the scopes or the IDP, and then sign in again. Issued tokens do not change.

	f) Before debugging an API, decode the token and check:

			iss		https://idp.dev.localhost:44392
			aud		the API resource (e.g. customer-onboarding-api)
			scope	the requested API scope
			role	a role the endpoint accepts

	g) Choose a demo user whose role fits the endpoint (e.g. sophie.cs for Customer Onboarding reads). Users and passwords: src\IDP\doc\IDP-Requirements.md, section 6.

	h) Reading the responses:

			unauthorized_client		The client is not registered or not enabled in the IDP.
			Stuck on "Redirecting"	Use the local callback server or the embedded browser (c).
			401 Unauthorized		The token lacks the API's audience or scope - fix the Bruno Scope field and clear the cache.
			403 Forbidden			Authenticated, but the user or role is not authorized for the endpoint - use an appropriate demo user.

	i) Checklist for a new API:

		1. Bruno client registered.
		2. Redirect URI matches exactly.
		3. PKCE enabled.
		4. Bruno requests the API scope.
		5. Cache cleared after changes.
		6. Token has the expected scope.
		7. Token has the expected audience.
		8. Suitable demo user.
