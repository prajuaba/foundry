import { describe, it, expect, vi, afterEach } from 'vitest';
import { compileToCs, deriveApiManifest, crudRouteFor } from './compiler';

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
 * The display-side route helper.
 *
 * The table below is deliberately the same one used by ApiManifestGeneratorTests on the C# side. It is
 * what keeps a mirrored implementation honest: if the compiler's derivation changes, this fails.
 */
describe('crudRouteFor', () => {
  it.each([
    ['Customer', '/api/customers'],
    ['Category', '/api/categories'],
    ['Address', '/api/addresses'],
    ['Box', '/api/boxes'],
    ['Branch', '/api/branches'],
    ['Day', '/api/days'],
    ['Order', '/api/orders'],
  ])('derives %s as %s, matching the compiler', (name, expected) => {
    expect(crudRouteFor(name)).toBe(expected);
  });

  it('never emits a version segment', () => {
    // Three places in the UI used to emit /api/v1/..., which the compiler does not generate, so the
    // designer displayed and the playground called URLs the running application never served.
    expect(crudRouteFor('Customer')).not.toContain('/v1/');
  });

  it('handles an empty name without producing a broken route', () => {
    expect(crudRouteFor('')).toBe('/api');
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
