using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.Simulators.ScreeningProvider;

enum Behaviour { Healthy, Slow, Failing, Down }

sealed record SimulatorState(Behaviour Behaviour, string? ForcedOutcome, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour, string? ForcedOutcome);

sealed record ScreeningRequest(string CustomerNumber, string ApplicationNumber, Guid RequestId, string[]? Lists, ScreeningSubject? Subject);

// Who is screened: real providers match the lists on name and address.
sealed record ScreeningSubject(string? FirstName, string? LastName, string? AddressLine1, string? City, string? State, string? PostalCode, string? CountryCode);

sealed record ScreeningMatch(string List, double Score);

sealed record ScreeningResponse(
    string ScreeningId,
    string Provider,
    string Outcome,
    string[] ListsChecked,
    ScreeningMatch[] Matches,
    DateTimeOffset ScreenedAt);