import { describe, it, expect, beforeEach } from 'vitest';
import { useStore } from './store';

/**
 * The api-manifest.json that Studio exports.
 *
 * This file exists because the manifest has *two* producers: this function, and
 * `ApiManifestGenerator` in the C# compiler that `foundry compile` uses. Both claim to turn the same
 * domain model into the same API surface, and where they disagree, an application behaves differently
 * depending on which tool wrote its manifest — with nothing reporting a conflict, because each
 * manifest is individually valid.
 *
 * The assertions below encode the compiler's output as the normative contract: it is the producer the
 * runtime smoke test exercises end to end, so it is the one known to serve real requests.
 */

/** Minimal class node, shaped as the store expects. */
function classNode(id: string, entity: Record<string, unknown>) {
  return {
    id,
    type: 'classNode',
    position: { x: 0, y: 0 },
    data: { entity: { name: 'Unnamed', properties: [], ...entity } },
  };
}

function resetStore(nodes: unknown[]) {
  useStore.setState({
    nodes: nodes as never,
    edges: [],
    customEndpoints: [],
    dtos: [],
    workflows: [],
    namespace: 'Test.Domain',
  } as never);
}

describe('exportToApiManifest', () => {
  beforeEach(() => {
    resetStore([]);
  });

  describe('route derivation must match the compiler', () => {
    it('uses /api/{plural} without a version segment', () => {
      // Studio emitted /api/v1/customers while the compiler emits /api/customers, so a client
      // generated from one manifest 404s against an application built from the other.
      resetStore([
        classNode('1', { name: 'Customer', apiEnabledMethods: ['GET'] }),
      ]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints[0].Route).toBe('/api/customers');
    });

    it.each([
      ['Customer', '/api/customers'],
      ['Category', '/api/categories'],
      ['Address', '/api/addresses'],
      ['Box', '/api/boxes'],
      ['Branch', '/api/branches'],
      ['Day', '/api/days'],
      ['Order', '/api/orders'],
    ])('pluralises %s to %s', (name, expected) => {
      // The same cases the compiler's Pluralize is tested against. Divergence here silently moves
      // a published endpoint.
      resetStore([classNode('1', { name, apiEnabledMethods: ['GET'] })]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints[0].Route).toBe(expected);
    });
  });

  describe('which entities get a REST surface', () => {
    it('omits an entity that declares no methods', () => {
      // Studio defaulted an entity with no apiEnabledMethods to full CRUD, while the compiler skips
      // it. An entity intended only as a workflow target or DTO source therefore acquired a complete
      // public CRUD surface — including DELETE — purely by being on the canvas.
      resetStore([classNode('1', { name: 'AuditRecord', apiEnabledMethods: [] })]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints).toHaveLength(0);
    });

    it('omits an entity whose methods are undefined', () => {
      resetStore([classNode('1', { name: 'AuditRecord' })]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints).toHaveLength(0);
    });

    it('includes only the methods that were declared', () => {
      resetStore([
        classNode('1', { name: 'Customer', apiEnabledMethods: ['GET', 'POST'] }),
      ]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints[0].Methods).toEqual(['GET', 'POST']);
      expect(manifest.Endpoints[0].Methods).not.toContain('DELETE');
    });

    it('exports one endpoint group per entity', () => {
      resetStore([
        classNode('1', { name: 'Customer', apiEnabledMethods: ['GET'] }),
        classNode('2', { name: 'Order', apiEnabledMethods: ['GET'] }),
      ]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints.map((e: { Entity: string }) => e.Entity).sort())
        .toEqual(['Customer', 'Order']);
    });
  });

  describe('manifest shape', () => {
    it('carries the namespace', () => {
      resetStore([classNode('1', { name: 'Customer', apiEnabledMethods: ['GET'] })]);

      expect(useStore.getState().exportToApiManifest().Namespace).toBe('Test.Domain');
    });

    it('always includes the collections the runtime deserialises', () => {
      // ApiManifest binds Endpoints and CustomEndpoints as non-null lists. Omitting one leaves the
      // runtime to fall back to an empty list, which serves no routes rather than reporting a
      // malformed manifest.
      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints).toBeDefined();
      expect(manifest.CustomEndpoints).toBeDefined();
    });

    it('propagates per-method roles', () => {
      resetStore([
        classNode('1', {
          name: 'Invoice',
          apiEnabledMethods: ['GET', 'DELETE'],
          apiRoles: { DELETE: ['Admin'] },
        }),
      ]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints[0].Roles.DELETE).toEqual(['Admin']);
    });

    it('does not carry roles for methods that are not exposed', () => {
      // A stale role entry for a method nobody can call is misleading when auditing access.
      resetStore([
        classNode('1', {
          name: 'Invoice',
          apiEnabledMethods: ['GET'],
          apiRoles: { DELETE: ['Admin'] },
        }),
      ]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints[0].Roles.DELETE).toBeUndefined();
    });

    it('propagates caching only when enabled', () => {
      resetStore([
        classNode('1', {
          name: 'Customer',
          apiEnabledMethods: ['GET', 'POST'],
          apiCaching: {
            GET: { enabled: true, ttlSeconds: 30 },
            POST: { enabled: false, ttlSeconds: 60 },
          },
        }),
      ]);

      const manifest = useStore.getState().exportToApiManifest();
      const caching = manifest.Endpoints[0].Caching ?? {};

      expect(caching.GET).toEqual({ Enabled: true, TtlSeconds: 30 });
      expect(caching.POST).toBeUndefined();
    });

    it('ignores non-entity nodes', () => {
      resetStore([
        classNode('1', { name: 'Customer', apiEnabledMethods: ['GET'] }),
        { id: '2', type: 'enumNode', position: { x: 0, y: 0 }, data: { enum: { name: 'Status', values: [] } } },
      ]);

      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints).toHaveLength(1);
    });

    it('produces an empty manifest for an empty canvas', () => {
      const manifest = useStore.getState().exportToApiManifest();

      expect(manifest.Endpoints).toHaveLength(0);
      expect(manifest.CustomEndpoints).toHaveLength(0);
    });
  });
});
