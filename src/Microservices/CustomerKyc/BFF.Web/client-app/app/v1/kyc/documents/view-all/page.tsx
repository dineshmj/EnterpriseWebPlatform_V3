'use client';

import { KycDocumentVerificationView } from '../../../../components/KycDocumentVerificationView';

export default function DocumentVerificationPage() {
  return (
    <KycDocumentVerificationView
      title="Document verification"
      subtitle="Review the tax proof submitted for the selected KYC case."
      documentLabel="Tax Proof"
      documentRoute="tax-proof"
      verificationStage="DocumentVerification"
    />
  );
}