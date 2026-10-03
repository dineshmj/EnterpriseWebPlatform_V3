import { Controller, Get, Headers, Post, Query, Req, Res, Inject } from '@nestjs/common';
import { Request, Response } from 'express';
import { randomBytes } from 'node:crypto';
import { KycBffOptions } from '../configuration/kyc-bff-options';
import { safeEqual } from './csrf';
import { OidcService } from './oidc.service';

@Controller('api/auth')
export class AuthController {
  constructor(
    private readonly oidc: OidcService,
    @Inject('KYC_BFF_OPTIONS') private readonly options: KycBffOptions,
  ) {}

  @Get('login')
  async login(
    @Query('returnUrl') returnUrl: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    const safeReturnUrl = this.safeReturnUrl(returnUrl);
    const url = await this.oidc.authorizationUrl(req, safeReturnUrl, false);
    return res.redirect(url);
  }

  @Get('silent-login')
  async silentLogin(
    @Query('returnUrl') returnUrl: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    const safeReturnUrl = this.safeReturnUrl(returnUrl);
    const url = await this.oidc.authorizationUrl(req, safeReturnUrl, true);
    return res.redirect(url);
  }

  @Get('callback')
  async callback(@Req() req: Request, @Res() res: Response) {
    const returnUrl = await this.oidc.handleCallback(req);
    return res.redirect(returnUrl);
  }

  @Get('csrf')
  csrf(@Req() req: Request) {
    req.session.csrfToken ??= randomBytes(32).toString('base64url');
    return { token: req.session.csrfToken };
  }

  /**
   * Ends the local session, revokes the refresh token and sends the browser to
   * the IDP end-session endpoint so the SSO session ends as well.
   */
  @Post('logout')
  async logout(
    @Headers('x-csrf-token') csrfToken: string | undefined,
    @Req() req: Request,
    @Res() res: Response,
  ) {
    if (!csrfToken || !safeEqual(csrfToken, req.session.csrfToken)) {
      return res.status(403).json({ message: 'Invalid CSRF token.' });
    }

    const endSessionUrl = await this.oidc.logout(req);
    return res.json({ redirectUrl: endSessionUrl });
  }

  private safeReturnUrl(returnUrl?: string): string {
    if (!returnUrl || !returnUrl.startsWith('/v1/kyc/')) return '/v1/kyc/cases/view-all/';
    return returnUrl;
  }
}

/**
 * Root-level OIDC sign-out endpoints, matching the URIs registered at the IDP:
 * - FrontChannelLogoutUri  = <BFF base URL>/signout-oidc
 * - PostLogoutRedirectUri  = <BFF base URL>/signout-callback-oidc
 */
@Controller()
export class FrontChannelLogoutController {
  constructor(private readonly oidc: OidcService) {}

  /** Loaded by the IDP in a hidden iframe when the user signs out anywhere in the SSO session. */
  @Get('signout-oidc')
  async frontChannelLogout(@Req() req: Request, @Res() res: Response) {
    await this.oidc.clearSession(req);
    res.setHeader('Cache-Control', 'no-cache, no-store');
    return res.status(200).send('');
  }

  @Get('signout-callback-oidc')
  signoutCallback(@Res() res: Response) {
    return res.redirect('/v1/kyc/cases/view-all/');
  }
}
