 'use client';

import { KycCaseWorkQueue } from '../../../../components/KycCaseWorkQueue';

export default function KycCasesPage() {
  return (
    <KycCaseWorkQueue
      title="KYC approval work queue"
      subtitle="Cases awaiting KYC review are displayed here."
      description="Select the KYC Case ID to open the case detail view. Authorization is enforced by the Customer KYC API; this screen is only the user interface."
    />
  );
}
