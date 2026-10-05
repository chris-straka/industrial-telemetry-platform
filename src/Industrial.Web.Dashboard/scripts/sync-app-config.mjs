// Copies the VITE_API_URL build/dev environment value into the served runtime
// config file. Angular cannot inline process env at serve time, so the
// dashboard resolves its API origin at runtime from assets/app-config.json:
//   1. The file below when it carries a non-empty apiUrl (written here from
//      VITE_API_URL, and in the production image from the same Docker ARG).
//   2. window.location.origin (single-origin deployments, plain `ng serve`).
// The file is gitignored: a missing file is a valid "same origin" config.
import { mkdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const target = join(root, 'src', 'assets', 'app-config.json');

// Compose passes VITE_API_URL as a real environment variable; plain local
// `npm run dev` may only have it in the (gitignored) .env file next door.
let apiUrl = (process.env['VITE_API_URL'] ?? '').trim();
if (apiUrl === '') {
  try {
    const { readFileSync, existsSync } = await import('node:fs');
    const envFile = join(root, '.env');
    if (existsSync(envFile)) {
      for (const line of readFileSync(envFile, 'utf8').split('\n')) {
        const match = line.match(/^\s*VITE_API_URL\s*=\s*(.*?)\s*$/);
        if (match) apiUrl = match[1].replace(/^["']|["']$/g, '').trim();
      }
    }
  } catch {
    // No .env: same-origin default below.
  }
}

mkdirSync(dirname(target), { recursive: true });
writeFileSync(target, `${JSON.stringify({ apiUrl }, null, 2)}\n`);
console.log(`app-config.json: apiUrl=${apiUrl === '' ? '(same origin)' : apiUrl}`);
