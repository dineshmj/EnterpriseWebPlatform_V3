# Banking Services System (BSS) — Business Requirements in Gherkin

This document states **what the Banking Services System must do**, as behaviour that a business analyst, a tester and a developer can all read the same way. Each requirement is a Gherkin scenario: *Given* a situation, *When* someone acts, *Then* this is the result.

It covers the **happy paths** (things going right) and the **boundary situations**: limits, refusals, duplicates, failures of other systems, and people trying to do what they may not do.

| If you want… | Read… |
| --- | --- |
| The detailed rules of one bounded context | That context's requirements document (`src/Microservices/<Context>/doc/`) |
| How authorization decisions are made | [Authorization-Model.md](../Authorization-Model.md) |
| The saga designs behind onboarding and payments | [EWP-V3-Saga-Choreography-and-Orchestration-Plans.md](../EWP-V3-Saga-Choreography-and-Orchestration-Plans.md) |
| Manual smoke tests that walk through many of these scenarios | [Smoke-Test-Cases.md](Smoke-Test-Cases.md) |

---

## 1. How to read this document

### 1.1 The people

The scenarios use the demo users. Their role, department, branch and clearance level come from the identity provider; every scenario relies on them.

| User | Name | Role | Branch | Clearance |
| --- | --- | --- | --- | --- |
| `sophie.cs` | Sophie Mitchell | Customer Service Agent | SYD001 (Sydney) | 2 |
| `mia.cs` | Mia Robinson | Customer Service Agent | MEL001 (Melbourne) | 2 |
| `ethan.kyc`, `noah.kyc` | Ethan Parker, Noah Hughes | KYC Officer | SYD001 | 3 |
| `olivia.compliance` | Olivia Bennett | Compliance Officer | SYD001 | 4 |
| `grace.compliance` | Grace Walsh | Compliance Officer (senior) | SYD001 | 5 |
| `jack.accounts` | Jack Wilson | Account Officer | SYD001 | 3 |
| `emily.payments` | Emily Carter | Payments Officer | SYD001 | 4 |
| `daniel.ops` | Daniel Cooper | Operations Administrator | BNE001 | 4 |
| `platform.admin` | Michael Turner | Platform Administrator + Operations Administrator | PER001 | 5 |
| `sarah.audit` | Sarah Collins | Auditor | ADL001 | 5 |

### 1.2 Conventions

- **Statuses** are written in capitals, as the system records them (`SUBMITTED`, `PENDING_APPROVAL`).
- **Numbers that are configuration** (thresholds, limits, ages) are given with their production value, and the Development value where it differs. Changing configuration must not need a change to these requirements.
- **Simulated external systems.** The screening provider, the core-banking system and the payment network are simulators. In Development and Test, an operations administrator switches their behaviour on the **Platform Health** page: `Healthy`, `Slow`, `Failing`, `Down`, `Refusing`, and for the payment network also `LosingAnswers`. A switched simulator returns to `Healthy` by itself after 15 minutes.
- **Tags** classify each scenario:

| Tag | Meaning |
| --- | --- |
| `@happy-path` | The normal, successful flow |
| `@boundary` | A limit, a refusal or an unusual but valid situation |
| `@security` | Who may (not) see or do something |
| `@resilience` | Another system is slow, down or loses answers |
| `@sod` | Separation of duties: one person may not do two things |
| `@compensation` | Undoing earlier steps after a later step failed |
| `@smoke` | Covered by the manual smoke tests |

---

## 2. Signing in

