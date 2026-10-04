'use client';

import { KycCaseWorkQueue } from '../../../../components/KycCaseWorkQueue';

export default function KycCasesPage() {
  return (
    <KycCaseWorkQueue
      title="KYC work queue"
      subtitle="Awaiting review"
      description="Cases of your branch awaiting a KYC decision. Open a case to see its details and review the evidence."
    />
  );
}