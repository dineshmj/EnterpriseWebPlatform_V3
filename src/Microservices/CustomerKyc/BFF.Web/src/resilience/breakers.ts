import { CircuitBreaker } from './resilient-fetch';

/** One circuit breaker per downstream service (the name appears in the message and the metric). */
export const breakers = {
  kycApi: new CircuitBreaker('KYC'),
  documentsManagement: new CircuitBreaker('Documents Management'),
  identityProvider: new CircuitBreaker('sign-in'),
};