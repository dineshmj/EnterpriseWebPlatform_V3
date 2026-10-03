import { createHash } from 'node:crypto';
import * as fs from 'node:fs';
import * as path from 'node:path';

/**
 * Strict Content-Security-Policy for this BFF's Next.js static export
 * (OWASP A03 / XSS defence in depth). Same rules as the .NET BFFs
 * (Common.WebUtilities ContentSecurityPolicy):
 *
 *  - scripts: 'self' plus the SHA-256 hashes of the exported pages' inline
 *    scripts, computed at startup from the files actually served, so an injected
 *    script is blocked;
 *  - styles: 'unsafe-inline' (React style="…" attributes cannot be hashed).
 */
export function buildContentSecurityPolicy(
  staticRoot: string,
  frameAncestors: string[],
  frameSources: string[],
): string {
  const scriptSources = ["'self'", ...inlineScriptHashes(staticRoot)].join(' ');

  return [
    "default-src 'self'",
    `script-src ${scriptSources}`,
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data: blob:",
    "font-src 'self' data:",
    "connect-src 'self'",
    `frame-src ${frameSources.length === 0 ? "'none'" : frameSources.join(' ')}`,
    `frame-ancestors ${frameAncestors.join(' ')}`,
    "object-src 'none'",
    "base-uri 'self'",
    "form-action 'self'",
  ].join('; ');
}

export function inlineScriptHashes(root: string): string[] {
  const hashes = new Set<string>();
  if (!fs.existsSync(root)) return [];

  for (const file of htmlFiles(root)) {
    const html = fs.readFileSync(file, 'utf8');
    // <script ...> without a src attribute; the browser hashes the body exactly as written.
    for (const match of html.matchAll(/<script(?![^>]*\bsrc\s*=)[^>]*>([\s\S]*?)<\/script>/gi)) {
      const digest = createHash('sha256').update(match[1], 'utf8').digest('base64');
      hashes.add(`'sha256-${digest}'`);
    }
  }

  return [...hashes].sort();
}

function* htmlFiles(dir: string): Generator<string> {
  for (const entry of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    if (entry.isDirectory()) yield* htmlFiles(full);
    else if (entry.name.toLowerCase().endsWith('.html')) yield full;
  }
}
