import { Controller, Get, Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { DPoPKeyService } from './auth/dpop-key.service';
import { DPoPProofVerifier } from './auth/dpop-proof.verifier';
import { JOURNEY_OPTIONS, JourneyOptions, loadOptions } from './configuration/journey-options';
import { DigitalGoldController } from './digital-gold/digital-gold.controller';
import { DigitalGoldService } from './digital-gold/digital-gold.service';
import { RateLimiter } from './http/rate-limit';
import { DomainApiClient } from './services/domain-api.client';
import { PurchaseWorkflowStarter } from './services/purchase-workflow.starter';
import { TokenExchangeService } from './services/token-exchange.service';

// ┌─ OBS ────────────────────────────────────────────────────────────────────────────────
// │ Probes only report that the process runs: this API holds no state of its own, and a
// │ Domain API being down shows on that API's own probe (Operations -> Platform Health).
// └──────────────────────────────────────────────────────────────────────────────────────
/** Probes for the orchestrator, like every other component. */
@Controller('health')
export class HealthController {
  @Get('live')
  live() {
    return { status: 'Healthy' };
  }

  @Get('ready')
  ready() {
    return { status: 'Healthy' };
  }
}

@Module({
  controllers: [DigitalGoldController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    DPoPProofVerifier,   // the callers' DPoP proofs (DelegatedTokenGuard)
    // This API's own DPoP key, read at start-up: missing or unreadable = no start-up.
    { provide: DPoPKeyService, useFactory: (o: JourneyOptions) => DPoPKeyService.load(o.dpopKeyPath, o.dpopKeyPasswordPath), inject: [JOURNEY_OPTIONS] },
    RateLimiter,   // the per-person limits (DelegatedTokenGuard)
    TokenExchangeService,   // the second exchange: the staff member's token for each Domain API
    DomainApiClient,
    PurchaseWorkflowStarter,   // starts the purchase's Temporal workflow (the Partner Products Worker runs it)
    DigitalGoldService,
  ],
})
export class AppModule {}