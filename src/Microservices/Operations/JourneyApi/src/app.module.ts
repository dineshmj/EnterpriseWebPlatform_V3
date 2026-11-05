import { Module } from '@nestjs/common';
import { ClientKeyService } from './auth/client-key.service';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { DPoPKeyService } from './auth/dpop-key.service';
import { DPoPProofVerifier } from './auth/dpop-proof.verifier';
import { JOURNEY_OPTIONS, JourneyOptions, loadOptions } from './configuration/journey-options';
import { FAULT_CONTROLLER } from './faults/fault-controller';
import { FaultControlService } from './faults/fault-control.service';
import { FaultRoutesController } from './faults/faults.controller';
import { ToxiproxyFaultController } from './faults/toxiproxy-fault-controller';
import { RateLimiter } from './http/rate-limit';
import { HealthController, PlatformController } from './platform/platform.controller';
import { HealthProbeService, SimulatorService } from './platform/platform.services';
import { ParkedMessagesController } from './recovery/parked-messages.controller';
import { ParkedMessagesService } from './recovery/parked-messages.service';
import { PersonalDataErasureController } from './privacy/personal-data-erasure.controller';
import { PersonalDataErasureService } from './privacy/personal-data-erasure.service';
import { TokenExchangeService } from './services/token-exchange.service';

@Module({
  controllers: [PlatformController, FaultRoutesController, ParkedMessagesController, PersonalDataErasureController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    DPoPProofVerifier,   // the callers' DPoP proofs (DelegatedTokenGuard)
    // This API's own DPoP key, read at start-up: missing or unreadable = no start-up.
    { provide: DPoPKeyService, useFactory: (o: JourneyOptions) => DPoPKeyService.load(o.dpopKeyPath, o.dpopKeyPasswordPath), inject: [JOURNEY_OPTIONS] },
    // This API's client key (private_key_jwt), read at start-up - and never the DPoP key as well.
    {
      provide: ClientKeyService,
      useFactory: async (o: JourneyOptions, dpop: DPoPKeyService) => {
        const key = ClientKeyService.load(o.clientKeyPath, o.clientKeyPasswordPath, o.clientId, o.authority);
        if ((await key.thumbprint()) === (await dpop.thumbprint())) throw new Error('The DPoP key must not be the client key.');
        return key;
      },
      inject: [JOURNEY_OPTIONS, DPoPKeyService],
    },
    RateLimiter,   // the per-person limits (DelegatedTokenGuard)
    HealthProbeService,
    SimulatorService,
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The fault controller port, bound to its Toxiproxy adapter (Development, Test, UAT);
    // │ a service-mesh adapter would be bound here instead - nothing else changes.
    // └──────────────────────────────────────────────────────────────────────────────────────
    { provide: FAULT_CONTROLLER, useClass: ToxiproxyFaultController },
    FaultControlService,
    TokenExchangeService,   // the second exchange: the operator's token for Accounts / Compliance / Partner Products / Customer Onboarding
    ParkedMessagesService,
    PersonalDataErasureService,
  ],
})
export class AppModule {}