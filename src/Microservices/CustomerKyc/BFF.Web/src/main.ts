import { NestFactory } from '@nestjs/core';
import { ValidationPipe } from '@nestjs/common';
import session from 'express-session';
import * as express from 'express';
import * as fs from 'node:fs';
import * as path from 'node:path';
import { AppModule } from './app.module';
import { loadOptions } from './configuration/kyc-bff-options';

async function bootstrap() {
  const options = loadOptions();

  const app = await NestFactory.create(AppModule, {
    httpsOptions: createHttpsOptions(
      options.tlsPfxPath,
      options.tlsPfxPassword
    ),
  });

  app.use(
    express.static(path.resolve(options.staticRoot), {
      extensions: ['html'],
    })
  );

  app.use(
    session({
      name: '__Host-KYC-BFF-SESSION',
      secret: options.sessionSecret,
      resave: false,
      saveUninitialized: false,
      cookie: {
        httpOnly: true,
        secure: true,
        sameSite: 'none',
        path: '/',
        maxAge: 30 * 60 * 1000,
      },
    })
  );

  app.useGlobalPipes(
    new ValidationPipe({
      whitelist: true,
      transform: true,
    })
  );

  await app.listen(options.port, '0.0.0.0');

  console.log(
    `Customer KYC BFF listening on https://kyc.dev.localhost:${options.port}`
  );
}

function createHttpsOptions(pfxPath?: string, pfxPassword?: string) {
  if (!pfxPath) {
    throw new Error(
      'KYC_BFF_TLS_PFX_PATH must be configured because the KYC BFF is embedded by the HTTPS Shell.'
    );
  }

  const resolved = path.resolve(pfxPath);

  if (!fs.existsSync(resolved)) {
    throw new Error(`KYC BFF TLS PFX file was not found: ${resolved}`);
  }

  return {
    pfx: fs.readFileSync(resolved),
    passphrase: pfxPassword,
  };
}

void bootstrap();
