import { defineConfig } from 'vitest/config';

export default defineConfig({
  test: {
    // The store reads localStorage at module scope, so importing it needs a browser-ish global.
    // A small shim is used instead of jsdom: these tests exercise pure export/derivation logic, not
    // the DOM, and a full DOM environment would be a large dependency for no added coverage.
    setupFiles: ['./src/test-setup.ts'],
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
  },
});
