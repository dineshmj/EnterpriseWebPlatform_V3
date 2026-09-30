import { Module } from '@nestjs/common';
import { AuthController } from './auth/auth.controller';
import { OidcService } from './auth/oidc.service';
import { KycCasesController } from './controllers/kyc-cases.controller';
import { loadOptions } from './configuration/kyc-bff-options';
import { KycApiService } from './services/kyc-api.service';
import { DocumentsManagementM2mService } from './services/documents-management-m2m.service';
import { DocumentsManagementService } from './services/documents-management.service';

const options = loadOptions();

@Module({
  controllers: [AuthController, KycCasesController],
  providers: [
    { provide: 'KYC_BFF_OPTIONS', useValue: options },
    OidcService,
    KycApiService,
    DocumentsManagementM2mService,
    DocumentsManagementService,
  ],
})
export class AppModule {}
