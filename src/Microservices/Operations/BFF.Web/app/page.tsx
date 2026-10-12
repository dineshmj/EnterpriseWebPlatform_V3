import { redirect } from 'next/navigation';
import { HEALTH_PAGE } from '@/lib/server/return-url';

export default function Home() {
  redirect(HEALTH_PAGE);
}