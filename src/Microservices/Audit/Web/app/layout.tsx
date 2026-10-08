import { headers } from 'next/headers';
import './globals.css';

export const metadata = {
  title: 'Audit Trail',
  description: 'Audit MFE',
};

export default async function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  // Reading the request makes every page render per request, so each gets its CSP nonce.
  await headers();
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}