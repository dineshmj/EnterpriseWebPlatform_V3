import { Controller, Get, Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { JOURNEY_OPTIONS, loadOptions } from './configuration/journey-options';
import { DigitalGoldController } from './digital-gold/digital-gold.controller';
import { DigitalGoldService } from './digital-gold/digital-gold.service';
import { RateLimiter } from './http/rate-limit';
import { DomainApiClient } from './services/domain-api.client';
import { TokenExchangeService } from './services/token-exchange.service';

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
    RateLimiter,   // the per-person limits (DelegatedTokenGuard)
    TokenExchangeService,   // the second exchange: the staff member's token for each Domain API
    DomainApiClient,
    DigitalGoldService,
  ],
})
export class AppModule {}