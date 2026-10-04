import { Badge } from './ui/feedback';

/** Risk rating from screening: LOW / MEDIUM / HIGH, with the clearance an approval needs. */
export function RiskBadge({ risk, clearance }: { risk?: string | null; clearance?: number | null }) {
  if (!risk) return <span className="text-ink-faint">—</span>;
  const tone = risk === 'HIGH' ? 'danger' : risk === 'MEDIUM' ? 'warning' : 'success';
  return (
    <Badge tone={tone} dot>
      {risk.charAt(0) + risk.slice(1).toLowerCase()}
      {clearance ? <span className="font-normal opacity-80"> · clearance {clearance}</span> : null}
    </Badge>
  );
}