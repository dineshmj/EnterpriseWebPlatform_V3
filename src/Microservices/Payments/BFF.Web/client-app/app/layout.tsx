import './globals.css';

export const metadata = {
  title: 'Payments',
  description: 'Payments MFE',
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}