```gherkin
Feature: Signing in to the Banking Services System
  Staff sign in once at the identity provider and work in every application of the
  platform without signing in again. The browser never holds a token.

  @happy-path @smoke
  Scenario: A member of staff signs in and sees only their own work
    Given Sophie is a Customer Service Agent
    When she signs in to the Shell
    Then she sees the "Customer Onboarding" and "Payments" menus
    And she does not see the KYC, Compliance, Accounts, Audit or Operations menus

  @happy-path
  Scenario: One sign-in serves every application
    Given Ethan has signed in to the Shell
    When he opens "View KYC Cases"
    Then the KYC application opens inside the Shell without asking him to sign in again

  @boundary @security
  Scenario: Repeated wrong passwords lock the account
    Given a user enters a wrong password 5 times in a row
    When they try to sign in again within 15 minutes
    Then the sign-in is refused, even with the right password
    And after 15 minutes they may try again

  @boundary @security
  Scenario: Sign-in attempts from one address are throttled
    Given more than 20 sign-in attempts have come from one network address within a minute
    When another attempt arrives from that address
    Then it is refused with "too many requests" until the minute has passed

  @boundary @security
  Scenario: An idle session ends
    Given Jack has not used the platform for 30 minutes
    When he clicks anything in an application
    Then he is asked to sign in again
    And no action is carried out with his old session

  @happy-path
  Scenario: Working inside an application keeps the Shell signed in
    Given Ethan signed in to the Shell and opened the KYC application
    And he works only inside the KYC application for 45 minutes
    Then the Shell's session stays active, because his clicks there count as activity
    And his live updates and notifications keep arriving

  @boundary @security
  Scenario: An ended Shell session is shown, not hidden
    Given the Shell's session has ended
    Then the workspace bar shows "Session ended" with "Sign in again"
    And live updates stop trying to reconnect
    And checking the session never extends it

  @happy-path
  Scenario: No consent page for the bank's own portal
    Given Ethan's sign-in at the identity provider is still valid
    When the Shell needs a new session, for example after F5
    Then he is signed in again without a password and without a consent page

  @happy-path @security
  Scenario: Signing out ends the session everywhere
    Given Emily is signed in to the Shell and has opened the Payments application
    When she signs out of the Shell
    Then her session in the Payments application ends as well
    And opening Payments again asks her to sign in

  @security
  Scenario Outline: Multi-factor authentication when it is switched on
    Given multi-factor authentication is switched on
    And <user> has enrolled an authenticator app
    When <user> signs in with the right password
    Then they are asked for the 6-digit code from the authenticator app
    And a code that was already used cannot be used again

    Examples:
      | user           |
      | emily.payments |
      | daniel.ops     |

  @boundary @security
  Scenario: A risky action needs a recent code, without signing out
    Given multi-factor authentication is switched on
    And Emily signed in with her authenticator code more than 15 minutes ago
    When she approves a payment
    Then a small window asks only for a fresh authenticator code, not her password
    And after a valid code the approval is carried out once
    And she stays signed in
```

---

## 3. The Shell: menu, workspace and notifications

```gherkin
Feature: Working in the Shell
  The Shell hosts every application. It shows each person only their own menu, keeps
  the context of the record they work on, and tells them about new work.

  @happy-path @smoke
  Scenario Outline: Each role sees its own menu
    Given <user> has signed in to the Shell
    Then the menu contains <menu>

    Examples:
      | user              | menu                                                                    |
      | sophie.cs         | View Customers, New Onboarding Application, New Payment, View Payments |
      | ethan.kyc         | View KYC Cases, Identity Verification, Document Verification          |
      | olivia.compliance | Compliance Monitor                                                      |
      | jack.accounts     | View Account Applications, View Accounts                               |
      | emily.payments    | Payment Approvals                                                       |
      | daniel.ops        | Platform Health, Payment Processing Monitor                            |
      | sarah.audit       | Audit Trail                                                             |

  @security
  Scenario: The menu only shows the way
    Given a menu item is hidden from Sophie
    When Sophie calls that application's address directly
    Then the application refuses her, because every request is authorized again

  @happy-path @smoke
  Scenario: New work arrives as a notification
    Given Sophie has submitted an onboarding application in Sydney
    When the KYC case is opened
    Then Ethan's bell shows a new notification for the KYC work
    And opening the notification takes him to that case

  @happy-path
  Scenario: The person who started the work is told about its progress
    Given Sophie submitted an onboarding application
    When the application moves on to compliance, account opening or completion
    Then Sophie receives a progress notification for each step

  @boundary @security
  Scenario: Notifications stay within their audience
    Given a new KYC case was opened in Sydney
    Then KYC officers of Sydney are notified
    And KYC officers of other branches are not notified
    And nobody can read a notification addressed to someone else

  @boundary @security
  Scenario: Too many open connections for one person
    Given Sophie already has 10 browser tabs connected for live notifications
    When she opens an 11th tab
    Then that tab is refused live updates and shows that it is reconnecting
    And the other 10 tabs keep working
```

---

## 4. Customers and onboarding applications

