'use client';

import { useEffect, useState } from 'react';
import { MfeShell } from './MfeShell';
import { getJson, postJson } from '../lib/api';

interface KycCase {
  kycCaseId: number;
  customerNumber: string;
  status: string;
  identityVerificationStatus: string;
  documentVerificationStatus: string;
  initiatedByUserId?: string | null;
  createdAt: string;
  updatedAt: string;
}

interface PageResult {
  items: KycCase[];
  pageNumber: number;
  pageSize: number;
  totalCount: number;
}

interface DocumentInfo {
  documentId: string;
  fileName: string;
  contentType: string;
  size: number;
  createdAt: string;
  documentType?: string | null;
  businessReference?: string | null;
  selectionReason: string;
}

interface DecisionResponse {
  kycCaseId?: number;
  stageStatus?: string;
  overallStatus?: string;
  decisionRemarks?: string | null;
}

interface Props {
  title: string;
  subtitle: string;
  documentLabel: string;
  documentRoute: 'identity-proof' | 'tax-proof';
  verificationStage: 'IdentityVerification' | 'DocumentVerification';
}

export function KycDocumentVerificationView({
  title,
  subtitle,
  documentLabel,
  documentRoute,
  verificationStage,
}: Props) {
  const [cases, setCases] = useState<KycCase[]>([]);
  const [selectedCaseId, setSelectedCaseId] = useState<number | null>(null);
  const [document, setDocument] = useState<DocumentInfo | null>(null);
  const [decisionRemarks, setDecisionRemarks] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [decisionMessage, setDecisionMessage] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadingDocument, setLoadingDocument] = useState(false);
  const [submittingDecision, setSubmittingDecision] = useState<'approve' | 'reject' | null>(null);

  const selectedCase = cases.find(item => item.kycCaseId === selectedCaseId) ?? null;
  const stageStatus = selectedCase
    ? (verificationStage === 'IdentityVerification'
      ? selectedCase.identityVerificationStatus
      : selectedCase.documentVerificationStatus)
    : null;
  const awaitingDecision = stageStatus === 'PENDING_REVIEW';

  useEffect(() => {
    getJson<PageResult>(
      `/bff/api/kyc/cases?pageNumber=1&pageSize=25&status=PENDING_REVIEW&stage=${encodeURIComponent(verificationStage)}`,
    )
      .then(result => {
        setCases(result.items);
        if (result.items.length > 0) {
          const raw = new URLSearchParams(window.location.search).get('caseId');
          const requested = Number(raw);
          const selected = result.items.some(x => x.kycCaseId === requested)
            ? requested
            : result.items[0].kycCaseId;
          setSelectedCaseId(selected);
        }
      })
      .catch(e => setError(e instanceof Error ? e.message : 'Unable to load KYC cases.'))
      .finally(() => setLoading(false));
  }, [verificationStage]);

  useEffect(() => {
    if (selectedCaseId === null) {
      setDocument(null);
      return;
    }

    setLoadingDocument(true);
    setError(null);
    setDecisionMessage(null);
    setDecisionRemarks('');

    getJson<DocumentInfo>(`/bff/api/kyc/cases/${selectedCaseId}/${documentRoute}`)
      .then(setDocument)
      .catch(e => {
        setDocument(null);
        setError(e instanceof Error ? e.message : `Unable to load ${documentLabel}.`);
      })
      .finally(() => setLoadingDocument(false));
  }, [selectedCaseId, documentRoute, documentLabel]);

  async function submitDecision(action: 'approve' | 'reject') {
    if (selectedCaseId === null || !awaitingDecision) return;

    const remarks = decisionRemarks.trim();
    if (action === 'reject' && !remarks) {
      setError('Decision remarks are required when rejecting a KYC case.');
      return;
    }

    setError(null);
    setDecisionMessage(null);
    setSubmittingDecision(action);

    try {
      const result = await postJson<DecisionResponse>(
        `/bff/api/kyc/cases/${selectedCaseId}/${verificationStage === 'IdentityVerification' ? 'identity-verification' : 'document-verification'}/${action}`,
        { decisionRemarks: remarks },
      );

      const stageName = verificationStage === 'IdentityVerification'
        ? 'Identity verification'
        : 'Document verification';
      const stageStatus = result.stageStatus ?? (action === 'approve' ? 'APPROVED' : 'REJECTED');
      const overallStatus = result.overallStatus ?? selectedCase?.status ?? 'PENDING_REVIEW';

      setCases(previous => previous.filter(item => item.kycCaseId !== selectedCaseId));
      setSelectedCaseId(null);
      setDocument(null);
      setDecisionRemarks('');
      setDecisionMessage(
        `${stageName} for KYC Case ${selectedCaseId} was ${stageStatus.toLowerCase()}. Overall KYC status: ${overallStatus}.`,
      );
    } catch (e) {
      setError(e instanceof Error ? e.message : `Unable to ${action} the KYC case.`);
    } finally {
      setSubmittingDecision(null);
    }
  }

  return (
    <MfeShell>
      <section className="card">
        <div className="header compact-header">
          <div>
            <h2>{title}</h2>
            <div className="hint">{subtitle}</div>
          </div>
          <span className="badge">{stageStatus ?? 'PENDING REVIEW'}</span>
        </div>

        {loading && <div className="empty">Loading KYC work queue...</div>}

        {!loading && cases.length === 0 && (
          <div className="empty">No KYC cases are currently awaiting this verification stage.</div>
        )}

        {cases.length > 0 && (
          <>
            <div className="field">
              <label htmlFor="kycCaseSelector">KYC Case</label>
              <select
                id="kycCaseSelector"
                value={selectedCaseId ?? ''}
                onChange={event => setSelectedCaseId(Number(event.target.value))}
              >
                {cases.map(item => (
                  <option key={item.kycCaseId} value={item.kycCaseId}>
                    Case {item.kycCaseId} — {item.customerNumber} — {item.status}
                  </option>
                ))}
              </select>
            </div>

            {error && <div className="error">{error}</div>}
            {decisionMessage && <div className="success">{decisionMessage}</div>}

            {loadingDocument && <div className="empty">Loading {documentLabel}...</div>}

            {document && !loadingDocument && (
              <>
                <div className="card">
                  <h3>{documentLabel}</h3>
                  <div className="detail-grid">
                    <div className="detail-item">
                      <span>Document ID</span>
                      <strong>{document.documentId}</strong>
                    </div>
                    <div className="detail-item">
                      <span>File</span>
                      <strong>{document.fileName}</strong>
                    </div>
                    <div className="detail-item">
                      <span>Type</span>
                      <strong>{document.contentType}</strong>
                    </div>
                    <div className="detail-item">
                      <span>Size</span>
                      <strong>{document.size.toLocaleString()} bytes</strong>
                    </div>
                  </div>
                </div>

                <div className="card">
                  <h3>PDF Preview</h3>
                  <iframe
                    title={`${documentLabel} PDF`}
                    src={`/bff/api/kyc/cases/${selectedCaseId!}/${documentRoute}/content`}
                    style={{
                      width: '100%',
                      height: '720px',
                      border: '1px solid #d1d5db',
                      borderRadius: '8px',
                      background: '#fff',
                    }}
                  />
                </div>

                <div className="card decision-card">
                  <h3>Human KYC Decision</h3>
                  <div className="hint">
                    Enter the remarks supporting the approval or rejection. The KYC API is responsible for the final authorization decision.
                  </div>

                  <label className="decision-label" htmlFor="decisionRemarks">
                    Remarks for decision
                  </label>
                  <textarea
                    id="decisionRemarks"
                    value={decisionRemarks}
                    onChange={event => setDecisionRemarks(event.target.value)}
                    maxLength={4000}
                    rows={5}
                    disabled={!awaitingDecision || submittingDecision !== null}
                    placeholder="Enter approval notes or the reason for rejection..."
                  />
                  <div className="character-count">{decisionRemarks.length} / 4000</div>

                  <div className="decision-actions">
                    <button
                      type="button"
                      className="danger-button"
                      onClick={() => submitDecision('reject')}
                      disabled={!awaitingDecision || submittingDecision !== null}
                    >
                      {submittingDecision === 'reject' ? 'Rejecting...' : 'Reject'}
                    </button>
                    <button
                      type="button"
                      className="primary-button"
                      onClick={() => submitDecision('approve')}
                      disabled={!awaitingDecision || submittingDecision !== null}
                    >
                      {submittingDecision === 'approve' ? 'Approving...' : 'Approve'}
                    </button>
                  </div>
                </div>
              </>
            )}
          </>
        )}
      </section>
    </MfeShell>
  );
}
