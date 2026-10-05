/** The applicant's residential address as submitted (KYC's snapshot; never refreshed). */
export interface ApplicantAddress {
  addressLine1?: string | null;
  addressLine2?: string | null;
  city?: string | null;
  state?: string | null;
  postalCode?: string | null;
  countryCode?: string | null;
}

/** "12 George St, Unit 4, Sydney NSW 2000, AU" - or an em dash when nothing is recorded. */
export function formatAddress(address: ApplicantAddress | null | undefined): string {
  if (!address) return '—';
  const locality = [address.city, address.state, address.postalCode].filter(Boolean).join(' ');
  const parts = [address.addressLine1, address.addressLine2, locality, address.countryCode].filter(Boolean);
  return parts.length > 0 ? parts.join(', ') : '—';
}