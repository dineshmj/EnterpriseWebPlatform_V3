Enterprise Web Platform V3 - Local Development Guide
====================================================

This file is ONLY the local-development guide: setting up a laptop, host names, HTTPS, databases, Kafka, building,
starting, demonstrating, troubleshooting and API testing.

What the platform is, how it is designed and what each component must do are documented elsewhere - see the
"Documentation map" in README.md. Nothing in this file repeats those documents.


0) Contents:

	1) Quick start on a new laptop (about 10 minutes)
	2) Host names and HTTPS
	3) Local URLs and ports
	4) Databases
	5) Kafka
	6) Running and debugging
	7) Demonstrations
	8) Upgrading an existing set-up
	9) Operations reference (health, traces, logs, metrics, security headers, rate limiting, scans, secrets)
	10) Troubleshooting
	11) Bruno API testing


1) Quick start on a new laptop (about 10 minutes):

	Every step is a command or a copy. Run the PowerShell commands from the repository root. The first run of steps 6 to 8
	downloads packages (NuGet, pnpm), which is most of the time; later runs are much faster.

	1. Prerequisites (installed once):
		.NET 10 SDK; Visual Studio 2026 (or 2022 17.14+, see 6a); Node.js 20.9+ (24 recommended) and pnpm;
		PostgreSQL 18 with pgAdmin (user "postgres", password "admin", port 5432); a Java runtime 21+;
		Apache Kafka 4.x (KRaft) at C:\Kafka.

		Kafka, first time only (a fresh C:\Kafka):
			cd C:\Kafka
			$id = .\bin\windows\kafka-storage.bat random-uuid
			.\bin\windows\kafka-storage.bat format --standalone -t $id -c config\server.properties
		and create C:\Kafka\StartKafka.bat with these two lines:
			cd /d C:\Kafka
			bin\windows\kafka-server-start.bat config\server.properties

	2. Hosts file (as Administrator): append the "127.0.0.1 ..." lines of Sample 'hosts'.txt (repository root) to
	   C:\Windows\System32\drivers\etc\hosts. Why and how to check: section 2.

	3. Certificates - trust the ASP.NET Core development certificate (it covers *.dev.localhost), and export it once
	   for the Node.js services (Audit Journey API, Audit web):
			dotnet dev-certs https --trust
			dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"
			dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pem" --format Pem --no-password
	   (The KYC BFF uses its own copy in src\Microservices\CustomerKyc\BFF.Web\certs.)

	4. Kafka - secure it once (SCRAM users, per-component ACLs, explicit topics). Details: section 5.
			C:\Kafka\StartKafka.bat                                 (in its own window; leave it running)
			.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare        (Kafka running)
			... stop Kafka (Ctrl+C in its window) ...
			.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Secure         (Kafka stopped)
			C:\Kafka\StartKafka.bat
			.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Acls           (Kafka running)

	5. Databases - all 11, their users and their demo data, in the right order, in one command (it asks for YES):
			.\ps\database\Initialize-EwpDatabases.ps1
	   It ends with "Done: 11 database(s) recreated." and no yellow WARNING lines. Details: section 4.

	6. Front ends - build and export the MFEs, the Shell SPA and the KYC NestJS BFF:
			.\ps\build\CompileAndExportBFFClients_V3.ps1

	7. Visual Studio - open EnterpriseWebPlatform.BSS.slnx, choose the multi-project launch profile ("New Profile",
	   EnterpriseWebPlatform.BSS.slnLaunch) and Start. It builds and starts the IDP, every API, BFF, worker and
	   simulator in their own console windows (list: section 6a).

	8. Node.js services - when Visual Studio has started everything:
			.\ps\run\Start-NodeServices.ps1
	   It opens the KYC BFF, the Audit Journey API and the Audit web app in their own windows (each builds on its
	   first run).

	9. Browse to https://shell.dev.localhost:46367 and sign in. The demo users, their roles and the password convention
	   are in src\IDP\doc\IDP-Requirements.md (section 6). A quick smoke test:
		- sophie.cs (customer service, SYD001): the Customer Onboarding, Payments and other menus appear;
		- sarah.audit (auditor): Audit -> Audit Trail shows the trail (empty on a new laptop until something happens);
		- the walkthroughs in section 7 exercise everything else.

	If something does not start: section 10 (troubleshooting).


