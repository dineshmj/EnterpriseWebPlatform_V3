import type { Behaviour } from './operations-types';

/** How a simulator's behaviour looks on both operations pages (Platform Health and the controls). */

export const BEHAVIOUR_TONE: Record<Behaviour | 'Unknown', 'success' | 'warning' | 'danger' | 'neutral'> = {
  Healthy: 'success', Slow: 'warning', Failing: 'danger', Down: 'danger', Refusing: 'warning', LosingAnswers: 'danger', Unknown: 'neutral',
};

export const BEHAVIOUR_LABEL: Record<Behaviour, string> = {
  Healthy: 'Healthy', Slow: 'Slow', Failing: 'Failing', Down: 'Down', Refusing: 'Refusing',
  LosingAnswers: 'Losing answers (sends, never replies)',
};