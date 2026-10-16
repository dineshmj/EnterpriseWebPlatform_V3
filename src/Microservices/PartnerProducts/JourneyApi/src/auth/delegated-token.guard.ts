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
  /** The token this API received: the subject of each further exchange (Domain APIs). */
  subjectToken: string;
  lanId?: string;
  branch?: string;
  permissions: string[];
}

export type DelegatedRequest = Request & { delegation?: Delegation };

interface Requirement {
  scope: string;
  permission: string;
}

const REQUIREMENT_KEY = 'partnerProductsRequirement';
/** The endpoint needs this scope in the token and this permission held by the person. */
export const Requires = (requirement: Requirement) => SetMetadata(REQUIREMENT_KEY, requirement);

export const VIEW = { scope: 'partner-products-journey.read', permission: 'partner.gold.view' };
export const ORDER = { scope: 'partner-products-journey.write', permission: 'partner.gold.order' };

// ┌─ :: SECURITY :: Partner Products Journey API: delegated tokens only ───────────────────────────
// │ Why : The Journey API must serve a person through its own web app - not a stolen machine token
// │       or another client holding the scope.
// │ How : JWT checked against the IDP's keys (issuer, audience, lifetime, at+jwt); a person (sub),
// │       the scope, the web BFF as "act", the endpoint's permission and a branch are all required;
// │       no rule on an endpoint = denied.
// │ Ref : Features 2.1.2, 2.2.1 | RFC 8693 | RFC 9068 | OWASP API2, API5 |
// │       Threat-Model .NET/NestJS parity
// └────────────────────────────────────────────────────────────────────────────────────────────────
/**
 * Accepts only a DELEGATED token: issued by the IDP for this API (audience), with the endpoint's
 * scope, whose "act" names the Partner Products web BFF - i.e. the BFF exchanged a signed-in
 * person's token for this call (RFC 8693). Then the person must hold the endpoint's permission and
 * belong to a branch (every customer and record is branch-scoped). Anything else: 401 or 403.
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
      throw new ForbiddenException('This endpoint has no access rule.');
    }

    const actingClient = (payload.act as { client_id?: unknown } | undefined)?.client_id;
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ No person, wrong scope or another acting client: refuse - only the Partner Products
    // │ web, acting for a signed-in person, may call.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (typeof payload.sub !== 'string' || !toList(payload.scope).includes(requirement.scope) || actingClient !== this.options.callerClientId) {
      throw new ForbiddenException('Only the Partner Products web application, acting for a signed-in person, may call this API.');
    }

    const isChange = requirement.scope.endsWith('.write');
    this.limiter.take(`${isChange ? 'change' : 'read'}|${payload.sub}|${actingClient}`, isChange ? CHANGE_LIMIT : READ_LIMIT);

    const permissions = toList(payload.permission);
    if (!permissions.includes(requirement.permission) || typeof payload.branch !== 'string' || payload.branch.length === 0) {
      throw new ForbiddenException('You are not allowed to do this.');
    }

    request.delegation = {
      sub: payload.sub,
      subjectToken: token,
      lanId: typeof payload.lan_id === 'string' ? payload.lan_id : undefined,
      branch: payload.branch,
      permissions,
    };
    return true;
  }
}

function toList(value: unknown): string[] {
  if (Array.isArray(value)) return value.filter((v): v is string => typeof v === 'string');
  if (typeof value === 'string') return value.split(' ').filter((v) => v.length > 0);
  return [];
}