```gherkin
Feature: Onboarding a new customer
  A Customer Service Agent creates a customer and an onboarding application, attaches
  the evidence and submits it. Submission starts the onboarding workflow.

  Background:
    Given Sophie is a Customer Service Agent in Sydney

  @happy-path @smoke
  Scenario: Sophie onboards a new customer
    Given Camilla Parkers lives in Sydney
    When Sophie creates Camilla as a customer with her residential address
    And creates an onboarding application for her
    And uploads an identity proof and a tax proof, both PDF
    And submits the application
    Then the application is SUBMITTED with an application number like "APP-yyyyMMdd-nnnnnn"
    And Camilla's status is ONBOARDING
    And Sophie is recorded as the initiator of the workflow
    And Sophie becomes Camilla's managing agent

  @boundary
  Scenario: Both kinds of evidence are required
    Given Sophie has uploaded only an identity proof
    When she tries to submit the application
    Then the submission is refused because the tax proof is missing

  @boundary
  Scenario Outline: Customer data must be valid
    When Sophie creates a customer with <problem>
    Then the customer is not created and Sophie is told <reason>

    Examples:
      | problem                                | reason                                |
      | no first or last name                  | the name is required                  |
      | an email address without "@"           | the email address is not valid        |
      | an email address over 254 characters   | the email address is too long         |
      | no city, state, postcode or country    | the address is incomplete             |

  @boundary @security
  Scenario: A customer outside the agent's branch cannot be created
    Given Mia lives in Melbourne
    When Sophie tries to create Mia's customer with a Melbourne address
    Then the creation is refused, because the address is outside Sophie's branch

  @boundary
  Scenario: One onboarding at a time
    Given Camilla already has an application in progress
    When Sophie tries to start another application for Camilla
    Then it is refused, because an application is already in progress

  @boundary
  Scenario: An onboarded customer is not onboarded again
    Given Camilla is ACTIVE
    When Sophie tries to start an application for Camilla
    Then it is refused, because Camilla is already onboarded

  @boundary
  Scenario: A new attempt after a rejection
    Given Jason's last application was REJECTED
    Then Jason's status is PROSPECT again
    And Sophie may start a new application for Jason

  @boundary
  Scenario: The customer's name is verified identity
    Given Camilla's application has been submitted
    When Sophie tries to change Camilla's name
    Then the change is refused
    And contact details may only be changed when no application is in progress

  @boundary
  Scenario: Two people editing the same application
    Given Sophie opened the application, and it was changed afterwards
    When she submits with the version she saw
    Then the submission is refused because the application changed in the meantime

  @boundary
  Scenario: Only a draft can be submitted
    Given the application is already SUBMITTED
    When Sophie submits it again
    Then the second submission is refused and nothing happens twice

  @security
  Scenario: Only the managing agent changes a customer
    Given Sophie manages Camilla
    And another Customer Service Agent works in the same Sydney branch
    When that agent tries to change Camilla or submit her application
    Then they are refused
    But they may still view Camilla

  @security
  Scenario: Another branch does not see the customer at all
    Given Camilla lives in Sydney
    When Mia from Melbourne looks for Camilla
    Then Camilla does not appear in her list
    And opening Camilla's record directly reports "not found"

  @security
  Scenario Outline: Roles that may not change customer data
    When <user> tries to create or change a customer
    Then the request is refused

    Examples:
      | user           |
      | daniel.ops     |
      | platform.admin |
      | sarah.audit    |
```

---

## 5. Evidence documents

```gherkin
Feature: Evidence documents
  Documents Management stores the evidence on behalf of the other contexts. A document
  is checked when it arrives, kept encrypted, and never deleted once it is evidence.

  @happy-path @smoke
  Scenario: Evidence is attached when the application is submitted
    Given Sophie uploaded an identity proof and a tax proof
    When she submits the application
    Then both documents become ATTACHED to that application
    And neither can be deleted any more

  @boundary @security
  Scenario: A document is checked by its content, not its name
    Given a file named "passport.pdf" that actually contains a web page
    When Sophie uploads it
    Then the upload is refused, because the content is not a PDF

  @boundary
  Scenario Outline: Allowed files
    When Sophie uploads <file>
    Then the upload is <result>

    Examples:
      | file                          | result   |
      | a PDF                         | accepted |
      | a PNG or JPEG image           | accepted |
      | a Word document               | refused  |
      | a file larger than 25 MB      | refused  |

  @happy-path @smoke
  Scenario: The KYC officer sees the evidence safely
    Given Ethan reviews a KYC case
    When he opens the identity proof
    Then a verified PDF is shown inside the page
    And any other type of file is offered as a download, never shown as a page

  @security
  Scenario: Evidence stays within its branch
    Given a document was uploaded in Sydney
    When an officer of another branch asks for it
    Then it is reported as "not found"

  @compensation @smoke
  Scenario: Evidence of a rejected application is kept, not deleted
    Given Jason's application was REJECTED
    Then his evidence documents are INVALIDATED
    And they are kept, with the reason and the application they belonged to
    And they cannot be deleted

  @boundary
  Scenario: An upload that was never submitted can be removed
    Given Sophie uploaded a document but the submission failed
    Then the uploaded document is removed
    And the customer and application that were already created remain
```

---

## 6. KYC review

