'use client';

import { KycDocumentVerificationView } from '../../../../components/KycDocumentVerificationView';

export default function IdentityVerificationPage() {
  return (
    <KycDocumentVerificationView
      title="Identity verification"
      subtitle="Review the identity proof submitted for the selected KYC case."
      documentLabel="Driver's License / Identity Proof"
      documentRoute="identity-proof"
      verificationStage="IdentityVerification"
    />
  );
}
