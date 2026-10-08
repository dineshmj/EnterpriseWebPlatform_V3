import path from 'node:path';
import type { NextConfig } from 'next';

// Not a static export: this app RUNS - its server side is the light BFF (sign-in, session,
// server actions that call the Audit Journey API). Started by server.mjs over HTTPS.
const nextConfig: NextConfig = {
  reactStrictMode: true,
  poweredByHeader: false,
  serverExternalPackages: ['pg', 'openid-client'],
  turbopack: {
    // The repository's src folder: lets the build read the shared design tokens in
    // src/Common/DesignSystem. Turbopack never reads above its root.
    root: path.join(__dirname, '../../..'),
  },
};

export default nextConfig;