import { Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { JOURNEY_OPTIONS, loadOptions } from './configuration/journey-options';
import { RateLimiter } from './http/rate-limit';
import { HealthController, PlatformController } from './platform/platform.controller';
import { HealthProbeService, SimulatorService } from './platform/platform.services';
import { ParkedMessagesController } from './recovery/parked-messages.controller';
import { ParkedMessagesService } from './recovery/parked-messages.service';
import { TokenExchangeService } from './services/token-exchange.service';

@Module({
  controllers: [PlatformController, ParkedMessagesController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    RateLimiter,   // the per-person limits (DelegatedTokenGuard)
    HealthProbeService,
    SimulatorService,
    TokenExchangeService,   // the second exchange: the operator's token for Accounts / Compliance
    ParkedMessagesService,
  ],
})
export class AppModule {}