import { Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { DPoPKeyService } from './auth/dpop-key.service';
import { DPoPProofVerifier } from './auth/dpop-proof.verifier';
import { JOURNEY_OPTIONS, JourneyOptions, loadOptions } from './configuration/journey-options';
import { RateLimiter } from './http/rate-limit';
import { AuditJourneysController, HealthController } from './controllers/audit-journeys.controller';
import { DomainApiClient } from './services/domain-api.client';
import { TokenExchangeService } from './services/token-exchange.service';

@Module({
  controllers: [AuditJourneysController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    DPoPProofVerifier,   // the callers' DPoP proofs (DelegatedTokenGuard)
    // This API's own DPoP key, read at start-up: missing or unreadable = no start-up.
    { provide: DPoPKeyService, useFactory: (o: JourneyOptions) => DPoPKeyService.load(o.dpopKeyPath, o.dpopKeyPasswordPath), inject: [JOURNEY_OPTIONS] },
    RateLimiter,   // the per-person limit (DelegatedTokenGuard)
    TokenExchangeService,
    DomainApiClient,
  ],
})
export class AppModule {}