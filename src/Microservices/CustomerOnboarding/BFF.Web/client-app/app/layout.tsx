import './globals.css';

export const metadata = {
  title: 'Customer Onboarding',
  description: 'Customer Onboarding MFE',
};

export default function RootLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <html lang="en">
      <body>{children}</body>
    </html>
  );
}
