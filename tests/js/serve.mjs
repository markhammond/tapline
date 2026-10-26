// A static server for the browser harnesses (grid-harness.html): Node's own modules, the repository's src/**/wwwroot
// and tests/js only, on the loopback. node tests/js/serve.mjs [port]
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
import { extname, normalize, resolve, sep } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(fileURLToPath(new URL('../..', import.meta.url)));
const port = Number(process.argv[2] ?? 5966);
const types = { '.html': 'text/html', '.js': 'text/javascript', '.mjs': 'text/javascript', '.css': 'text/css', '.json': 'application/json' };

createServer(async (request, response) => {
  const path = normalize(decodeURIComponent(new URL(request.url, 'http://x').pathname)).replace(/^[/\\]+/, '');
  const file = resolve(root, path);
  const allowed = file.startsWith(resolve(root, 'tests', 'js') + sep) || (file.startsWith(resolve(root, 'src') + sep) && file.includes(`${sep}wwwroot${sep}`));
  if (request.method !== 'GET' || !allowed || !types[extname(file)]) {
    response.writeHead(404).end();
    return;
  }
  try {
    const body = await readFile(file);
    response.writeHead(200, { 'content-type': types[extname(file)], 'cache-control': 'no-store' }).end(body);
  } catch {
    response.writeHead(404).end();
  }
}).listen(port, '127.0.0.1', () => console.log(`serving ${root} on http://127.0.0.1:${port}/tests/js/grid-harness.html`));
