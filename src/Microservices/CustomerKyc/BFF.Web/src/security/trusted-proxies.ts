import { BlockList, isIP } from 'node:net';

// ┌─ :: SECURITY :: Forwarded headers only from the ingress ───────────────────────────────────────
// │ Why : Behind a TLS-ending ingress the BFF sees plain HTTP; only X-Forwarded-Proto tells it the
// │       browser used HTTPS (express-session sets the Secure cookie only then). Anyone else could
// │       send the same header, or a false X-Forwarded-For.
// │ How : KYC_BFF_TRUSTED_PROXIES lists the ingress's addresses and networks (comma-separated,
// │       e.g. "10.0.0.0/8, 192.168.1.10"); express trusts the forwarded headers only when the
// │       connection comes from one of them, and only one hop (the ingress's own entry). None
// │       listed = off. The same rule as Common/WebUtilities ProxyForwardedHeaders.cs (KnownNetworks
// │       / KnownProxies, ForwardLimit 1) in the .NET BFFs; the Host header is never forwarded.
// │ Ref : doc/Containers.md, "Behind the ingress" | OWASP A05 | CWE-348
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function trustedProxies(setting: string | undefined): ((address: string, hop: number) => boolean) | undefined {
  const entries = (setting ?? '').split(',').map((entry) => entry.trim()).filter((entry) => entry.length > 0);
  if (entries.length === 0) return undefined;

  const list = new BlockList();
  for (const entry of entries) {
    const [address, prefix, extra] = entry.split('/');
    const version = isIP(address);
    const bits = Number(prefix);
    if (version === 0 || extra !== undefined
        || (prefix !== undefined && (!/^\d+$/.test(prefix) || bits > (version === 4 ? 32 : 128)))) {
      throw new Error(`KYC_BFF_TRUSTED_PROXIES: "${entry}" is not an IP address or network; the KYC BFF refuses to start.`);
    }
    const family = version === 4 ? 'ipv4' : 'ipv6';
    if (prefix === undefined) list.addAddress(address, family);
    else list.addSubnet(address, bits, family);
  }

  return (address, hop) => {
    if (hop !== 0) return false;
    // An IPv4 client on a dual-stack socket arrives as ::ffff:a.b.c.d.
    const plain = address.startsWith('::ffff:') && isIP(address.slice(7)) === 4 ? address.slice(7) : address;
    const version = isIP(plain);
    return version !== 0 && list.check(plain, version === 4 ? 'ipv4' : 'ipv6');
  };
}