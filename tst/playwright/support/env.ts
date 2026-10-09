import * as path from 'node:path';
import { config } from 'dotenv';

// tst\playwright\.env (git-ignored): the demo users' passwords and test settings.
config({ path: path.resolve(__dirname, '..', '.env'), quiet: true });

/** Minutes a saved sign-in is reused before signing in again (the BFF sessions slide for 30). */
export const sessionReuseMinutes = Number(process.env.EWP_SESSION_REUSE_MINUTES ?? '20');