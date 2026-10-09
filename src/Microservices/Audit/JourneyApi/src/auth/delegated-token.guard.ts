import {
  CanActivate,
  ExecutionContext,
  ForbiddenException,
  Inject,
  Injectable,
  UnauthorizedException,
} from '@nestjs/common';
import { Reflector } from '@nestjs/core';
import { SetMetadata } from '@nestjs/common';
import type { Request } from 'express';
import { createRemoteJWKSet, jwtVerify, JWTPayload } from 'jose';
import { JOURNEY_OPTIONS, JourneyOptions } from '../configuration/journey-options';

/** The person this request acts for, and the token that proves it (exchanged further downstream). */
export interface Delegation {
  subjectToken: string;
  sub: string;
  lanId?: string;
  permissions: string[];
}

export type DelegatedRequest = Request & { delegation?: Delegation };

const PERMISSION_KEY = 'requiredPermission';
/** The person must hold this permission (audit.view, audit.search). */
export const RequiresPermission = (permission: string) => SetMetadata(PERMISSION_KEY, permission);

/**
 * Accepts only a DELEGATED token: issued by the IDP for this API (audience), with the scope
 * audit-journey.read, whose "act" names the Audit web BFF - i.e. the BFF exchanged a signed-in
 * person's token for this call (RFC 8693). Then the person must hold the endpoint's permission.
 * Anything else: 401 (no or invalid token) or 403 (wrong caller, no permission). Fail closed.
 */
@Injectable()
export class DelegatedTokenGuard implements CanActivate {
  private readonly jwks;

  constructor(
    @Inject(JOURNEY_OPTIONS) private readonly options: JourneyOptions,
    private readonly reflector: Reflector,
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

    const scopes = toList(payload.scope);
    const actingClient = (payload.act as { client_id?: unknown } | undefined)?.client_id;
    if (typeof payload.sub !== 'string' || !scopes.includes('audit-journey.read') || actingClient !== this.options.callerClientId) {
      throw new ForbiddenException('Only the Audit web application, acting for a signed-in person, may call this API.');
    }

    const permissions = toList(payload.permission);
    const required = this.reflector.get<string | undefined>(PERMISSION_KEY, context.getHandler());
    if (!required || !permissions.includes(required)) {
      throw new ForbiddenException('You are not allowed to see the audit trail.');
    }

    request.delegation = {
      subjectToken: token,
      sub: payload.sub,
      lanId: typeof payload.lan_id === 'string' ? payload.lan_id : undefined,
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