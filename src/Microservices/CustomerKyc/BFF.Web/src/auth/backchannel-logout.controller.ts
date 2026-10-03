import { Body, Controller, HttpCode, Inject, Logger, Post, Res } from '@nestjs/common';
import { Response } from 'express';
import { createRemoteJWKSet, jwtVerify, JWTPayload } from 'jose';
import { Issuer } from 'openid-client';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { destroySessions } from './session-registry';

const BACKCHANNEL_LOGOUT_EVENT = 'http://schemas.openid.net/event/backchannel-logout';

/**
 * OpenID Connect Back-Channel Logout 1.0: the IDP calls this endpoint server-to-server
 * when the user signs out anywhere in the SSO session (registered at the IDP as
 * BackChannelLogoutUri = <BFF base URL>/backchannel-logout). Unlike front-channel
 * logout it does not depend on the browser, iframes or third-party cookies.
 *
 * The logout token is a JWT signed by the IDP and is fully validated before any
 * session is touched: signature (IDP JWKS), issuer, audience (this client), the
 * back-channel event, no nonce, sid or sub present, and a one-time jti.
 */
@Controller()
export class BackChannelLogoutController {
  private readonly logger = new Logger(BackChannelLogoutController.name);
  private jwks?: ReturnType<typeof createRemoteJWKSet>;
  private issuer?: string;
  private readonly seenTokenIds = new Map<string, number>();

  constructor(@Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions) {}

  @Post('backchannel-logout')
  @HttpCode(200)
  async backChannelLogout(@Body('logout_token') logoutToken: string | undefined, @Res() res: Response) {
    res.setHeader('Cache-Control', 'no-cache, no-store');

    if (!logoutToken) return res.status(400).json({ error: 'invalid_request' });

    let claims: JWTPayload;
    try {
      claims = await this.validate(logoutToken);
    } catch (error) {
      this.logger.warn(`Rejected back-channel logout token: ${(error as Error).message}`);
      return res.status(400).json({ error: 'invalid_request' });
    }

    const destroyed = await destroySessions({
      sid: typeof claims.sid === 'string' ? claims.sid : undefined,
      sub: typeof claims.sub === 'string' ? claims.sub : undefined,
    });

    this.logger.log(`Back-channel logout: ended ${destroyed} session(s) (sid=${String(claims.sid ?? '-')}).`);
    return res.status(200).send('');
  }

  private async validate(token: string): Promise<JWTPayload> {
    if (!this.jwks || !this.issuer) {
      const discovered = await Issuer.discover(this.options.authority);
      this.issuer = discovered.metadata.issuer;
      this.jwks = createRemoteJWKSet(new URL(String(discovered.metadata.jwks_uri)));
    }

    const { payload } = await jwtVerify(token, this.jwks, {
      issuer: this.issuer,
      audience: this.options.clientId,
      maxTokenAge: '5m',
    });

    const events = payload.events as Record<string, unknown> | undefined;
    if (!events || typeof events !== 'object' || !(BACKCHANNEL_LOGOUT_EVENT in events))
      throw new Error('missing back-channel logout event');
    if ('nonce' in payload) throw new Error('a logout token must not contain a nonce');
    if (!payload.sid && !payload.sub) throw new Error('sid or sub is required');
    if (!payload.jti) throw new Error('jti is required');

    // Replay protection: each logout token is accepted once (kept for its 5-minute validity).
    const now = Date.now();
    for (const [jti, expiresAt] of this.seenTokenIds) if (expiresAt < now) this.seenTokenIds.delete(jti);
    if (this.seenTokenIds.has(payload.jti)) throw new Error('logout token replayed');
    this.seenTokenIds.set(payload.jti, now + 5 * 60_000);

    return payload;
  }
}