2) Host names and HTTPS:

	Each tier uses its own host name. Browser cookies are scoped by host name, not by port. When several applications
	share "localhost", their authentication, correlation, nonce, session and BFF cookies are all sent to each other, and
	the request headers grow until the server answers:

		HTTP 400 - Request Too Long
		The size of the request headers is too long.

	Giving each tier its own host name (all resolving to 127.0.0.1) gives each application an independent cookie namespace.

	a) Open "C:\Windows\System32\drivers\etc\hosts" as Administrator and append the lines below (a complete, current
	   copy of a working hosts file is kept in the repository root: Sample 'hosts'.txt):

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

		127.0.0.1    audit.dev.localhost
		127.0.0.1    audit-api.dev.localhost
		127.0.0.1    audit-journey.dev.localhost

	b) Check each name with "ping <hostname>"; each must resolve to 127.0.0.1. (Browsers resolve any *.localhost name
	   to this machine by themselves - PowerShell, Node.js and .NET use the hosts file, so a missing line shows there first.)

	c) ASP.NET Core applications run on Kestrel using the "https" launch profile (not IIS Express). The ASP.NET Core
	   development certificate covers "*.dev.localhost". This applies to every ASP.NET Core application: the Shell BFF,
	   the Customer Onboarding, Compliance, Accounts and Payments BFFs, every API, the workers and the simulators.
		dotnet dev-certs https --check
		dotnet dev-certs https --trust

	d) Node.js applications need the development certificate as files, because Node does not use the Windows
	   certificate store:
		- Audit Journey API and Audit web: the PFX (to serve HTTPS) and the PEM (NODE_EXTRA_CA_CERTS, to trust the IDP
		  and the APIs), exported to %USERPROFILE%\.aspnet\https (section 1, step 3); their runnow.bat checks for them.
		- Customer KYC BFF (NestJS): its own copy in its certs folder - see src\Microservices\CustomerKyc\BFF.Web\README.md.

	NOTE:
		Kestrel is used deliberately in V3 so that the local topology is explicit and consistent. The IDP is self-hosted
		and opens its own console window.


3) Local URLs and ports:

		IDP								https://idp.dev.localhost:46392
		Shell BFF (serves Shell SPA)	https://shell.dev.localhost:46367
		Customer Onboarding BFF (MFE)	https://customer.dev.localhost:46311
		Customer Onboarding API			https://customer-api.dev.localhost:46363
		Customer KYC BFF (MFE)			https://kyc.dev.localhost:33800   (Node.js; outside Visual Studio)
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
		Audit web (Next.js SPA + BFF)	https://audit.dev.localhost:46380   (Node.js; outside Visual Studio; menu "Audit Trail")
		Audit API						https://audit-api.dev.localhost:46378   (the tamper-evident audit trail)
		Audit Journey API (NestJS)		https://audit-journey.dev.localhost:46379   (Node.js; outside Visual Studio)
		Kafka UI						http://localhost:8080   (optional)

		Workers (health and metrics only): http://localhost:5101 ... 5109 - see section 9a.

	The Shell Menu DB seed registers these same URLs.

	PORTS: never use 44300-44399. Visual Studio 2026 reserves ports from that range (the IIS Express SSL pool) for its
	web tooling, e.g. Browser Link, through Windows HTTP.sys - a different one at each start - and Kestrel then fails with
	"An attempt was made to access a socket in a way forbidden by its access permissions". The platform's ports were moved
	from 443xx to 463xx for this reason. Check a suspect port with:  netsh http show servicestate view=requestq | Select-String <port>