```gherkin
Feature: KYC review
  A KYC officer verifies the customer's identity and evidence in two stages: identity
  verification and document verification. Both must be approved.

  Background:
    Given Sophie submitted Camilla's application in Sydney
    And a KYC case was opened for it, unassigned, in the Sydney work queue

  @happy-path @smoke
  Scenario: Ethan approves both stages
    When Ethan approves the identity verification
    And Ethan approves the document verification
    Then the KYC case is APPROVED
    And the onboarding application moves to KYC_COMPLETED and then to compliance

  @happy-path
  Scenario: The stages may be decided in either order
    When Ethan approves the document verification first
    And then approves the identity verification
    Then the KYC case is APPROVED

  @boundary
  Scenario: Rejecting needs a reason
    When Ethan rejects the identity verification without remarks
    Then the rejection is refused until he gives remarks
    And remarks may be at most 4000 characters

  @compensation
  Scenario: A rejected stage rejects the onboarding
    When Ethan rejects the identity verification with remarks
    Then the KYC case is REJECTED
    And the onboarding application is REJECTED
    And Camilla is a PROSPECT again
    And her evidence documents are INVALIDATED and kept

  @boundary
  Scenario: A decided stage is final
    Given Ethan approved the identity verification
    When anyone decides the identity verification again
    Then the decision is refused as a conflict

  @boundary
  Scenario: The first officer to act owns the case
    Given Ethan and Noah both open the same case
    When Ethan decides the identity verification first
    Then the case is assigned to Ethan
    And Noah is refused when he tries to decide the document verification
    Until Ethan releases the case

  @boundary
  Scenario: Two officers decide the same stage at the same moment
    When Ethan and Noah decide the identity verification at the same moment
    Then exactly one decision is recorded
    And the other is refused as a conflict

  @sod @security
  Scenario: Nobody decides their own work
    Given a KYC officer submitted the onboarding application themselves
    When they try to decide either stage
    Then they are refused

  @sod @security
  Scenario: An unknown initiator fails closed
    Given the initiator of an application was not recorded
    When any officer tries to decide its KYC case
    Then the decision is refused, because separation of duties cannot be proven

  @security
  Scenario Outline: Who may decide KYC
    When <who> tries to decide a KYC stage
    Then the decision is <result>

    Examples:
      | who                                            | result  |
      | Ethan (KYC, clearance 3, Sydney)               | allowed |
      | a KYC officer with clearance 2                 | refused |
      | a KYC officer of another branch                | refused |
      | an officer outside the KYC department          | refused |

  @boundary @resilience
  Scenario: The submission arrives twice
    Given the "application submitted" event is delivered twice
    Then exactly one KYC case exists for the application
```

---

## 7. Compliance review

```gherkin
Feature: Compliance review
  After KYC approval, the applicant is screened against sanctions and politically
  exposed person lists by an external provider. A compliance officer then decides, with
  a clearance that matches the risk.

  Background:
    Given Camilla's KYC case was APPROVED by Ethan
    And a compliance case was opened for her application in Sydney

  @happy-path @smoke
  Scenario: A low-risk applicant is approved
    Given the screening result is CLEAR, so the risk is LOW
    When Olivia approves the case
    Then the compliance case is APPROVED
    And the onboarding application moves to COMPLIANCE_COMPLETED and then to account opening

  @boundary
  Scenario Outline: The officer's clearance must match the risk
    Given the screening result is <screening>, so the risk is <risk>
    When <officer> with clearance <clearance> tries to approve
    Then the approval is <result>

    Examples:
      | screening       | risk   | officer | clearance | result  |
      | CLEAR           | LOW    | Olivia  | 4         | allowed |
      | POTENTIAL_MATCH | MEDIUM | Olivia  | 4         | allowed |
      | MATCH           | HIGH   | Olivia  | 4         | refused |
      | MATCH           | HIGH   | Grace   | 5         | allowed |

  @boundary
  Scenario: Any compliance officer may reject or hold a high-risk case
    Given the risk is HIGH
    When Olivia rejects the case with remarks
    Then the compliance case is REJECTED and the onboarding application is REJECTED

  @boundary
  Scenario: A case on hold cannot be approved
    Given Olivia placed the case on hold with a reason
    When she tries to approve it
    Then the approval is refused until she releases the hold
    But she may reject it while it is on hold

  @sod @security
  Scenario: Compliance is independent of KYC
    Given Ethan decided the KYC stages of this application
    When Ethan, or the person who submitted the application, tries to decide the compliance case
    Then they are refused

  @resilience @smoke
  Scenario: The screening provider is down
    Given Daniel switches the screening provider to "Down"
    When the compliance case is opened
    Then the case waits in SCREENING and is retried with growing pauses (15 seconds, doubling to 5 minutes)
    And it is never approved by default
    And after 2 minutes the Compliance API shows as Degraded on Platform Health
    When Daniel switches the screening provider back to "Healthy"
    Then the screening completes and the case moves to UNDER_REVIEW

  @resilience
  Scenario: The screening provider is slow
    Given Daniel switches the screening provider to "Slow"
    Then each attempt times out after 5 seconds and counts as a failure
    And the case stays in SCREENING until an attempt succeeds

  @security
  Scenario: Only the applicant's verified name and address are sent for screening
    When the applicant is screened
    Then the provider receives the name and residential address as KYC verified them
    And no email address or phone number leaves the bank
```

---

## 8. Account opening

