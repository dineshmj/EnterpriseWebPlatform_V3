/**
 * The routes operations may cut, one per API: every server-side caller of that API goes through
 * it (the browser, the IDP and Kafka never do). The key is the route's name in
 * ps\toxiproxy\toxiproxy.json; the component is the API's name on Platform Health, whose probe
 * goes to the API directly - so a cut API still shows Healthy there, with a "cut at the proxy" mark.
 *
 * Not here: the Operations Journey API (cutting it would take away these controls) and the
 * Partner Bank API (its TLS name points at the Partner Bank's machine; a local proxy cannot keep it).
 */
export const faultRoutes = [
  { key: 'customer-onboarding-api', component: 'Customer Onboarding API', usedBy: 'Customer Onboarding BFF, Partner Products Journey API, OnboardingOutcomeSubscriber' },
  { key: 'customer-kyc-api', component: 'Customer KYC API', usedBy: 'Customer KYC BFF, KycCaseOpeningSubscriber' },
  { key: 'documents-management-api', component: 'Documents Management API', usedBy: 'Customer Onboarding BFF, Customer KYC BFF, Partner Products Journey API, DocumentInvalidationSubscriber' },
  { key: 'compliance-api', component: 'Compliance API', usedBy: 'Compliance BFF, Operations Journey API (resend), ComplianceCaseOpeningSubscriber' },
  { key: 'accounts-api', component: 'Accounts API', usedBy: 'Accounts BFF, Payments BFF, Partner Products and Operations Journey APIs, two Accounts subscribers' },
  { key: 'payments-api', component: 'Payments API', usedBy: 'Payments BFF, Audit and Partner Products Journey APIs, Partner Products Worker, PaymentsSagaReplySubscriber' },
  { key: 'notifications-api', component: 'Notifications API', usedBy: 'Shell BFF (the bell\'s list and live connection), NotificationsSubscriber' },
  { key: 'audit-api', component: 'Audit API', usedBy: 'Audit Journey API' },
  { key: 'partner-products-api', component: 'Partner Products API', usedBy: 'Partner Products Journey API, Partner Products Worker, Operations Journey API (resend)' },
  { key: 'audit-journey-api', component: 'Audit Journey API', usedBy: 'Audit web' },
  { key: 'partner-products-journey-api', component: 'Partner Products Journey API', usedBy: 'Partner Products web' },
] as const;

export type FaultRouteKey = (typeof faultRoutes)[number]['key'];