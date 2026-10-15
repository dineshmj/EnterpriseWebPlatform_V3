import { BadRequestException, Body, Controller, Get, Param, Post, Req, UseGuards } from '@nestjs/common';
import { DelegatedRequest, DelegatedTokenGuard, Requires } from '../auth/delegated-token.guard';
import { ParkedMessagesService, RecoveryContext, recoveryContexts } from './parked-messages.service';

const VIEW = { scope: 'operations-journey.read', permission: 'outbox.view' };
const RESEND = { scope: 'operations-journey.write', permission: 'workflow.reprocess', stepUp: true };
const MESSAGE_ID = /^[0-9a-fA-F-]{36}$/;

/** Parked Outbox messages of Accounts and Compliance: see them, and resend one with a reason. */
@Controller('v1/journeys/operations/parked-messages')
@UseGuards(DelegatedTokenGuard)
export class ParkedMessagesController {
  constructor(private readonly parked: ParkedMessagesService) {}

  @Get()
  @Requires(VIEW)
  list(@Req() request: DelegatedRequest) {
    return this.parked.listAll(request.delegation!.subjectToken);
  }

  @Post(':context/:messageId/resend')
  @Requires(RESEND)
  resend(@Req() request: DelegatedRequest, @Param('context') context: string, @Param('messageId') messageId: string, @Body() body: { reason?: unknown }) {
    // ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    // │ Only the known contexts and a GUID message ID are forwarded - the path to the Domain
    // │ API is never built from free input. The reason's length is checked there, too.
    // └──────────────────────────────────────────────────────────────────────────────────────
    if (!(recoveryContexts as readonly string[]).includes(context)) throw new BadRequestException('Unknown context.');
    if (!MESSAGE_ID.test(messageId)) throw new BadRequestException('Unknown message.');
    if (typeof body?.reason !== 'string') throw new BadRequestException('A reason is required.');
    return this.parked.resend(request.delegation!.subjectToken, context as RecoveryContext, messageId, body.reason);
  }
}