```gherkin
Feature: Opening the customer's account
  When compliance approves, an account application is created. An account officer
  approves it with a product, and only the bank's core-banking system opens the account.

  Background:
    Given Olivia approved the compliance case of Camilla's application in Sydney
    And an account application was created for it

  @happy-path @smoke
  Scenario: Jack opens Camilla's account
    When Jack approves the account application with the "Everyday transaction account" product
    Then the core-banking system opens the account with a BSB and account number
    And the account is ACTIVE with an opening balance of 5,000.00 AUD (demo deposit)
    And the onboarding application is COMPLETED
    And Camilla is ACTIVE

  @boundary
  Scenario: Rejecting the account needs a reason
    When Jack rejects the account application without remarks
    Then the rejection is refused until he gives remarks

  @compensation
  Scenario: Jack rejects the account
    When Jack rejects the account application with remarks
    Then the onboarding application is REJECTED
    And Camilla is a PROSPECT again
    And her evidence documents are INVALIDATED and kept

  @resilience @compensation @smoke
  Scenario: The core-banking system refuses to open the account
    Given Daniel switches the core-banking system to "Refusing"
    When Jack approves the account application
    Then the account application FAILED at once, with the reason core banking gave
    And Jack sees "The account could not be opened"
    And the onboarding application goes through COMPENSATING to REJECTED
    And Camilla is a PROSPECT again
    And her evidence documents are INVALIDATED and kept

  @resilience
  Scenario: The core-banking system is down for a while
    Given Daniel switches the core-banking system to "Down"
    When Jack approves the account application
    Then opening is retried with growing pauses (15 seconds, doubling to 5 minutes)
    And if core banking comes back before the 6th failure, the account is opened normally
    And after 6 failures the account application FAILED and the onboarding is compensated

  @resilience @boundary
  Scenario: An answer lost on the way never opens two accounts
    Given core banking opened the account but its answer was lost
    When the opening is retried with the same idempotency key
    Then core banking returns the same account
    And exactly one account exists for the application

  @sod @security
  Scenario: The account officer is independent of earlier decisions
    When the person who submitted the application, or the compliance officer who approved it, tries to approve the account
    Then they are refused

  @security
  Scenario: The account application belongs to its branch
    When an account officer of another branch looks for the application
    Then it is reported as "not found"
```

---

## 9. Monitoring the onboarding workflow

```gherkin
Feature: Noticing onboarding applications that stopped moving
  Onboarding has no central coordinator, so the Customer Onboarding API watches how
  long each application has waited in its step. It tells people; it never decides.

  @boundary
  Scenario Outline: An application waits too long
    Given an application has waited in <step> for longer than <age>
    Then the Customer Onboarding API shows as Degraded on Platform Health
    And the health details count the applications per step, without any personal data
    And nothing is rejected or compensated because of the waiting time

    Examples:
      | step                                                             | age (production) | age (Development) |
      | SUBMITTED, KYC_COMPLETED, COMPLIANCE_COMPLETED, COMPENSATING     | 15 minutes       | 5 minutes         |
      | KYC_IN_PROGRESS, COMPLIANCE_IN_PROGRESS, ACCOUNT_OPENING_IN_PROGRESS | 1 day        | 60 minutes        |

  @boundary
  Scenario: Failed compensation always needs attention
    Given an application is in COMPENSATION_FAILED
    Then the Customer Onboarding API shows as Degraded, however recent the failure
```

---

## 10. Payments: capturing and approving