4) Databases:

	a) One command creates everything (section 1, step 5):

			.\ps\database\Initialize-EwpDatabases.ps1                        # all databases (asks for YES)
			.\ps\database\Initialize-EwpDatabases.ps1 -Database EwpAuditDb   # only the named ones; -Force skips the question

		In this order: it creates every database that does not exist yet, creates / refreshes the service users
		(ps\database\Apply-EwpServiceDbUsers.ps1), then runs each database's script connected to that database.
		Users BEFORE scripts matters: the scripts grant their own new schemas and tables to those users.

	b) Each database has ONE complete script. It drops and recreates that database's tables and seeds its reference /
	   demo data:

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
										empties the trail - see section 8 to re-record it from Kafka)
			EwpBffStateDb				db\EwpBffStateDb.sql   (sessions and Data Protection keys of the .NET BFFs, and the
										Audit web app's sessions (schema audit_bff); re-running it signs everybody out)

		WARNING:
			Running a script erases that database's data. Each script must run while connected to ITS OWN database.

	c) Running one script by hand, from the repository root in PowerShell:

			$psql = 'C:\Program Files\PostgreSQL\18\bin\psql.exe'
			$env:PGPASSWORD = 'admin'

			# Create a database that does not exist yet (the quotes keep the mixed-case names used by the connection strings):
			& $psql -h localhost -U postgres -c 'CREATE DATABASE "EwpBssShellDb";'

			# Run a script against its database:
			& $psql -h localhost -U postgres -d EwpBssShellDb -v ON_ERROR_STOP=1 -f .\src\Shell\MenuDB\EwpBssShellDb.sql

		pgAdmin's Query Tool, opened on the right database, works as well (its warnings appear in the Messages tab, not the
		result grid). With the docker-compose PostgreSQL container instead, prefix the same psql commands with
		"docker exec -i ewp-postgres" and pipe the script in, e.g.:

			Get-Content .\src\Shell\MenuDB\EwpBssShellDb.sql -Raw | docker exec -i ewp-postgres psql -U postgres -d EwpBssShellDb -v ON_ERROR_STOP=1

		(The container creates EwpIdentityAccessDb automatically.)

		After recreating the IDP database, sign out and sign in again so that new claims are issued.

	d) Service database users (least privilege): ps\database\Apply-EwpServiceDbUsers.ps1 runs db\EwpServiceDbUsers.sql with
	   psql (it checks that every database exists first). Initialize-EwpDatabases.ps1 runs it for you; run it by hand
	   after creating a NEW database or dropping a database itself. It is idempotent and safe to run at any time. The SQL
	   file cannot run in pgAdmin's Query Tool - it uses psql's \connect; pgAdmin's Tools > PSQL Tool with \i <path> works too.

		Each service connects with its own user (ewp_idp, ewp_shell, ewp_customer_onboarding_api, ewp_customer_outbox_relay,
		ewp_kyc_api, ewp_documents_api, ewp_compliance_api, ewp_accounts_api, ewp_notifications_api, ewp_payments_api,
		ewp_audit_api (SELECT and INSERT only); the BFFs ewp_co_bff, ewp_compliance_bff, ewp_accounts_bff, ewp_payments_bff,
		ewp_audit_web and ewp_shell each own one schema of EwpBffStateDb) that may read and write ITS OWN database only: no
		DDL and no access to other services' databases; the CO outbox relay may only read and update outbox_messages. The
		database scripts still run as postgres; the grants survive re-running them. Without the users the services cannot connect.

		The relay's grant is per TABLE, so EwpCustomerDb.sql re-applies it (watch for its NOTICE / WARNING line).
		Symptom if it is missing: CO outbox rows stay unpublished (attempt_count 0) and KYC never opens a case.
		Repair: re-run ps\database\Apply-EwpServiceDbUsers.ps1.


