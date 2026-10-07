import path from 'node:path';
import type { NextConfig } from 'next';

const nextConfig: NextConfig = {
  output: 'export',
  trailingSlash: true,
  reactStrictMode: true,
  turbopack: {
    // The repository's src folder: lets the build read the shared design tokens in
    // src/Common/DesignSystem (outside this app). Turbopack never reads above its root.
    root: path.join(__dirname, '../../../..'),
  },
};

export default nextConfig;