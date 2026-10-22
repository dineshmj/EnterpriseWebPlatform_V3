import { headers } from 'next/headers';
import Script from 'next/script';
import './globals.css';

export const metadata = {
  title: 'Operations',
  description: 'Operations MFE',
};

export default async function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  // Reading the request makes every page render per request, so each gets its CSP nonce -
  // also on the runtime-config script ('strict-dynamic' trusts only scripts with the nonce).
  const nonce = (await headers()).get('x-nonce') ?? undefined;
  return (
    <html lang="en">
      <body>
        {/* The Shell origin from this BFF (lib/shell-origin.ts): loaded before Next.js starts the page. */}
        <Script src="/ewp-runtime-config.js" strategy="beforeInteractive" nonce={nonce} />
        {children}
      </body>
    </html>
  );
}