## 1) Customer Onboarding: choreography saga (no coordinator; each context reacts to events)

| # | File | Method | What happens |
| --- | --- | --- | --- |
| **A. Sophie submits**(Customer Onboarding BFF → Customer Onboarding API) |  |  |  |
| 1 | `Microservices/CustomerOnboarding/BFF.Web/Controllers/OnboardingController.cs` | `CreateAndSubmit` | BFF: creates the customer, the application, uploads the 2 PDFs to Documents Management (token exchange), then submits |
| 2 | `Microservices/CustomerOnboarding/API/API/Controllers/OnboardingApplicationsController.cs` | `SubmitApplication` | API endpoint |
| 3 | `…/CustomerOnboarding/API/Application/Onboarding/Commands/SubmitApplication/SubmitOnboardingApplicationCommandHandler.cs` | `HandleAsync` | Application layer: loads the aggregate, calls the domain |
| 4 | `…/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs` | `Submit` | **Domain**: rules checked,**domain event raised** |
| 5 | `…/CustomerOnboarding/API/Infrastructure/Persistence/CustomerDbContext.cs` | `SaveChangesAsync` | Domain event →**integration event → Outbox row**, in the same transaction |
| **B. Relay → Kafka** |  |  |  |
| 6 | `AsyncWorkflows/Publishers/CustomerOnboarding/CustomerOutboxPublisher/Publishing/CustomerOutboxPublisher.cs` | `PublishBatchAsync` | Claims rows (`FOR UPDATE SKIP LOCKED`), publishes**`onboarding.application.submitted`** |
| **C. KYC opens a case** |  |  |  |
| 7 | `AsyncWorkflows/Infrastructure/Subscribers/KafkaSubscriberHostedService.cs` | `ConsumeLoopAsync` | Shared consume loop of**every**subscriber (dead-letter:`DeadLetterAsync`) |
| 8 | `AsyncWorkflows/Subscribers/CustomerKyc/KycCaseOpeningSubscriber/Processing/KycCaseOpeningProcessor.cs` | `ProcessAsync`→`SendAsync` | Translates the message, calls the KYC API with its machine token |
| 9 | `Microservices/CustomerKyc/API/Controllers/InternalKycCasesController.cs` | `CreateFromApplicationSubmitted` | Pinned-client internal endpoint; Inbox check |
| 10 | `…/CustomerKyc/API/Application/Commands/OpenKycCase/OpenKycCaseCommandHandler.cs` | `HandleAsync` |  |
| 11 | `…/CustomerKyc/API/Domain/Aggregates/KycCase.cs` | `Open` | Case created,`KycCaseCreated`raised |
| 12 | `…/CustomerKyc/API/Infrastructure/KycDbContext.cs` | `SaveChangesAsync` | Outbox rows |
| 13 | `…/CustomerKyc/API/Infrastructure/KycOutboxPublisher.cs` | `PublishBatchAsync` | KYC's relay**runs inside the KYC API**(unlike Customer Onboarding's separate worker) |
| **D. Ethan decides both stages**(KYC BFF, NestJS → KYC API) |  |  |  |
| 14 | `Microservices/CustomerKyc/API/Controllers/KycCasesController.cs` | `ApproveIdentity`/`ApproveDocument…`/`Reject…` |  |
| 15 | `…/CustomerKyc/API/Application/Commands/DecideVerificationStage/DecideVerificationStageCommandHandler.cs` | `HandleAsync` | Row lock + version check |
| 16 | `…/CustomerKyc/API/Domain/Aggregates/KycCase.cs` | `DecideStage` | **SoD and ABAC enforced here**; after the 2nd stage,`KycCaseApproved`or`Rejected`→ published**`kyc.case.approved`**/**`.rejected`** |
| **E. Two subscribers react to the same KYC event**(fan-out) |  |  |  |
| 17 | `AsyncWorkflows/Subscribers/CustomerOnboarding/OnboardingOutcomeSubscriber/Processing/OnboardingOutcomeProcessor.cs` | `ProcessAsync` | Tells Customer Onboarding the outcome |
| 18 | `…/CustomerOnboarding/API/API/Controllers/InternalOnboardingApplicationsController.cs` | `RecordKycOutcome`(also`RecordComplianceOutcome`,`RecordAccountOutcome`) |  |
| 19 | `…/CustomerOnboarding/API/Application/Onboarding/Commands/RecordKycOutcome/RecordKycOutcomeCommandHandler.cs` | `HandleAsync` | Checks application ref ⇄ number |
| 20 | `…/CustomerOnboarding/API/Domain/Aggregates/OnboardingApplication.cs` | `RecordKycApproved`/`RecordKycRejected`(and`RecordCompliance…`,`RecordAccount…`) | State transitions; a rejection →**`onboarding.application.rejected`** |
| 21 | `AsyncWorkflows/Subscribers/Compliance/ComplianceCaseOpeningSubscriber/Processing/ComplianceCaseOpeningProcessor.cs` | `ProcessAsync` | Same KYC event → opens a Compliance case |
| **F. Compliance** |  |  |  |
| 22 | `Microservices/Compliance/API/Controllers/InternalComplianceCasesController.cs` | `Open` |  |
| 23 | `…/Compliance/API/Application/Commands/OpenComplianceCase.cs` | `HandleAsync` |  |
| 24 | `…/Compliance/API/Domain/Aggregates/ComplianceCase.cs` | `Open` |  |
| 25 | `…/Compliance/API/Infrastructure/Screening/ScreeningWorker.cs`→`Application/Commands/ScreenDueCase.cs` | `ExecuteAsync`→`HandleAsync` | **Background worker**calls the screening provider (`ScreeningProviderClient.cs`) |
| 26 | `…/Compliance/API/Domain/Aggregates/ComplianceCase.cs` | `RecordScreeningResult`/`RecordScreeningFailure` | Risk set; a failure is never a pass |
| 27 | `…/Compliance/API/Application/Commands/OfficerActionCommand.cs` | `HandleAsync` | Olivia's decision… |
| 28 | `…/Compliance/API/Domain/Aggregates/ComplianceCase.cs` | `Approve`/`Reject` | …**clearance-by-risk + cross-context SoD**; published via`Infrastructure/Messaging/ComplianceOutboxPublisher.cs``PublishBatchAsync` |
| **G. Accounts** |  |  |  |
| 29 | `AsyncWorkflows/Subscribers/Accounts/AccountApplicationOpeningSubscriber/Processing/AccountApplicationOpeningProcessor.cs` | `ProcessAsync` | `compliance.case.approved`→ Accounts |
| 30 | `Microservices/Accounts/API/Controllers/InternalAccountApplicationsController.cs`→`Application/Commands/OpenAccountApplication.cs` | `Open`→`HandleAsync` |  |
| 31 | `…/Accounts/API/Application/Commands/OfficerActionCommand.cs`→`Domain/Aggregates/AccountApplication.cs` | `HandleAsync`→`Approve`/`Reject` | Jack's decision |
| 32 | `…/Accounts/API/Infrastructure/CoreBanking/AccountOpeningWorker.cs`→`Application/Commands/OpenDueAccount.cs` | `ExecuteAsync`→`HandleAsync`/`OpenAsync` | **Background worker**→ core banking (`CoreBankingClient.cs`, with an Idempotency-Key) |
| 33 | `…/Accounts/API/Domain/Aggregates/AccountApplication.cs` | `RecordAccountOpened` | `AccountOpened`→`AccountsOutboxPublisher.cs``PublishBatchAsync`→ back to steps 17–20 →**COMPLETED** |
| **H. Compensation on a rejection** |  |  |  |
| 34 | `AsyncWorkflows/Subscribers/DocumentsManagement/DocumentInvalidationSubscriber/Processing/DocumentEvidenceProcessor.cs` | `ProcessAsync` | `onboarding.application.rejected`→ evidence invalidated (retained, not deleted) |

## 2) Payments: orchestration saga (one coordinator,`PaymentSaga`, decides every step)

| # | File | Method | What happens |
| --- | --- | --- | --- |
| 1 | `Microservices/Payments/API/Controllers/PaymentsController.cs` | `Initiate` | Sophie's "Transfer" |
| 2 | `…/Payments/API/Application/Commands/InitiatePayment.cs` | `HandleAsync` | Payment + saga + first command,**one transaction**, answers 202 |
| 3 | `…/Payments/API/Domain/Aggregates/PaymentSaga.cs` | `Start`→`SendFundsCommand` | **The orchestrator**: issues*ReserveFunds* |
| 4 | `…/Payments/API/Infrastructure/Persistence/PaymentsDbContext.cs` | `SaveAsync` | Saga state + Outbox row |
| 5 | `…/Payments/API/Infrastructure/Messaging/PaymentsOutboxPublisher.cs` | `PublishBatchAsync` | →**`accounts.commands`** |
| 6 | `AsyncWorkflows/Subscribers/Accounts/AccountsCommandSubscriber/Processing/AccountsCommandProcessor.cs` | `ProcessAsync`→`SendAsync` | Courier → Accounts |
| 7 | `Microservices/Accounts/API/Controllers/InternalFundsCommandsController.cs` | `Apply` |  |
| 8 | `…/Accounts/API/Application/Commands/FundsCommand.cs` | `HandleAsync` | Reserve / settle / release (FundsHold) → reply on**`accounts.funds.replies`** |
| 9 | `AsyncWorkflows/Subscribers/Payments/PaymentsSagaReplySubscriber/Processing/PaymentsSagaReplyProcessor.cs` | `ProcessAsync` | Courier → Payments |
| 10 | `…/Payments/API/Controllers/InternalPaymentSagaRepliesController.cs`→`Application/Commands/HandleSagaReply.cs` | `Handle`→`HandleAsync` |  |
| 11 | `…/Payments/API/Domain/Aggregates/PaymentSaga.cs` | `OnFundsReserved`/`OnFundsReservationFailed` | **Decision**: approval needed (→ waits), or send to the network |
| 12 | `…/Payments/API/Controllers/PaymentsController.cs`→`Application/Commands/DecidePayment.cs` | `Approve`/`Reject`→`HandleAsync` | Emily's decision →`PaymentSaga.OnApproved`/`OnApprovalRejected`(limits + SoD in`Payment.cs``EnsureMayDecide`) |
| 13 | `…/Payments/API/Infrastructure/Saga/SagaStepRunner.cs`→`Application/Commands/RunDueSagaStep.cs` | `ExecuteAsync`→`HandleAsync`/`SendToNetworkAsync` | **The saga's timer**: calls the network when due; wakes sagas whose reply is late |
| 14 | `…/Payments/API/Infrastructure/PaymentNetwork/PaymentNetworkClient.cs` | `SendAsync` | Idempotency-Key = PaymentRef |
| 15 | `…/Payments/API/Domain/Aggregates/PaymentSaga.cs` | `OnNetworkAccepted`/`OnNetworkRefused`/`OnNetworkUnavailable` | →*SettleFunds*(happy) or*ReleaseFunds*(compensation), back through steps 5–11 |
| 16 | same | `OnFundsSettled`/`OnFundsReleased`/`OnReplyTimeout`/`RetryCompensation` | End states: COMPLETED / FAILED / REJECTED / COMPENSATION\_FAILED |