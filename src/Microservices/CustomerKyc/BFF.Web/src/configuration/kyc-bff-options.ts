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
  shellOrigin: string;
  documentsManagementApiBaseUrl: string;
}

/**
 * Secrets have no defaults: the BFF refuses to start without them (fail closed)
 * rather than falling back to values that are published in source control.
 */
function required(name: string): string {
  const value = process.env[name];
  if (!value || value.trim() === '') {
    throw new Error(`Required configuration '${name}' is not set.`);
  }
  return value;
}

export function loadOptions(): KycBffOptions {
  return {
    port: Number(process.env.KYC_BFF_PORT ?? '33800'),
    authority: process.env.KYC_IDP_AUTHORITY ?? 'https://idp.dev.localhost:46392',
    clientId: process.env.KYC_BFF_CLIENT_ID ?? 'CustomerKYC.Microservice.BFF.ClientID',
    clientSecret: required('KYC_BFF_CLIENT_SECRET'),
    callbackUrl: process.env.KYC_BFF_CALLBACK_URL ?? 'https://kyc.dev.localhost:33800/api/auth/callback',
    postLogoutRedirectUri: process.env.KYC_BFF_POST_LOGOUT_REDIRECT_URI ?? 'https://kyc.dev.localhost:33800/signout-callback-oidc',
    apiBaseUrl: process.env.KYC_API_BASE_URL ?? 'https://kyc-api.dev.localhost:46305',
    sessionSecret: required('KYC_BFF_SESSION_SECRET'),
    tlsPfxPath: process.env.KYC_BFF_TLS_PFX_PATH,
    tlsPfxPassword: process.env.KYC_BFF_TLS_PFX_PASSWORD,
    staticRoot: process.env.KYC_BFF_STATIC_ROOT ?? 'client-app/out',
    shellOrigin: process.env.KYC_BFF_SHELL_ORIGIN ?? 'https://shell.dev.localhost:46367',
    documentsManagementApiBaseUrl:
      process.env.KYC_DOCUMENTS_MANAGEMENT_API_BASE_URL ?? 'https://documents-management-api.dev.localhost:49486',
  };
}