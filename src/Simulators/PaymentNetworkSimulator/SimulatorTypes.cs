using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace EnterpriseWebPlatform.Simulators.PaymentNetwork;

enum Behaviour { Healthy, Slow, Failing, Down, Refusing, LosingAnswers }

sealed record SimulatorState(Behaviour Behaviour, int SlowDelaySeconds);

sealed record BehaviourChange(Behaviour Behaviour);

sealed record AccountParty(string? Bsb, string? AccountNumber, string? Name);

sealed record PaymentRequest(
    string? EndToEndId,
    AccountParty? Debtor,
    AccountParty? Creditor,
    decimal Amount,
    string? Currency,
    string? RemittanceInformation);

sealed record SentPayment(string NetworkReference, string Status, DateTimeOffset SettledAt);

sealed record PayeeConfirmationRequest(string? Bsb, string? AccountNumber, string? AccountName);

sealed record PayeeConfirmation(string Result, string? AccountNameHeld);

sealed record BsbEntry(string Bsb, string Bank, string Branch, string State, bool Npp);

enum FraudBehaviour { Healthy, Slow, Failing, Down }

sealed record FraudScoringState(FraudBehaviour Behaviour, int SlowDelayMilliseconds);

sealed record FraudBehaviourChange(FraudBehaviour Behaviour);

sealed record FraudScoreRequest(
    string? EndToEndId,
    string? CustomerNumber,
    AccountParty? Debtor,
    AccountParty? Creditor,
    decimal Amount,
    string? Currency,
    string? Channel,
    bool FirstPaymentToPayee);

sealed record FraudScore(int Score, string Decision, IReadOnlyList<string> Reasons);

/// <summary>The demo's fraud rules: fixed, so a demo (and a test) knows the answer in advance.</summary>
static class FraudRules
{
    public static FraudScore Score(FraudScoreRequest request)
    {
        if (request.Creditor?.AccountNumber?.Trim().EndsWith("13", StringComparison.Ordinal) == true)
            return new FraudScore(950, "DECLINE", ["PAYEE_ON_MULE_LIST"]);
        if (request.Amount >= 50_000m)
            return new FraudScore(720, "REVIEW", ["HIGH_AMOUNT"]);
        if (request.Amount >= 500m && request.FirstPaymentToPayee)
            return new FraudScore(680, "REVIEW", ["NEW_PAYEE", "HIGH_AMOUNT_FOR_NEW_PAYEE"]);

        var score = (int)Math.Min(400m, 50m + request.Amount / 25m) + (request.FirstPaymentToPayee ? 50 : 0);
        return new FraudScore(score, "ALLOW", request.FirstPaymentToPayee ? ["NEW_PAYEE"] : []);
    }
}

/// <summary>
/// A tiny stand-in for the Australian BSB directory: the first two digits name the bank,
/// the third the state. Real enough for a demo; 999 is a deliberately "closed" bank.
/// </summary>
static class BsbDirectory
{
    private static readonly Dictionary<string, string> Banks = new()
    {
        ["01"] = "ANZ", ["03"] = "Westpac", ["06"] = "Commonwealth Bank", ["08"] = "National Australia Bank",
        ["11"] = "St.George Bank", ["18"] = "Macquarie Bank", ["48"] = "Suncorp Bank", ["63"] = "Bendigo Bank",
        ["73"] = "Westpac", ["80"] = "Cuscal (credit unions)", ["99"] = "Closed Bank (demo)"
    };

    private static readonly Dictionary<char, string> States = new()
    {
        ['2'] = "NSW", ['3'] = "VIC", ['4'] = "QLD", ['5'] = "SA", ['6'] = "WA", ['7'] = "TAS", ['0'] = "NT", ['1'] = "ACT"
    };

    public static BsbEntry? Find(string? bsb)
    {
        var digits = new string((bsb ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (digits.Length != 6 || !Banks.TryGetValue(digits[..2], out var bank))
            return null;

        var state = States.GetValueOrDefault(digits[2], "NSW");
        var formatted = $"{digits[..3]}-{digits[3..]}";
        return new BsbEntry(formatted, bank, $"{bank} branch {digits[3..]}, {state}", state, Npp: digits[..2] != "99");
    }
}