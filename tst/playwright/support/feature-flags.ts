import * as fs from 'node:fs';
import * as path from 'node:path';
import { test } from '@playwright/test';
import './env';

/**
 * The platform's feature flags on this PC: the repository's flag file feature-flags\ewp-flags.json,
 * which every API re-reads every 5 s (Common/WebUtilities/FeatureFlags). A test switches a flag by
 * editing the file and puts the file back exactly as it was afterwards.
 */
export const flagFile = path.resolve(__dirname, '..', '..', '..', 'feature-flags', 'ewp-flags.json');

/** The APIs poll the file every 5 s: two polls and a margin. */
const pickedUpMs = 12_000;

// ┌─ :: ARCHITECTURE :: Only when the APIs read the file ─────────────────────────────────────────
// │ Why : With EWP_LAUNCHDARKLY_SDK_KEY set, the APIs read LaunchDarkly and ignore the file -
// │       editing it would prove nothing, and the test would fail for the wrong reason.
// │ How : The key is a user-level environment variable, so this process sees it too: then the
// │       spec is skipped with the reason (switch the flag in LaunchDarkly by hand instead).
// │ Ref : feature-flags/README.md
// └──────────────────────────────────────────────────────────────────────────────────────────────
export const flagFileNotUsedReason = process.env.EWP_LAUNCHDARKLY_SDK_KEY
  ? 'EWP_LAUNCHDARKLY_SDK_KEY is set: the APIs read LaunchDarkly, not feature-flags\\ewp-flags.json.'
  : undefined;

interface FlagFile { flags: Record<string, { defaultVariant: string }> }

/**
 * Runs `work` with `flag` switched to `variant` ('on' / 'off'); the file is restored byte for byte
 * afterwards, also when `work` fails, and the APIs are given time to pick up each change.
 */
export async function withFlag<T>(flag: string, variant: 'on' | 'off', work: () => Promise<T>): Promise<T> {
  const original = fs.readFileSync(flagFile, 'utf8');
  const flags = JSON.parse(original) as FlagFile;
  if (!flags.flags[flag]) throw new Error(`${flag} is not in ${flagFile}.`);
  flags.flags[flag].defaultVariant = variant;

  await test.step(`Feature flag ${flag} -> ${variant} (and the APIs pick it up)`, async () => {
    fs.writeFileSync(flagFile, JSON.stringify(flags, null, 2));
    await new Promise(resolve => setTimeout(resolve, pickedUpMs));
  });
  try {
    return await work();
  } finally {
    await test.step(`Feature flag ${flag} back as it was`, async () => {
      fs.writeFileSync(flagFile, original);
      await new Promise(resolve => setTimeout(resolve, pickedUpMs));
    });
  }
}