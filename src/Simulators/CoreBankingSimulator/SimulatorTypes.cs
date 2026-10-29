using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.Simulators.CoreBanking;

enum Behaviour { Healthy, Slow, Failing, Down, Refusing }

sealed record SimulatorState(Behaviour Behaviour, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour);

sealed record OpenAccountRequest(string CustomerNumber, string? AccountName, string? BranchCode, string? Product);

sealed record OpenedAccount(
    string AccountNumber,
    string Bsb,
    string Reference,
    string Product,
    DateTimeOffset OpenedAt);