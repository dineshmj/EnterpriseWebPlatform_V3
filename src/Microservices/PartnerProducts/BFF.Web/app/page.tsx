import { redirect } from 'next/navigation';
import { DIGITAL_GOLD_PAGE } from '@/lib/server/return-url';

export default function Home() {
  redirect(DIGITAL_GOLD_PAGE);
}