import { Controller, Get, Query, Req, Res, Inject } from '@nestjs/common';
import { Request, Response } from 'express';
import { randomBytes } from 'node:crypto';
import { KycBffOptions } from '../configuration/kyc-bff-options';
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

  @Get('logout')
  logout(@Req() req: Request, @Res() res: Response) {
    this.oidc.clearSession(req);
    return res.redirect('/signout-callback-oidc');
  }

  @Get('signout-callback-oidc')
  signoutCallback(@Res() res: Response) {
    return res.redirect('/v1/kyc/cases/view-all/');
  }

  private safeReturnUrl(returnUrl?: string): string {
    if (!returnUrl || !returnUrl.startsWith('/v1/kyc/')) return '/v1/kyc/cases/view-all/';
    return returnUrl;
  }
}
