### ST-1 — Sign-in, menus and Platform Health (all 8)

1.  Sign in to the Shell in each window.
2.  Each person should see only their own menu entries:
    -   Sophie: Customers / Onboarding / New Payment / Partner Products (Digital Gold);
    -   Ethan: KYC;
    -   Olivia: Compliance Monitor;
    -   Jack: Account Applications / Accounts;
    -   Emily: Payment Approvals;
    -   Sarah: Audit Trail only;
    -   Daniel: Platform Health and the Payment Processing Monitor;
    -   platform.admin: Platform Health, with no business approval screens.
3.  **Daniel → Platform Health: **every component should be**Healthy**. The page refreshes every 10 seconds.

### ST-2 — Onboarding happy path (Sophie → Ethan → Olivia → Jack)

1.  **Sophie → New Onboarding Application:**
    -   customer **Camilla Parkers**, residential address in **Sydney** (Sophie's branch);
    -   upload `tst\playwright\fixtures\documents\camilla-parkers\identity-proof.pdf` and `tax-proof.pdf`;
    -   submit.
    -   **Note the customer number's last digit:** 9 means HIGH risk, which Olivia (clearance 4) cannot approve. After a reset it should end in 1.
2.  **Bell notifications:** Ethan gets new KYC work. Sophie sees progress as the application moves on.
3.  **Ethan:** decide **Identity Verification**, then **Document Verification** (open both PDFs: they should show inline). The KYC case should be **Approved**.
4.  **Olivia → Compliance Monitor:** once screening finishes (LOW risk), take the case and **approve** it.
5.  **Jack → Account Applications:** approve. The account should open with a **$5,000.00** balance.
6.  **Sophie → View Onboarding Applications:** the application should show **Completed**.

### ST-3 — Payment below the approval threshold (Sophie)

-   **New Payment:** $250 from Camilla's account to BSB **062-000**, account **12345678** (Confirmation of Payee: **MATCH**).
-   **Expected:**
    -   no approval needed (threshold $1,000);
    -   the payment shows **COMPLETED**;
    -   the balance is **$4,750**.

### ST-4 — Payment above the threshold, approved (Sophie → Emily)

-   Sophie pays **$1,500** to the same payee. It waits in **Payment Approvals**.
-   Emily approves it.
-   **Expected:**
    -   **COMPLETED**;
    -   the balance is **$3,250**;
    -   Sophie is notified.

### ST-5 — Payment rejected (Sophie → Emily)

-   Sophie pays **$1,200**. Emily rejects it with a remark.
-   **Expected:**
    -   **REJECTED**;
    -   the hold is released, so the balance is still **$3,250**.

### ST-6 — Compensation without any switch: closed bank (Sophie)

-   Pay $100 to BSB **999-000**.
-   **Expected:**
    -   **FAILED** ("payee's account is closed");
    -   funds released, so the balance is still **$3,250**;
    -   the payment timeline shows the release step.

### ST-7 — Confirmation of Payee warnings (Sophie, no submission)

-   Payee account ending in **0** → the screen warns **NO\_MATCH**.
-   Payee account ending in **9** → it shows **CLOSE\_MATCH** ("J Citizen").
-   Cancel both.

### ST-8 — Simulator "LosingAnswers": paid exactly once (Daniel → Sophie)

1.  **Daniel:** set **Payment Network → LosingAnswers**.
2.  **Sophie:** pay **$300**.
3.  **Expected:**
    -   after 4 sends without an answer, the timeline shows **CONFIRM\_WITH\_NETWORK**;
    -   the status enquiry finds it SENT, and the payment ends **COMPLETED**, paid once;
    -   the balance is **$2,950**.
4.  **Daniel:** set the Payment Network back to **Healthy**.

### ST-9 — Simulator "Down" + four-eyes reconciliation (Daniel → Sophie → Daniel → platform.admin)

1.  **Daniel:** set **Payment Network → Down**.
2.  **Sophie:** pay **$400**. The payment stays in **CONFIRM\_WITH\_NETWORK**, and the $400 stays held.
3.  **After about 2 minutes:**
    -   Daniel's **Payment Processing Monitor** shows the **"Unconfirmed by network"** tile and badge;
    -   **Platform Health** shows the Payments API **Degraded**;
    -   Daniel and platform.admin get a "payment to reconcile" notification.
4.  **Daniel → payment details → Reconciliation:** propose **NOT\_SENT** with evidence text. Daniel's own **Approve** button should be disabled, with the reason shown.
5.  **platform.admin:** approve the proposal.
    -   **Expected:** **FAILED**, funds released, balance back to **$2,950**.
6.  **Daniel:** set the Payment Network back to **Healthy**.
    -   The switch also reverts by itself after 15 minutes.

### ST-10 — Simulators on onboarding: screening Down, then core banking Refusing (second customer)

1.  **Sophie:** onboard **Jason Millers** (Sydney, using the `jason-millers` PDFs). **Ethan** approves both KYC stages, but **only after** step 2.
2.  **Daniel:** set **Screening Provider → Down** before Ethan's last decision.
3.  **Expected:**
    -   the compliance case waits in **Screening**;
    -   after about 2 minutes, Platform Health shows the **Compliance API Degraded** ("waiting for screening").
4.  **Daniel:** set the Screening Provider back to **Healthy**. Within moments the risk is set, and Olivia approves.
5.  **Daniel:** set **Core Banking → Refusing**. **Jack** approves the account application.
6.  **Expected:**
    -   account opening **fails at once**;
    -   Sophie's application becomes **Rejected** (compensation), and Sophie is notified;
    -   Jason's documents are invalidated but kept.
7.  **Daniel:** set Core Banking back to **Healthy**.

### ST-11 — Audit trail (Sarah)

1.  **Search:**
    -   Camilla's application number shows the whole journey (submitted, KYC, compliance, account opened);
    -   the ST-9 payment shows the reconciliation steps, with **Daniel** as proposer and **Michael Turner** as approver.
2.  **Verify integrity** should report the chain **intact and anchored**.
3.  Search again for **Sarah's own** searches: they should be recorded as access entries.

### ST-12 — Digital Gold at the counter (Sophie; the Partner Bank on the home PC)

Needs the Partner Bank running on the PC (`.\ps\Start-PartnerBank.ps1 -Lan`), Temporal (`.\ps\temporal\Start-Temporal.ps1`) and `.\ps\run\Start-NodeServices.ps1` (it starts the Partner Products Worker).

1.  **Sophie → Partner Products → Digital Gold:** find **Camilla**; record **Form VII** (a new form number, signed today, her date of birth, photo ID and signature checked, a PDF scan) and **Send to the Partner Bank**. Expected: **Account open** with a `PB-DGA-…` number (or "Linked to account …" if the Partner Bank already holds hers).
2.  **Transfer slip:** **$20** from Camilla's account, signed today, her mobile, a scan. Then **Send & pay**. Expected: **Ordered** with the Partner Bank's order number and grams, and a payment number; within about 30 seconds **Gold allocated** (select Camilla again to refresh); the holding shows the grams; the Temporal UI (http://localhost:8233) shows `gold-purchase-DGP-…` **Completed**.
3.  **View at the Partner Bank ↗:** a new tab shows the Partner Bank's read-only page for Camilla's account, naming **somit (SYD001)**. Reloading it shows "This view is no longer available".
4.  **Daniel → Payments → Payment Processing Monitor** (optional): the $20 payment to "Partner Bank Gold Settlement" is **COMPLETED**.

### ST-13 — Step-up in the overlay (Daniel; MFA on)

Needs `.\ps\Toggle-MFA.ps1 -On` and the restarts it lists (switch it off again afterwards).

1.  Daniel signs in with his authenticator code, opens **Platform Health**, and waits until the code is older than the step-up limit (15 minutes by default).
2.  He switches the Payment Network to **Slow**. Expected: no new window; the page darkens and **"Confirm it's you"** asks only for the code.
3.  He enters the code. Expected: the overlay closes and the switch shows **Slow**; he is still signed in.
4.  He switches it back to **Healthy** at once: no code is asked.
5.  **Cancel** (optional): repeat after the limit and press **Cancel**: the change is not made.
---

**Running balance of Camilla's account:**

| After | Balance |
| --- | --- |
| ST-2 | $5,000 |
| ST-3 | $4,750 |
| ST-4 | $3,250 |
| ST-5 | $3,250 |
| ST-6 | $3,250 |
| ST-8 | $2,950 |
| ST-9 | $2,950 |
| ST-12 | $2,930 |

**Not covered on purpose:**

-   **MFA step-up** in the main run: MFA is normally off; ST-13 covers it with MFA switched on.
-   **The onboarding age alert:** in Development it needs a machine hand-off stuck for 5 minutes, which means stopping a subscriber.