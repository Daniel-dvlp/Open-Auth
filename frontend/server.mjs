import http from 'node:http';
import https from 'node:https';
import { readFile } from 'node:fs/promises';
import { extname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('.', import.meta.url));
const port = Number(process.env.FRONTEND_PORT || 5173);
const apiTarget = new URL(process.env.API_BASE_URL || 'http://localhost:8080');
const walletApiTarget = new URL(process.env.BILLETERA_API_BASE_URL || 'http://localhost:8082');
for (const target of [apiTarget, walletApiTarget]) {
  if (!['http:', 'https:'].includes(target.protocol)) throw new Error(`Protocolo de API no admitido: ${target.protocol}`);
}
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml' };

function proxyRequest(req, res, target, pathname) {
  const upstream = new URL(`${pathname}${new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`).search}`, target);
  const headers = { ...req.headers, host: target.host };
  delete headers.connection;
  const transport = upstream.protocol === 'https:' ? https : http;
  const proxyReq = transport.request(upstream, { method: req.method, headers }, proxyRes => {
    res.writeHead(proxyRes.statusCode || 502, proxyRes.headers);
    proxyRes.pipe(res);
  });
  proxyReq.on('error', () => {
    if (!res.headersSent) res.writeHead(502, { 'content-type': 'application/problem+json; charset=utf-8' });
    res.end(JSON.stringify({ title: 'API no disponible', detail: `No se pudo conectar con ${target.origin}.` }));
  });
  req.pipe(proxyReq);
}

http.createServer(async (req, res) => {
  const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
  if (url.pathname.startsWith('/wallet-api/')) {
    proxyRequest(req, res, walletApiTarget, url.pathname.slice('/wallet-api'.length));
    return;
  }
  if (url.pathname.startsWith('/v1/') || url.pathname.startsWith('/health/')) {
    proxyRequest(req, res, apiTarget, url.pathname);
    return;
  }

  let requested;
  const publicPath = url.pathname === '/billetera' || url.pathname === '/billetera/' ? '/billetera.html' : url.pathname;
  try { requested = decodeURIComponent(publicPath === '/' ? '/index.html' : publicPath); }
  catch { res.writeHead(400).end(); return; }
  const file = resolve(root, `.${requested}`);
  const pathFromRoot = relative(root, file);
  if (pathFromRoot === '..' || pathFromRoot.startsWith(`..${sep}`)) { res.writeHead(403).end(); return; }
  try {
    const body = await readFile(file);
    res.writeHead(200, { 'content-type': types[extname(file)] || 'application/octet-stream', 'cache-control': 'no-store' });
    res.end(body);
  } catch {
    res.writeHead(404, { 'content-type': 'text/plain; charset=utf-8' }).end('No encontrado');
  }
}).listen(port, () => console.log(`Escudo Yuma frontend: http://localhost:${port} · Escudo: ${apiTarget.origin} · Billetera: ${walletApiTarget.origin}`));
