namespace EnterpriseWebPlatform.CustomerOnboarding.Infrastructure.Messaging;

// ┌─ CONTRACT ───────────────────────────────────────────────────────────────────────────
// │ Topic customer.personal-data.erasure, key = the erasure request's ID (one workflow per
// │ request: the decision's causation is the request), three event types:
// │ PersonalDataErasureRequested (Status PENDING), PersonalDataErasureRejected (REJECTED)
// │ and CustomerPersonalDataErased (ERASED). The envelope's initiator is the requester;
// │ DecisionBy* is the second person. Identifiers and a reason code only: no name, no
// │ address, no remarks - so nothing published here ever needs erasing itself.
// │ CustomerPersonalDataErased is what every other context acts on (shred its own copy).
// └──────────────────────────────────────────────────────────────────────────────────────
public sealed record PersonalDataErasureIntegrationEvent(
    long ErasureRequestId,
    long CustomerId,
    string CustomerNumber,
    string ReasonCode,
    string Status,
    string? DecisionByUserId,
    string? DecisionByLanId);