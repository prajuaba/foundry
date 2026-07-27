import { create } from 'zustand';
import { 
  applyNodeChanges, 
  applyEdgeChanges, 
  addEdge,
  type Edge, 
  type OnNodesChange, 
  type OnEdgesChange, 
  type OnConnect 
} from '@xyflow/react';
import type { Entity, Property, Index, AppNode, ClassNode, EnumNode, CustomEndpoint, DtoModel, WorkflowDefinition, Connector } from './types';
import { deriveApiManifest, parseManifestRoutes } from './compiler';

/**
 * Schema signature the cached routes were derived from.
 *
 * Module-level rather than store state: it is bookkeeping for the cache, not something a component
 * should be able to read or react to.
 */
let lastRouteSignature: string | null = null;

interface StoreState {
  namespace: string;
  nodes: AppNode[];
  edges: Edge[];
  activeTool: 'Association' | 'Composition' | 'Inheritance' | 'Select';

  // React Flow actions
  onNodesChange: OnNodesChange;
  onEdgesChange: OnEdgesChange;
  onConnect: OnConnect;

  // Domain specific actions
  setActiveTool: (tool: 'Association' | 'Composition' | 'Inheritance' | 'Select') => void;
  setNamespace: (ns: string) => void;
  addClassNode: (x: number, y: number) => void;
  addEnumNode: (x: number, y: number) => void;
  deleteNode: (nodeId: string) => void;
  deleteEdge: (edgeId: string) => void;
  updateEdgeRelationship: (edgeId: string, type: 'Association' | 'Composition' | 'Inheritance' | 'Select') => void;
  updateClassNode: (nodeId: string, updatedData: Partial<Entity>) => void;
  updateEnumNode: (nodeId: string, updatedData: { name: string; values: string[] }) => void;
  updateProperty: (nodeId: string, propIndex: number, updatedProp: Partial<Property>) => void;
  addProperty: (nodeId: string) => void;
  deleteProperty: (nodeId: string, propIndex: number) => void;
  addIndex: (nodeId: string) => void;
  deleteIndex: (nodeId: string, indexIdx: number) => void;
  updateIndex: (nodeId: string, indexIdx: number, fields: string[], unique: boolean) => void;

  // Serialization & Deserialization
  exportToSchema: () => any;
  importFromSchema: (schema: any) => void;
  /** Visual layout only (node positions and edges); carries no domain semantics. */
  exportLayout: () => any;
  exportProject: () => any;
  importProject: (project: any) => void;
  
  // Custom setter for bulk node/edge updates
  setCanvasData: (nodes: AppNode[], edges: Edge[]) => void;

  /**
   * Entity-to-route map, as the compiler derives it, cached from the last successful refresh.
   *
   * An entity absent from this map has no generated route -- either it declares no API methods, or
   * the routes have not been derived yet. Callers must distinguish those with `routesStatus` rather
   * than filling the gap with a guess. Guessing is what the removed `crudRouteFor` mirror amounted
   * to, and its predecessors in the UI guessed wrongly -- `/api/v1/customer` against an application
   * serving `/api/customers`.
   */
  routes: Readonly<Record<string, string>>;
  routesStatus: 'idle' | 'loading' | 'ready' | 'unavailable';
  routesError: string | null;

  /** Derives routes from the current schema, unless the schema has not changed since the last one. */
  refreshRoutes: () => Promise<void>;

  customEndpoints: CustomEndpoint[];
  addCustomEndpoint: () => void;
  updateCustomEndpoint: (index: number, updated: Partial<CustomEndpoint>) => void;
  deleteCustomEndpoint: (index: number) => void;

  dtos: DtoModel[];
  addDto: (name?: string) => void;
  updateDto: (index: number, updated: Partial<DtoModel>) => void;
  deleteDto: (index: number) => void;

  connectors: Connector[];
  addConnector: () => void;
  updateConnector: (index: number, updated: Partial<Connector>) => void;
  deleteConnector: (index: number) => void;

  workflows: WorkflowDefinition[];
  addWorkflow: () => void;
  updateWorkflow: (index: number, updated: Partial<WorkflowDefinition>) => void;
  deleteWorkflow: (index: number) => void;

  ollamaHost: string;
  ollamaModel: string;
  setOllamaHost: (host: string) => void;
  setOllamaModel: (model: string) => void;
  ollamaModels: string[];
  fetchOllamaModels: () => Promise<void>;

  undo: () => void;
  redo: () => void;
  canUndo: boolean;
  canRedo: boolean;
}

// Module-level history variables to persist history state across set calls
const pastStates: any[] = [];
const futureStates: any[] = [];

