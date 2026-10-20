import { defineSignal } from '@temporalio/workflow';

/**
 * "The purchase's payment has an outcome - look again." Sent by the payment-event listener when
 * payments.payment.events reports the payment completed, rejected or failed. It carries nothing:
 * the workflow reads the outcome from the Payments API itself (the event is a hint, not the truth).
 */
export const paymentSettledSignal = defineSignal('paymentSettled');