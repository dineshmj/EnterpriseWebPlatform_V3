using System.Security.Cryptography.X509Certificates;
using System.Text.Json;

using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using AwesomeAssertions;

using EnterpriseWebPlatform.PartnerProducts.Api.PartnerBank;
using EnterpriseWebPlatform.PartnerProducts.Api.Tests.Support;

namespace EnterpriseWebPlatform.PartnerProducts.Api.Tests.PartnerBank;

// ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
// │ What the Partner Bank receives must let it prove later exactly what EWP instructed:
// │ EWP's own token, and the instruction as a JWT signed with EWP's key (ES256, typ
// │ partner-instruction+jwt, issuer = EWP's client, audience partner-api, 5 minutes,
// │ jti = EWP's instruction ID). A verifier that holds only EWP's public key accepts it.
// └──────────────────────────────────────────────────────────────────────────────────────
[TestFixture]
[Category("Security")]
public sealed class PartnerBankClient_PlacePurchaseAsync_Happy_SignedInstruction
{
    private PartnerBankStub _partnerBank = null!;

    [SetUp]
    public void SetUp() => _partnerBank = new PartnerBankStub().IssuesTokens("partner-token-1");

    [TearDown]
    public void TearDown() => _partnerBank.Dispose();

    [Test]
    [Category("Happy")]
    public async Task Given_ThePartnerBankAcceptsTheOrder_When_APurchaseIsPlaced_Then_TheAnswerIsAcceptedWithItsBody()
    {
        // Given
        _partnerBank.AnswersPurchase(201, new { orderNumber = "PB-ORD-000001", status = "ORDERED" });

        // When
        var answer = await _partnerBank.Client.PlacePurchaseAsync(PartnerBankStub.PurchaseInstruction(), Guid.NewGuid(), CancellationToken.None);

        // Then
        answer.Outcome.Should().Be(PartnerOutcome.Accepted);
        ((string?)answer.Body?["orderNumber"]).Should().Be("PB-ORD-000001");
        answer.Reason.Should().BeNull();
    }

    [Test]
    public async Task Given_ATokenFromThePartnerBank_When_APurchaseIsPlaced_Then_ItAsksWithClientCredentialsAndSendsThatTokenAsBearer()
    {
        // Given
        _partnerBank.AnswersPurchase(201, new { orderNumber = "PB-ORD-000002" });

        // When
        await _partnerBank.Client.PlacePurchaseAsync(PartnerBankStub.PurchaseInstruction(), Guid.NewGuid(), CancellationToken.None);

        // Then: client credentials for EWP's own client, scope partner.distribution ...
        var tokenRequest = _partnerBank.Received(PartnerBankStub.TokenPath).Should().ContainSingle().Subject.RequestMessage!;
        tokenRequest.Body!.Should().Contain("grant_type=client_credentials")
            .And.Contain($"client_id={Uri.EscapeDataString(PartnerBankStub.ClientId)}")
            .And.Contain("scope=partner.distribution");

        // ... and that token on the instruction.
        var order = _partnerBank.Received(PartnerBankStub.GoldOrdersPath).Should().ContainSingle().Subject.RequestMessage!;
        order.Headers!["Authorization"].Should().ContainSingle().Which.Should().Be("Bearer partner-token-1");
        order.Headers["Content-Type"].Single().Should().StartWith("application/jwt");
    }

    [Test]
    public async Task Given_AnInstruction_When_ItIsSent_Then_TheBodyIsAJwtSignedWithEwpsKeyThatAVerifierAccepts()
    {
        // Given
        var instructionId = Guid.NewGuid();
        _partnerBank.AnswersPurchase(201, new { orderNumber = "PB-ORD-000003" });

        // When
        await _partnerBank.Client.PlacePurchaseAsync(PartnerBankStub.PurchaseInstruction("DGP-261021-SIGNED"), instructionId, CancellationToken.None);

        // Then: verified as the Partner Bank does - EWP's public key, issuer, audience, type, lifetime.
        var jwt = _partnerBank.Received(PartnerBankStub.GoldOrdersPath).Single().RequestMessage!.Body!;
        var verification = await new JsonWebTokenHandler().ValidateTokenAsync(jwt, new TokenValidationParameters
        {
            IssuerSigningKey = new ECDsaSecurityKey(PartnerBankStub.SigningCertificate.GetECDsaPublicKey()!),
            ValidIssuer = PartnerBankStub.ClientId,
            ValidAudience = "partner-api",
            ValidTypes = ["partner-instruction+jwt"],
            ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256]
        });
        verification.IsValid.Should().BeTrue(verification.Exception?.Message);

        var token = (JsonWebToken)verification.SecurityToken;
        token.Id.Should().Be(instructionId.ToString(), "the jti is EWP's instruction ID");
        token.Kid.Should().Be(PartnerBankStub.SigningCertificate.Thumbprint);
        (token.ValidTo - token.IssuedAt).Should().Be(TimeSpan.FromMinutes(5));
        token.GetPayloadValue<JsonElement>("instruction").GetProperty("ewpReference").GetString().Should().Be("DGP-261021-SIGNED");
    }
}