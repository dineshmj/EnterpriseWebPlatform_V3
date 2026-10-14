import {
  CanActivate,
  ExecutionContext,
  ForbiddenException,
  Inject,
  Injectable,
  SetMetadata,
  UnauthorizedException,
} from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import type { Request } from 'express';
import { createRemoteJWKSet, jwtVerify, JWTPayload } from 'jose';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';
import { CHANGE_LIMIT, RateLimiter, READ_LIMIT } from '../http/rate-limit';

/** The person this request acts for. */
export interface Delegation {
  sub: string;
  lanId?: string;
  permissions: string[];
}

export type DelegatedRequest = Request & { delegation?: Delegation };

interface Requirement {
  scope: string;
  permission: string;
  /** A recent MFA sign-in, while MFA is on (changing a simulator's behaviour). */
  stepUp?: boolean;
}

const REQUIREMENT_KEY = 'operationsRequirement';
/** The endpoint needs this scope in the token and this permission held by the person. */
export const Requires = (requirement: Requirement) => SetMetadata(REQUIREMENT_KEY, requirement);

// ┌─ :: SECURITY :: Operations Journey API: delegated tokens only ─────────────────────────────────
// │ Why : The Journey API must serve a person through its own web app - not a stolen machine token
// │       or another client holding the scope.
// │ How : JWT checked against the IDP's keys (issuer, audience, lifetime, at+jwt); a person (sub),
// │       the scope, the web BFF as "act" and the endpoint's permission are all required; no rule
// │       on an endpoint = denied.
// │ Ref : Features 2.1.2, 2.2.1 | RFC 8693 | RFC 9068 | OWASP API2, API5 |
// │       Threat-Model .NET/NestJS parity
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * Accepts only a DELEGATED token: issued by the IDP for this API (audience), with the endpoint's
 * scope, whose "act" names the Operations web BFF - i.e. the BFF exchanged a signed-in person's
 * token for this call (RFC 8693). Then the person must hold the endpoint's permission, and - for a
 * change while MFA is on - must have signed in with MFA recently (the exchanged token keeps the
 * person's original sign-in time and methods). Anything else: 401 or 403. Fail closed.
 *
 * Each person, through each calling client, has a request limit: reads and - much lower - changes
 * (429 with Retry-After beyond it).
 */
@Injectable()
export class DelegatedTokenGuard implements CanActivate {
  private readonly jwks;

  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    private readonly reflector: Reflector,
    private readonly limiter: RateLimiter,
  ) {
    this.jwks = createRemoteJWKSet(new URL(`${options.authority}/.well-known/openid-configuration/jwks`));
  }

  async canActivate(context: ExecutionContext): Promise<boolean> {
    const request = context.switchToHttp().getRequest<DelegatedRequest>();
    const header = request.headers.authorization ?? '';
    const token = header.startsWith('Bearer ') ? header.slice('Bearer '.length).trim() : '';
    if (!token) {
      throw new UnauthorizedException('A bearer token is required.');
    }

    let payload: JWTPayload;
    try {
      ({ payload } = await jwtVerify(token, this.jwks, {
        issuer: this.options.authority,
        audience: this.options.audience,
        typ: 'at+jwt',   // access tokens only (RFC 9068), like the .NET APIs
      }));
    } catch {
      throw new UnauthorizedException('The token is not valid.');
    }

    const requirement = this.reflector.get<Requirement | undefined>(REQUIREMENT_KEY, context.getHandler());
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ An endpoint without an access rule is denied - deny by default.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (!requirement) {
      throw new ForbiddenException('This endpoint has no access rule.');   // deny by default
    }

    const actingClient = (payload.act as { client_id?: unknown } | undefined)?.client_id;
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ No person, wrong scope or another acting client: refuse - only the Operations web,
    // │ acting for a signed-in person, may call.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (typeof payload.sub !== 'string' || !toList(payload.scope).includes(requirement.scope) || actingClient !== this.options.callerClientId) {
      throw new ForbiddenException('Only the Operations web application, acting for a signed-in person, may call this API.');
    }

    const isChange = requirement.scope.endsWith('.write');
    this.limiter.take(`${isChange ? 'change' : 'read'}|${payload.sub}|${actingClient}`, isChange ? CHANGE_LIMIT : READ_LIMIT);

    const permissions = toList(payload.permission);
    if (!permissions.includes(requirement.permission)) {
      throw new ForbiddenException('You are not allowed to do this.');
    }

    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ A risky change needs a recent MFA sign-in (amr + auth_time): otherwise 403
    // │ mfa_required and the screen steps up.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (requirement.stepUp && this.options.mfaEnabled && !recentMfa(payload, this.options.mfaMaxAgeMinutes)) {
      throw new ForbiddenException({
        code: 'mfa_required',
        message: 'This change needs a recent confirmation with your authenticator code. Enter the code in the window that opens, then the change continues.',
      });
    }

    request.delegation = {
      sub: payload.sub,
      lanId: typeof payload.lan_id === 'string' ? payload.lan_id : undefined,
      permissions,
    };
    return true;
  }
}

/** amr contains "mfa" and auth_time (Unix seconds) is at most maxAgeMinutes old - like MfaStepUp.cs in the .NET APIs. */
function recentMfa(payload: JWTPayload, maxAgeMinutes: number): boolean {
  const authTime = typeof payload.auth_time === 'number' ? payload.auth_time : Number(payload.auth_time);
  const nowSeconds = Math.floor(Date.now() / 1000);
  return toList(payload.amr).includes('mfa') &&
    Number.isFinite(authTime) && nowSeconds - authTime <= maxAgeMinutes * 60 && authTime <= nowSeconds + 300;
}

function toList(value: unknown): string[] {
  if (Array.isArray(value)) return value.filter((v): v is string => typeof v === 'string');
  if (typeof value === 'string') return value.split(' ').filter((v) => v.length > 0);
  return [];
}