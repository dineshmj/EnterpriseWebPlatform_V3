import './globals.css';

export const metadata = {
  title: 'Compliance',
  description: 'Compliance MFE',
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}