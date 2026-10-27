using System.Globalization;

using EnterpriseWebPlatform.Payments.Api.Application.Queries;
using EnterpriseWebPlatform.Payments.Api.Domain.ValueObjects;

namespace EnterpriseWebPlatform.Payments.Api.Bian;

/// <summary>A BIAN payment order, translated into what EWP's InitiatePaymentCommand needs.</summary>
public sealed record BianPaymentOrder(
    string CustomerNumber,
    string FromBsb,
    string FromAccountNumber,
    string PayeeName,
    string ToBsb,
    string ToAccountNumber,
    decimal Amount,
    string? Reference);

/// <summary>A decision on an order awaiting approval: approve, or reject with remarks.</summary>
public sealed record BianOrderDecision(bool Approve, string? Remarks);

/// <summary>
/// Translates between BIAN's PaymentOrderInitiation contract and EWP's payment. Pure: no I/O,
/// so every mapping rule is unit-tested (tst/dotnet/Payments.Api.Tests/Bian).
/// </summary>
public static class PaymentOrderInitiationMapper
{
    /// <summary>The path value naming the order's approval step in .../OrderInitiation/{id}/Exchange (one per payment).</summary>
    public const string ApprovalStep = "approval";

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ Where EWP's payment sits in BIAN's properties (doc/bian/README.md, "Payments"):
    // │   payer = the customer (PayerIdentifier = customer number), paying from an account
    // │   (PayerBankIdentifier = BSB, PayerProductInstanceReference = account number); payee
    // │   likewise. BIAN's Payee has no name property, only PayeeIdentifier: a domestic payee is
    // │   identified by name + BSB + account, so the payee's name goes there. The reference
    // │   (remittance information) is BIAN's PaymentPurpose ("details captured for reference").
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static (BianPaymentOrder? Order, IReadOnlyList<string> Errors) ToPaymentOrder(PaymentOrderInitiationTransaction? request)
    {
        var errors = new List<string>();
        if (request is null)
            return (null, ["A PaymentOrderInitiationTransaction body is required."]);

        // SEC: BIAN makes everything optional; EWP requires what a payment needs and names what is missing.
        var customer = Required(request.PayerReference?.PayerIdentifier, "PayerReference.PayerIdentifier (the customer number)", errors);
        var fromBsb = Required(request.PayerBankReference?.PayerBankIdentifier, "PayerBankReference.PayerBankIdentifier (the paying BSB)", errors);
        var fromAccount = Required(request.PayerProductInstanceReference?.ProductAgreementIdentification?.IdentifierValue,
            "PayerProductInstanceReference.ProductAgreementIdentification.IdentifierValue (the paying account number)", errors);
        var payee = Required(request.PayeeReference?.PayeeIdentifier, "PayeeReference.PayeeIdentifier (the payee's name)", errors);
        var toBsb = Required(request.PayeeBankReference?.PayeeBankIdentifier, "PayeeBankReference.PayeeBankIdentifier (the payee's BSB)", errors);
        var toAccount = Required(request.PayeeProductInstanceReference?.ProductAgreementIdentification?.IdentifierValue,
            "PayeeProductInstanceReference.ProductAgreementIdentification.IdentifierValue (the payee's account number)", errors);

        var amountText = Required(request.Amount?.AmountValue, "Amount.AmountValue", errors);
        decimal amount = 0;
        if (amountText is not null && !decimal.TryParse(amountText, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out amount))
            errors.Add("Amount.AmountValue must be a decimal number with a '.' (e.g. 125.50).");

        // SEC: the currency, where given (in either place BIAN offers), must be the one EWP pays in.
        foreach (var currency in new[] { request.Amount?.AmountCurrency?.Currencycode, request.Currency?.Currencycode })
        {
            if (!string.IsNullOrWhiteSpace(currency) && !string.Equals(currency.Trim(), PaymentRules.Aud, StringComparison.OrdinalIgnoreCase))
                errors.Add($"Only {PaymentRules.Aud} payments are supported (currency '{currency.Trim()}').");
        }

        return errors.Count > 0
            ? (null, errors)
            : (new BianPaymentOrder(customer!, fromBsb!, fromAccount!, payee!, toBsb!, toAccount!, amount,
                string.IsNullOrWhiteSpace(request.PaymentPurpose) ? null : request.PaymentPurpose), errors);
    }