```gherkin
Feature: Capturing and approving a payment
  A Customer Service Agent captures a payment for a customer at the counter or on the
  phone. Larger payments need a payments officer's approval within their limit.

  Background:
    Given Camilla's everyday account in Sydney has 5,000.00 AUD available
    And the approval threshold is 1,000.00 AUD

  @happy-path @smoke
  Scenario: A small payment goes straight through
    When Sophie pays 250.00 AUD from Camilla's account to BSB 062-000, account 12345678
    Then the payee's name is confirmed as a MATCH
    And the funds are reserved, the payment is sent to the network and the funds are debited
    And the payment is COMPLETED
    And Camilla's account has 4,750.00 AUD

  @happy-path @smoke
  Scenario: A large payment waits for approval
    When Sophie pays 1,500.00 AUD to the same payee
    Then the funds are reserved and the payment is PENDING_APPROVAL
    And it appears in Emily's "Payment Approvals" queue
    When Emily approves it
    Then the payment is COMPLETED
    And Sophie is notified

  @compensation @smoke
  Scenario: A large payment is rejected
    Given Sophie captured a payment of 1,200.00 AUD, which is PENDING_APPROVAL
    When Emily rejects it with remarks
    Then the reserved funds are released
    And the payment is REJECTED
    And Camilla's available balance is unchanged

  @boundary
  Scenario: Rejecting a payment needs a reason
    When Emily rejects a payment without remarks
    Then the rejection is refused until she gives remarks

  @boundary
  Scenario Outline: The approver's limit depends on clearance
    Given a payment of <amount> AUD is PENDING_APPROVAL
    When a payments officer with clearance <clearance> approves it
    Then the approval is <result>

    Examples:
      | amount     | clearance | result  |
      | 9,000.00   | 3         | allowed |
      | 15,000.00  | 3         | refused |
      | 15,000.00  | 4         | allowed |
      | 150,000.00 | 4         | refused |
      | 150,000.00 | 5         | allowed |

  @sod @security
  Scenario: Nobody approves their own payment
    Given a person initiated a payment
    When the same person tries to approve it
    Then the approval is refused

  @boundary
  Scenario: Not enough money
    When Sophie pays more than Camilla's available balance
    Then the reservation is refused
    And the payment is REJECTED with nothing reserved and nothing to undo

  @boundary
  Scenario: Two payments cannot spend the same money
    Given Camilla has 1,000.00 AUD available
    When two payments of 800.00 AUD are captured at the same moment
    Then exactly one of them reserves the funds
    And the other is refused for insufficient funds

  @boundary @smoke
  Scenario Outline: Confirmation of payee
    When Sophie enters payee account <account>
    Then the payee check shows <result>

    Examples:
      | account  | result                                            |
      | 12345678 | MATCH                                             |
      | 12345679 | CLOSE_MATCH ("J Citizen" held for "Jane Citizen") |
      | 12345670 | NO_MATCH, with a warning before she submits       |

  @boundary
  Scenario: The same request sent twice creates one payment
    Given Sophie submitted a payment with an idempotency key
    When the same request arrives again with the same key and the same details
    Then the same payment is returned and nothing is sent twice

  @boundary @security
  Scenario: The same key with other details is refused
    Given a payment was submitted with an idempotency key
    When a request with the same key but another amount, payee or account arrives
    Then it is refused as a conflict

  @security
  Scenario: A payment from another branch's account is refused
    Given an account belongs to a customer of another branch
    When Sophie tries to pay from that account, even by calling the system directly
    Then Accounts refuses the reservation, because the account is not in Sophie's branch

  @security
  Scenario: Payments belong to their branch
    When an officer of another branch looks for a Sydney payment
    Then it is reported as "not found"

  @security
  Scenario: The auditor reads payments only through the Audit Trail
    When Sarah opens the Payments screens directly
    Then she is refused
    But she can see the payments' history in the Audit Trail
```

---

## 11. Payments: sending, failures and unknown outcomes

```gherkin
Feature: Sending a payment through the payment network
  The payment network is an external system. A payment must never be paid twice, and a
  customer's money must never be released while it might already have been paid.

  Background:
    Given Camilla's account has enough money available
    And Daniel can switch the payment network simulator on Platform Health

  @compensation @smoke
  Scenario: The payee's bank is closed
    When Sophie pays to BSB 999-000
    Then the network refuses the payment ("the payee's account is closed")
    And the reserved funds are released
    And the payment is FAILED

  @resilience @compensation
  Scenario: The network refuses the payment
    Given Daniel switches the payment network to "Refusing"
    When Sophie pays 100.00 AUD
    Then the reserved funds are released at once
    And the payment is FAILED

  @resilience
  Scenario: The network is slow
    Given Daniel switches the payment network to "Slow"
    When Sophie pays 100.00 AUD
    Then each attempt times out after 5 seconds and is retried
    And if no attempt is answered, the outcome is treated as unknown (see below)

  @resilience @smoke
  Scenario: The network loses its answers, but the payment is paid exactly once
    Given Daniel switches the payment network to "LosingAnswers"
    When Sophie pays 300.00 AUD
    Then after 4 unanswered sends the payment waits in CONFIRM_WITH_NETWORK
    And the network's status enquiry says the payment was SENT
    And the funds are debited and the payment is COMPLETED
    And the payee is paid exactly once

  @resilience @boundary @smoke
  Scenario: Unknown outcome: the money stays reserved
    Given Daniel switches the payment network to "Down"
    When Sophie pays 400.00 AUD
    Then the payment waits in CONFIRM_WITH_NETWORK, asking the network again and again
    And the 400.00 AUD stays reserved
    And it is never released only because time passed

  @resilience @boundary @smoke
  Scenario: Unknown outcome for too long raises the alarm
    Given a payment has waited in CONFIRM_WITH_NETWORK for longer than 60 minutes (Development: 2 minutes)
    Then the Payment Processing Monitor shows it under "Unconfirmed by network"
    And the Payments API shows as Degraded on Platform Health
    And the operations administrators are notified that a payment needs reconciling

  @happy-path @sod @smoke
  Scenario: Two operators reconcile a payment that was not sent
    Given a payment is "Unconfirmed by network"
    And Daniel checked the network's own records and found no such payment
    When Daniel proposes "NOT SENT" with his evidence
    Then Daniel cannot approve his own proposal
    When Michael (platform.admin) approves the proposal
    Then the reserved funds are released
    And the payment is FAILED
    And both steps appear on the payment's timeline and in the Audit Trail, with who did what

  @happy-path @sod
  Scenario: Two operators reconcile a payment that was sent
    Given a payment is "Unconfirmed by network"
    And Daniel found the payment in the network's records
    When Daniel proposes "SENT" with the network's reference and his evidence
    And Michael approves the proposal
    Then the funds are debited
    And the payment is COMPLETED

  @boundary @sod
  Scenario: A reconciliation proposal can be rejected
    Given Daniel proposed a resolution
    When Michael rejects the proposal with remarks
    Then nothing changes: the payment stays in CONFIRM_WITH_NETWORK and the money stays reserved
    And a new proposal may be made

  @boundary
  Scenario: The network's own answer wins
    Given Daniel proposed "NOT SENT"
    When the network comes back and answers that the payment was SENT, before the proposal is approved
    Then the network's answer is applied and the payment is COMPLETED
    And the proposal is marked as superseded

  @boundary @security
  Scenario: Reconciliation rules
    Then a proposal can only be made after the alert time has passed
    And "SENT" needs the network's reference
    And the evidence is 10 to 500 characters
    And neither the proposer nor the payment's initiator may approve

  @boundary @resilience
  Scenario: A reply from an earlier attempt is ignored
    Given the funds reservation was retried with a new command
    When a late reply to the earlier attempt arrives
    Then it does not change the payment
    And only the reply to the current command counts
```

