import { redirect } from 'next/navigation';
import { TRAIL_PAGE } from '@/lib/server/return-url';

export default function Home() {
  redirect(TRAIL_PAGE);
}