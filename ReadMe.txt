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
		127.0.0.1    compliance-api.dev.localhost

		127.0.0.1    accounts.dev.localhost
		127.0.0.1    accounts-api.dev.localhost

		127.0.0.1    notifications-api.dev.localhost

		127.0.0.1    payments.dev.localhost
		127.0.0.1    payments-api.dev.localhost

		127.0.0.1    audit-api.dev.localhost

	b) Check each name with "ping <hostname>"; each must resolve to 127.0.0.1.

	c) ASP.NET Core applications run on Kestrel using the "https" launch profile (not IIS Express). The ASP.NET Core development certificate covers "*.dev.localhost".

		This applies to every ASP.NET Core application: the Shell BFF, the Customer Onboarding, Compliance, Accounts and Payments BFFs, every API, the workers and the simulators.

	d) Node.js applications need the development certificate exported as a PFX file:

		- Customer KYC BFF (NestJS)  - see src\Microservices\CustomerKyc\BFF.Web\README.md

	NOTE:
		Kestrel is used deliberately in V3 so that the local topology is explicit and consistent. The IDP is self-hosted and opens its own console window.


2) Local URLs:

	Live:

		IDP								https://idp.dev.localhost:46392
		Shell BFF (serves Shell SPA)	https://shell.dev.localhost:46367
		Customer Onboarding BFF (MFE)	https://customer.dev.localhost:46311
		Customer Onboarding API			https://customer-api.dev.localhost:46363
		Customer KYC BFF (MFE)			https://kyc.dev.localhost:33800
		Customer KYC API				https://kyc-api.dev.localhost:46305
		Documents Management API		https://documents-management-api.dev.localhost:49486
		Compliance BFF (MFE)			https://compliance.dev.localhost:46399
		Compliance API					https://compliance-api.dev.localhost:46306
		Screening Provider Simulator	https://localhost:46366   (stands in for an external AML / sanctions vendor)
		Accounts BFF (MFE)				https://accounts.dev.localhost:45456
		Accounts API					https://accounts-api.dev.localhost:48486
		Core Banking Simulator			https://localhost:46376   (stands in for the bank's core-banking system)
		Notifications API				https://notifications-api.dev.localhost:46377   (no UI of its own; the Shell proxies it)
		Payments API					https://payments-api.dev.localhost:44488   (the payment saga ORCHESTRATOR lives here)
		Payment Network Simulator		https://localhost:46386   (stands in for an NPP-style payment network)
		Payments BFF (MFE)				https://payments.dev.localhost:46388
		Audit API						https://audit-api.dev.localhost:46378   (the tamper-evident audit trail; no UI yet)
		Kafka UI						http://localhost:8080

	The Shell Menu DB seed registers these same URLs.

	PORTS: never use 44300-44399. Visual Studio 2026 reserves ports from that range (the IIS Express SSL pool) for its
	web tooling, e.g. Browser Link, through Windows HTTP.sys - a different one at each start - and Kestrel then fails with
	"An attempt was made to access a socket in a way forbidden by its access permissions". The platform's ports were moved
	from 443xx to 463xx for this reason. Check a suspect port with:  netsh http show servicestate view=requestq | Select-String <port>