---

## 12. Payments: operational recovery

```gherkin
Feature: Recovering payments that need an operator
  Some failures need a person. Operations can fix them only through controlled,
  recorded actions, never by editing the database.

  @boundary
  Scenario: The release of the funds could not be confirmed
    Given a payment failed and its funds release was not confirmed after the retries
    Then the payment is COMPENSATION_FAILED and appears on the Payment Processing Monitor
    When Daniel retries the release
    Then the release is sent again and, once confirmed, the payment is FAILED with the funds released

  @boundary
  Scenario: A message could not be delivered to Kafka
    Given an outgoing payment message was parked after its delivery attempts
    Then it appears under "Parked messages" for operations, without its content
    When Daniel resends it with a reason of 10 to 500 characters
    Then the same message, with the same ID, is sent again
    And the resend is recorded on the payment's timeline with Daniel's name and reason
    And a receiver that already has the message ignores the repeat

  @security
  Scenario: Operators do not take business decisions
    When Daniel tries to start, approve or reject a payment
    Then he is refused
```

---

## 13. The audit trail

```gherkin
Feature: The audit trail
  Every business event of the platform is recorded in a tamper-evident trail. Auditors
  read it there and only there, and every look is itself recorded.

  Background:
    Given Sarah is an Auditor

  @happy-path @smoke
  Scenario: Sarah follows a record from start to end
    When Sarah searches the Audit Trail for Camilla's application number
    Then she sees every step: submitted, KYC, compliance, account opened
    And for each step: when, what, and who did it

  @happy-path @smoke
  Scenario: The reconciliation of a payment is attributed
    When Sarah opens the payment that two operators reconciled
    Then she sees the proposal by Daniel and the approval by Michael, with their evidence

  @security @smoke
  Scenario: Reading the trail is recorded too
    When Sarah searches the Audit Trail
    Then her search is itself recorded as an access entry

  @happy-path @smoke
  Scenario: The trail can be checked for tampering
    When Sarah verifies the integrity of the trail
    Then she is told that the chain is intact and anchored

  @boundary @security
  Scenario: A changed entry is detected
    Given someone with database access changed or removed an earlier entry
    When the trail is verified
    Then the verification reports the chain as broken, naming where
    And the Audit API shows as Degraded on Platform Health until the cause is investigated
    And a broken chain is never anchored as if it were good

  @security
  Scenario: Nobody can change the trail through the system
    Then the trail has no screen or endpoint to change or delete an entry
    And the database itself refuses changes and deletions for every user

  @boundary
  Scenario: Search input is limited
    When Sarah searches with a page size above the maximum, or a filter over 100 characters
    Then the search is refused with a clear message

  @security
  Scenario: Only auditors use the Audit Trail
    When anyone without the auditor role opens the Audit Trail
    Then they are refused
```

---

## 14. Operations: platform health and simulators

