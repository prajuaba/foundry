import { describe, it, expect, vi, afterEach } from 'vitest';
import { compileToCs, deriveApiManifest, parseManifestRoutes } from './compiler';

/**
 * Obtaining the manifest from the compiler.
 *
 * The behaviour that matters here is what happens when the backend is unavailable: this must fail with
 * a message, never fall back to computing a manifest locally. A fallback would reintroduce the exact
 * divergence this replaced, where Studio and `foundry compile` produced different routes for the same
 * domain model and nothing reported the conflict.
 *
 * The contract itself — routes, which entities are exposed, roles and caching — is tested in
 * ApiManifestGeneratorTests on the C# side, which is now the only implementation.
 */
describe('deriveApiManifest', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const schema = { Namespace: 'Test.Domain', Entities: [] };

  it('returns the manifest the backend produced, verbatim', async () => {
    // Returned as text rather than parsed and re-serialised, so the bytes Studio offers for download
    // are the bytes the compiler wrote.
    const backendOutput = '{\n  "Namespace": "Test.Domain",\n  "Endpoints": []\n}';
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(backendOutput, { status: 200 })));

    await expect(deriveApiManifest(schema)).resolves.toBe(backendOutput);
  });

  it('posts the schema to the manifest endpoint', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await deriveApiManifest(schema);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toContain('/api/manifest');
    expect(init.method).toBe('POST');
    expect(JSON.parse(init.body)).toEqual(schema);
  });

  it('explains that the backend must be running when it cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    await expect(deriveApiManifest(schema)).rejects.toThrow(/backend/i);
  });

  it('surfaces the status and detail when the backend rejects the schema', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response('{"error":"Invalid schema. Namespace is required."}', {
          status: 400,
          statusText: 'Bad Request',
        }),
      ),
    );

    await expect(deriveApiManifest(schema)).rejects.toThrow(/400|Namespace is required/);
  });

  it('never returns a locally-computed manifest as a fallback', async () => {
    // The point of the whole change. If the backend is down the caller must see an error, not a
    // plausible-looking manifest that may disagree with what `foundry compile` produces.
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    await expect(deriveApiManifest(schema)).rejects.toThrow();
  });
});

/**
 * Reading routes out of a compiler-derived manifest.
 *
 * This replaces a test table that was deliberately duplicated from `ApiManifestGeneratorTests` to
 * keep a mirrored `crudRouteFor` honest. A shared table catches a derivation that *changes*; it
 * cannot catch a rule the compiler gains and the mirror never hears about, because a rule nobody
 * wrote down twice is not in the table. There is now one derivation, and these tests cover reading
 * its output rather than reproducing it.
 */
describe('parseManifestRoutes', () => {
  it('maps each entity to the route the compiler generated', () => {
    const manifest = JSON.stringify({
      Namespace: 'Test.Domain',
      Endpoints: [
        { Entity: 'Customer', Route: '/api/customers', Methods: ['GET'] },
        { Entity: 'Category', Route: '/api/categories', Methods: ['GET', 'POST'] },
      ],
    });

    expect(parseManifestRoutes(manifest)).toEqual({
      Customer: '/api/customers',
      Category: '/api/categories',
    });
  });

  it('omits an entity the compiler generated no endpoint for', () => {
    // An entity declaring no methods is skipped by the compiler. The designer used to show a route
    // for it anyway, which advertised an endpoint the application would never serve.
    const manifest = JSON.stringify({
      Endpoints: [{ Entity: 'Customer', Route: '/api/customers', Methods: ['GET'] }],
    });

    expect(parseManifestRoutes(manifest)).not.toHaveProperty('Order');
  });

  it('returns nothing for a manifest with no endpoints', () => {
    expect(parseManifestRoutes(JSON.stringify({ Namespace: 'X' }))).toEqual({});
    expect(parseManifestRoutes(JSON.stringify({ Endpoints: [] }))).toEqual({});
  });

  it('skips malformed entries rather than inventing a route', () => {
    const manifest = JSON.stringify({
      Endpoints: [
        { Entity: 'Customer', Route: '/api/customers' },
        { Entity: 'NoRoute' },
        { Route: '/api/orphans' },
        { Entity: '', Route: '/api/empty' },
        null,
      ],
    });

    expect(parseManifestRoutes(manifest)).toEqual({ Customer: '/api/customers' });
  });

  it('reports invalid JSON rather than returning an empty map', () => {
    // An empty map is indistinguishable from "this schema has no endpoints", which would make a
    // broken backend look like an empty design.
    expect(() => parseManifestRoutes('{not json')).toThrow(/not valid JSON/);
  });
});

describe('compileToCs', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  const schema = { Namespace: 'Test.Domain', Entities: [], Enums: [] };

  it('returns the files the compiler produced', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(JSON.stringify({ files: { 'Customer.cs': 'public partial record Customer;' } }), {
          status: 200,
        }),
      ),
    );

    await expect(compileToCs(schema)).resolves.toEqual({
      'Customer.cs': 'public partial record Customer;',
    });
  });

  it('posts the schema to the compile endpoint', async () => {
    const fetchMock = vi.fn().mockResolvedValue(new Response('{"files":{}}', { status: 200 }));
    vi.stubGlobal('fetch', fetchMock);

    await compileToCs(schema);

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toContain('/api/compile');
    expect(JSON.parse(init.body)).toEqual(schema);
  });

  it('returns an empty map when the compiler emitted no files', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('{}', { status: 200 })));

    await expect(compileToCs(schema)).resolves.toEqual({});
  });

  it('explains that the backend must be running when it cannot be reached', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    await expect(compileToCs(schema)).rejects.toThrow(/backend/i);
  });

  it('surfaces the compiler’s rejection rather than returning nothing', async () => {
    // An empty preview with no explanation reads as "this schema has no entities", which is the wrong
    // conclusion and sends someone editing a model that was fine.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response('{"error":"Invalid schema. Namespace is required."}', {
          status: 400,
          statusText: 'Bad Request',
        }),
      ),
    );

    await expect(compileToCs(schema)).rejects.toThrow(/400|Namespace is required/);
  });

  it('never falls back to generating C# locally', async () => {
    // The reason the TypeScript generators were removed. A local fallback would reintroduce output that
    // does not match `foundry compile` -- previously including a namespace that does not exist and a
    // missing `partial` modifier.
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new TypeError('Failed to fetch')));

    await expect(compileToCs(schema)).rejects.toThrow();
  });
});

describe('compileToCs filenames', () => {
  afterEach(() => vi.unstubAllGlobals());

  it('appends .cs, matching what the compiler writes to disk', async () => {
    // The compiler keys files by path without an extension and appends .cs in its writer. Without
    // mirroring that, Studio offered downloads named "Customer" with no extension.
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(
        new Response(
          JSON.stringify({ files: { Customer: 'x', 'Handlers/SubmitOrderHandler': 'y' } }),
          { status: 200 },
        ),
      ),
    );

    const files = await compileToCs({ Namespace: 'Test.Domain' });

    expect(Object.keys(files).sort()).toEqual(['Customer.cs', 'Handlers/SubmitOrderHandler.cs']);
  });

  it('does not double up an extension the compiler already supplied', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue(new Response(JSON.stringify({ files: { 'Customer.cs': 'x' } }), { status: 200 })),
    );

    expect(Object.keys(await compileToCs({ Namespace: 'Test.Domain' }))).toEqual(['Customer.cs']);
  });
});