3) First-Time Setup:

	a) Install: .NET 10 SDK, Node.js 18+, pnpm, PostgreSQL 18, a Java runtime (21+) and Apache Kafka 4.x (KRaft) at C:\Kafka.

	b) Configure the hosts file (section 1).

	c) Trust the ASP.NET Core development certificate:

			dotnet dev-certs https --check
			dotnet dev-certs https --trust

	d) Start PostgreSQL and Kafka:

			PostgreSQL listens on localhost:5432 (user "postgres", password "admin").
			Kafka: C:\Kafka\StartKafka.bat (single KRaft node; clients on localhost:9092).

		Secure Kafka once (SCRAM users, per-component ACLs, explicit topics) - see kafka\README.md:

			.\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare     (Kafka running)
			... stop Kafka ...
			.\kafka\Setup-KafkaSecurity.ps1 -Phase Secure      (Kafka stopped)
			... start Kafka ...
			.\kafka\Setup-KafkaSecurity.ps1 -Phase Acls        (Kafka running)

		Optional: Kafka UI (read-only browser) - installation steps in kafka\README.md section 2.

		IMPORTANT:
			These are development-only credentials. Real environments must use a secret store.

	e) Create the databases and run their scripts.

		Each database has ONE complete script. It drops and recreates that database's tables and seeds its reference/demo data:

			EwpIdentityAccessDb			src\IDP\IdentityAccessDB\IdentityAccessDb.sql
			EwpBssShellDb				src\Shell\MenuDB\EwpBssShellDb.sql
			EwpCustomerDb				src\Microservices\CustomerOnboarding\API\CustomerDB\EwpCustomerDb.sql
			EwpKycDb					src\Microservices\CustomerKyc\API\KycDb\EwpKycDb.sql
			EwpDocumentsManagementDb	src\Microservices\DocumentsManagement\API\DocumentMgmtDB\EwpDocumentsManagementDb.sql
			EwpComplianceDb				src\Microservices\Compliance\API\ComplianceDb\EwpComplianceDb.sql
			EwpAccountsDb				src\Microservices\Accounts\API\AccountsDb\EwpAccountsDb.sql
			EwpNotificationsDb			src\Microservices\Notifications\API\NotificationsDb\EwpNotificationsDb.sql
			EwpPaymentsDb				src\Microservices\Payments\API\PaymentsDb\EwpPaymentsDb.sql
			EwpAuditDb					src\Microservices\Audit\API\AuditDb\EwpAuditDb.sql   (append-only; recreating it
										empties the trail - reset the group audit.trail-subscriber to --to-earliest to re-record)
			EwpBffStateDb				db\EwpBffStateDb.sql   (sessions and Data Protection keys of the .NET BFFs;
										run it AFTER Apply-EwpServiceDbUsers.ps1 - it grants each BFF user its own schema)

		Upgrading an EXISTING EwpIdentityAccessDb (keeps its users; adds the schema identity_server - Duende's refresh tokens,
		PAR requests, signing keys and the IDP's key ring): src\IDP\IdentityAccessDB\Upgrade-6b-OperationalStore.sql,
		then run db\Apply-EwpServiceDbUsers.ps1 again (it grants ewp_idp the new schema).

		Upgrading an EXISTING EwpAccountsDb for Payments (keeps its accounts; adds balances and funds holds, and gives
		existing accounts the demo opening deposit): src\Microservices\Accounts\API\AccountsDb\Upgrade-5a-Funds.sql

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

		Service database users (least privilege) - run ONCE after the databases exist (and again only if you drop a database itself):

			.\db\Apply-EwpServiceDbUsers.ps1

		(It checks that every database exists and runs db\EwpServiceDbUsers.sql with psql. The SQL file cannot run in
		pgAdmin's Query Tool - it uses psql's \connect; pgAdmin's Tools > PSQL Tool with \i <path> works too.)

		Each service connects with its own user (ewp_idp, ewp_shell, ewp_customer_onboarding_api, ewp_customer_outbox_relay,
		ewp_kyc_api, ewp_documents_api, ewp_compliance_api, ewp_accounts_api, ewp_notifications_api, ewp_payments_api, ewp_audit_api (SELECT and INSERT only); the BFFs
		ewp_co_bff, ewp_compliance_bff, ewp_accounts_bff, ewp_payments_bff and ewp_shell each own one schema of EwpBffStateDb) that may read and write ITS OWN database only: no DDL and no access to other
		services' databases; the CO outbox relay may only read and update outbox_messages. The database scripts above
		still run as postgres; the grants survive re-running them. Without this step the services cannot connect.

		The relay's grant is per TABLE, so EwpCustomerDb.sql re-applies it (watch for its NOTICE / WARNING line).
		Symptom if it is missing: CO outbox rows stay unpublished (attempt_count 0) and KYC never opens a case.
		Repair: re-run EwpServiceDbUsers.sql - it is idempotent and safe to run at any time.

	f2) Content-Security-Policy, rate limiting and dependency scanning.

		Rate limiting (OWASP API4): every .NET BFF and API limits requests per signed-in person (600 per minute, of which 60 may be
		changes - payments, decisions, uploads), per machine client (3,000) and per IP address before sign-in (120). Over the limit
		the answer is 429 with Retry-After and a message the screens show. Configuration section "RateLimiting" (Enabled,
		PerPersonPerMinute, ChangesPerPersonPerMinute, AnonymousPerIpPerMinute, PerMachineClientPerMinute). To see it, from PowerShell:

			1..130 | ForEach-Object { curl.exe -sk -o NUL -w "%{http_code}`n" https://payments.dev.localhost:46388/api/auth/user } | Group-Object

		shows about 120 x 401 (not signed in) and then 429.

		The Shell, CO BFF, KYC BFF, Compliance BFF, Accounts BFF and Payments BFF send a strict CSP: scripts only from the BFF itself plus the SHA-256 hashes of the
		exported pages' inline scripts, computed at startup from the files served. After re-exporting the MFEs
		(CompileAndExportBFFClients_V3.ps1), restart the BFFs so the hashes are recomputed.
		If a page is blocked by CSP, switch to report-only (violations appear in the browser console) while investigating:
			Shell / CO BFF / Compliance BFF / Accounts BFF / Payments BFF:  appsettings: "Security": { "CspReportOnly": true }
			KYC BFF:         environment variable KYC_BFF_CSP_REPORT_ONLY=true

		Known-vulnerability scan of all .NET and npm dependencies (fails only on deployed dependencies):

			.\Scan-Dependencies.ps1            # or -FailOn critical

	f) Kafka topics.

		Topic auto-creation is disabled, so every topic is created explicitly: Setup-KafkaSecurity.ps1 -Phase Prepare creates
		all of them (event topics and the dead-letter topics). To create topics later, as the admin user:

			C:\Kafka\bin\windows\kafka-topics.bat --bootstrap-server localhost:9092 --command-config C:\Kafka\config\admin.properties ^
			    --create --if-not-exists --topic <name> --partitions 1 --replication-factor 1

		The authoritative topic list is doc\Integration-Event-Catalogue.md.

		IMPORTANT - recreating databases means recreating topics:
			Database IDs restart at 1 when a database is recreated, but Kafka keeps the old messages, and consumer groups would replay
			them against the new data (e.g. an old "application 1" event applied to a new application 1). Whenever you recreate the
			Customer Onboarding, KYC or Compliance database, delete and recreate the topics above (Kafka UI, or kafka-topics.sh --delete followed by the
			creation above, or simply re-run Setup-KafkaSecurity.ps1 -Phase Prepare). Deleting a topic also discards the consumer
		groups' offsets for it.

	g) Build and export the front ends:

			.\CompileAndExportBFFClients_V3.ps1

		This builds the Shell SPA, the Customer Onboarding MFE, the Customer KYC MFE, the KYC NestJS BFF, the Compliance MFE, the Accounts MFE and the Payments MFE,
		and copies each static export to where its BFF serves it.
		Restart the Shell, CO BFF, KYC BFF, Compliance BFF, Accounts BFF and Payments BFF afterwards (the KYC BFF runs outside Visual Studio - easy to forget): their Content-Security-Policy hashes are computed at startup from the exported pages.

	h) Configure the Customer KYC BFF: its environment variables and PFX certificate are described in src\Microservices\CustomerKyc\BFF.Web\README.md (runnow.bat sets them and starts the BFF).

	NOTE - secrets:
		Each component reads its own client secrets from its own configuration; nothing is compiled into Common.Landscape any more.
		Development values (client secrets, Kafka passwords, the simulators' API keys): the appsettings.Development.json of the
		component that uses them - the IDP, the Shell BFF, every .NET MFE BFF and API, every relay and subscriber, and the three
		simulators - and runnow.bat of the KYC BFF.
		A component refuses to start when a secret is missing. Outside Development, supply them as environment variables or from a secret store.
		Outside Development, the IDP and every .NET BFF also need DataProtection:CertificatePath (and CertificatePassword): the
		certificate that encrypts their key ring in the database. In Development on Windows, DPAPI is used instead.

	i) Open EnterpriseWebPlatform.BSS.slnx in Visual Studio and restore the NuGet packages.
	   (.slnx is the XML solution format: Visual Studio 2026, or Visual Studio 2022 17.14+; 17.10-17.13 need the preview
	   feature "Use Solution File Persistence Model". The dotnet CLI needs SDK 9.0.200+.)