```gherkin
Feature: Platform health and the simulator switch
  Operations administrators see the health of every component on one page. In
  Development and Test they can also make the simulated external systems misbehave.

  Background:
    Given Daniel is an Operations Administrator

  @happy-path @smoke
  Scenario: Daniel sees the whole platform
    When Daniel opens Platform Health
    Then he sees every API, BFF, worker and simulator with its status and response time
    And the page refreshes every 10 seconds

  @boundary
  Scenario Outline: What the statuses mean
    Given a component reports <status>
    Then it means <meaning>

    Examples:
      | status    | meaning                                                                      |
      | Healthy   | working normally                                                             |
      | Degraded  | working, but something needs a look (a backlog, a stuck item, a retry)       |
      | Unhealthy | not working; the orchestrator should stop sending it traffic or restart it   |

  @resilience @smoke
  Scenario: Switching a simulator
    When Daniel switches the payment network to "Down"
    Then payments behave as if the real network were down
    And the change is logged with Daniel's LAN ID
    And the simulator returns to "Healthy" by itself after 15 minutes

  @boundary @security
  Scenario: The switch exists only for testing
    Given the platform runs outside Development and Test
    Then the simulator switch does not exist (the request is answered "not found")
    And the platform refuses to start if it is configured on

  @boundary @security
  Scenario: Too many changes
    When Daniel makes more than 10 simulator changes within a minute
    Then further changes are refused with "too many requests" until the minute has passed

  @security
  Scenario: Platform Health is for operations
    When Sophie, Ethan or Sarah tries to open Platform Health
    Then they are refused
```

---

## 15. Rules that apply everywhere

```gherkin
Feature: Platform-wide rules
  These rules hold for every context, so they are stated once.

  @security
  Scenario: Every request is authorized again
    Given a screen hides an action from a person
    When the person calls the action directly
    Then the API checks the person's role, permissions, branch and relationships again

  @security
  Scenario: Records of another branch do not exist for you
    When a person opens a record of a branch they do not belong to
    Then the answer is "not found", so they cannot even learn that it exists

  @sod @security
  Scenario: Nobody decides their own work
    Then the person who started a workflow never decides any step of it
    And a KYC decider never decides compliance for the same application
    And a compliance approver never approves the account for the same application
    And when the initiator is unknown, every decision is refused

  @boundary
  Scenario: Every decision that refuses needs a reason
    Then rejections need remarks, holds need a reason, and reconciliations need evidence

  @resilience
  Scenario: A message delivered twice changes nothing twice
    Given any business message is delivered more than once
    Then the receiving context processes it exactly once
    And the repeat is answered as "already processed"

  @resilience
  Scenario: A change and its message are saved together
    When a context saves a business change
    Then the message announcing it is saved in the same transaction
    And it is published even if the message broker was briefly unavailable

  @security
  Scenario: Too many requests
    When one person, machine or network address sends more requests than its budget allows
    Then the extra requests are refused with "too many requests" and a time to retry
    And other people are not affected

  @security
  Scenario: Errors reveal nothing internal
    When something unexpected goes wrong
    Then the person sees a plain message with a reference number
    And no technical details, database text or stack trace are shown

  @security
  Scenario: Account numbers are masked in logs
    When the system logs a payment or a funds command
    Then only the BSB and the last four digits of the account number appear
```

---

## 16. Traceability

| Section | Main source | Smoke tests |
| --- | --- | --- |
| 2. Signing in | [IDP-Requirements.md](../../src/IDP/doc/IDP-Requirements.md) | ST-1 |
| 3. The Shell | [Shell-Requirements.md](../../src/Shell/doc/Shell-Requirements.md) | ST-1, ST-2 |
| 4. Customers and onboarding | [CustomerOnboarding-Requirements.md](../../src/Microservices/CustomerOnboarding/doc/CustomerOnboarding-Requirements.md) | ST-2 |
| 5. Evidence documents | [DocumentsManagement-Requirements.md](../../src/Microservices/DocumentsManagement/doc/DocumentsManagement-Requirements.md) | ST-2, ST-10 |
| 6. KYC review | [CustomerKyc-Requirements.md](../../src/Microservices/CustomerKyc/doc/CustomerKyc-Requirements.md) | ST-2 |
| 7. Compliance review | [Compliance-Requirements.md](../../src/Microservices/Compliance/doc/Compliance-Requirements.md) | ST-2, ST-10 |
| 8. Account opening | [Accounts-Requirements.md](../../src/Microservices/Accounts/doc/Accounts-Requirements.md) | ST-2, ST-10 |
| 9. Monitoring onboarding | [Customer Onboarding API README](../../src/Microservices/CustomerOnboarding/API/README.md) | — |
| 10–12. Payments | [Payments-Requirements.md](../../src/Microservices/Payments/doc/Payments-Requirements.md) | ST-3 to ST-9 |
| 13. Audit trail | [Audit API README](../../src/Microservices/Audit/API/README.md) | ST-11 |
| 14. Operations | [Operations Journey API README](../../src/Microservices/Operations/JourneyApi/README.md) | ST-1, ST-8 to ST-10 |
| 15. Platform-wide rules | [Authorization-Model.md](../Authorization-Model.md), [Threat-Model.md](../Threat-Model.md) | all |