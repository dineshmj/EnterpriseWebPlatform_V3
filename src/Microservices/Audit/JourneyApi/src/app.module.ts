import { Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { JOURNEY_OPTIONS, loadOptions } from './configuration/journey-options';
import { AuditJourneysController, HealthController } from './controllers/audit-journeys.controller';
import { DomainApiClient } from './services/domain-api.client';
import { TokenExchangeService } from './services/token-exchange.service';

@Module({
  controllers: [AuditJourneysController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    TokenExchangeService,
    DomainApiClient,
  ],
})
export class AppModule {}