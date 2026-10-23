import { BlockList, isIP } from 'node:net';

// ┌─ :: SECURITY :: Forwarded headers only from the ingress ───────────────────────────────────────
// │ Why : Next.js reads X-Forwarded-For / -Proto / -Host as they arrive (the Host it checks a
// │       server action's Origin against, for one). Behind the ingress they are the ingress's;
// │       from anyone else they are whatever the sender wrote.
// │ How : <PREFIX>_TRUSTED_PROXIES lists the ingress's addresses and networks (comma-separated,
// │       e.g. "10.0.0.0/8, 192.168.1.10"). A request from one of them keeps X-Forwarded-For and
// │       -Proto, cut to one hop (the ingress's own entry); from anywhere else - or with none
// │       listed - both are removed. X-Forwarded-Host and -Port are always removed: the Host
// │       header is what the ingress passes on. The same rule as Common/WebUtilities
// │       ProxyForwardedHeaders.cs (KnownNetworks / KnownProxies, ForwardLimit 1) in .NET.
// │ Ref : doc/Containers.md, "Behind the ingress" | OWASP A05 | CWE-348
// └────────────────────────────────────────────────────────────────────────────────────────────────
export function forwardedHeaders(settingName) {
  const entries = (process.env[settingName] ?? '').split(',').map((entry) => entry.trim()).filter((entry) => entry.length > 0);
  const list = new BlockList();
  for (const entry of entries) {
    const [address, prefix, extra] = entry.split('/');
    const version = isIP(address);
    const bits = Number(prefix);
    if (version === 0 || extra !== undefined
        || (prefix !== undefined && (!/^\d+$/.test(prefix) || bits > (version === 4 ? 32 : 128)))) {
      throw new Error(`${settingName}: "${entry}" is not an IP address or network; refusing to start.`);
    }
    const family = version === 4 ? 'ipv4' : 'ipv6';
    if (prefix === undefined) list.addAddress(address, family);
    else list.addSubnet(address, bits, family);
  }

  const fromTrustedProxy = (address) => {
    if (!address || entries.length === 0) return false;
    // An IPv4 client on a dual-stack socket arrives as ::ffff:a.b.c.d.
    const plain = address.startsWith('::ffff:') && isIP(address.slice(7)) === 4 ? address.slice(7) : address;
    const version = isIP(plain);
    return version !== 0 && list.check(plain, version === 4 ? 'ipv4' : 'ipv6');
  };
  const lastHop = (value) => (Array.isArray(value) ? value.join(',') : value).split(',').at(-1).trim();

  return (req) => {
    delete req.headers['x-forwarded-host'];
    delete req.headers['x-forwarded-port'];
    if (fromTrustedProxy(req.socket.remoteAddress)) {
      if (req.headers['x-forwarded-for']) req.headers['x-forwarded-for'] = lastHop(req.headers['x-forwarded-for']);
      if (req.headers['x-forwarded-proto']) req.headers['x-forwarded-proto'] = lastHop(req.headers['x-forwarded-proto']);
    } else {
      delete req.headers['x-forwarded-for'];
      delete req.headers['x-forwarded-proto'];
    }
  };
}