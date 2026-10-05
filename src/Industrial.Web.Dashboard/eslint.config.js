import js from '@eslint/js';
import tseslint from 'typescript-eslint';
import { defineConfig, globalIgnores } from 'eslint/config';

export default defineConfig([
  globalIgnores(['dist', 'out-tsc']),
  {
    files: ['scripts/**/*.mjs'],
    extends: [js.configs.recommended],
    languageOptions: {
      ecmaVersion: 'latest',
      sourceType: 'module',
      globals: {
        console: 'readonly',
        process: 'readonly',
      },
    },
  },
  {
    files: ['src/**/*.ts'],
    extends: [js.configs.recommended, tseslint.configs.recommended],
    languageOptions: {
      globals: {
        clearInterval: 'readonly',
        clearTimeout: 'readonly',
        console: 'readonly',
        document: 'readonly',
        fetch: 'readonly',
        setInterval: 'readonly',
        setTimeout: 'readonly',
        window: 'readonly',
      },
    },
  },
]);
