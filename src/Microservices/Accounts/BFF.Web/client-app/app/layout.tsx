import Script from 'next/script';
import './globals.css';

export const metadata = {
  title: 'Accounts',
  description: 'Accounts MFE',
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>
        {/* The Shell origin from this BFF (lib/shell-origin.ts): loaded before Next.js starts the page. */}
        <Script src="/ewp-runtime-config.js" strategy="beforeInteractive" />
        {children}
      </body>
    </html>
  );
}