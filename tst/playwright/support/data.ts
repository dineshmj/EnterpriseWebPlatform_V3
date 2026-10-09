import * as path from 'node:path';

/**
 * Test data. Every journey creates its OWN customer, with a unique name, so tests never depend
 * on each other or on demo records (and Customer Onboarding has no uniqueness rule on names).
 * The seed scenarios create the fixed demo customers (Camilla Parker, Jason Millers) instead.
 */
export interface NewCustomer {
  firstName: string;
  lastName: string;
  email: string;
  phoneNumber: string;
  addressLine1: string;
  city: string;
  state: string;
  postalCode: string;
  countryCode: string;
}

/** Letters only (a run stamp), so the name reads like a name and is still unique. */
function stamp(): string {
  let n = Date.now() - Date.UTC(2026, 0, 1);
  let s = '';
  do { s = String.fromCharCode(97 + (n % 26)) + s; n = Math.floor(n / 26); } while (n > 0);
  return s.charAt(0).toUpperCase() + s.slice(1);
}

/**
 * A customer of the Sydney branch (SYD001): Sophie and every officer are in Sydney, and the
 * Customer Onboarding API requires the address to be in the agent's branch city.
 */
export function newSydneyCustomer(firstName = 'Test', label = 'Journey'): NewCustomer {
  const unique = stamp();
  return {
    firstName,
    lastName: `${label}-${unique}`,
    email: `${firstName}.${unique}@example.com`.toLowerCase(),
    phoneNumber: '+61 412 345 678',
    addressLine1: '1 George Street',
    city: 'Sydney',
    state: 'NSW',
    postalCode: '2000',
    countryCode: 'AU',
  };
}

/** A minimal, valid PDF (Documents Management checks the file's signature, not its name). */
export function samplePdf(title: string): { name: string; mimeType: string; buffer: Buffer } {
  const text = `%PDF-1.4
1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj
2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj
3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] /Contents 4 0 R /Resources << /Font << /F1 5 0 R >> >> >> endobj
4 0 obj << /Length 60 >> stream
BT /F1 18 Tf 72 760 Td (${title.replace(/[()\\]/g, '')}) Tj ET
endstream endobj
5 0 obj << /Type /Font /Subtype /Type1 /BaseFont /Helvetica >> endobj
trailer << /Root 1 0 R >>
%%EOF`;
  return { name: `${title.replace(/\W+/g, '-').toLowerCase()}.pdf`, mimeType: 'application/pdf', buffer: Buffer.from(text, 'latin1') };
}

/** A customer's evidence: the identity proof (driver licence) and the tax proof (Notice of Assessment). */
export interface Evidence {
  identityProof: string | { name: string; mimeType: string; buffer: Buffer };
  taxProof: string | { name: string; mimeType: string; buffer: Buffer };
}

const documentsDir = path.resolve(__dirname, '..', 'fixtures', 'documents');

/**
 * The fixed demo customers, with the synthetic documents made for them (fixtures\documents,
 * names as printed on the documents). Used by the seed scenarios, which re-create the demo
 * records after a database re-create; the journeys use generated customers instead.
 */
export const demoCustomers = {
  camillaParkers: {
    customer: { ...newSydneyCustomer('Camilla'), lastName: 'Parkers', email: 'camilla.parkers@example.com' } as NewCustomer,
    evidence: { identityProof: `${documentsDir}/camilla-parkers/identity-proof.pdf`, taxProof: `${documentsDir}/camilla-parkers/tax-proof.pdf` } as Evidence,
  },
  jasonMillers: {
    customer: { ...newSydneyCustomer('Jason'), lastName: 'Millers', email: 'jason.millers@example.com' } as NewCustomer,
    evidence: { identityProof: `${documentsDir}/jason-millers/identity-proof.pdf`, taxProof: `${documentsDir}/jason-millers/tax-proof.pdf` } as Evidence,
  },
};
