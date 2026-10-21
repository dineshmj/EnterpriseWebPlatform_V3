import { Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { JOURNEY_OPTIONS, loadOptions } from './configuration/journey-options';
import { FAULT_CONTROLLER } from './faults/fault-controller';
import { FaultControlService } from './faults/fault-control.service';
import { FaultRoutesController } from './faults/faults.controller';
import { ToxiproxyFaultController } from './faults/toxiproxy-fault-controller';
import { RateLimiter } from './http/rate-limit';
import { HealthController, PlatformController } from './platform/platform.controller';
import { HealthProbeService, SimulatorService } from './platform/platform.services';
import { ParkedMessagesController } from './recovery/parked-messages.controller';
import { ParkedMessagesService } from './recovery/parked-messages.service';
import { TokenExchangeService } from './services/token-exchange.service';

@Module({
  controllers: [PlatformController, FaultRoutesController, ParkedMessagesController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    RateLimiter,   // the per-person limits (DelegatedTokenGuard)
    HealthProbeService,
    SimulatorService,
    // ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    // │ The fault controller port, bound to its Toxiproxy adapter (Development, Test, UAT);
    // │ a service-mesh adapter would be bound here instead - nothing else changes.
    // └──────────────────────────────────────────────────────────────────────────────────────
    { provide: FAULT_CONTROLLER, useClass: ToxiproxyFaultController },
    FaultControlService,
    TokenExchangeService,   // the second exchange: the operator's token for Accounts / Compliance / Partner Products
    ParkedMessagesService,
  ],
})
export class AppModule {}