5) Kafka:

	a) Secure the broker once (SCRAM users, per-component ACLs, explicit topics) - section 1, step 4; details in kafka\README.md:

			.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare     (Kafka running: users, topics, consumer-group positions)
			.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Secure      (Kafka stopped: switches the broker to SASL + ACLs)
			.\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Acls        (Kafka running: grants each user only what it needs)

		A NEW component (a new Kafka user or topic) needs only Prepare and Acls again, with Kafka running - no restart.
		Stop Kafka without its window (e.g. under PowerShell ISE): .\ps\kafka\Stop-Kafka.ps1.
		Optional: Kafka UI (read-only browser) - installation steps in kafka\README.md section 2.

		IMPORTANT:
			These are development-only credentials. Real environments must use a secret store.

	b) Topics. Topic auto-creation is disabled, so every topic is created explicitly: ps\kafka\Setup-KafkaSecurity.ps1
	   -Phase Prepare creates all of them (event topics and the dead-letter topics). To create topics later, as the admin user:

			C:\Kafka\bin\windows\kafka-topics.bat --bootstrap-server localhost:9092 --command-config C:\Kafka\config\admin.properties ^
			    --create --if-not-exists --topic <name> --partitions 1 --replication-factor 1

		The authoritative topic list is doc\Integration-Event-Catalogue.md.

	c) IMPORTANT - recreating databases means recreating topics: see section 8b.


6) Running and debugging:

	a) Visual Studio: open EnterpriseWebPlatform.BSS.slnx and use the multi-project launch profile in
	   EnterpriseWebPlatform.BSS.slnLaunch. (.slnx is the XML solution format: Visual Studio 2026, or Visual Studio 2022
	   17.14+; 17.10-17.13 need the preview feature "Use Solution File Persistence Model". The dotnet CLI needs SDK 9.0.200+.)
	   It starts:

		IDP, Documents Management API, Customer Onboarding API, Customer KYC API, CustomerOutboxPublisher, KycCaseOpeningSubscriber,
		OnboardingOutcomeSubscriber, Compliance API, ComplianceCaseOpeningSubscriber, DocumentInvalidationSubscriber, Accounts API,
		AccountApplicationOpeningSubscriber, Notifications API, NotificationsSubscriber, Audit API, Payments API, AccountsCommandSubscriber,
		PaymentsSagaReplySubscriber, Screening Provider Simulator, Core Banking Simulator, Payment Network Simulator, Shell BFF,
		Customer Onboarding BFF, Compliance BFF, Accounts BFF and Payments BFF.

		Stop everything BEFORE "Rebuild Solution": a rebuild while services run can delete a project's output without
		writing the new one (the service then fails to start).

		Every publisher and subscriber is a console (generic host) application. Several instances of each may run in parallel:
		publishers claim Outbox rows with FOR UPDATE SKIP LOCKED, subscribers share one Kafka consumer group per subscriber (one
		partition = one instance), and consumers are idempotent. Topics are created with 1 partition, so extra subscriber instances
		are hot standbys until the partition count is raised.

	b) The Node.js services are NOT in that profile: the Customer KYC BFF (NestJS + Next.js), the Audit Journey API
	   (NestJS) and the Audit web app (Next.js SPA + light BFF). After Visual Studio has started the solution, start all
	   three - each in its own console window, the Audit web app after the Journey API is listening:

			.\ps\run\Start-NodeServices.ps1                 (-Only Kyc / -Only Audit;  -Stop stops them)

	   Each window runs that service's runnow.bat (which installs and builds on its first run). The KYC BFF's environment
	   variables are set by its runnow.bat and described in src\Microservices\CustomerKyc\BFF.Web\README.md.

	c) Debugging a Node.js service: close its window (Ctrl+C), open its folder in VS Code, open a "JavaScript Debug
	   Terminal" and run "runnow.bat dev" there (the Audit services; development / watch mode with source maps):
	   breakpoints in the TypeScript sources are hit - Next.js server actions, route handlers and lib\server for the web
	   app; the browser's DevTools for its client code. The other services keep running.

	d) After changing a front end (MFE or Shell): .\ps\build\CompileAndExportBFFClients_V3.ps1, then restart the Shell, CO,
	   KYC, Compliance, Accounts and Payments BFFs (the KYC BFF runs outside Visual Studio - easy to forget): their
	   Content-Security-Policy hashes are computed at start-up from the exported pages. The Audit web app is not exported:
	   its runnow.bat builds it, and its CSP uses a per-request nonce.


