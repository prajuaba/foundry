import { describe, it, expect, beforeEach } from 'vitest';
import { useStore } from './store';

/**
 * The IR that Studio exports.
 *
 * This is now Studio's whole responsibility on the export path: it authors the domain model, and the
 * compiler derives everything else from it — C# types, the API manifest, SDKs. So the IR is the one
 * artefact whose correctness is Studio's to guarantee, and it previously had no tests at all.
 */
function classNode(id: string, entity: Record<string, unknown>) {
  return {
    id,
    type: 'classNode',
    position: { x: 0, y: 0 },
    data: { entity: { name: 'Unnamed', properties: [], ...entity } },
  };
}

function enumNode(id: string, name: string, values: string[]) {
  return { id, type: 'enumNode', position: { x: 0, y: 0 }, data: { enum: { name, values } } };
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

describe('exportToSchema', () => {
  beforeEach(() => resetStore([]));

  it('carries the namespace', () => {
    expect(useStore.getState().exportToSchema().Namespace).toBe('Test.Domain');
  });

  it('exports entities with their properties', () => {
    resetStore([
      classNode('1', {
        name: 'Customer',
        properties: [
          { name: 'Id', type: 'ObjectId', isKey: true },
          { name: 'FullName', type: 'string', attributes: ['Required'] },
        ],
      }),
    ]);

    const entity = useStore.getState().exportToSchema().Entities[0];

    expect(entity.Name).toBe('Customer');
    expect(entity.Properties).toHaveLength(2);
  });

  it('preserves the declared API methods', () => {
    // The compiler decides the REST surface from this field, and an entity with none is deliberately
    // given no endpoints -- so dropping it here would silently remove an entity's API.
    resetStore([classNode('1', { name: 'Customer', apiEnabledMethods: ['GET', 'POST'] })]);

    expect(useStore.getState().exportToSchema().Entities[0].ApiEnabledMethods).toEqual(['GET', 'POST']);
  });

  it('exports enums separately from entities', () => {
    resetStore([
      classNode('1', { name: 'Order' }),
      enumNode('2', 'OrderStatus', ['Draft', 'Submitted']),
    ]);

    const schema = useStore.getState().exportToSchema();

    expect(schema.Entities).toHaveLength(1);
    expect(schema.Enums).toHaveLength(1);
    expect(schema.Enums[0].Name).toBe('OrderStatus');
  });

  it('always includes the collections the compiler binds', () => {
    // The compiler deserialises these as lists; a missing one becomes an empty list, which silently
    // generates nothing rather than reporting a malformed document.
    const schema = useStore.getState().exportToSchema();

    expect(schema.Entities).toBeDefined();
    expect(schema.Enums).toBeDefined();
  });

  it('produces an empty but valid document for an empty canvas', () => {
    const schema = useStore.getState().exportToSchema();

    expect(schema.Entities).toHaveLength(0);
    expect(schema.Namespace).toBe('Test.Domain');
  });
});
