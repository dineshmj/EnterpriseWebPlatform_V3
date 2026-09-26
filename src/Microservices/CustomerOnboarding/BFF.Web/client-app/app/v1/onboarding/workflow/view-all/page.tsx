import { MfeShell } from '../../../../components/MfeShell';

export default function WorkflowPage() {
  return <MfeShell title="Onboarding Workflow Monitor" subtitle="Onboarding Workflow · Saga and asynchronous processing visibility">
    <div className="card">
      <h2>Current workflow architecture</h2>
      <div className="workflow">
        <div className="step"><span className="stepNo">1</span><div><div className="stepTitle">Customer created</div><div className="stepText">CO API commits the customer and transactional Outbox record.</div></div></div>
        <div className="step"><span className="stepNo">2</span><div><div className="stepTitle">customer.created published</div><div className="stepText">Customer Outbox Publisher publishes the integration event to Kafka.</div></div></div>
        <div className="step"><span className="stepNo">3</span><div><div className="stepTitle">KYC subscriber</div><div className="stepText">Customer KYC subscriber consumes customer.created. KYC API integration is the next vertical slice.</div></div></div>
        <div className="step"><span className="stepNo">4</span><div><div className="stepTitle">Human decision gates</div><div className="stepText">KYC and Compliance approvals will be added with their bounded contexts and workflow projection.</div></div></div>
      </div>
    </div>
    <div className="card">
      <h2>Implementation status</h2>
      <div style={{ color:'#667085', fontSize:'.8rem', lineHeight:1.6 }}>
        This screen is intentionally read-only at this stage. SignalR workflow projection will become the authoritative real-time monitor once the KYC bounded context is implemented.
      </div>
    </div>
  </MfeShell>;
}