7) Demonstrations:

	a) A typical end-to-end check:

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
		  the application to COMPLIANCE_COMPLETED or REJECTED. (The same actions are available through the API with Bruno, section 11.)
		- Compensation: when KYC or Compliance REJECTS an application, its two evidence documents become INVALIDATED in
		  EwpDocumentsManagementDb.documents (status, invalidated_at, invalidation_reason) - retained, not deleted - via the
		  onboarding.application.rejected topic and the DocumentInvalidationSubscriber.
		- Compliance approval opens an ACCOUNT APPLICATION (AccountApplicationOpeningSubscriber): the application moves to
		  ACCOUNT_OPENING_IN_PROGRESS and EwpAccountsDb.account_applications has a PENDING_REVIEW row. jack.accounts (SYD001) decides
		  it in the Shell: Account Applications -> open the application -> choose the product -> Approve and open account.
		  (Through the API with Bruno, section 11, scope accounts.read accounts.write: POST .../v1/accounts/applications/{id}/approve.)
		  He is neither the initiator nor the Compliance approver (separation of duties). On approval the core-banking system
		  opens the account within seconds (EwpAccountsDb.accounts: BSB 062-000 + account number) and the onboarding is COMPLETED.
		  A rejection (with remarks) ends it REJECTED and invalidates the evidence, as above.

	b) An unreliable external provider (Screening Provider Simulator, localhost only):

			$sim = 'https://localhost:46366/admin/behaviour'
			Invoke-RestMethod $sim -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Healthy
			Invoke-RestMethod $sim -Method Put -ContentType 'application/json' -Body '{"behaviour":"Healthy","forcedOutcome":"MATCH"}'
			Invoke-RestMethod $sim                                                                              # current behaviour

		While the provider is Down / Failing / Slow, new Compliance cases stay in SCREENING (a provider failure is never a pass),
		are retried with back-off (15 s doubling to 5 min), the circuit breaker opens after repeated failures (Compliance API log:
		"circuit open"), and /health/ready of the Compliance API reports Degraded once a case waits more than 2 minutes.
		Set it back to Healthy and the waiting cases are screened automatically.

	c) An unreliable core-banking system (Core Banking Simulator, localhost only):

			$cbs = 'https://localhost:46376/admin/behaviour'
			Invoke-RestMethod $cbs -Method Put -ContentType 'application/json' -Body '{"behaviour":"Down"}'      # or Failing / Slow / Refusing / Healthy

		While Down / Failing / Slow, approved account applications stay OPENING and are retried with back-off; every request
		carries an Idempotency-Key (the ApplicationRef), so a retry after a lost answer never opens a second account. After 6
		failures - or at once when Refusing (HTTP 422) - the application is FAILED and AccountOpeningFailed is published;
		Customer Onboarding compensates: COMPENSATING -> REJECTED (RejectedBy ACCOUNT_OPENING), evidence INVALIDATED, customer PROSPECT.
		Back to Healthy, waiting accounts are opened automatically.

	d) Payments - the ORCHESTRATED saga:

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

	e) Audit - the tamper-evident trail, in the customer's pattern (Next.js light BFF -> NestJS Journey API -> Domain APIs):

		Every business event above is recorded by the Audit API from Kafka. Sign in as sarah.audit (auditor) and open
		Audit -> Audit Trail: search by record (e.g. a PAY-... or APP-... number), by person (a LAN ID such as somit) or by
		event; click a record to see where it stands now (for a payment, live from the Payments API) and everything that
		happened to it; "Verify integrity" re-walks the hash chain; "Who read the trail" lists every search and view - each
		recorded too. Other users have no Audit menu, and the Audit API refuses anything but the Journey API acting for an
		auditor (token exchange). Tampering test and details: src\Microservices\Audit\API\README.md ("Try it").

	f) Two-step sign-in (MFA) with Google Authenticator - OFF by default:

		Switch it on in FIVE appsettings.json files (keep them equal): src\IDP ("Mfa": { "Enabled": true }) - the sign-in -
		and the Payments, Accounts, Compliance and Customer KYC APIs - the step-up for decisions. Restart them.

		- Every user is asked to enrol at their next sign-in: in Google Authenticator tap +, "Scan a QR code" (or "Enter a
		  setup key"); the entry appears as "EWP V3 Demo: <username>". Enter the 6-digit code it shows, save the 10 recovery
		  codes (shown once), continue. ONE phone holds any number of users - one entry each.
		- From then on: password, then the current code (or one unused recovery code). Wrong codes count towards the same
		  lockout as wrong passwords (5 attempts, 15 minutes); a code is never accepted twice.
		- Step-up: approving or rejecting a payment, "Retry release", and the KYC, Compliance and account-opening decisions
		  need a sign-in WITH the code (the token's amr contains "mfa"). A session from before MFA was switched on is refused
		  with "This action needs a sign-in with your authenticator code" - sign out and in again.
		- Switch it off again (false in the same five files): sign-in and decisions work as before. Enrolments are kept, so
		  switching it on later does not ask anybody to enrol again.
		Details: src\IDP\doc\IDP-Requirements.md (section 7).


8) Upgrading an existing set-up:

	a) Upgrade scripts keep an existing database's data (a new laptop never needs them - it runs the full scripts):
		- EwpIdentityAccessDb (keeps its users; adds the schema identity_server - Duende's refresh tokens, PAR requests,
		  signing keys and the IDP's key ring): src\IDP\IdentityAccessDB\Upgrade-6b-OperationalStore.sql, then run
		  ps\database\Apply-EwpServiceDbUsers.ps1 again (it grants ewp_idp the new schema).
		- EwpIdentityAccessDb for two-step sign-in (keeps its users; adds the empty tables user_mfa and
		  user_mfa_recovery_codes): src\IDP\IdentityAccessDB\Upgrade-6c-Mfa.sql
		- EwpAccountsDb for Payments (keeps its accounts; adds balances and funds holds, and gives existing accounts the demo
		  opening deposit): src\Microservices\Accounts\API\AccountsDb\Upgrade-5a-Funds.sql

	b) Recreating databases means emptying Kafka. Database IDs restart at 1 when a database is recreated, but Kafka keeps
	   the old messages: consumer groups could replay them against the new data (e.g. an old "application 1" event applied to
	   a new application 1), and the audit trail - which starts at the earliest offset - would record history about customers
	   that no longer exist. A CLEAN SLATE, in this order (stop Visual Studio and .\ps\run\Start-NodeServices.ps1 -Stop first;
	   Kafka and PostgreSQL running):

			.\ps\kafka\Reset-KafkaRecords.ps1               (empties every topic; topics, ACLs and consumer groups stay)
			.\ps\database\Initialize-EwpDatabases.ps1        (recreates every database)

	   then start Visual Studio and the Node.js services. Reset-KafkaRecords.ps1 deletes each topic's records up to its
	   current end (kafka-delete-records): offsets do not restart at 0, so every consumer group simply has nothing old to
	   read. (The heavier alternative - deleting and recreating the topics with Kafka UI or kafka-topics.bat --delete, then
	   ps\kafka\Setup-KafkaSecurity.ps1 -Phase Prepare - also discards the consumer groups' offsets.)

	c) Re-recording the audit trail after recreating EwpAuditDb: stop the Audit API, then reset its consumer group to the
	   start of every topic it reads, and start the API again (it records everything Kafka still holds):

			C:\Kafka\bin\windows\kafka-consumer-groups.bat --bootstrap-server localhost:9092 --command-config C:\Kafka\config\admin.properties ^
			    --group audit.trail-subscriber --reset-offsets --to-earliest --all-topics --execute

	d) A new database or a new service user: run ps\database\Apply-EwpServiceDbUsers.ps1, then the database's script (or
	   .\ps\database\Initialize-EwpDatabases.ps1 -Database <name>).

	e) Re-running db\EwpBffStateDb.sql signs everybody out (it recreates every BFF's session tables).


9) Operations reference:

	a) Health endpoints (as used by Kubernetes liveness / readiness probes):

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
			Audit Journey API (Node.js):  https://audit-journey.dev.localhost:46379/health/live | /health/ready

		live  = the process (and its background loop) is working; 503 means "restart it".
		ready = its dependencies are reachable (database; for a subscriber, its Kafka consumer group). "Degraded" (still 200)
		        means "working, but look": an Outbox relay with parked or old messages, a subscriber retrying a message, or
		        the audit chain broken.

	b) Optional - distributed traces (OpenTelemetry):

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
		The Node.js services (KYC BFF, Audit Journey API, Audit web) are not instrumented yet: a request through them starts a
		new trace at the .NET API behind them.

	c) Logs and metrics (Serilog, Prometheus):

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
			                                                (running, overdue, compensationFailed), the Outbox backlog and
			                                                the audit chain (entriesVerified, brokenAtSequence)
		The health metrics are refreshed every 30 seconds. Outside Development /metrics is off unless
		"Observability:Metrics:Enabled" is true; it is anonymous and must be reachable from the cluster's scraper only.
		With OTEL_EXPORTER_OTLP_ENDPOINT set (9b), logs and metrics are also sent over OTLP, next to the traces.

	d) Content-Security-Policy, rate limiting and dependency scanning:

		The Shell, CO BFF, KYC BFF, Compliance BFF, Accounts BFF and Payments BFF send a strict CSP: scripts only from the BFF
		itself plus the SHA-256 hashes of the exported pages' inline scripts, computed at startup from the files served - so
		restart them after re-exporting the MFEs (6d). The Audit web app uses a per-request nonce instead.
		If a page is blocked by CSP, switch to report-only (violations appear in the browser console) while investigating:
			Shell / CO BFF / Compliance BFF / Accounts BFF / Payments BFF:  appsettings: "Security": { "CspReportOnly": true }
			KYC BFF:         environment variable KYC_BFF_CSP_REPORT_ONLY=true

		Rate limiting (OWASP API4): every .NET BFF and API limits requests per signed-in person (600 per minute, of which 60 may be
		changes - payments, decisions, uploads), per machine client (3,000) and per IP address before sign-in (120). Over the limit
		the answer is 429 with Retry-After and a message the screens show. Configuration section "RateLimiting" (Enabled,
		PerPersonPerMinute, ChangesPerPersonPerMinute, AnonymousPerIpPerMinute, PerMachineClientPerMinute). To see it, from PowerShell:

			1..130 | ForEach-Object { curl.exe -sk -o NUL -w "%{http_code}`n" https://payments.dev.localhost:46388/api/auth/user } | Group-Object

		shows about 120 x 401 (not signed in) and then 429.

		Known-vulnerability scan of all .NET and npm dependencies (fails only on deployed dependencies):

			.\ps\build\Scan-Dependencies.ps1            # or -FailOn critical

	e) Secrets:
		Each component reads its own client secrets from its own configuration; nothing is compiled into Common.Landscape any more.
		Development values (client secrets, Kafka passwords, the simulators' API keys): the appsettings.Development.json of the
		component that uses them - the IDP, the Shell BFF, every .NET MFE BFF and API, every relay and subscriber, and the three
		simulators - and the runnow.bat of the Node.js services (KYC BFF, Audit Journey API, Audit web).
		A component refuses to start when a secret is missing. Outside Development, supply them as environment variables or from a secret store.
		Outside Development, the IDP and every .NET BFF also need DataProtection:CertificatePath (and CertificatePassword): the
		certificate that encrypts their key ring in the database. In Development on Windows, DPAPI is used instead.


10) Troubleshooting:

	a) "HTTP 400 - Request Too Long" / "The size of the request headers is too long":

		- Use the *.dev.localhost host names, never localhost.
		- Do not work around this by raising the server header limits.
		- Clear the cookies of the affected host if stale OIDC correlation or nonce cookies remain.
		- Restart the application and sign in again.

	b) HTTPS certificate warning for a *.dev.localhost URL:

		- ASP.NET Core: make sure the "https" launch profile is used, not IIS Express.
		- Run "dotnet dev-certs https --check", and "--trust" if needed.
		- Node.js (KYC BFF): re-export the PFX and check the KYC_BFF_TLS_PFX_* settings.
		- Node.js (Audit services): their runnow.bat stops with "the development certificate was not found" until it is
		  exported to %USERPROFILE%\.aspnet\https (section 1, step 3).

	c) A host name does not resolve: check the hosts file and ping it (section 2). Remember that the browser resolves
	   *.localhost by itself: a missing line may show only in PowerShell or Node.js ("remote name could not be resolved").

	d) The menu does not appear:

		- Check that PostgreSQL is running and EwpBssShellDb exists with its snake_case tables.
		- Check that the signed-in user has role claims, and that the role codes match the menu_items_and_roles rows.
		- Remember that menu visibility is not authorization.

	e) Expected menu items are missing after a role change: sign out and in again, so new role claims are issued.

	f) A menu item opens Chrome's error page in the frame (no response): that MFE is not running. For the KYC BFF and the
	   Audit web app, start the Node.js services (section 6b); check with
	   Get-NetTCPConnection -LocalPort <port> -State Listen.

	g) Nothing appears in KYC after an onboarding submission:

		- Check that the Kafka topics exist (5b); auto-creation is disabled.
		- Check that CustomerOutboxPublisher is running, and look at outbox_messages.published_at and last_error in EwpCustomerDb.
		- Check that KycCaseOpeningSubscriber is running. A message it cannot process currently stops the worker; its console shows the cause.

	h) The KYC BFF shows "documents cannot be shown" or 403 for evidence:

		- Documents are branch-scoped. The officer's branch (IDP employment profile) must match the branch of the agent who uploaded them.
		- Documents uploaded before branch scoping existed have no branch and are inaccessible; re-submit the onboarding.
		- Sign out and in again after IDP changes, so the "organization" claims are issued.

	i) A component logs "Kafka client error ... SASL authentication failed" or "Topic authorization failed", or its /health/ready
	   says it is not connected to its consumer group:
		- Run ps\kafka\Setup-KafkaSecurity.ps1 (all three phases) - the users or ACLs are missing.
		- Check that the component's appsettings.Development.json has Kafka:SaslPassword (it refuses to start without it
		  when Kafka:SecurityProtocol is SaslPlaintext).
		- A component that stops at start-up with "Kafka:SaslPassword is not configured" was started without
		  DOTNET_ENVIRONMENT=Development, so appsettings.Development.json was not loaded: start it with its launch profile.

	j) A Kafka CLI tool hangs or reports "Disconnected" after securing Kafka: add --command-config C:\Kafka\config\admin.properties.

	k) Visual Studio uses a stale launch profile: close VS, delete the solution's ".vs" folder, reopen, and check the start-up profile.

	l) A service does not start after "Rebuild Solution" (its bin folder has no DLL): stop everything, Rebuild again, then start (6a).

	m) A .NET BFF or the Audit web app fails on its first request with "database "EwpBffStateDb" does not exist" or
	   "permission denied for schema ..._bff": run .\ps\database\Initialize-EwpDatabases.ps1 -Database EwpBffStateDb (it
	   creates the database and the users first). Sessions live there, so restarting the BFFs or Visual Studio does not
	   sign anybody out; re-running EwpBffStateDb.sql does (it recreates the tables).

	n) A database script printed "WARNING: Role ... does not exist yet": the users were created after the script ran. Run
	   ps\database\Apply-EwpServiceDbUsers.ps1, then that script again (Initialize-EwpDatabases.ps1 does both in the right order).


11) Bruno API testing:

	Bruno can call the Microservice APIs directly using OAuth 2.0 Authorization Code + PKCE against the IDP.

	a) IDP client:

		Client ID:			BSS.ApiTesting.Bruno.ClientID
		Grant:				Authorization Code, PKCE required, no client secret (public client)
		Identity scopes:	openid profile email roles
		API scopes:			customer-onboarding.read/.write, customer-kyc.read/.write, documents-management.read/.write, compliance.read/.write, accounts.*, payments.*

		Redirect URIs:		http://127.0.0.1:3000/callback (local callback server, recommended - see c)
							https://oauth.usebruno.com/callback

		The Bruno client is registered ONLY when the IDP runs in the Development environment. The Audit APIs cannot be
		called this way: they accept only tokens obtained by token exchange (src\Microservices\Audit\JourneyApi\README.md).

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