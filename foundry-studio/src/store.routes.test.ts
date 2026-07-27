import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import { useStore } from './store';
import { BACKEND_URL } from './compiler';

/**
 * Route caching in the store.
 *
 * Studio used to derive CRUD routes itself, in a `crudRouteFor` that mirrored the compiler's
 * `RouteFor`. That mirror was the last one in the codebase, and it existed for a real reason: the
 * designer and playground need a route to display, and a request per keystroke would be absurd.
 *
 * The cache is what made deleting it affordable. These tests cover the two properties that matter —
 * a keystroke that cannot change routing costs no request, and an unreachable backend leaves the
 * routes *unknown* rather than guessed.
 */
function classNode(id: string, name: string, apiEnabledMethods: string[] = ['GET']) {
  return {
    id,
    type: 'classNode',
    position: { x: 0, y: 0 },
    data: {
      entity: {
        name,
        properties: [{ name: 'Id', type: 'ObjectId', isKey: true }],
        apiEnabledMethods,
      },
    },
  };
}

function manifestWith(routes: Record<string, string>) {
  return JSON.stringify({
    Namespace: 'Test.Domain',
    Endpoints: Object.entries(routes).map(([Entity, Route]) => ({ Entity, Route, Methods: ['GET'] })),
  });
}

function reset(nodes: unknown[]) {
  useStore.setState({
    nodes: nodes as never,
    edges: [],
    namespace: 'Test.Domain',
    customEndpoints: [],
    dtos: [],
    workflows: [],
    routes: {},
    routesStatus: 'idle',
    routesError: null,
  } as never);
}

describe('refreshRoutes', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    reset([classNode('1', 'Customer')]);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function stubBackend(body: string, ok = true) {
    const fetchMock = vi.fn().mockResolvedValue({
      ok,
      status: ok ? 200 : 500,
      statusText: ok ? 'OK' : 'Internal Server Error',
      text: () => Promise.resolve(body),
    });
    vi.stubGlobal('fetch', fetchMock);
    return fetchMock;
  }

  it('caches the routes the compiler derived', async () => {
    stubBackend(manifestWith({ Customer: '/api/customers' }));

    await useStore.getState().refreshRoutes();

    expect(useStore.getState().routes).toEqual({ Customer: '/api/customers' });
    expect(useStore.getState().routesStatus).toBe('ready');
  });

  it('asks the compiler rather than deriving the route locally', async () => {
    const fetchMock = stubBackend(manifestWith({ Customer: '/api/customers' }));

    await useStore.getState().refreshRoutes();

    expect(fetchMock).toHaveBeenCalledWith(
      `${BACKEND_URL}/api/manifest`,
      expect.objectContaining({ method: 'POST' }),
    );
  });

  it('does not re-derive when nothing route-affecting changed', async () => {
    // This is what pays for removing the mirror. Without it, a route display would cost a round trip
    // on every keystroke and the local derivation would be the only sane option.
    const fetchMock = stubBackend(manifestWith({ Customer: '/api/customers' }));

    await useStore.getState().refreshRoutes();
    await useStore.getState().refreshRoutes();

    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('re-derives when an entity is renamed', async () => {
    stubBackend(manifestWith({ Customer: '/api/customers' }));
    await useStore.getState().refreshRoutes();

    // Only the node changes -- routesStatus stays 'ready' -- so the changed signature is the only
    // thing that can force the second derive. Resetting the store here would have re-derived
    // regardless and proven nothing.
    useStore.setState({ nodes: [classNode('1', 'Client')] as never });
    const fetchMock = stubBackend(manifestWith({ Client: '/api/clients' }));
    await useStore.getState().refreshRoutes();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(useStore.getState().routes).toEqual({ Client: '/api/clients' });
  });

  it('re-derives when an entity gains or loses API methods', async () => {
    // Whether an entity has a route at all depends on this, so it has to be part of the cache key.
    stubBackend(manifestWith({ Customer: '/api/customers' }));
    await useStore.getState().refreshRoutes();

    useStore.setState({ nodes: [classNode('1', 'Customer', [])] as never });
    const fetchMock = stubBackend(manifestWith({}));
    await useStore.getState().refreshRoutes();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(useStore.getState().routes).toEqual({});
  });

  it('reports an unreachable backend instead of guessing a route', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('connection refused')));

    await useStore.getState().refreshRoutes();

    expect(useStore.getState().routesStatus).toBe('unavailable');
    expect(useStore.getState().routesError).toMatch(/backend/i);
    expect(useStore.getState().routes).toEqual({});
  });

  it('keeps the last known routes when the backend goes away', async () => {
    // A backend that stopped responding does not make the previously derived routes wrong, and
    // blanking the designer mid-edit is worse than showing them beside the error.
    stubBackend(manifestWith({ Customer: '/api/customers' }));
    await useStore.getState().refreshRoutes();

    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('connection refused')));
    useStore.setState({ nodes: [classNode('1', 'Customer', ['GET', 'POST'])] as never });
    await useStore.getState().refreshRoutes();

    expect(useStore.getState().routesStatus).toBe('unavailable');
    expect(useStore.getState().routes).toEqual({ Customer: '/api/customers' });
  });

  it('retries after a failure rather than caching the failed signature', async () => {
    vi.stubGlobal('fetch', vi.fn().mockRejectedValue(new Error('connection refused')));
    await useStore.getState().refreshRoutes();

    const fetchMock = stubBackend(manifestWith({ Customer: '/api/customers' }));
    await useStore.getState().refreshRoutes();

    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(useStore.getState().routesStatus).toBe('ready');
  });

  it('omits an entity the compiler generates no endpoint for', async () => {
    // The designer used to show a route for such an entity, advertising an endpoint that would
    // never be served.
    reset([classNode('1', 'Customer'), classNode('2', 'Draft', [])]);
    stubBackend(manifestWith({ Customer: '/api/customers' }));

    await useStore.getState().refreshRoutes();

    expect(useStore.getState().routes).not.toHaveProperty('Draft');
  });
});
