import { Module } from '@nestjs/common';
import { DelegatedTokenGuard } from './auth/delegated-token.guard';
import { JOURNEY_OPTIONS, loadOptions } from './configuration/journey-options';
import { HealthController, PlatformController } from './platform/platform.controller';
import { HealthProbeService, SimulatorService } from './platform/platform.services';

@Module({
  controllers: [PlatformController, HealthController],
  providers: [
    { provide: JOURNEY_OPTIONS, useFactory: loadOptions },
    DelegatedTokenGuard,
    HealthProbeService,
    SimulatorService,
  ],
})
export class AppModule {}