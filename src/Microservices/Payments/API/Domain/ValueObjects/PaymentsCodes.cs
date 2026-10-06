namespace EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

/// <summary>The business state of a payment, as people see it.</summary>
public enum PaymentStatus
{
    Initiated = 1,
    ReservingFunds = 2,

    /// <summary>Above the approval tier: waits for a payments officer (step 5b).</summary>
    PendingApproval = 3,

    SendingToNetwork = 4,
    SettlingFunds = 5,
    Completed = 6,

    /// <summary>Not paid and nothing left to undo (no funds, or the approver said no).</summary>
    Rejected = 7,

    /// <summary>Something failed after the funds were reserved: the reservation is being released.</summary>
    Compensating = 8,

    /// <summary>Not paid; the reserved funds were released.</summary>
    Failed = 9,

    /// <summary>The release itself kept failing: funds may still be held. Operations must act.</summary>
    CompensationFailed = 10
}

/// <summary>Where the saga is: the step whose outcome it waits for, or Done.</summary>
public enum SagaStep
{
    ReserveFunds = 1,
    AwaitApproval = 2,
    SendToNetwork = 3,
    SettleFunds = 4,
    ReleaseFunds = 5,
    Done = 6
}

public enum SagaStatus
{
    /// <summary>A step is in progress: a reply, a timeout or a due network call will move it on.</summary>
    Running = 1,

    /// <summary>Waiting for a person (approval). No timer.</summary>
    WaitingForPerson = 2,

    /// <summary>Finished: the payment completed, or ended with nothing left held.</summary>
    Finished = 3,

    /// <summary>Compensation failed: stopped, waiting for operations to retry the release.</summary>
    Stuck = 4
}

/// <summary>
/// The persisted / published codes (UPPER_SNAKE_CASE), matching the CHECK constraints in
/// EwpPaymentsDb.sql and the Integration Event Catalogue.
/// </summary>
public static class PaymentsCodes
{
    public static string ToCode(this PaymentStatus status) => status switch
    {
        PaymentStatus.Initiated => "INITIATED",
        PaymentStatus.ReservingFunds => "RESERVING_FUNDS",
        PaymentStatus.PendingApproval => "PENDING_APPROVAL",
        PaymentStatus.SendingToNetwork => "SENDING_TO_NETWORK",
        PaymentStatus.SettlingFunds => "SETTLING_FUNDS",
        PaymentStatus.Completed => "COMPLETED",
        PaymentStatus.Rejected => "REJECTED",
        PaymentStatus.Compensating => "COMPENSATING",
        PaymentStatus.Failed => "FAILED",
        PaymentStatus.CompensationFailed => "COMPENSATION_FAILED",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static PaymentStatus ParsePaymentStatus(string code) =>
        Parse<PaymentStatus>(code, ToCode, "payment status");

    public static bool TryParsePaymentStatus(string? code, out PaymentStatus status)
    {
        try { status = ParsePaymentStatus(code?.Trim().ToUpperInvariant() ?? string.Empty); return true; }
        catch (ArgumentOutOfRangeException) { status = default; return false; }
    }

    public static string ToCode(this SagaStep step) => step switch
    {
        SagaStep.ReserveFunds => "RESERVE_FUNDS",
        SagaStep.AwaitApproval => "AWAIT_APPROVAL",
        SagaStep.SendToNetwork => "SEND_TO_NETWORK",
        SagaStep.SettleFunds => "SETTLE_FUNDS",
        SagaStep.ReleaseFunds => "RELEASE_FUNDS",
        SagaStep.Done => "DONE",
        _ => throw new ArgumentOutOfRangeException(nameof(step), step, null)
    };

    public static SagaStep ParseSagaStep(string code) =>
        Parse<SagaStep>(code, ToCode, "saga step");

    public static string ToCode(this SagaStatus status) => status switch
    {
        SagaStatus.Running => "RUNNING",
        SagaStatus.WaitingForPerson => "WAITING_FOR_PERSON",
        SagaStatus.Finished => "FINISHED",
        SagaStatus.Stuck => "STUCK",
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, null)
    };

    public static SagaStatus ParseSagaStatus(string code) =>
        Parse<SagaStatus>(code, ToCode, "saga status");

    private static TEnum Parse<TEnum>(string code, Func<TEnum, string> toCode, string what) where TEnum : struct, Enum
    {
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (toCode(value) == code)
                return value;
        }

        throw new ArgumentOutOfRangeException(nameof(code), code, $"Unknown {what} code.");
    }
}