import http from 'node:http';
import https from 'node:https';
import { readFile } from 'node:fs/promises';
import { extname, relative, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('.', import.meta.url));
const port = Number(process.env.FRONTEND_PORT || 5173);
const apiTarget = new URL(process.env.API_BASE_URL || 'http://localhost:8080');
const types = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml' };

http.createServer(async (req, res) => {
  const url = new URL(req.url || '/', `http://${req.headers.host || 'localhost'}`);
  if (url.pathname.startsWith('/v1/') || url.pathname.startsWith('/health/')) {
    const upstream = new URL(`${url.pathname}${url.search}`, apiTarget);
    const headers = { ...req.headers, host: apiTarget.host };
    delete headers.connection;
    try {
      const transport = upstream.protocol === 'https:' ? https : http;
      const proxyReq = transport.request(upstream, { method: req.method, headers }, proxyRes => {
        res.writeHead(proxyRes.statusCode || 502, proxyRes.headers);
        proxyRes.pipe(res);
      });
      proxyReq.on('error', () => {
        if (!res.headersSent) res.writeHead(502, { 'content-type': 'application/problem+json; charset=utf-8' });
        res.end(JSON.stringify({ title: 'API no disponible', detail: `No se pudo conectar con ${apiTarget.origin}.` }));
      });
      req.pipe(proxyReq);
    } catch {
      res.writeHead(502, { 'content-type': 'application/problem+json; charset=utf-8' });
      res.end(JSON.stringify({ title: 'API no disponible' }));
    }
    return;
  }

  let requested;
  try { requested = decodeURIComponent(url.pathname === '/' ? '/index.html' : url.pathname); }
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
}).listen(port, () => console.log(`Escudo Yuma frontend: http://localhost:${port} · API: ${apiTarget.origin}`));