export const useStore = create<StoreState>((rawSet, get) => {
  const captureSnapshot = () => {
    const state = get();
    if (!state) return null;
    return {
      nodes: JSON.parse(JSON.stringify(state.nodes || [])),
      edges: JSON.parse(JSON.stringify(state.edges || [])),
      customEndpoints: JSON.parse(JSON.stringify(state.customEndpoints || [])),
      dtos: JSON.parse(JSON.stringify(state.dtos || [])),
      workflows: JSON.parse(JSON.stringify(state.workflows || [])),
      namespace: state.namespace
    };
  };

  const set = (
    slice: Partial<StoreState> | ((state: StoreState) => Partial<StoreState> | StoreState),
    replace?: boolean
  ) => {
    const state = get();
    if (state) {
      const nextState = typeof slice === 'function' ? slice(state) : slice;
      const hasRelevantChange = 
        'nodes' in nextState || 
        'edges' in nextState || 
        'customEndpoints' in nextState || 
        'dtos' in nextState || 
        'workflows' in nextState || 
        'namespace' in nextState;

      let shouldSave = hasRelevantChange;
      if (shouldSave && 'nodes' in nextState) {
        const currentNodes = state.nodes || [];
        const newNodes = nextState.nodes || [];
        if (currentNodes.length === newNodes.length) {
          const onlyPosOrSel = newNodes.every((n: any, i: number) => {
            const cur = currentNodes[i];
            return cur && 
              cur.id === n.id && 
              cur.type === n.type && 
              JSON.stringify(cur.data) === JSON.stringify(n.data);
          });
          if (onlyPosOrSel) {
            shouldSave = false;
          }
        }
      }

      if (shouldSave) {
        const snap = captureSnapshot();
        if (snap) {
          pastStates.push(snap);
          if (pastStates.length > 50) pastStates.shift();
          futureStates.length = 0;
          rawSet({ canUndo: true, canRedo: false });
        }
      }
    }
    
    if (replace) {
      rawSet(slice as any, true);
    } else {
      rawSet(slice as any);
    }
  };

  return {
    namespace: 'Paperclip.OrderingSystem.Domain',
  nodes: [],
  edges: [],
  activeTool: 'Select',
  customEndpoints: [],
  dtos: [],
  workflows: [],
  canUndo: false,
  canRedo: false,

  undo: () => {
    if (pastStates.length === 0) return;
    const prev = pastStates.pop();
    const current = {
      nodes: JSON.parse(JSON.stringify(get().nodes || [])),
      edges: JSON.parse(JSON.stringify(get().edges || [])),
      customEndpoints: JSON.parse(JSON.stringify(get().customEndpoints || [])),
      dtos: JSON.parse(JSON.stringify(get().dtos || [])),
      workflows: JSON.parse(JSON.stringify(get().workflows || [])),
      namespace: get().namespace
    };
    futureStates.push(current);
    rawSet({
      ...prev,
      canUndo: pastStates.length > 0,
      canRedo: true
    });
  },

  redo: () => {
    if (futureStates.length === 0) return;
    const next = futureStates.pop();
    const current = {
      nodes: JSON.parse(JSON.stringify(get().nodes || [])),
      edges: JSON.parse(JSON.stringify(get().edges || [])),
      customEndpoints: JSON.parse(JSON.stringify(get().customEndpoints || [])),
      dtos: JSON.parse(JSON.stringify(get().dtos || [])),
      workflows: JSON.parse(JSON.stringify(get().workflows || [])),
      namespace: get().namespace
    };
    pastStates.push(current);
    rawSet({
      ...next,
      canUndo: true,
      canRedo: futureStates.length > 0
    });
  },
  // Default to a local Ollama. A specific developer's machine name must never be the
  // default: it made this feature fail for every other user of the repo.
  ollamaHost: localStorage.getItem('ollama_host') || 'http://localhost:11434',
  ollamaModel: localStorage.getItem('ollama_model') || 'qwen3-coder:30b',
  ollamaModels: JSON.parse(localStorage.getItem('ollama_models') || '[]'),

  setOllamaHost: (host) => {
    localStorage.setItem('ollama_host', host);
    set({ ollamaHost: host });
  },
  setOllamaModel: (model) => {
    localStorage.setItem('ollama_model', model);
    set({ ollamaModel: model });
  },
  fetchOllamaModels: async () => {
    const host = get().ollamaHost;
    if (!host) return;
    try {
      const response = await fetch(`http://localhost:5100/api/ai/models?host=${encodeURIComponent(host)}`);
      if (!response.ok) {
        throw new Error(`Failed to fetch models: ${response.statusText}`);
      }
      const data = await response.json();
      if (Array.isArray(data)) {
        localStorage.setItem('ollama_models', JSON.stringify(data));
        set({ ollamaModels: data });
        if (data.length > 0 && !data.includes(get().ollamaModel)) {
          get().setOllamaModel(data[0]);
        }
      }
    } catch (err) {
      console.error(err);
      throw err;
    }
  },

  addCustomEndpoint: () => set((state) => ({
    customEndpoints: [
      ...state.customEndpoints,
      { 
        route: '/api/v1/custom-route', 
        method: 'POST', 
        requestType: 'CustomCommand', 
        roles: ['Admin'], 
        operationType: 'Custom', 
        assignments: [], 
        businessRules: [] 
      }
    ]
  })),

  updateCustomEndpoint: (index, updated) => set((state) => {
    const list = [...state.customEndpoints];
    list[index] = { ...list[index], ...updated };
    return { customEndpoints: list };
  }),

  deleteCustomEndpoint: (index) => set((state) => ({
    customEndpoints: state.customEndpoints.filter((_, i) => i !== index)
  })),

  addDto: (name) => set((state) => ({
    dtos: [
      ...state.dtos,
      { name: name || `Dto${state.dtos.length + 1}`, properties: [] }
    ]
  })),

  updateDto: (index, updated) => set((state) => {
    const list = [...state.dtos];
    list[index] = { ...list[index], ...updated };
    return { dtos: list };
  }),

  deleteDto: (index) => set((state) => ({
    dtos: state.dtos.filter((_, i) => i !== index)
  })),

  connectors: [],
  addConnector: () => set((state) => ({
    connectors: [
      ...(state.connectors || []),
      { name: `Connector${(state.connectors || []).length + 1}`, type: 'REST', baseUrl: 'https://api.external.com', authType: 'None', timeoutSeconds: 30, maxRetries: 3 }
    ]
  })),

  updateConnector: (index, updated) => set((state) => {
    const list = [...(state.connectors || [])];
    list[index] = { ...list[index], ...updated };
    return { connectors: list };
  }),

  deleteConnector: (index) => set((state) => ({
    connectors: (state.connectors || []).filter((_, i) => i !== index)
  })),

  addWorkflow: () => set((state) => ({
    workflows: [
      ...state.workflows,
      {
        id: `workflow_${state.workflows.length + 1}`,
        name: `New Workflow ${state.workflows.length + 1}`,
        entity: '',
        version: '1.0.0',
        effectiveDate: new Date().toISOString().split('T')[0],
        expirationDate: '',
        isActive: true,
        states: [
          { name: 'Draft', isInitial: true, isFinal: false, allowedRoles: ['Admin'] },
          { name: 'Approved', isInitial: false, isFinal: true, allowedRoles: ['Admin'] }
        ],
        transitions: [],
        choiceNodes: []
      }
    ]
  })),

  updateWorkflow: (index, updated) => set((state) => {
    const list = [...state.workflows];
    list[index] = { ...list[index], ...updated };
    return { workflows: list };
  }),

  deleteWorkflow: (index) => set((state) => ({
    workflows: state.workflows.filter((_, i) => i !== index)
  })),

  // React Flow actions
  onNodesChange: (changes) => {
    set((state) => ({
      nodes: applyNodeChanges(changes, state.nodes) as AppNode[],
    }));
  },

  onEdgesChange: (changes) => {
    set((state) => {
      let updatedNodes = [...state.nodes];
      
      // Intercept removed edges to perform property sync cleanup on keyboard delete
      changes.forEach(change => {
        if (change.type === 'remove') {
          const edgeId = change.id;
          const edge = state.edges.find(e => e.id === edgeId);
          if (edge && edge.source && edge.target) {
            const sourceNode = state.nodes.find(n => n.id === edge.source);
            const targetNode = state.nodes.find(n => n.id === edge.target);
            
            if (sourceNode && targetNode && sourceNode.type === 'classNode') {
              const targetName = targetNode.type === 'classNode'
                ? (targetNode as ClassNode).data.entity.name
                : (targetNode as EnumNode).data.enum.name;
                
              const relType = edge.data?.relationshipType;
              
              if (relType === 'Inheritance') {
                updatedNodes = updatedNodes.map(node => {
                  if (node.id === sourceNode.id && node.type === 'classNode') {
                    const cn = node as ClassNode;
                    return {
                      ...cn,
                      data: {
                        ...cn.data,
                        entity: {
                          ...cn.data.entity,
                          baseClass: undefined,
                        }
                      }
                    } as AppNode;
                  }
                  return node;
                });
              } else if (relType === 'Composition') {
                const propName = targetName.endsWith('s') ? `${targetName}List` : `${targetName}s`;
                updatedNodes = updatedNodes.map(node => {
                  if (node.id === sourceNode.id && node.type === 'classNode') {
                    const cn = node as ClassNode;
                    return {
                      ...cn,
                      data: {
                        ...cn.data,
                        entity: {
                          ...cn.data.entity,
                          properties: cn.data.entity.properties.filter(p => p.name !== propName),
                        }
                      }
                    } as AppNode;
                  }
                  return node;
                });
              } else if (relType === 'Association') {
                const isEnum = targetNode.type === 'enumNode';
                const propName = isEnum ? targetName : `${targetName}Id`;
                updatedNodes = updatedNodes.map(node => {
                  if (node.id === sourceNode.id && node.type === 'classNode') {
                    const cn = node as ClassNode;
                    return {
                      ...cn,
                      data: {
                        ...cn.data,
                        entity: {
                          ...cn.data.entity,
                          properties: cn.data.entity.properties.filter(p => p.name !== propName),
                        }
                      }
                    } as AppNode;
                  }
                  return node;
                });
              }
            }
          }
        }
      });
      
      return {
        nodes: updatedNodes,
        edges: applyEdgeChanges(changes, state.edges) as Edge[],
      };
    });
  },

  onConnect: (connection) => {
    set((state) => {
      const sourceNode = state.nodes.find(n => n.id === connection.source);
      const targetNode = state.nodes.find(n => n.id === connection.target);
      
      let updatedNodes = [...state.nodes];
      
      if (sourceNode && targetNode && sourceNode.type === 'classNode') {
        const targetName = targetNode.type === 'classNode' 
          ? (targetNode as ClassNode).data.entity.name 
          : (targetNode as EnumNode).data.enum.name;
        
        if (state.activeTool === 'Inheritance') {
          // Update base class
          updatedNodes = state.nodes.map(node => {
            if (node.id === sourceNode.id && node.type === 'classNode') {
              const cn = node as ClassNode;
              return {
                ...cn,
                data: {
                  ...cn.data,
                  entity: {
                    ...cn.data.entity,
                    baseClass: targetName,
                  }
                }
              } as AppNode;
            }
            return node;
          });
        } else if (state.activeTool === 'Composition') {
          // Add List<TargetName> property
          const propName = targetName.endsWith('s') ? `${targetName}List` : `${targetName}s`;
          const newProp: Property = {
            name: propName,
            type: `List<${targetName}>`,
            isKey: false,
            isEnum: false,
            attributes: [],
          };
          
          updatedNodes = state.nodes.map(node => {
            if (node.id === sourceNode.id && node.type === 'classNode') {
              const cn = node as ClassNode;
              if (cn.data.entity.properties.some(p => p.name === propName)) {
                return cn;
              }
              return {
                ...cn,
                data: {
                  ...cn.data,
                  entity: {
                    ...cn.data.entity,
                    properties: [...cn.data.entity.properties, newProp],
                  }
                }
              } as AppNode;
            }
            return node;
          });
        } else if (state.activeTool === 'Association') {
          // Add targetNameId property of type ObjectId (or if the target is an enum, targetName of type targetName)
          const isEnum = targetNode.type === 'enumNode';
          const propName = isEnum ? targetName : `${targetName}Id`;
          const propType = isEnum ? targetName : 'ObjectId';
          
          const newProp: Property = {
            name: propName,
            type: propType,
            isKey: false,
            isEnum: isEnum,
            attributes: isEnum ? [] : ['Index'],
          };
          
          updatedNodes = state.nodes.map(node => {
            if (node.id === sourceNode.id && node.type === 'classNode') {
              const cn = node as ClassNode;
              if (cn.data.entity.properties.some(p => p.name === propName)) {
                return cn;
              }
              return {
                ...cn,
                data: {
                  ...cn.data,
                  entity: {
                    ...cn.data.entity,
                    properties: [...cn.data.entity.properties, newProp],
                  }
                }
              } as AppNode;
            }
            return node;
          });
        }
      }
      
      const newEdge = {
        ...connection,
        type: 'orthogonal',
        data: { relationshipType: state.activeTool },
        style: { strokeWidth: 2, stroke: '#475569' },
      };
      
      return {
        nodes: updatedNodes,
        edges: addEdge(newEdge, state.edges),
      };
    });
  },

  // Domain specific actions
  setActiveTool: (tool) => set({ activeTool: tool }),
  setNamespace: (ns) => set({ namespace: ns }),

  addClassNode: (x, y) =>
    set((state) => ({
      nodes: [
        ...state.nodes,
        {
          id: `class-${Date.now()}`,
          type: 'classNode',
          position: { x, y },
          data: {
            entity: {
              name: 'NewClass',
              softDelete: false,
              auditable: false,
              indexes: [],
              properties: [],
              apiEnabledMethods: ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'],
              apiRoles: {
                'GET': ['Admin', 'User'],
                'GET_BY_ID': ['Admin', 'User'],
                'POST': ['Admin'],
                'PUT': ['Admin'],
                'DELETE': ['Admin']
              },
              apiCaching: {
                'GET': { enabled: false, ttlSeconds: 60 },
                'GET_BY_ID': { enabled: false, ttlSeconds: 120 }
              }
            },
          },
        } as ClassNode,
      ],
    })),

  addEnumNode: (x, y) =>
    set((state) => ({
      nodes: [
        ...state.nodes,
        {
          id: `enum-${Date.now()}`,
          type: 'enumNode',
          position: { x, y },
          data: {
            enum: {
              name: 'NewEnum',
              values: [],
            },
          },
        } as EnumNode,
      ],
    })),

  deleteNode: (nodeId) =>
    set((state) => ({
      nodes: state.nodes.filter((node) => node.id !== nodeId),
      edges: state.edges.filter(
        (edge) => edge.source !== nodeId && edge.target !== nodeId
      ),
    })),

  deleteEdge: (edgeId) =>
    set((state) => {
      const edge = state.edges.find(e => e.id === edgeId);
      let updatedNodes = [...state.nodes];
      
      if (edge && edge.source && edge.target) {
        const sourceNode = state.nodes.find(n => n.id === edge.source);
        const targetNode = state.nodes.find(n => n.id === edge.target);
        
        if (sourceNode && targetNode && sourceNode.type === 'classNode') {
          const targetName = targetNode.type === 'classNode'
            ? (targetNode as ClassNode).data.entity.name
            : (targetNode as EnumNode).data.enum.name;
            
          const relType = edge.data?.relationshipType;
          
          if (relType === 'Inheritance') {
            updatedNodes = state.nodes.map(node => {
              if (node.id === sourceNode.id && node.type === 'classNode') {
                const cn = node as ClassNode;
                return {
                  ...cn,
                  data: {
                    ...cn.data,
                    entity: {
                      ...cn.data.entity,
                      baseClass: undefined,
                    }
                  }
                } as AppNode;
              }
              return node;
            });
          } else if (relType === 'Composition') {
            const propName = targetName.endsWith('s') ? `${targetName}List` : `${targetName}s`;
            updatedNodes = state.nodes.map(node => {
              if (node.id === sourceNode.id && node.type === 'classNode') {
                const cn = node as ClassNode;
                return {
                  ...cn,
                  data: {
                    ...cn.data,
                    entity: {
                      ...cn.data.entity,
                      properties: cn.data.entity.properties.filter(p => p.name !== propName),
                    }
                  }
                } as AppNode;
              }
              return node;
            });
          } else if (relType === 'Association') {
            const isEnum = targetNode.type === 'enumNode';
            const propName = isEnum ? targetName : `${targetName}Id`;
            updatedNodes = state.nodes.map(node => {
              if (node.id === sourceNode.id && node.type === 'classNode') {
                const cn = node as ClassNode;
                return {
                  ...cn,
                  data: {
                    ...cn.data,
                    entity: {
                      ...cn.data.entity,
                      properties: cn.data.entity.properties.filter(p => p.name !== propName),
                    }
                  }
                } as AppNode;
              }
              return node;
            });
          }
        }
      }
      
      return {
        nodes: updatedNodes,
        edges: state.edges.filter((e) => e.id !== edgeId),
      };
    }),

  updateEdgeRelationship: (edgeId, newType) =>
    set((state) => {
      const edge = state.edges.find(e => e.id === edgeId);
      if (!edge || !edge.source || !edge.target) return {};

      const sourceNode = state.nodes.find(n => n.id === edge.source);
      const targetNode = state.nodes.find(n => n.id === edge.target);
      if (!sourceNode || !targetNode || sourceNode.type !== 'classNode') return {};

      const targetName = targetNode.type === 'classNode'
        ? (targetNode as ClassNode).data.entity.name
        : (targetNode as EnumNode).data.enum.name;

      const oldType = edge.data?.relationshipType;
      let updatedNodes = [...state.nodes];

      // 1. Clean up old properties
      if (oldType === 'Inheritance') {
        updatedNodes = updatedNodes.map(node => {
          if (node.id === sourceNode.id && node.type === 'classNode') {
            const cn = node as ClassNode;
            return {
              ...cn,
              data: {
                ...cn.data,
                entity: { ...cn.data.entity, baseClass: undefined }
              }
            } as AppNode;
          }
          return node;
        });
      } else if (oldType === 'Composition') {
        const propName = targetName.endsWith('s') ? `${targetName}List` : `${targetName}s`;
        updatedNodes = updatedNodes.map(node => {
          if (node.id === sourceNode.id && node.type === 'classNode') {
            const cn = node as ClassNode;
            return {
              ...cn,
              data: {
                ...cn.data,
                entity: { ...cn.data.entity, properties: cn.data.entity.properties.filter(p => p.name !== propName) }
              }
            } as AppNode;
          }
          return node;
        });
      } else if (oldType === 'Association') {
        const isEnum = targetNode.type === 'enumNode';
        const propName = isEnum ? targetName : `${targetName}Id`;
        updatedNodes = updatedNodes.map(node => {
          if (node.id === sourceNode.id && node.type === 'classNode') {
            const cn = node as ClassNode;
            return {
              ...cn,
              data: {
                ...cn.data,
                entity: { ...cn.data.entity, properties: cn.data.entity.properties.filter(p => p.name !== propName) }
              }
            } as AppNode;
          }
          return node;
        });
      }

      // 2. Add new properties
      if (newType === 'Inheritance') {
        updatedNodes = updatedNodes.map(node => {
          if (node.id === sourceNode.id && node.type === 'classNode') {
            const cn = node as ClassNode;
            return {
              ...cn,
              data: {
                ...cn.data,
                entity: { ...cn.data.entity, baseClass: targetName }
              }
            } as AppNode;
          }
          return node;
        });
      } else if (newType === 'Composition') {
        const propName = targetName.endsWith('s') ? `${targetName}List` : `${targetName}s`;
        const newProp: Property = {
          name: propName,
          type: `List<${targetName}>`,
          isKey: false,
          isEnum: false,
          attributes: [],
        };
        updatedNodes = updatedNodes.map(node => {
          if (node.id === sourceNode.id && node.type === 'classNode') {
            const cn = node as ClassNode;
            if (cn.data.entity.properties.some(p => p.name === propName)) return cn;
            return {
              ...cn,
              data: {
                ...cn.data,
                entity: { ...cn.data.entity, properties: [...cn.data.entity.properties, newProp] }
              }
            } as AppNode;
          }
          return node;
        });
      } else if (newType === 'Association') {
        const isEnum = targetNode.type === 'enumNode';
        const propName = isEnum ? targetName : `${targetName}Id`;
        const propType = isEnum ? targetName : 'ObjectId';
        const newProp: Property = {
          name: propName,
          type: propType,
          isKey: false,
          isEnum: isEnum,
          attributes: isEnum ? [] : ['Index'],
        };
        updatedNodes = updatedNodes.map(node => {
          if (node.id === sourceNode.id && node.type === 'classNode') {
            const cn = node as ClassNode;
            if (cn.data.entity.properties.some(p => p.name === propName)) return cn;
            return {
              ...cn,
              data: {
                ...cn.data,
                entity: { ...cn.data.entity, properties: [...cn.data.entity.properties, newProp] }
              }
            } as AppNode;
          }
          return node;
        });
      }

      // 3. Update edge data
      const updatedEdges = state.edges.map(e => {
        if (e.id === edgeId) {
          return {
            ...e,
            data: { relationshipType: newType }
          };
        }
        return e;
      });

      return {
        nodes: updatedNodes,
        edges: updatedEdges
      };
    }),

  updateClassNode: (nodeId, updatedData) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: { ...classNode.data.entity, ...updatedData },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  updateEnumNode: (nodeId, updatedData) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'enumNode') {
          const enumNode = node as EnumNode;
          return {
            ...enumNode,
            data: {
              ...enumNode.data,
              enum: { ...enumNode.data.enum, ...updatedData },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  updateProperty: (nodeId, propIndex, updatedProp) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          const properties = [...classNode.data.entity.properties];
          properties[propIndex] = { ...properties[propIndex], ...updatedProp };
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: {
                ...classNode.data.entity,
                properties,
              },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  addProperty: (nodeId) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          const newProp: Property = {
            name: 'NewProperty',
            type: 'string',
            isKey: false,
            isEnum: false,
            attributes: [],
          };
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: {
                ...classNode.data.entity,
                properties: [...classNode.data.entity.properties, newProp],
              },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  deleteProperty: (nodeId, propIndex) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          const properties = [...classNode.data.entity.properties];
          properties.splice(propIndex, 1);
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: {
                ...classNode.data.entity,
                properties,
              },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  addIndex: (nodeId) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          const newIndex: Index = {
            fields: [],
            unique: false,
          };
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: {
                ...classNode.data.entity,
                indexes: [...classNode.data.entity.indexes, newIndex],
              },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  deleteIndex: (nodeId, indexIdx) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          const indexes = [...classNode.data.entity.indexes];
          indexes.splice(indexIdx, 1);
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: {
                ...classNode.data.entity,
                indexes,
              },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  updateIndex: (nodeId, indexIdx, fields, unique) =>
    set((state) => ({
      nodes: state.nodes.map((node) => {
        if (node.id === nodeId && node.type === 'classNode') {
          const classNode = node as ClassNode;
          const indexes = [...classNode.data.entity.indexes];
          indexes[indexIdx] = { ...indexes[indexIdx], fields, unique };
          return {
            ...classNode,
            data: {
              ...classNode.data,
              entity: {
                ...classNode.data.entity,
                indexes,
              },
            },
          } as AppNode;
        }
        return node;
      }),
    })),

  // Routes, as the compiler derives them
  routes: {},
  routesStatus: 'idle',
  routesError: null,

  refreshRoutes: async () => {
    const schema = get().exportToSchema();

    // Only the entities and their declared methods affect routing, so a keystroke in a property name
    // must not cost a round trip. The signature is what makes reading routes from the backend
    // affordable enough to delete the local mirror that existed to avoid it.
    const signature = JSON.stringify(
      (schema.Entities ?? []).map((e: any) => [e.Name, e.ApiEnabledMethods ?? []]),
    );

    if (signature === lastRouteSignature && get().routesStatus === 'ready') return;

    set({ routesStatus: 'loading', routesError: null });

    try {
      const routes = parseManifestRoutes(await deriveApiManifest(schema));
      lastRouteSignature = signature;
      set({ routes, routesStatus: 'ready', routesError: null });
    } catch (error) {
      // Deliberately keeps the previous routes rather than clearing them: a backend that went away
      // does not make the last known-good routes wrong, and blanking the designer mid-edit is worse
      // than showing what was last derived alongside the error.
      lastRouteSignature = null;
      set({
        routesStatus: 'unavailable',
        routesError: error instanceof Error ? error.message : String(error),
      });
    }
  },

  // Serialization
  exportToSchema: () => {
    const { nodes, namespace } = get();
    const entities: any[] = [];
    const enums: any[] = [];

    nodes.forEach((node) => {
      if (node.type === 'classNode') {
        const classNode = node as ClassNode;
        const entity = classNode.data?.entity;
        if (!entity) return;
        entities.push({
          Name: entity.name || 'UnnamedEntity',
          BaseClass: entity.baseClass || null,
          SoftDelete: !!entity.softDelete,
          Auditable: !!entity.auditable,
          Indexes: (entity.indexes || []).map((index) => ({
            Fields: index?.fields || [],
            Unique: !!index?.unique,
          })),
          Properties: (entity.properties || []).map((prop) => ({
            Name: prop?.name || 'UnnamedProp',
            Type: prop?.type || 'string',
            IsKey: !!prop?.isKey,
            IsEnum: !!prop?.isEnum,
            Attributes: prop?.attributes || [],
          })),
          ApiEnabledMethods: entity.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'],
          ApiRoles: entity.apiRoles || {},
          ApiCaching: entity.apiCaching || {},
          ApiBusinessRules: entity.apiBusinessRules || {},
          RealTime: entity.realTime !== false,
          RealTimeRoles: entity.realTimeRoles || [],
        });
      } else if (node.type === 'enumNode') {
        const enumNode = node as EnumNode;
        const enumData = enumNode.data?.enum;
        if (!enumData) return;
        enums.push({
          Name: enumData.name || 'UnnamedEnum',
          Values: enumData.values || [],
        });
      }
    });

    return {
      Namespace: namespace || 'Domain',
      Entities: entities,
      Enums: enums,
      Dtos: (get().dtos || []).map(d => ({
        Name: d?.name || 'UnnamedDto',
        Properties: (d?.properties || []).map(p => ({
          Name: p?.name || 'UnnamedProp',
          Type: p?.type || 'string',
          SourceEntity: p?.sourceEntity,
          SourceProperty: p?.sourceProperty,
          IsRequired: !!p?.isRequired,
          Attributes: p?.attributes || []
        }))
      })),
      CustomEndpoints: (get().customEndpoints || []).map(e => ({
        Route: e?.route || '',
        Method: e?.method || 'POST',
        RequestType: e?.requestType || 'Command',
        Roles: e?.roles || [],
        OperationType: e?.operationType || 'Custom',
        TargetEntity: e?.targetEntity,
        FilterField: e?.filterField,
        FilterOperator: e?.filterOperator,
        FilterSourceValue: e?.filterSourceValue,
        Assignments: e?.assignments || [],
        BusinessRules: e?.businessRules || []
      })),
      Workflows: (get().workflows || []).map(w => ({
        Id: w?.id || '',
        Name: w?.name || '',
        Entity: w?.entity || '',
        Version: w?.version || '1.0.0',
        EffectiveDate: w?.effectiveDate || '',
        ExpirationDate: w?.expirationDate || '',
        IsActive: w?.isActive !== false,
        States: (w?.states || []).map(s => ({
          Name: s?.name || '',
          IsInitial: !!s?.isInitial,
          IsFinal: !!s?.isFinal,
          AllowedRoles: s?.allowedRoles || [],
          X: s?.x,
          Y: s?.y
        })),
        Transitions: (w?.transitions || []).map(t => ({
          Id: t?.id || '',
          Name: t?.name || '',
          FromState: t?.fromState || '',
          ToState: t?.toState || '',
          Trigger: t?.trigger || '',
          UseCustomCommand: !!t?.useCustomCommand,
          RequiredRoles: t?.requiredRoles || [],
          Conditions: (t?.conditions || []).map(c => ({
            Type: c?.type,
            Property: c?.property,
            Operator: c?.operator,
            Value: c?.value
          })),
          Actions: (t?.actions || []).map(a => ({
            Type: a?.type,
            RequestType: a?.type === 'InternalApi' ? a?.requestType : undefined,
            PayloadTemplate: a?.type === 'InternalApi' ? a?.payloadTemplate : undefined,
            Method: a?.type === 'ExternalApi' ? a?.method : undefined,
            Url: a?.type === 'ExternalApi' ? a?.url : undefined,
            Headers: a?.type === 'ExternalApi' ? a?.headers : undefined,
            BodyTemplate: a?.type === 'ExternalApi' ? a?.bodyTemplate : undefined
          }))
        })),
        ChoiceNodes: (w?.choiceNodes || []).map(c => ({
          Id: c?.id || '',
          Name: c?.name || '',
          DefaultState: c?.defaultState || '',
          X: c?.x,
          Y: c?.y,
          Branches: (c?.branches || []).map(b => ({
            ToState: b?.toState,
            Conditions: (b?.conditions || []).map(cond => ({
              Type: cond?.type,
              Property: cond?.property,
              Operator: cond?.operator,
              Value: cond?.value
            }))
          }))
        }))
      }))
    };
  },

  // exportToApiManifest was removed.
  //
  // It derived api-manifest.json by walking the canvas, making Studio a second producer of a contract
  // the C# compiler already owns via ApiManifestGenerator -- and the two disagreed: this emitted
  // /api/v1/{plural} where the compiler emits /api/{plural}, and it defaulted an entity with no
  // declared methods to full CRUD where the compiler skips it. An application therefore served
  // different URLs depending on which tool wrote its manifest, and nothing reported the conflict
  // because each manifest was individually valid.
  //
  // The manifest is now always derived from the IR by the compiler. Studio exports the IR
  // (exportToSchema) and asks the backend to derive the manifest, so there is one implementation of
  // the contract and one place to test it.

  importFromSchema: (schema) => {
    const namespace = schema.Namespace || schema.namespace || 'Paperclip.OrderingSystem.Domain';
    const entities = schema.Entities || schema.entities || [];
    const enums = schema.Enums || schema.enums || [];
    
    const existingNodes = get().nodes;
    const newNodes: AppNode[] = [];
    const newEdges: Edge[] = [];
    
    // Track existing positions to preserve layout
    const positionMap = new Map<string, { x: number, y: number }>();
    existingNodes.forEach(node => {
      const name = node.type === 'classNode' 
        ? (node as ClassNode).data.entity.name 
        : (node as EnumNode).data.enum.name;
      positionMap.set(name, node.position);
    });
    
    let currentX = 100;
    let currentY = 100;
    
    const getNodePosition = (name: string) => {
      if (positionMap.has(name)) {
        return positionMap.get(name)!;
      }
      const pos = { x: currentX, y: currentY };
      currentX += 280;
      if (currentX > 900) {
        currentX = 100;
        currentY += 300;
      }
      return pos;
    };
    
    // Map entities to ClassNodes
    entities.forEach((entity: any) => {
      const name = entity.Name || entity.name;
      const position = getNodePosition(name);
      
      const properties: Property[] = (entity.Properties || entity.properties || []).map((p: any) => ({
        name: p.Name || p.name,
        type: p.Type || p.type,
        isKey: p.IsKey || p.isKey || false,
        isEnum: p.IsEnum || p.isEnum || false,
        attributes: p.Attributes || p.attributes || [],
      }));
      
      const indexes: Index[] = (entity.Indexes || entity.indexes || []).map((idx: any) => ({
        fields: idx.Fields || idx.fields || [],
        unique: idx.Unique || idx.unique || false,
      }));
      
      const apiEnabledMethods = entity.ApiEnabledMethods || entity.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'];
      const apiRoles = entity.ApiRoles || entity.apiRoles || {
        'GET': ['Admin', 'User'],
        'GET_BY_ID': ['Admin', 'User'],
        'POST': ['Admin'],
        'PUT': ['Admin'],
        'DELETE': ['Admin']
      };
      const apiCaching = entity.ApiCaching || entity.apiCaching || {
        'GET': { enabled: false, ttlSeconds: 60 },
        'GET_BY_ID': { enabled: false, ttlSeconds: 120 }
      };
      const apiBusinessRules = entity.ApiBusinessRules || entity.apiBusinessRules || {};
      const realTime = entity.RealTime !== undefined ? entity.RealTime : (entity.realTime !== undefined ? entity.realTime : true);
      const realTimeRoles = entity.RealTimeRoles || entity.realTimeRoles || [];
 
      newNodes.push({
        id: `class-${name}`,
        type: 'classNode',
        position,
        data: {
          entity: {
            name,
            baseClass: entity.BaseClass || entity.baseClass || undefined,
            softDelete: entity.SoftDelete || entity.softDelete || false,
            auditable: entity.Auditable || entity.auditable || false,
            properties,
            indexes,
            apiEnabledMethods,
            apiRoles,
            apiCaching,
            apiBusinessRules,
            realTime,
            realTimeRoles
          }
        }
      } as ClassNode);
    });
    
    // Map enums to EnumNodes
    enums.forEach((en: any) => {
      const name = en.Name || en.name;
      const position = getNodePosition(name);
      
      newNodes.push({
        id: `enum-${name}`,
        type: 'enumNode',
        position,
        data: {
          enum: {
            name,
            values: en.Values || en.values || [],
          }
        }
      } as EnumNode);
    });
    
    // Recreate edges from relations
    newNodes.forEach((node) => {
      if (node.type === 'classNode') {
        const classNode = node as ClassNode;
        const entity = classNode.data.entity;
        
        // 1. Inheritance edges
        if (entity.baseClass) {
          const parentNode = newNodes.find(n => 
            n.type === 'classNode' && (n as ClassNode).data.entity.name === entity.baseClass
          );
          if (parentNode) {
            newEdges.push({
              id: `edge-${node.id}-to-${parentNode.id}`,
              source: node.id,
              target: parentNode.id,
              type: 'orthogonal',
              data: { relationshipType: 'Inheritance' },
              style: { strokeWidth: 2, stroke: '#475569' },
            });
          }
        }
        
        // 2. Composition / Association edges
        entity.properties.forEach(prop => {
          let targetType = prop.type;
          let isComposition = false;
          
          if (targetType.startsWith('List<') && targetType.endsWith('>')) {
            targetType = targetType.substring(5, targetType.length - 1);
            isComposition = true;
          }
          
          let targetNodeName = targetType;
          if (prop.type === 'ObjectId' && prop.name.endsWith('Id')) {
            targetNodeName = prop.name.substring(0, prop.name.length - 2);
          }
          
          const targetNode = newNodes.find(n => {
            const name = n.type === 'classNode' 
              ? (n as ClassNode).data.entity.name 
              : (n as EnumNode).data.enum.name;
            return name.toLowerCase() === targetNodeName.toLowerCase();
          });
          
          if (targetNode) {
            const relType = isComposition ? 'Composition' : 'Association';
            newEdges.push({
              id: `edge-${node.id}-to-${targetNode.id}-${prop.name}`,
              source: node.id,
              target: targetNode.id,
              type: 'orthogonal',
              data: { relationshipType: relType },
              style: { strokeWidth: 2, stroke: '#475569' },
            });
          }
        });
      }
    });

    const customEndpointsImported = (schema.CustomEndpoints || schema.customEndpoints || []).map((e: any) => ({
      route: e.Route || e.route || '/api/v1/custom',
      method: e.Method || e.method || 'POST',
      requestType: e.RequestType || e.requestType || 'CustomCommand',
      roles: e.Roles || e.roles || ['Admin'],
      operationType: e.OperationType || e.operationType || 'Custom',
      targetEntity: e.TargetEntity || e.targetEntity,
      filterField: e.FilterField || e.filterField,
      filterOperator: e.FilterOperator || e.filterOperator,
      filterSourceValue: e.FilterSourceValue || e.filterSourceValue,
      assignments: e.Assignments || e.assignments || [],
      businessRules: e.BusinessRules || e.businessRules || []
    }));

    const dtosImported = (schema.Dtos || schema.dtos || []).map((d: any) => ({
      name: d.Name || d.name || 'UnnamedDto',
      properties: (d.Properties || d.properties || []).map((p: any) => ({
        name: p.Name || p.name || 'Property',
        type: p.Type || p.type || 'string',
        sourceEntity: p.SourceEntity || p.sourceEntity,
        sourceProperty: p.SourceProperty || p.sourceProperty,
        isRequired: p.IsRequired !== undefined ? p.IsRequired : (p.isRequired !== undefined ? p.isRequired : false),
        attributes: p.Attributes || p.attributes || []
      }))
    }));

    const workflowsImported = (schema.Workflows || schema.workflows || []).map((w: any) => ({
      id: w.Id || w.id || 'workflow_1',
      name: w.Name || w.name || 'Unnamed Workflow',
      entity: w.Entity || w.entity || '',
      version: w.Version || w.version || '1.0.0',
      effectiveDate: w.EffectiveDate || w.effectiveDate || '',
      expirationDate: w.ExpirationDate || w.expirationDate || '',
      isActive: w.IsActive !== undefined ? w.IsActive : (w.isActive !== undefined ? w.isActive : true),
      states: (w.States || w.states || []).map((s: any) => ({
        name: s.Name || s.name || '',
        isInitial: s.IsInitial !== undefined ? s.IsInitial : (s.isInitial !== undefined ? s.isInitial : false),
        isFinal: s.IsFinal !== undefined ? s.IsFinal : (s.isFinal !== undefined ? s.isFinal : false),
        allowedRoles: s.AllowedRoles || s.allowedRoles || [],
        x: s.X || s.x,
        y: s.Y || s.y
      })),
      transitions: (w.Transitions || w.transitions || []).map((t: any) => ({
        id: t.Id || t.id || '',
        name: t.Name || t.name || '',
        fromState: t.FromState || t.fromState || '',
        toState: t.ToState || t.toState || '',
        trigger: t.Trigger || t.trigger || '',
        useCustomCommand: t.UseCustomCommand !== undefined ? t.UseCustomCommand : (t.useCustomCommand !== undefined ? t.useCustomCommand : false),
        requiredRoles: t.RequiredRoles || t.requiredRoles || [],
        conditions: (t.Conditions || t.conditions || []).map((c: any) => ({
          type: c.Type || c.type || 'PropertyComparison',
          property: c.Property || c.property || '',
          operator: c.Operator || c.operator || 'Equal',
          value: c.Value || c.value || ''
        })),
        actions: (t.Actions || t.actions || []).map((a: any) => ({
          type: a.Type || a.type || 'InternalApi',
          requestType: a.RequestType || a.requestType || '',
          payloadTemplate: a.PayloadTemplate || a.payloadTemplate || '',
          method: a.Method || a.method || 'POST',
          url: a.Url || a.url || '',
          headers: a.Headers || a.headers || {},
          bodyTemplate: a.BodyTemplate || a.bodyTemplate || ''
        }))
      })),
      choiceNodes: (w.ChoiceNodes || w.choiceNodes || []).map((c: any) => ({
        id: c.Id || c.id || '',
        name: c.Name || c.name || '',
        defaultState: c.DefaultState || c.defaultState || '',
        x: c.X || c.x,
        y: c.Y || c.y,
        branches: (c.Branches || c.branches || []).map((b: any) => ({
          toState: b.ToState || b.toState || '',
          conditions: (b.Conditions || b.conditions || []).map((cond: any) => ({
            type: cond.Type || cond.type || 'PropertyComparison',
            property: cond.Property || cond.property || '',
            operator: cond.Operator || cond.operator || 'Equal',
            value: cond.Value || cond.value || ''
          }))
        }))
      }))
    }));
    
    set({
      namespace,
      nodes: newNodes,
      edges: newEdges,
      customEndpoints: customEndpointsImported,
      dtos: dtosImported,
      workflows: workflowsImported
    });
  },

  // Visual layout, keyed by node id. Kept separate from the semantic model so that dragging a
  // box on the canvas does not show up as a change to the domain: the IR is what is reviewed,
  // diffed and fed to the compiler, and it should not churn on cosmetic edits. It is also why an
  // AI is never asked to invent x/y coordinates — layout is not part of the contract.
  exportLayout: () => {
    const { nodes, edges } = get();
    return {
      version: 1,
      nodes: nodes.map((n) => ({
        id: n.id,
        type: n.type,
        position: n.position,
      })),
      edges,
    };
  },

  exportProject: () => {
    const { namespace } = get();
    return {
      // Semantic model — the same normative IR the compiler consumes.
      ir: get().exportToSchema(),
      // Presentation only.
      layout: get().exportLayout(),
      // Retained so a project file identifies itself without parsing the IR.
      Namespace: namespace,
    };
  },

  importProject: (project) => {
    if (!project) return;

    // New form: { ir, layout }.
    if (project.ir) {
      const layout = project.layout ?? {};
      const positions = new Map<string, { x: number; y: number }>(
        (layout.nodes ?? []).map((n: { id: string; position: { x: number; y: number } }) => [n.id, n.position])
      );

      get().importFromSchema(project.ir);

      set((state) => ({
        nodes: state.nodes.map((node) => {
          const position = positions.get(node.id);
          return position ? { ...node, position } : node;
        }),
        edges: layout.edges ?? state.edges,
      }));
      return;
    }

    // Legacy form: canvas nodes and edges inline. Still read so existing .foundry.json files
    // continue to open; they are written back in the new form on the next save.
    set({
      namespace: project.Namespace || project.namespace || 'Paperclip.OrderingSystem.Domain',
      nodes: project.Nodes || project.nodes || [],
      edges: project.Edges || project.edges || [],
      customEndpoints: project.CustomEndpoints || project.customEndpoints || [],
      dtos: project.Dtos || project.dtos || [],
      workflows: project.Workflows || project.workflows || []
    });
  },

  setCanvasData: (nodes, edges) => set({ nodes, edges }),
  };
});