    /// <summary>"Approve" or "Reject" in OrderInitiationTaskResult.Task; the status reason carries the remarks.</summary>
    public static (BianOrderDecision? Decision, string? Error) ToDecision(OrderInitiation? request)
    {
        var task = request?.OrderInitiationTaskResult?.Task?.Trim();
        var remarks = request?.PaymentOrderProcedureInstanceStatus?.StatusReason;
        return task?.ToUpperInvariant() switch
        {
            "APPROVE" => (new BianOrderDecision(true, remarks), null),
            "REJECT" => (new BianOrderDecision(false, remarks), null),
            _ => (null, "OrderInitiationTaskResult.Task must be 'Approve' or 'Reject'.")
        };
    }

    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────────
    // │ EWP's statuses in BIAN's (ISO 20022 style) status types. EWP's own code always travels in
    // │ StatusReason, so nothing is lost where BIAN's list is coarser (in progress, compensating).
    // └──────────────────────────────────────────────────────────────────────────────────────────
    public static string ToBianStatusType(string ewpStatus) => ewpStatus switch
    {
        "INITIATED" => "Received",
        "RESERVING_FUNDS" => "Acceptedtechnicalvalidation",
        "PENDING_APPROVAL" => "Pending",
        "SENDING_TO_NETWORK" or "SETTLING_FUNDS" => "Acceptedsettlementinprocess",
        "COMPLETED" => "Acceptedsettlementcompleted",
        "REJECTED" or "FAILED" => "Rejected",
        "COMPENSATING" or "COMPENSATION_FAILED" => "Pending",
        _ => "Pending"
    };

    public static PaymentOrderInitiationTransaction ToTransaction(PaymentDetail payment) => new()
    {
        PaymentTransactionType = "FinancialTransaction",
        PaymentTransaction = new BianPaymentTransaction
        {
            PaymentTransactionIdentification = new BianPaymentIdentification
            {
                PaymentIdentification = new BianIdentifier { IdentifierValue = payment.PaymentNumber },
                PaymentIdentificationType = "PaymentOrderID"
            },
            PaymentTransactionStatus = new BianPaymentTransactionStatus
            {
                PaymentTransactionStatus = new BianStatus
                {
                    StatusReason = payment.Status,
                    StatusDateTime = Datetime(payment.UpdatedAt)
                },
                PaymentTransactionStatusType = ToBianStatusType(payment.Status)
            },
            PaymentTransactionAmount = Amount(payment.Amount, payment.Currency)
        },
        PayerReference = new BianPayer { PayerIdentifier = payment.CustomerNumber, PaymentInstructionInvolvementType = "PayerOrDebtor" },
        PayerBankReference = new BianPayerBank { PayerBankIdentifier = payment.FromBsb, PaymentInstructionInvolvementType = "PayerBankOrDebtorAgent" },
        PayerProductInstanceReference = new BianProductAgreement { ProductAgreementIdentification = new BianIdentifier { IdentifierValue = payment.FromAccountNumber } },
        PayeeReference = new BianPayee { PayeeIdentifier = payment.PayeeName, PaymentInstructionInvolvementType = "PayeeOrCreditor" },
        PayeeBankReference = new BianPayeeBank { PayeeBankIdentifier = payment.ToBsb, PaymentInstructionInvolvementType = "PayeeBankOrCreditorAgent" },
        PayeeProductInstanceReference = new BianProductAgreement { ProductAgreementIdentification = new BianIdentifier { IdentifierValue = payment.ToAccountNumber } },
        Amount = Amount(payment.Amount, payment.Currency),
        Currency = new BianCurrencyCode { Currencycode = payment.Currency },
        DateType = "InitiatedDate",
        Date = Datetime(payment.CreatedAt),
        // A domestic BECS Direct Entry transfer: an automated clearing house payment in BIAN's terms.
        PaymentMechanism = "ACH",
        PaymentPurpose = payment.Reference
    };

    public static OrderInitiation ToOrderInitiation(string paymentNumber, string newStatus, bool approved) => new()
    {
        PaymentOrderProcedureInstanceReference = new BianIdentifier { IdentifierValue = paymentNumber },
        PaymentOrderProcedureInstanceStatus = new BianStatus { StatusReason = newStatus },
        OrderInitiationTaskResult = new BianTask { Task = approved ? "Approved" : "Rejected" }
    };

    private static string? Required(string? value, string name, List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(value))
            return value.Trim();
        errors.Add($"{name} is required.");
        return null;
    }

    private static BianAmount Amount(decimal amount, string currency) => new()
    {
        AmountValue = amount.ToString("0.00", CultureInfo.InvariantCulture),
        AmountCurrency = new BianCurrencyCode { Currencycode = currency }
    };

    private static BianDatetime Datetime(DateTimeOffset value) => new() { DateTimeContent = value.ToString("o", CultureInfo.InvariantCulture) };
}