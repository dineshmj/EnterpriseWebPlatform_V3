import './globals.css';

export const metadata = {
  title: 'Accounts',
  description: 'Accounts MFE',
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}