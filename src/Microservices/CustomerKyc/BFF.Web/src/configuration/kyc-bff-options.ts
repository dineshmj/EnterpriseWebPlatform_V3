export interface KycBffOptions {
  port: number;
  authority: string;
  clientId: string;
  clientSecret: string;
  callbackUrl: string;
  postLogoutRedirectUri: string;
  apiBaseUrl: string;
  sessionSecret: string;
  tlsPfxPath?: string;
  tlsPfxPassword?: string;
  staticRoot: string;
  documentsManagementApiBaseUrl: string;
  documentsManagementIdpAuthority: string;
  documentsManagementM2mClientId: string;
  documentsManagementM2mClientSecret: string;
}

export function loadOptions(): KycBffOptions {
  return {
    port: Number(process.env.KYC_BFF_PORT ?? '33800'),
    authority: process.env.KYC_IDP_AUTHORITY ?? 'https://idp.dev.localhost:44392',
    clientId: process.env.KYC_BFF_CLIENT_ID ?? 'CustomerKYC.Microservice.BFF.ClientID',
    clientSecret: process.env.KYC_BFF_CLIENT_SECRET ?? '3ac91ab3-7ba0-4727-b4f7-36120bec10c5',
    callbackUrl: process.env.KYC_BFF_CALLBACK_URL ?? 'https://kyc.dev.localhost:33800/api/auth/callback',
    postLogoutRedirectUri: process.env.KYC_BFF_POST_LOGOUT_REDIRECT_URI ?? 'https://kyc.dev.localhost:33800/signout-callback-oidc',
    apiBaseUrl: process.env.KYC_API_BASE_URL ?? 'https://kyc-api.dev.localhost:44305',
    sessionSecret: process.env.KYC_BFF_SESSION_SECRET ?? 'EWP-V3-KYC-BFF-development-session-secret-change-me',
    tlsPfxPath: process.env.KYC_BFF_TLS_PFX_PATH,
    tlsPfxPassword: process.env.KYC_BFF_TLS_PFX_PASSWORD,
    staticRoot: process.env.KYC_BFF_STATIC_ROOT ?? 'client-app/out',
    documentsManagementApiBaseUrl: process.env.DOCUMENTS_MANAGEMENT_API_BASE_URL ?? 'https://documents-management-api.dev.localhost:49486',
    documentsManagementIdpAuthority: process.env.DOCUMENTS_MANAGEMENT_IDP_AUTHORITY ?? process.env.KYC_IDP_AUTHORITY ?? 'https://idp.dev.localhost:44392',
    documentsManagementM2mClientId: process.env.DOCUMENTS_MANAGEMENT_M2M_CLIENT_ID ?? 'KycBFFToDocumentsManagementM2M',
    documentsManagementM2mClientSecret: process.env.DOCUMENTS_MANAGEMENT_M2M_CLIENT_SECRET ?? '',
  };
}