4) Starting the Platform:

	a) Visual Studio: use the multi-project launch profile in EnterpriseWebPlatform.BSS.slnLaunch. It starts:

		IDP, Documents Management API, Customer Onboarding API, Customer KYC API, CustomerOutboxPublisher, KycCaseOpeningSubscriber,
		OnboardingOutcomeSubscriber, Compliance API, ComplianceCaseOpeningSubscriber, DocumentInvalidationSubscriber, Accounts API,
		AccountApplicationOpeningSubscriber, Notifications API, NotificationsSubscriber, Audit API, Payments API, AccountsCommandSubscriber,
		PaymentsSagaReplySubscriber, Screening Provider Simulator, Core Banking Simulator, Payment Network Simulator, Shell BFF,
		Customer Onboarding BFF, Compliance BFF, Accounts BFF and Payments BFF.

		Every publisher and subscriber is a console (generic host) application. Several instances of each may run in parallel:
		publishers claim Outbox rows with FOR UPDATE SKIP LOCKED, subscribers share one Kafka consumer group per subscriber (one
		partition = one instance), and consumers are idempotent. Topics are created with 1 partition, so extra subscriber instances
		are hot standbys until the partition count is raised.

	b) The Customer KYC BFF is NestJS and is NOT in that profile. Start it separately:

			cd src\Microservices\CustomerKyc\BFF.Web
			pnpm run start

	c) Browse to https://shell.dev.localhost:46367 and sign in. The demo users, their roles and the password convention are listed in src\IDP\doc\IDP-Requirements.md (section 6).

	d) A typical end-to-end check:

		- Sign in as sophie.cs (branch SYD001, Sydney), open Onboarding Applications, create a customer with a residential address in Sydney, AU, attach both PDFs and submit.
		- Optional ABAC check: sign in as mia.cs (MEL001, Melbourne) - Sophie's customer is not visible, and a Sydney address is refused with 403.
		- In Kafka UI, check that customer.created and the onboarding.application.* topics received messages.
		- Sign out, sign in as ethan.kyc or noah.kyc, open KYC Cases, and decide the identity and document stages.
		- In Kafka UI, check the kyc.* topics.
		- Back as sophie.cs, the application's status has moved SUBMITTED -> KYC_IN_PROGRESS (when the KYC case opened) -> KYC_COMPLETED
		  (when KYC approved), recorded by OnboardingOutcomeSubscriber. EwpCustomerDb.inbox_messages holds one row per KYC event processed.
		- KYC approval opens a Compliance case (ComplianceCaseOpeningSubscriber): the application moves to COMPLIANCE_IN_PROGRESS and
		  EwpComplianceDb.compliance_cases has a row that the Compliance API screens within seconds (status SCREENING -> UNDER_REVIEW,
		  risk LOW / MEDIUM / HIGH by the customer number's last digit: 9 = MATCH/HIGH, 7-8 = POTENTIAL_MATCH/MEDIUM, else CLEAR/LOW).
		- Sign in as olivia.compliance (SYD001, clearance 4), open Compliance Monitor, open the case and approve, reject or put it on
		  hold. She is neither the onboarding initiator nor a KYC decider (separation of duties); a HIGH-risk case needs clearance 5
		  to approve, so she can only reject or hold one; grace.compliance (clearance 5, senior) can approve it. The decision moves
		  the application to COMPLIANCE_COMPLETED or REJECTED. (The same actions are available through the API with Bruno, section 6.)
		- Compensation: when KYC or Compliance REJECTS an application, its two evidence documents become INVALIDATED in
		  EwpDocumentsManagementDb.documents (status, invalidated_at, invalidation_reason) - retained, not deleted - via the
		  onboarding.application.rejected topic and the DocumentInvalidationSubscriber.
		- Compliance approval opens an ACCOUNT APPLICATION (AccountApplicationOpeningSubscriber): the application moves to
		  ACCOUNT_OPENING_IN_PROGRESS and EwpAccountsDb.account_applications has a PENDING_REVIEW row. jack.accounts (SYD001) decides
		  it in the Shell: Account Applications -> open the application -> choose the product -> Approve and open account.
		  (Through the API with Bruno, section 6, scope accounts.read accounts.write: POST .../v1/accounts/applications/{id}/approve.)
		  He is neither the initiator nor the Compliance approver (separation of duties). On approval the core-banking system
		  opens the account within seconds (EwpAccountsDb.accounts: BSB 062-000 + account number) and the onboarding is COMPLETED.
		  A rejection (with remarks) ends it REJECTED and invalidates the evidence, as above.

	g2) Demonstrating an unreliable external provider (Screening Provider Simulator, localhost only):

			$sim = 'https://localhost:46366/admin/behaviour'
			Invoke-RestMethod $sim -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Healthy
			Invoke-RestMethod $sim -Method Put -ContentType 'application/json' -Body '{"behaviour":"Healthy","forcedOutcome":"MATCH"}'
			Invoke-RestMethod $sim                                                                              # current behaviour

		While the provider is Down / Failing / Slow, new Compliance cases stay in SCREENING (a provider failure is never a pass),
		are retried with back-off (15 s doubling to 5 min), the circuit breaker opens after repeated failures (Compliance API log:
		"circuit open"), and /health/ready of the Compliance API reports Degraded once a case waits more than 2 minutes.
		Set it back to Healthy and the waiting cases are screened automatically.

	g3) Demonstrating an unreliable core-banking system (Core Banking Simulator, localhost only):

			$cbs = 'https://localhost:46376/admin/behaviour'
			Invoke-RestMethod $cbs -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Refusing / Healthy

		While Down / Failing / Slow, approved account applications stay OPENING and are retried with back-off; every request
		carries an Idempotency-Key (the ApplicationRef), so a retry after a lost answer never opens a second account. After 6
		failures - or at once when Refusing (HTTP 422) - the application is FAILED and AccountOpeningFailed is published;
		Customer Onboarding compensates: COMPENSATING -> REJECTED (RejectedBy ACCOUNT_OPENING), evidence INVALIDATED, customer PROSPECT. Back to Healthy, waiting accounts are opened automatically.

	g4) Payments - the ORCHESTRATED saga:

		A new account starts with a demo balance of 5,000.00 AUD (Accounts:DemoOpeningDeposit). As sophie.cs, open Payments -> New Payment:
		find the customer, pick the paying account, enter the payee (BSB, account, name - Confirmation of Payee checks it), the amount, then
		Transfer. The status page follows the PaymentSaga live: ReserveFunds (Accounts) -> send to the Payment Network Simulator -> SettleFunds.
		Confirmation of Payee (simulator): an account number ending in 0 = no match, in 9 = close match, otherwise match.
		Above 1,000.00 the payment waits for approval: emily.payments (SYD001, clearance 4) is notified, opens Payments -> Payment
		Approvals and approves (sent, completed) or rejects with remarks (funds released, REJECTED). Nobody approves their own payment;
		clearance 4 approves up to 100,000.
		Compensation failure and recovery: stop the AccountsCommandSubscriber, then pay $100. In Development the saga gives up the
		unanswered reservation after about a minute, releases - also unanswered - and after about 2.5 minutes the payment is
		COMPENSATION_FAILED. daniel.ops (operations, all branches) sees it in Payments -> Payment Processing Monitor, opens it and
		presses Retry release; start the subscriber again and the payment ends FAILED, funds released.
		Details: src\Microservices\Payments\API\README.md. To see compensation, pay to a BSB starting with 999 (the network refuses it) or:

			$pns = 'https://localhost:46386/admin/behaviour'
			Invoke-RestMethod $pns -Method Put -ContentType 'application/json' -Body '{"behaviour":"Refusing"}'   # or Down / Failing / Slow / Healthy

	e) Health endpoints (as used by Kubernetes liveness / readiness probes):

			APIs, BFFs, IDP:              https://<host>/health/live   and   https://<host>/health/ready
			CustomerOutboxPublisher:      http://localhost:5101/health/live | /health/ready
			KycCaseOpeningSubscriber:     http://localhost:5102/health/live | /health/ready
			OnboardingOutcomeSubscriber:  http://localhost:5103/health/live | /health/ready
			ComplianceCaseOpeningSubscriber: http://localhost:5104/health/live | /health/ready
			DocumentInvalidationSubscriber:  http://localhost:5105/health/live | /health/ready
			AccountApplicationOpeningSubscriber: http://localhost:5106/health/live | /health/ready
			NotificationsSubscriber:      http://localhost:5107/health/live | /health/ready
			AccountsCommandSubscriber:    http://localhost:5108/health/live | /health/ready
			PaymentsSagaReplySubscriber:  http://localhost:5109/health/live | /health/ready

		live  = the process (and its background loop) is working; 503 means "restart it".
		ready = its dependencies are reachable (database; for a subscriber, its Kafka consumer group). "Degraded" (still 200)
		        means "working, but look": an Outbox relay with parked or old messages, or a subscriber retrying a message.

	f) Optional - view distributed traces (OpenTelemetry):

		Every .NET component creates W3C trace context and carries it across HTTP calls, the Outbox and Kafka ("traceparent" header),
		so one onboarding is one trace: CO BFF -> CO API -> relay -> KycCaseOpeningSubscriber -> KYC API -> ... -> CO API.
		Spans are exported only when an OTLP endpoint is configured. With Jaeger (single native binary, no Docker needed):

			1. Download the Windows build from https://www.jaegertracing.io/download/ and run:  .\jaeger.exe
			   (it accepts OTLP on localhost:4317 / 4318 and serves its UI on http://localhost:16686)
			2. Point every component at it (once, as a user environment variable), then restart Visual Studio:
			       setx OTEL_EXPORTER_OTLP_ENDPOINT http://localhost:4317
			3. Run an onboarding, open http://localhost:16686, choose service "customer-onboarding-bff" and open the trace.

		Alternative: the .NET Aspire dashboard (docker run -p 18888:18888 -p 4317:18889 mcr.microsoft.com/dotnet/aspire-dashboard).
		Log lines carry the same TraceId, so a log entry can be matched to its trace.
		The KYC BFF (NestJS) is not instrumented yet: a KYC officer's decision starts a new trace at the KYC API.

	h) Logs and metrics (Serilog, Prometheus):

		Logs - every .NET component logs through Serilog: one line per HTTP request (method, path without the query string,
		status, duration, the caller's subject ID or client ID) and every entry with its service name and trace ID.
		Development: readable text, e.g.
			[18:06:21 INF] HTTP GET /v1/notifications responded 401 in 8 ms  <Serilog.AspNetCore.RequestLoggingMiddleware>  trace=f26d82e9...
		Elsewhere: one JSON object per line (@t, @l, @m, @tr = trace ID, service, ...), ready for a log shipper.
		Force either with "Observability": { "LogFormat": "Json" } (or "Text"). Levels still come from "Logging:LogLevel".

		Metrics - every .NET component serves a Prometheus scrape endpoint, GET /metrics (workers on their health port):
			https://payments-api.dev.localhost:44488/metrics          (an API; every BFF and API the same)
			http://localhost:5109/metrics                              (a worker, e.g. PaymentsSagaReplySubscriber)
		What is in it: HTTP requests and durations (http_server_request_duration_seconds), rate-limited requests
		(aspnetcore_rate_limiting_*), sign-ins and authorization decisions, outgoing calls, retries and circuit breakers
		(polly_*), database connections (npgsql_*), GC / CPU / memory (dotnet_*), and EWP's own:
			ewp_messaging_published_total{topic,outcome}    Kafka messages published (ok / failed), dead-letter writes included
			ewp_messaging_consumed_total{topic,outcome}     messages handled by a subscriber (processed / dead_lettered / retried)
			ewp_health_status{check}                        each health check: 2 healthy, 1 degraded, 0 unhealthy
			ewp_health_value{check,key}                     the numbers the checks report, e.g. the payment sagas
			                                                (running, overdue, compensationFailed) and the Outbox backlog
		The health metrics are refreshed every 30 seconds. Outside Development /metrics is off unless
		"Observability:Metrics:Enabled" is true; it is anonymous and must be reachable from the cluster's scraper only.
		With OTEL_EXPORTER_OTLP_ENDPOINT set (step f), logs and metrics are also sent over OTLP, next to the traces.


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
		- Check that KycCaseOpeningSubscriber is running. A message it cannot process currently stops the worker; its console shows the cause.

	g) The KYC BFF shows "documents cannot be shown" or 403 for evidence:

		- Documents are branch-scoped. The officer's branch (IDP employment profile) must match the branch of the agent who uploaded them.
		- Documents uploaded before branch scoping existed have no branch and are inaccessible; re-submit the onboarding.
		- Sign out and in again after IDP changes, so the "organization" claims are issued.

	h) A component logs "Kafka client error ... SASL authentication failed" or "Topic authorization failed", or its /health/ready
	   says it is not connected to its consumer group:
		- Run kafka\Setup-KafkaSecurity.ps1 (all three phases) - the users or ACLs are missing.
		- Check that the component's appsettings.Development.json has Kafka:SaslPassword (it refuses to start without it
		  when Kafka:SecurityProtocol is SaslPlaintext).
		- A component that stops at start-up with "Kafka:SaslPassword is not configured" was started without
		  DOTNET_ENVIRONMENT=Development, so appsettings.Development.json was not loaded: start it with its launch profile.

	i) A Kafka CLI tool hangs or reports "Disconnected" after securing Kafka: add --command-config C:\Kafka\config\admin.properties.

	j) Visual Studio uses a stale launch profile: close VS, delete the solution's ".vs" folder, reopen, and check the start-up profile.

	k) A .NET BFF fails on its first request with "database "EwpBffStateDb" does not exist" or "permission denied for schema ..._bff":
		create EwpBffStateDb, run db\Apply-EwpServiceDbUsers.ps1, then db\EwpBffStateDb.sql (section 3 e). Sessions live there,
		so restarting the BFFs or Visual Studio does not sign anybody out; re-running EwpBffStateDb.sql does (it recreates the tables).


6) Bruno API Testing:

	Bruno can call the Microservice APIs directly using OAuth 2.0 Authorization Code + PKCE against the IDP.

	a) IDP client:

		Client ID:			BSS.ApiTesting.Bruno.ClientID
		Grant:				Authorization Code, PKCE required, no client secret (public client)
		Identity scopes:	openid profile email roles
		API scopes:			customer-onboarding.read/.write, customer-kyc.read/.write, documents-management.read/.write, compliance.read/.write, accounts.*, payments.*

		Redirect URIs:		http://127.0.0.1:3000/callback (local callback server, recommended - see c)
							https://oauth.usebruno.com/callback

		The Bruno client is registered ONLY when the IDP runs in the Development environment.

	b) Bruno OAuth settings:

		Authorization URL:	https://idp.dev.localhost:46392/connect/authorize
		Access Token URL:	https://idp.dev.localhost:46392/connect/token
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

			iss		https://idp.dev.localhost:46392
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