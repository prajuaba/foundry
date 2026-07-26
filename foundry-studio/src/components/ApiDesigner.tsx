import React, { useState, useEffect, useMemo } from 'react';
import { useStore } from '../store';
import { compileToCs, crudRouteFor } from '../compiler';
import type { ClassNode, DtoProperty, CustomEndpoint, Assignment } from '../types';
import { Search, Plus, Trash2, Shield, Globe, Clock, Copy, Check, Link, Type, Braces, Database } from 'lucide-react';

// Frontend C# generator for DTOs
// C# previews come from the compiler, not from here.
//
// This file carried its own DTO and handler generators -- a second copy of the pair that StudioWorkspace
// had, and a third implementation of output the C# PocoGenerator owns. They emitted code that would not
// compile against the real libraries, so the "live preview" showed something other than what
// `foundry compile` produces.
//
// The preview is now looked up from the compiler's output. See src/compiler.ts.

export const ApiDesigner: React.FC = () => {
  const {
    nodes,
    namespace,
    customEndpoints,
    dtos,
    connectors,
    addCustomEndpoint,
    updateCustomEndpoint,
    deleteCustomEndpoint,
    updateClassNode,
    addDto,
    updateDto,
    deleteDto,
    addConnector,
    updateConnector,
    deleteConnector
  } = useStore();

  // C# previews are the compiler's output, fetched rather than reimplemented. Debounced, because the
  // designer changes on every keystroke.
  const [compiledFiles, setCompiledFiles] = useState<Record<string, string>>({});
  const [compileMessage, setCompileMessage] = useState<string | null>(null);
  const { exportToSchema } = useStore();

  useEffect(() => {
    let cancelled = false;
    const timer = setTimeout(async () => {
      try {
        const files = await compileToCs(exportToSchema());
        if (cancelled) return;
        setCompiledFiles(files);
        setCompileMessage(null);
      } catch (error) {
        if (cancelled) return;
        setCompiledFiles({});
        setCompileMessage(error instanceof Error ? error.message : String(error));
      }
    }, 300);

    return () => {
      cancelled = true;
      clearTimeout(timer);
    };
  }, [exportToSchema, nodes, dtos, customEndpoints, namespace]);

  const [searchQuery, setSearchQuery] = useState('');
  const [selectedItemId, setSelectedItemId] = useState<string | null>(null);
  const [copied, setCopied] = useState(false);

  // States for adding composite properties
  const [selectedEntityForClone, setSelectedEntityForClone] = useState<string>('');
  const [selectedPropForClone, setSelectedPropForClone] = useState<string>('');

  // States for adding assignments
  const [newAssignProp, setNewAssignProp] = useState<string>('');
  const [newAssignSrcVal, setNewAssignSrcVal] = useState<string>('');

  // Helper to get color code for HTTP methods
  const getMethodColor = (method: string) => {
    switch (method.toUpperCase()) {
      case 'GET': return 'text-emerald-400 bg-emerald-950/40 border-emerald-900/30 dark:bg-emerald-950/40 dark:border-emerald-900/30';
      case 'POST': return 'text-sky-400 bg-sky-950/40 border-sky-900/30 dark:bg-sky-950/40 dark:border-sky-900/30';
      case 'PUT': return 'text-amber-400 bg-amber-950/40 border-amber-900/30 dark:bg-amber-950/40 dark:border-amber-900/30';
      case 'DELETE': return 'text-rose-400 bg-rose-950/40 border-rose-900/30 dark:bg-rose-950/40 dark:border-rose-900/30';
      default: return 'text-slate-400 bg-slate-100 border-slate-800 dark:bg-slate-900 dark:border-slate-800';
    }
  };

  // List of standard entities
  const entitiesList = useMemo(() => {
    return nodes
      .filter(n => n.type === 'classNode')
      .map(n => (n as ClassNode).data.entity);
  }, [nodes]);

  // Combined searchable route and DTO list
  const filteredItems = useMemo(() => {
    const query = searchQuery.toLowerCase();
    
    const standard = entitiesList.map(entity => {
      const routePath = crudRouteFor(entity.name);
      return {
        id: `entity-${entity.name}`,
        type: 'standard' as const,
        name: entity.name,
        route: routePath,
        // Displayed as declared. Defaulting to full CRUD here showed endpoints the compiler
        // does not generate, since it skips an entity with no declared methods.
        methods: entity.apiEnabledMethods || [],
        entity
      };
    });

    const custom = customEndpoints.map((ep, idx) => {
      return {
        id: `custom-${idx}`,
        type: 'custom' as const,
        name: `Custom Endpoint #${idx + 1}`,
        route: ep.route,
        method: ep.method,
        customIndex: idx,
        ep
      };
    });

    const dtoItems = dtos.map((dto, idx) => {
      return {
        id: `dto-${idx}`,
        type: 'dto' as const,
        name: dto.name,
        route: `DTO: ${dto.name}`,
        dtoIndex: idx,
        dto
      };
    });

    const connectorItems = (connectors || []).map((conn, idx) => {
      return {
        id: `connector-${idx}`,
        type: 'connector' as const,
        name: conn.name,
        route: `Connector (${conn.type}): ${conn.baseUrl}`,
        connectorIndex: idx,
        conn
      };
    });

    const all = [...standard, ...custom, ...dtoItems, ...connectorItems];
    if (!query) return all;

    return all.filter(r => 
      r.route.toLowerCase().includes(query) || 
      r.name.toLowerCase().includes(query)
    );
  }, [entitiesList, customEndpoints, dtos, connectors, searchQuery]);

  // Selected item detail
  const selectedItemDetail = useMemo(() => {
    if (!selectedItemId) return null;
    return filteredItems.find(r => r.id === selectedItemId) || null;
  }, [selectedItemId, filteredItems]);

  // Live preview content
  const livePreviewContent = useMemo(() => {
    if (!selectedItemDetail) return '';

    if (selectedItemDetail.type === 'standard') {
      const entity = selectedItemDetail.entity;
      if (!entity) return '';
      const enabledMethods = entity.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'];
      
      const roles: Record<string, string[]> = {};
      const caching: Record<string, any> = {};

      enabledMethods.forEach((method: string) => {
        if (entity.apiRoles && entity.apiRoles[method]) {
          roles[method] = entity.apiRoles[method];
        }
        if (entity.apiCaching && entity.apiCaching[method] && entity.apiCaching[method].enabled) {
          caching[method] = {
            Enabled: true,
            TtlSeconds: entity.apiCaching[method].ttlSeconds
          };
        }
      });

      return JSON.stringify({
        Route: selectedItemDetail.route,
        Entity: entity.name,
        Methods: enabledMethods,
        Roles: Object.keys(roles).length > 0 ? roles : undefined,
        Caching: Object.keys(caching).length > 0 ? caching : undefined
      }, null, 2);
    } else if (selectedItemDetail.type === 'custom') {
      const ep = selectedItemDetail.ep;
      if (!ep) return '';
      
      // If it has auto-generated visual MongoDB mappings, show the compiler's C# for it.
      if (ep.operationType && ep.operationType !== 'Custom') {
        return (
          compiledFiles[`Handlers/${ep.requestType}Handler.cs`] ??
          compileMessage ??
          'Generating…'
        );
      }

      // Fallback: show JSON mapping manifest entry
      return JSON.stringify({
        Route: ep.route,
        Method: ep.method,
        RequestType: ep.requestType,
        Roles: ep.roles.length > 0 ? ep.roles : undefined,
        OperationType: ep.operationType || 'Custom',
        TargetEntity: ep.targetEntity || undefined,
        FilterField: ep.filterField || undefined,
        FilterOperator: ep.filterOperator || undefined,
        FilterSourceValue: ep.filterSourceValue || undefined,
        Assignments: (ep.assignments && ep.assignments.length > 0) ? ep.assignments : undefined
      }, null, 2);
    } else if (selectedItemDetail.type === 'dto') {
      const dto = selectedItemDetail.dto;
      if (!dto) return '';
      return compiledFiles[`${dto.name}.cs`] ?? compileMessage ?? 'Generating…';
    }
    return '';
  }, [selectedItemDetail, namespace, compiledFiles, compileMessage]);

  const handleCopy = () => {
    navigator.clipboard.writeText(livePreviewContent);
    setCopied(true);
    setTimeout(() => setCopied(false), 2000);
  };

  const handleAddCustom = () => {
    addCustomEndpoint();
    const newIdx = customEndpoints.length;
    setSelectedItemId(`custom-${newIdx}`);
  };

  const handleCreateDto = () => {
    addDto();
    const newIdx = dtos.length;
    setSelectedItemId(`dto-${newIdx}`);
  };

  // Clone properties from source entity
  const handleAddCloneProperty = (dtoIndex: number) => {
    if (!selectedEntityForClone || !selectedPropForClone) return;

    const sourceEntityObj = entitiesList.find(e => e.name === selectedEntityForClone);
    if (!sourceEntityObj) return;

    const sourcePropObj = sourceEntityObj.properties.find(p => p.name === selectedPropForClone);
    if (!sourcePropObj) return;

    const currentDto = dtos[dtoIndex];
    
    // Check if property name already exists
    let newPropName = `${sourceEntityObj.name}${sourcePropObj.name}`;
    let counter = 1;
    while (currentDto.properties.some(p => p.name === newPropName)) {
      newPropName = `${sourceEntityObj.name}${sourcePropObj.name}${counter++}`;
    }

    const newProp: DtoProperty = {
      name: newPropName,
      type: sourcePropObj.type,
      sourceEntity: sourceEntityObj.name,
      sourceProperty: sourcePropObj.name,
      isRequired: sourcePropObj.attributes.includes('Required'),
      attributes: [...sourcePropObj.attributes]
    };

    updateDto(dtoIndex, {
      ...currentDto,
      properties: [...currentDto.properties, newProp]
    });

    setSelectedPropForClone('');
  };

  // Add custom manual field
  const handleAddCustomField = (dtoIndex: number) => {
    const currentDto = dtos[dtoIndex];
    let newName = 'NewProperty';
    let counter = 1;
    while (currentDto.properties.some(p => p.name === newName)) {
      newName = `NewProperty${counter++}`;
    }

    const newProp: DtoProperty = {
      name: newName,
      type: 'string',
      isRequired: false,
      attributes: []
    };

    updateDto(dtoIndex, {
      ...currentDto,
      properties: [...currentDto.properties, newProp]
    });
  };

  // Update specific property inside DTO
  const handleUpdateDtoProp = (dtoIndex: number, propIndex: number, updatedFields: Partial<DtoProperty>) => {
    const currentDto = dtos[dtoIndex];
    const updatedProps = [...currentDto.properties];
    updatedProps[propIndex] = { ...updatedProps[propIndex], ...updatedFields };
    updateDto(dtoIndex, { ...currentDto, properties: updatedProps });
  };

  // Delete specific property from DTO
  const handleDeleteDtoProp = (dtoIndex: number, propIndex: number) => {
    const currentDto = dtos[dtoIndex];
    const updatedProps = currentDto.properties.filter((_, idx) => idx !== propIndex);
    updateDto(dtoIndex, { ...currentDto, properties: updatedProps });
  };

  // Add Assignment helper
  const handleAddAssignment = (customIdx: number, ep: CustomEndpoint) => {
    if (!newAssignProp || !newAssignSrcVal) return;
    const currentAssignments = ep.assignments || [];
    const newAssign: Assignment = {
      entityProperty: newAssignProp,
      sourceType: 'RequestProperty',
      sourceValue: newAssignSrcVal
    };

    updateCustomEndpoint(customIdx, {
      assignments: [...currentAssignments, newAssign]
    });

    setNewAssignProp('');
    setNewAssignSrcVal('');
  };

  // Delete Assignment helper
  const handleDeleteAssignment = (customIdx: number, ep: CustomEndpoint, assignIdx: number) => {
    const nextAssignments = (ep.assignments || []).filter((_, idx) => idx !== assignIdx);
    updateCustomEndpoint(customIdx, { assignments: nextAssignments });
  };

  // Available properties for cloning
  const clonePropertiesList = useMemo(() => {
    if (!selectedEntityForClone) return [];
    const entity = entitiesList.find(e => e.name === selectedEntityForClone);
    return entity ? entity.properties : [];
  }, [selectedEntityForClone, entitiesList]);

  // Selected custom endpoint's target entity properties
  const selectedEpTargetEntityProperties = useMemo(() => {
    if (selectedItemDetail?.type !== 'custom') return [];
    const ep = (selectedItemDetail as any).ep as CustomEndpoint;
    if (!ep?.targetEntity) return [];
    const entity = entitiesList.find(e => e.name === ep.targetEntity);
    return entity ? entity.properties : [];
  }, [selectedItemDetail, entitiesList]);

  // Selected custom endpoint's request DTO properties
  const selectedEpRequestProperties = useMemo(() => {
    if (selectedItemDetail?.type !== 'custom') return [];
    const ep = (selectedItemDetail as any).ep as CustomEndpoint;
    if (!ep?.requestType) return [];
    const dto = dtos.find(d => d.name === ep.requestType);
    if (dto) return dto.properties;
    
    const ent = entitiesList.find(e => e.name === ep.requestType);
    return ent ? ent.properties : [];
  }, [selectedItemDetail, dtos, entitiesList]);

  return (
    <div className="flex flex-1 overflow-hidden h-full">
      {/* Column 1: Endpoint & DTO Navigator */}
      <div className="w-1/4 bg-slate-50 border-r border-slate-200 flex flex-col h-full dark:bg-slate-950/40 dark:border-slate-800">
        <div className="p-4 border-b border-slate-200 flex flex-col gap-3 dark:border-slate-800">
          <div className="flex items-center justify-between">
            <span className="text-xs font-semibold text-slate-500 uppercase tracking-wider font-mono">Navigator</span>
            <div className="flex gap-1.5">
              <button
                onClick={handleAddCustom}
                className="flex items-center gap-0.5 text-[9px] text-sky-400 hover:text-sky-350 font-bold bg-sky-500/10 border border-sky-500/20 px-1.5 py-1 rounded transition-colors cursor-pointer dark:bg-sky-500/10 dark:border-sky-500/20 dark:hover:text-sky-350"
              >
                <Plus className="w-2.5 h-2.5" /> ROUTE
              </button>
              <button
                onClick={handleCreateDto}
                className="flex items-center gap-0.5 text-[9px] text-purple-400 hover:text-purple-350 font-bold bg-purple-500/10 border border-purple-500/20 px-1.5 py-1 rounded transition-colors cursor-pointer dark:bg-purple-500/10 dark:border-purple-500/20 dark:hover:text-purple-350"
              >
                <Plus className="w-2.5 h-2.5" /> DTO
              </button>
              <button
                onClick={() => {
                  addConnector();
                  const newIdx = (connectors || []).length;
                  setSelectedItemId(`connector-${newIdx}`);
                }}
                className="flex items-center gap-0.5 text-[9px] text-emerald-400 hover:text-emerald-350 font-bold bg-emerald-500/10 border border-emerald-500/20 px-1.5 py-1 rounded transition-colors cursor-pointer dark:bg-emerald-500/10 dark:border-emerald-500/20 dark:hover:text-emerald-350"
              >
                <Plus className="w-2.5 h-2.5" /> CONN
              </button>
            </div>
          </div>
          <div className="flex items-center gap-2 bg-slate-100 border border-slate-200 rounded px-2.5 py-1.5 dark:bg-slate-950 dark:border-slate-800">
            <Search className="w-3.5 h-3.5 text-slate-500" />
            <input
              type="text"
              value={searchQuery}
              onChange={(e) => setSearchQuery(e.target.value)}
              placeholder="Search routes or DTOs..."
              className="w-full bg-transparent border-0 text-xs text-slate-800 focus:outline-none placeholder:text-slate-600 dark:bg-transparent dark:border-0 dark:text-white dark:placeholder:text-slate-600"
            />
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-3 flex flex-col gap-3">
          {/* Endpoints Sub-list */}
          <div>
            <span className="text-[9px] font-bold text-slate-500 uppercase tracking-wider font-mono block mb-2 px-1 dark:text-slate-500">Endpoints</span>
            <div className="flex flex-col gap-1.5">
              {filteredItems.filter(item => item.type !== 'dto').map((route) => {
                const isSelected = selectedItemId === route.id;
                return (
                  <button
                    key={route.id}
                    onClick={() => setSelectedItemId(route.id)}
                    className={`p-2.5 rounded text-left border transition-all cursor-pointer flex flex-col gap-1.5 ${
                      isSelected 
                        ? 'bg-sky-500/10 border-sky-500/40 dark:bg-sky-500/10 dark:border-sky-500/40' 
                        : 'bg-slate-50 border-slate-200/40 hover:bg-slate-100/20 hover:border-slate-200/80 dark:bg-slate-900/30 dark:border-slate-800/40 dark:hover:bg-slate-100/20 dark:hover:border-slate-800/80'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span className="text-[8px] text-slate-500 font-mono font-bold uppercase dark:text-slate-500">
                        {route.type}
                      </span>
                      {route.type === 'custom' && (
                        <span className={`px-1 py-0.2 rounded text-[7px] font-bold border font-mono ${getMethodColor(route.method || 'GET')}`}>
                          {route.method}
                        </span>
                      )}
                    </div>
                    <div className="text-xs text-slate-800 font-mono font-bold truncate dark:text-white">
                      {route.route}
                    </div>
                  </button>
                );
              })}
            </div>
          </div>

          {/* DTOs Sub-list */}
          <div className="border-t border-slate-200 pt-3 dark:border-slate-900">
            <span className="text-[9px] font-bold text-purple-400 uppercase tracking-wider font-mono block mb-2 px-1 dark:text-purple-400">Custom DTOs (Payloads)</span>
            <div className="flex flex-col gap-1.5">
              {filteredItems.filter(item => item.type === 'dto').map((item) => {
                const isSelected = selectedItemId === item.id;
                return (
                  <button
                    key={item.id}
                    onClick={() => setSelectedItemId(item.id)}
                    className={`p-2.5 rounded text-left border transition-all cursor-pointer flex flex-col gap-1.5 ${
                      isSelected 
                        ? 'bg-purple-500/10 border-purple-500/40 dark:bg-purple-500/10 dark:border-purple-500/40' 
                        : 'bg-slate-50 border-slate-200/40 hover:bg-slate-100/20 hover:border-slate-200/80 dark:bg-slate-900/30 dark:border-slate-800/40 dark:hover:bg-slate-100/20 dark:hover:border-slate-800/80'
                    }`}
                  >
                    <div className="flex items-center justify-between">
                      <span className="text-[8px] text-purple-400 font-mono font-bold uppercase dark:text-purple-400">DTO Record</span>
                      <span className="text-[8px] text-slate-500 font-mono dark:text-slate-500">{item.dto?.properties.length || 0} fields</span>
                    </div>
                    <div className="text-xs text-slate-800 font-mono font-bold truncate dark:text-white">
                      {item.name}
                    </div>
                  </button>
                );
              })}
              {dtos.length === 0 && (
                <div className="text-center py-4 text-[10px] text-slate-600 italic dark:text-slate-600">
                  No custom DTOs defined yet.
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Column 2: Detail Configurator */}
      <div className="flex-1 bg-slate-50 flex flex-col h-full border-r border-slate-200 overflow-y-auto dark:bg-slate-950/20 dark:border-slate-800">
        {selectedItemDetail ? (
          <div className="p-6 flex flex-col gap-6 max-w-xl w-full">
            
            {/* 2A: Standard CRUD Configuration */}
            {selectedItemDetail.type === 'standard' && (
              <>
                <div className="flex flex-col gap-1 border-b border-slate-200 pb-4 dark:border-slate-800">
                  <div className="flex items-center gap-2">
                    <Globe className="w-5 h-5 text-sky-500" />
                    <h2 className="text-sm font-bold text-slate-800 uppercase tracking-wider font-mono">
                      Standard CRUD Endpoint
                    </h2>
                  </div>
                  <span className="text-xs text-slate-600 font-mono mt-1 bg-slate-100 px-3 py-1.5 border border-slate-200 rounded dark:bg-slate-950 dark:border-slate-800 dark:text-slate-400">
                    {selectedItemDetail.route}
                  </span>
                </div>

                <div className="flex flex-col gap-5">
                  {['GET', 'GET_BY_ID', 'POST', 'PUT', 'DELETE'].map((method) => {
                    const entity = selectedItemDetail.entity;
                    if (!entity) return null;
                    const enabledMethods: string[] = entity.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'];
                    const isEnabled = enabledMethods.includes(method);

                    const rolesObj = entity.apiRoles || {};
                    const currentRoles: string[] = rolesObj[method] || (method === 'GET' || method === 'GET_BY_ID' ? ['Admin', 'User'] : ['Admin']);

                    const cacheObj = entity.apiCaching || {};
                    const currentCache = cacheObj[method] || { enabled: false, ttlSeconds: 60 };

                    const classNode = nodes.find(n => n.type === 'classNode' && (n as ClassNode).data.entity.name === entity.name);
                    if (!classNode) return null;

                    return (
                      <div key={method} className="p-4 bg-slate-50 border border-slate-200 rounded flex flex-col gap-3 dark:bg-slate-900/40 dark:border-slate-800">
                        <div className="flex items-center justify-between border-b border-slate-200 pb-2.5 dark:border-slate-800">
                          <div className="flex items-center gap-2">
                            <span className={`px-2 py-0.5 rounded text-[10px] font-bold border font-mono ${getMethodColor(method === 'GET_BY_ID' ? 'GET' : method)}`}>
                              {method === 'GET_BY_ID' ? 'GET' : method}
                            </span>
                            <span className="text-xs text-slate-600 font-mono font-medium dark:text-slate-350">
                              {method === 'GET_BY_ID' ? 'Fetch single resource by ID' : `Perform ${method.toLowerCase()} operations`}
                            </span>
                          </div>

                          <label className="flex items-center gap-1.5 cursor-pointer">
                            <input
                              type="checkbox"
                              checked={isEnabled}
                              onChange={() => {
                                const nextMethods = isEnabled
                                  ? enabledMethods.filter(m => m !== method)
                                  : [...enabledMethods, method];
                                updateClassNode(classNode.id, { apiEnabledMethods: nextMethods });
                              }}
                              className="w-4 h-4 text-sky-500 bg-slate-50 border-slate-200 rounded focus:ring-0 cursor-pointer dark:bg-slate-950 dark:border-slate-800"
                            />
                            <span className="text-xs font-semibold text-slate-500 font-mono select-none dark:text-slate-400">ENABLED</span>
                          </label>
                        </div>

                        {isEnabled && (
                          <div className="grid grid-cols-2 gap-4 mt-1">
                            <div className="flex flex-col gap-1.5">
                              <label className="text-[10px] text-slate-500 font-mono flex items-center gap-1 dark:text-slate-500">
                                <Shield className="w-3 h-3 text-slate-500" /> AUTHORIZED ROLES
                              </label>
                              <input
                                type="text"
                                value={currentRoles.join(', ')}
                                onChange={(e) => {
                                  const nextRoles = {
                                    ...rolesObj,
                                    [method]: e.target.value.split(',').map(s => s.trim()).filter(Boolean)
                                  };
                                  updateClassNode(classNode.id, { apiRoles: nextRoles });
                                }}
                                className="w-full px-3 py-1.5 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-sky-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                placeholder="Admin, User"
                              />
                            </div>

                            {(method === 'GET' || method === 'GET_BY_ID') ? (
                              <div className="flex flex-col gap-1.5">
                                <label className="text-[10px] text-slate-500 font-mono flex items-center gap-1 dark:text-slate-500">
                                  <Clock className="w-3 h-3 text-slate-500" /> RESPONSE CACHING (L2)
                                </label>
                                <div className="flex items-center gap-3 bg-slate-100 border border-slate-200 rounded px-3 py-1 dark:bg-slate-950 dark:border-slate-800">
                                  <label className="flex items-center gap-1.5 cursor-pointer">
                                    <input
                                      type="checkbox"
                                      checked={currentCache.enabled}
                                      onChange={(e) => {
                                        const nextCaching = {
                                          ...cacheObj,
                                          [method]: { ...currentCache, enabled: e.target.checked }
                                        };
                                        updateClassNode(classNode.id, { apiCaching: nextCaching });
                                      }}
                                      className="w-3.5 h-3.5 text-sky-500 bg-slate-50 border-slate-200 rounded focus:ring-0 cursor-pointer dark:bg-slate-950 dark:border-slate-800"
                                    />
                                    <span className="text-[10px] font-semibold text-slate-500 font-mono select-none dark:text-slate-400">CACHE</span>
                                  </label>
                                  
                                  {currentCache.enabled && (
                                    <div className="flex items-center gap-1 flex-1 justify-end">
                                      <span className="text-[9px] text-slate-500 font-mono dark:text-slate-500">TTL:</span>
                                      <input
                                        type="number"
                                        value={currentCache.ttlSeconds}
                                        onChange={(e) => {
                                          const nextCaching = {
                                            ...cacheObj,
                                            [method]: { ...currentCache, ttlSeconds: parseInt(e.target.value, 10) || 60 }
                                          };
                                          updateClassNode(classNode.id, { apiCaching: nextCaching });
                                        }}
                                        className="w-16 px-1.5 py-0.5 bg-slate-100 border border-slate-200 rounded text-[10px] text-slate-800 text-center focus:outline-none dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                        min={1}
                                      />
                                      <span className="text-[9px] text-slate-500 font-mono dark:text-slate-500">s</span>
                                    </div>
                                  )}
                                </div>
                              </div>
                            ) : (
                              <div className="flex items-center justify-center border border-dashed border-slate-200 rounded bg-slate-100/20 text-[10px] text-slate-600 font-mono italic dark:border-slate-800 dark:bg-slate-950/20 dark:text-slate-600">
                                Caching is read-only (GET)
                              </div>
                            )}
                          </div>
                        )}
                      </div>
                    );
                  })}
                </div>
              </>
            )}

            {/* 2B: Custom Route Configuration */}
            {selectedItemDetail.type === 'custom' && selectedItemDetail.ep && (
              <>
                <div className="flex flex-col gap-1 border-b border-slate-200 pb-4 dark:border-slate-800">
                  <div className="flex items-center gap-2">
                    <Globe className="w-5 h-5 text-sky-500" />
                    <h2 className="text-sm font-bold text-slate-800 uppercase tracking-wider font-mono">
                      Custom Route Endpoint
                    </h2>
                  </div>
                  <span className="text-xs text-slate-600 font-mono mt-1 bg-slate-100 px-3 py-1.5 border border-slate-200 rounded dark:bg-slate-950 dark:border-slate-800 dark:text-slate-400">
                    {selectedItemDetail.route}
                  </span>
                </div>

                <div className="flex flex-col gap-5 bg-slate-50 border border-slate-200 p-5 rounded dark:bg-slate-900/30 dark:border-slate-800">
                  <div className="grid grid-cols-2 gap-4">
                    <div className="flex flex-col gap-1.5">
                      <label className="text-[10px] text-slate-500 font-mono block dark:text-slate-500">HTTP METHOD</label>
                      <select
                        value={selectedItemDetail.ep.method}
                        onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { method: e.target.value })}
                        className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-sky-500 font-sans font-semibold cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      >
                        <option value="GET">GET</option>
                        <option value="POST">POST</option>
                        <option value="PUT">PUT</option>
                        <option value="DELETE">DELETE</option>
                        <option value="PATCH">PATCH</option>
                      </select>
                    </div>

                    <div className="flex flex-col gap-1.5">
                      <label className="text-[10px] text-slate-500 font-mono block dark:text-slate-500">REQUEST CONTRACT TYPE</label>
                      <select
                        value={selectedItemDetail.ep.requestType}
                        onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { requestType: e.target.value })}
                        className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-sky-500 font-sans font-semibold cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      >
                        <option value="Void">Void (No Payload)</option>
                        <optgroup label="Domain Entities">
                          {entitiesList.map((e) => (
                            <option key={e.name} value={e.name}>{e.name}</option>
                          ))}
                        </optgroup>
                        <optgroup label="Custom DTOs (Payloads)">
                          {dtos.map((d) => (
                            <option key={d.name} value={d.name}>{d.name}</option>
                          ))}
                        </optgroup>
                      </select>
                    </div>
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label className="text-[10px] text-slate-500 font-mono block dark:text-slate-500">ROUTE PATH</label>
                    <input
                      type="text"
                      value={selectedItemDetail.ep.route}
                      onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { route: e.target.value })}
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-sky-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      placeholder="/api/v1/custom-route"
                    />
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label className="text-[10px] text-slate-500 font-mono flex items-center gap-1 dark:text-slate-500">
                      <Shield className="w-3 h-3 text-slate-500" /> AUTHORIZED SECURITY ROLES
                    </label>
                    <input
                      type="text"
                      value={selectedItemDetail.ep.roles.join(', ')}
                      onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { roles: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })}
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-sky-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      placeholder="Admin, User"
                    />
                  </div>

                  <div className="flex flex-col gap-1.5">
                    <label className="text-[10px] text-slate-500 font-mono block dark:text-slate-500">
                      BUSINESS RULES (COMMA SEPARATED)
                    </label>
                    <input
                      type="text"
                      value={(selectedItemDetail.ep.businessRules || []).join(', ')}
                      onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { businessRules: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })}
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-sky-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      placeholder="e.g. LimitRule, StatusRule"
                    />
                  </div>

                  {/* Visual MongoDB Query Builder UI Block */}
                  <div className="border-t border-slate-200/80 pt-4 flex flex-col gap-4 mt-2 dark:border-slate-800/80">
                    <span className="text-[10px] font-bold text-sky-400 font-mono tracking-wider flex items-center gap-1.5 dark:text-sky-400">
                      <Database className="w-3.5 h-3.5 text-sky-400" /> MONGO DB DATABASE INTEGRATION BUILDER
                    </span>

                    <div className="flex flex-col gap-1.5">
                      <label className="text-[9px] text-slate-500 font-mono dark:text-slate-500">DATABASE HANDLER TYPE</label>
                      <select
                        value={selectedItemDetail.ep.operationType || 'Custom'}
                        onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { operationType: e.target.value as any })}
                        className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none font-semibold cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      >
                        <option value="Custom">Write C# Code (MediatR Custom Handler)</option>
                        <option value="Query">Auto-Generate MongoDB Query (Read projection)</option>
                        <option value="Update">Auto-Generate MongoDB Update (Modify fields)</option>
                        <option value="Insert">Auto-Generate MongoDB Insert (Create document)</option>
                      </select>
                    </div>

                    {selectedItemDetail.ep.operationType && selectedItemDetail.ep.operationType !== 'Custom' && (
                      <div className="flex flex-col gap-4 bg-slate-100/40 p-4 border border-slate-200 rounded dark:bg-slate-950/40 dark:border-slate-800">
                        {/* Target Entity Selector */}
                        <div className="flex flex-col gap-1">
                          <label className="text-[8px] text-slate-500 font-mono dark:text-slate-500 tracking-wider font-bold">TARGET COLLECTION ENTITY</label>
                          <select
                            value={selectedItemDetail.ep.targetEntity || ''}
                            onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { targetEntity: e.target.value })}
                            className="w-full px-2.5 py-1.5 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                          >
                            <option value="">-- Select target Entity --</option>
                            {entitiesList.map(e => (
                              <option key={e.name} value={e.name}>{e.name} Collection</option>
                            ))}
                          </select>
                        </div>

                        {/* Filter criteria (for Query or Update) */}
                        {(selectedItemDetail.ep.operationType === 'Query' || selectedItemDetail.ep.operationType === 'Update') && (
                          <div className="flex flex-col gap-2">
                            <span className="text-[8px] text-slate-500 font-mono tracking-wider font-bold dark:text-slate-500">DOCUMENT FILTER CRITERIA</span>
                            <div className="grid grid-cols-3 gap-2">
                              {/* Filter Field */}
                              <div>
                                <select
                                  value={selectedItemDetail.ep.filterField || ''}
                                  onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { filterField: e.target.value })}
                                  className="w-full px-2 py-1.5 bg-slate-100 border border-slate-200 rounded text-[10px] text-slate-800 focus:outline-none cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                >
                                  <option value="">Entity Field</option>
                                  {selectedEpTargetEntityProperties.map(p => (
                                    <option key={p.name} value={p.name}>{p.name}</option>
                                  ))}
                                </select>
                              </div>
                              {/* Filter Operator */}
                              <div>
                                <select
                                  value={selectedItemDetail.ep.filterOperator || 'Equals'}
                                  onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { filterOperator: e.target.value as any })}
                                  className="w-full px-2 py-1.5 bg-slate-100 border border-slate-200 rounded text-[10px] text-slate-800 focus:outline-none cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                >
                                  <option value="Equals">== (Equals)</option>
                                  <option value="Contains">Contains</option>
                                  <option value="GreaterThan">&gt; (Greater Than)</option>
                                </select>
                              </div>
                              {/* Filter Source Value */}
                              <div>
                                <select
                                  value={selectedItemDetail.ep.filterSourceValue || ''}
                                  onChange={(e) => updateCustomEndpoint(selectedItemDetail.customIndex!, { filterSourceValue: e.target.value })}
                                  className="w-full px-2 py-1.5 bg-slate-100 border border-slate-200 rounded text-[10px] text-slate-800 focus:outline-none cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                >
                                  <option value="">Payload Field</option>
                                  {selectedEpRequestProperties.map(p => (
                                    <option key={p.name} value={p.name}>request.{p.name}</option>
                                  ))}
                                </select>
                              </div>
                            </div>
                          </div>
                        )}

                        {/* Assignments (for Update operations) */}
                        {selectedItemDetail.ep.operationType === 'Update' && (
                          <div className="flex flex-col gap-2 border-t border-slate-200 pt-3 dark:border-slate-800">
                            <span className="text-[8px] text-slate-500 font-mono tracking-wider font-bold dark:text-slate-500">FIELD ASSIGNMENTS</span>
                            
                            {/* List existing assignments */}
                            <div className="flex flex-col gap-1.5">
                              {(selectedItemDetail.ep.assignments || []).map((assign, idx) => (
                                <div key={idx} className="flex items-center justify-between bg-slate-100 px-3 py-1.5 rounded border border-slate-200 text-[10px] dark:bg-slate-950 dark:border-slate-800">
                                  <span className="font-mono text-slate-600 dark:text-slate-350">
                                    entity.<b className="text-purple-400"> {assign.entityProperty}</b> = request.<b className="text-sky-400">{assign.sourceValue}</b>
                                  </span>
                                  <button
                                    onClick={() => handleDeleteAssignment(selectedItemDetail.customIndex!, selectedItemDetail.ep, idx)}
                                    className="text-slate-500 hover:text-red-400 cursor-pointer dark:text-slate-500"
                                  >
                                    <Trash2 className="w-3 h-3" />
                                  </button>
                                </div>
                              ))}
                              {(selectedItemDetail.ep.assignments || []).length === 0 && (
                                <span className="text-[9px] text-slate-600 font-mono italic dark:text-slate-600">No fields mapped for update yet.</span>
                              )}
                            </div>

                            {/* Add assignment builder row */}
                            <div className="grid grid-cols-3 gap-2 mt-1.5 items-end">
                              <div>
                                <label className="text-[7px] text-slate-600 block mb-0.5 dark:text-slate-600">TARGET PROPERTY</label>
                                <select
                                  value={newAssignProp}
                                  onChange={(e) => setNewAssignProp(e.target.value)}
                                  className="w-full px-2 py-1.5 bg-slate-100 border border-slate-200 rounded text-[10px] text-slate-800 focus:outline-none cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                >
                                  <option value="">-- Choose --</option>
                                  {selectedEpTargetEntityProperties.filter(p => !p.isKey).map(p => (
                                    <option key={p.name} value={p.name}>{p.name}</option>
                                  ))}
                                </select>
                              </div>
                              <div>
                                <label className="text-[7px] text-slate-600 block mb-0.5 dark:text-slate-600">SOURCE PAYLOAD FIELD</label>
                                <select
                                  value={newAssignSrcVal}
                                  onChange={(e) => setNewAssignSrcVal(e.target.value)}
                                  className="w-full px-2 py-1.5 bg-slate-100 border border-slate-200 rounded text-[10px] text-slate-800 focus:outline-none cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                                >
                                  <option value="">-- Choose --</option>
                                  {selectedEpRequestProperties.map(p => (
                                    <option key={p.name} value={p.name}>request.{p.name}</option>
                                  ))}
                                </select>
                              </div>
                              <button
                                onClick={() => handleAddAssignment(selectedItemDetail.customIndex!, selectedItemDetail.ep)}
                                disabled={!newAssignProp || !newAssignSrcVal}
                                className="px-2.5 py-1.5 bg-sky-600 hover:bg-sky-500 disabled:bg-slate-100 disabled:text-slate-600 text-white font-bold rounded text-[10px] transition-colors cursor-pointer dark:bg-sky-600 dark:hover:bg-sky-500 dark:disabled:bg-slate-100 dark:disabled:text-slate-600"
                              >
                                MAP FIELD
                              </button>
                            </div>
                          </div>
                        )}
                      </div>
                    )}
                  </div>

                  <div className="border-t border-slate-200 pt-4 flex justify-end">
                    <button
                      onClick={() => deleteCustomEndpoint(selectedItemDetail.customIndex!)}
                      className="flex items-center gap-1.5 px-4 py-2 bg-red-650/10 hover:bg-red-600 border border-red-950/40 text-red-400 hover:text-white rounded text-xs font-semibold transition-colors cursor-pointer dark:bg-red-650/10 dark:hover:bg-red-600 dark:border-red-950/40 dark:text-red-400 dark:hover:text-white"
                    >
                      <Trash2 className="w-3.5 h-3.5" /> DELETE CUSTOM ROUTE
                    </button>
                  </div>
                </div>
              </>
            )}

            {/* 2C: DTO Record Configuration */}
            {selectedItemDetail.type === 'dto' && selectedItemDetail.dto && (
              <>
                <div className="flex flex-col gap-1 border-b border-slate-200 pb-4 dark:border-slate-800">
                  <div className="flex items-center gap-2">
                    <Braces className="w-5 h-5 text-purple-400" />
                    <h2 className="text-sm font-bold text-slate-800 uppercase tracking-wider font-mono dark:text-white">
                      Custom DTO Payload Record
                    </h2>
                  </div>
                  <div className="flex gap-2 items-center mt-2">
                    <span className="text-xs text-slate-500 font-mono dark:text-slate-500">DTO Name:</span>
                    <input
                      type="text"
                      value={selectedItemDetail.dto.name}
                      onChange={(e) => updateDto(selectedItemDetail.dtoIndex!, { name: e.target.value })}
                      className="px-3 py-1 bg-slate-100 border border-slate-200 rounded text-xs font-mono font-bold text-purple-300 focus:outline-none focus:border-purple-500 w-64 dark:bg-slate-950 dark:border-slate-800 dark:text-purple-300"
                    />
                  </div>

                  {/* Enterprise Engines & Protocols for Composite DTO */}
                  <div className="flex flex-col gap-2 mt-3 pt-3 border-t border-slate-200 dark:border-slate-800">
                    <label className="text-[10px] text-slate-500 font-mono uppercase font-bold text-sky-400">Enterprise Protocols (DTO Level)</label>

                    <div className="grid grid-cols-2 gap-2">
                      {/* Kafka Outbox */}
                      <div className="flex flex-col gap-1 p-2 bg-slate-100/50 dark:bg-slate-900/50 border border-slate-200 dark:border-slate-800 rounded">
                        <label className="flex items-center justify-between cursor-pointer text-[10px] font-mono font-bold text-slate-800 dark:text-white">
                          <span>Kafka Event Outbox</span>
                          <input
                            type="checkbox"
                            checked={selectedItemDetail.dto.kafkaOutboxEnabled ?? false}
                            onChange={(e) => updateDto(selectedItemDetail.dtoIndex!, { kafkaOutboxEnabled: e.target.checked })}
                            className="w-3.5 h-3.5 rounded text-amber-500 border-slate-300 dark:border-slate-700 focus:ring-amber-500"
                          />
                        </label>
                        {selectedItemDetail.dto.kafkaOutboxEnabled && (
                          <input
                            type="text"
                            value={selectedItemDetail.dto.kafkaTopic || ''}
                            onChange={(e) => updateDto(selectedItemDetail.dtoIndex!, { kafkaTopic: e.target.value })}
                            placeholder="Topic e.g. composite-orders-topic"
                            className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-[10px] font-mono text-slate-800 dark:text-white focus:outline-none focus:border-amber-500 mt-1"
                          />
                        )}
                      </div>

                      {/* FileIO Storage */}
                      <div className="flex flex-col gap-1 p-2 bg-slate-100/50 dark:bg-slate-900/50 border border-slate-200 dark:border-slate-800 rounded">
                        <label className="flex items-center justify-between cursor-pointer text-[10px] font-mono font-bold text-slate-800 dark:text-white">
                          <span>FileIO Storage Pipelines</span>
                          <input
                            type="checkbox"
                            checked={selectedItemDetail.dto.fileIoEnabled ?? false}
                            onChange={(e) => updateDto(selectedItemDetail.dtoIndex!, { fileIoEnabled: e.target.checked })}
                            className="w-3.5 h-3.5 rounded text-emerald-500 border-slate-300 dark:border-slate-700 focus:ring-emerald-500"
                          />
                        </label>
                        {selectedItemDetail.dto.fileIoEnabled && (
                          <input
                            type="text"
                            value={(selectedItemDetail.dto.fileIoAllowedExtensions || ['.csv', '.xlsx']).join(', ')}
                            onChange={(e) => updateDto(selectedItemDetail.dtoIndex!, { fileIoAllowedExtensions: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })}
                            placeholder=".csv, .xlsx"
                            className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-[10px] font-mono text-slate-800 dark:text-white focus:outline-none focus:border-emerald-500 mt-1"
                          />
                        )}
                      </div>
                    </div>
                  </div>
                </div>

                {/* Clone Composite Property Row */}
                <div className="p-4 bg-slate-50 border border-slate-200 rounded flex flex-col gap-3 dark:bg-slate-900/40 dark:border-slate-800">
                  <span className="text-[10px] text-slate-600 font-bold font-mono tracking-wider dark:text-slate-400">
                    COMPOSITE: ADD PROPERTY FROM DOMAIN ENTITY
                  </span>

                  <div className="grid grid-cols-2 gap-3">
                    <div className="flex flex-col gap-1">
                      <span className="text-[8px] text-slate-500 font-mono dark:text-slate-500">SOURCE CLASS</span>
                      <select
                        value={selectedEntityForClone}
                        onChange={(e) => {
                          setSelectedEntityForClone(e.target.value);
                          setSelectedPropForClone('');
                        }}
                        className="w-full px-2.5 py-1.5 bg-slate-100 border border-slate-200 rounded text-[11px] text-slate-800 focus:outline-none font-semibold cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      >
                        <option value="">-- Choose Entity --</option>
                        {entitiesList.map(ent => (
                          <option key={ent.name} value={ent.name}>{ent.name}</option>
                        ))}
                      </select>
                    </div>

                    <div className="flex flex-col gap-1">
                      <span className="text-[8px] text-slate-500 font-mono dark:text-slate-500">SOURCE PROPERTY</span>
                      <select
                        value={selectedPropForClone}
                        onChange={(e) => setSelectedPropForClone(e.target.value)}
                        disabled={!selectedEntityForClone}
                        className="w-full px-2.5 py-1.5 bg-slate-100 border border-slate-200 rounded text-[11px] text-slate-800 focus:outline-none font-semibold disabled:opacity-40 cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                      >
                        <option value="">-- Choose Field --</option>
                        {clonePropertiesList.map(prop => (
                          <option key={prop.name} value={prop.name}>{prop.name} ({prop.type})</option>
                        ))}
                      </select>
                    </div>
                  </div>

                  <div className="flex justify-end mt-1">
                    <button
                      onClick={() => handleAddCloneProperty(selectedItemDetail.dtoIndex!)}
                      disabled={!selectedEntityForClone || !selectedPropForClone}
                      className="flex items-center gap-1 px-3 py-1.5 bg-purple-600 hover:bg-purple-500 disabled:bg-slate-100 disabled:text-slate-500 text-white rounded text-[11px] font-bold transition-all shadow-md shadow-purple-600/10 cursor-pointer dark:bg-purple-600 dark:hover:bg-purple-500 dark:disabled:bg-slate-100 dark:disabled:text-slate-500"
                    >
                      <Link className="w-3 h-3" /> LINK PROPERTY
                    </button>
                  </div>
                </div>

                {/* Property List */}
                <div className="flex flex-col gap-3">
                  <div className="flex items-center justify-between border-b border-slate-200 pb-1.5 dark:border-slate-800">
                    <span className="text-xs font-semibold text-slate-600 dark:text-slate-400">Properties ({selectedItemDetail.dto.properties.length})</span>
                    <button
                      onClick={() => handleAddCustomField(selectedItemDetail.dtoIndex!)}
                      className="flex items-center gap-1 text-[10px] text-purple-400 hover:text-purple-300 font-bold cursor-pointer dark:text-purple-400 dark:hover:text-purple-300"
                    >
                      <Plus className="w-3.5 h-3.5" /> ADD STANDALONE FIELD
                    </button>
                  </div>

                  <div className="flex flex-col gap-2.5">
                    {selectedItemDetail.dto.properties.map((prop, propIdx) => (
                      <div key={propIdx} className="p-3 bg-slate-100/40 border border-slate-200 rounded flex flex-col gap-2 relative group/prop dark:bg-slate-950/40 dark:border-slate-800">
                        <button
                          onClick={() => handleDeleteDtoProp(selectedItemDetail.dtoIndex!, propIdx)}
                          className="absolute top-2 right-2 p-1 text-slate-500 hover:text-red-400 opacity-0 group-hover/prop:opacity-100 transition-opacity cursor-pointer dark:text-slate-500"
                          title="Remove property"
                        >
                          <Trash2 className="w-3.5 h-3.5" />
                        </button>

                        <div className="grid grid-cols-2 gap-3">
                          <div className="flex flex-col gap-1">
                            <span className="text-[8px] text-slate-500 font-mono dark:text-slate-500">FIELD NAME</span>
                            <input
                              type="text"
                              value={prop.name}
                              onChange={(e) => handleUpdateDtoProp(selectedItemDetail.dtoIndex!, propIdx, { name: e.target.value })}
                              className="px-2.5 py-1 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 font-mono focus:outline-none focus:border-purple-500 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                          </div>
                          
                          <div className="flex flex-col gap-1">
                            <span className="text-[8px] text-slate-500 font-mono dark:text-slate-500">DATA TYPE</span>
                            <input
                              type="text"
                              value={prop.type}
                              onChange={(e) => handleUpdateDtoProp(selectedItemDetail.dtoIndex!, propIdx, { type: e.target.value })}
                              disabled={!!prop.sourceEntity}
                              className="px-2.5 py-1 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 font-mono focus:outline-none focus:border-purple-500 disabled:opacity-50 dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                            />
                          </div>
                        </div>

                        <div className="flex items-center justify-between mt-1 text-[10px] dark:text-slate-400">
                          {prop.sourceEntity ? (
                            <span className="text-slate-600 flex items-center gap-1 font-mono italic dark:text-slate-500">
                              <Link className="w-3 h-3 text-purple-400" /> Cloned from {prop.sourceEntity}.{prop.sourceProperty}
                            </span>
                          ) : (
                            <span className="text-slate-600 flex items-center gap-1 font-mono italic dark:text-slate-500">
                              <Type className="w-3 h-3 text-slate-500" /> Standalone DTO property
                            </span>
                          )}

                          <label className="flex items-center gap-1 cursor-pointer select-none dark:text-slate-400">
                            <input
                              type="checkbox"
                              checked={prop.isRequired}
                              onChange={(e) => handleUpdateDtoProp(selectedItemDetail.dtoIndex!, propIdx, { isRequired: e.target.checked })}
                              className="w-3 h-3 text-purple-500 bg-slate-50 border-slate-200 rounded focus:ring-0 cursor-pointer dark:bg-slate-950 dark:border-slate-800"
                            />
                            <span className="text-[10px] text-slate-500 font-mono dark:text-slate-400">REQUIRED</span>
                          </label>
                        </div>
                      </div>
                    ))}
                  </div>
                </div>

                {/* Delete DTO */}
                <div className="border-t border-slate-200 pt-4 flex justify-end">
                  <button
                    onClick={() => {
                      deleteDto(selectedItemDetail.dtoIndex!);
                      setSelectedItemId(null);
                    }}
                    className="flex items-center gap-1.5 px-4 py-2 bg-red-650/10 hover:bg-red-600 border border-red-950/40 text-red-400 hover:text-white rounded text-xs font-semibold transition-colors cursor-pointer dark:bg-red-650/10 dark:hover:bg-red-600 dark:border-red-950/40 dark:text-red-400 dark:hover:text-white"
                  >
                    <Trash2 className="w-3.5 h-3.5" /> DELETE PAYLOAD DTO
                  </button>
                </div>
              </>
            )}

            {/* 2D: External Connector Configuration */}
            {selectedItemDetail.type === 'connector' && (
              <div className="flex flex-col gap-5">
                <div className="flex justify-between items-center bg-slate-100 p-4 border border-slate-200 rounded dark:bg-slate-950/40 dark:border-slate-800">
                  <div>
                    <h4 className="font-bold text-sm text-slate-800 flex items-center gap-2 dark:text-emerald-400 font-mono">
                      <Globe className="w-4 h-4 text-emerald-400" />
                      External Connector: {selectedItemDetail.conn?.name}
                    </h4>
                    <p className="text-[10px] text-slate-500 font-mono mt-0.5 dark:text-slate-500">
                      Configure external SOAP, REST, or GraphQL service connections
                    </p>
                  </div>
                  <button
                    onClick={() => {
                      deleteConnector(selectedItemDetail.connectorIndex!);
                      setSelectedItemId(null);
                    }}
                    className="flex items-center gap-1 px-3 py-1.5 bg-rose-500/10 hover:bg-rose-500/20 text-rose-400 border border-rose-500/30 rounded text-xs font-semibold transition-colors cursor-pointer"
                  >
                    <Trash2 className="w-3.5 h-3.5" /> Delete Connector
                  </button>
                </div>

                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="text-[10px] text-slate-500 font-mono block mb-1">CONNECTOR NAME</label>
                    <input
                      type="text"
                      value={selectedItemDetail.conn?.name || ''}
                      onChange={(e) => updateConnector(selectedItemDetail.connectorIndex!, { name: e.target.value })}
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-emerald-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                    />
                  </div>

                  <div>
                    <label className="text-[10px] text-slate-500 font-mono block mb-1">PROTOCOL TYPE</label>
                    <select
                      value={selectedItemDetail.conn?.type || 'REST'}
                      onChange={(e) => updateConnector(selectedItemDetail.connectorIndex!, { type: e.target.value as any })}
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-emerald-500 font-mono cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white font-bold"
                    >
                      <option value="REST">REST API</option>
                      <option value="SOAP">SOAP Web Service</option>
                      <option value="GraphQL">GraphQL Endpoint</option>
                    </select>
                  </div>
                </div>

                <div>
                  <label className="text-[10px] text-slate-500 font-mono block mb-1">BASE URL / WSDL ENDPOINT</label>
                  <input
                    type="text"
                    value={selectedItemDetail.conn?.baseUrl || ''}
                    onChange={(e) => updateConnector(selectedItemDetail.connectorIndex!, { baseUrl: e.target.value })}
                    placeholder="https://api.external-service.com"
                    className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-emerald-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                  />
                </div>

                <div className="grid grid-cols-2 gap-4">
                  <div>
                    <label className="text-[10px] text-slate-500 font-mono block mb-1">SECURITY / AUTH TYPE</label>
                    <select
                      value={selectedItemDetail.conn?.authType || 'None'}
                      onChange={(e) => updateConnector(selectedItemDetail.connectorIndex!, { authType: e.target.value as any })}
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-emerald-500 font-mono cursor-pointer dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                    >
                      <option value="None">None (Public)</option>
                      <option value="Basic">Basic Auth (Username / Password)</option>
                      <option value="ApiKey">API Key Header</option>
                      <option value="Bearer">Bearer Token</option>
                      <option value="OAuth2">OAuth2 Client Credentials</option>
                    </select>
                  </div>

                  <div>
                    <label className="text-[10px] text-slate-500 font-mono block mb-1">API KEY / TOKEN VALUE</label>
                    <input
                      type="text"
                      value={selectedItemDetail.conn?.apiKey || selectedItemDetail.conn?.token || ''}
                      onChange={(e) => updateConnector(selectedItemDetail.connectorIndex!, { apiKey: e.target.value, token: e.target.value })}
                      placeholder="Secret token or key..."
                      className="w-full px-3 py-2 bg-slate-100 border border-slate-200 rounded text-xs text-slate-800 focus:outline-none focus:border-emerald-500 font-mono dark:bg-slate-950 dark:border-slate-800 dark:text-white"
                    />
                  </div>
                </div>
              </div>
            )}
          </div>
        ) : (
          <div className="flex-1 flex flex-col items-center justify-center p-8 text-center bg-slate-100/10 dark:bg-slate-950/10">
            <Globe className="w-12 h-12 text-slate-700 mb-4 animate-pulse dark:text-slate-700" />
            <h3 className="text-sm font-semibold text-slate-400 font-mono dark:text-slate-400">Select an Item</h3>
            <p className="text-xs text-slate-600 max-w-sm mt-1.5 dark:text-slate-600">
              Choose a standard CRUD endpoint, custom route endpoint, or composite DTO payload from the navigator on the left to configure it.
            </p>
          </div>
        )}
      </div>

      {/* Column 3: Live Preview */}
      <div className="w-1/4 bg-slate-50 flex flex-col h-full overflow-hidden dark:bg-slate-950/40">
        <div className="p-4 border-b border-slate-200 flex items-center justify-between dark:border-slate-800">
          <span className="text-xs font-semibold text-slate-500 uppercase tracking-wider font-mono dark:text-slate-400">
            {selectedItemDetail?.type === 'dto' ? 'C# Code Preview' : (selectedItemDetail?.type === 'custom' && (selectedItemDetail as any).ep?.operationType && (selectedItemDetail as any).ep?.operationType !== 'Custom') ? 'C# Handler Preview' : 'Manifest Entry'}
          </span>
          {selectedItemDetail && (
            <button
              onClick={handleCopy}
              className="flex items-center gap-1 text-[10px] text-slate-500 hover:text-slate-800 font-bold bg-slate-200 hover:bg-slate-200 px-2 py-1 rounded transition-colors cursor-pointer border border-slate-300 dark:bg-slate-800 dark:hover:bg-slate-100 dark:border-slate-750 dark:text-slate-400 dark:hover:text-white"
            >
              {copied ? <Check className="w-3 h-3 text-emerald-400" /> : <Copy className="w-3 h-3" />}
              {copied ? 'COPIED' : 'COPY'}
            </button>
          )}
        </div>

        <div className="flex-1 p-4 bg-slate-100 font-mono text-[11px] text-slate-600 overflow-auto dark:bg-slate-950 dark:text-slate-400">
          {selectedItemDetail ? (
            <pre className="whitespace-pre-wrap">{livePreviewContent}</pre>
          ) : (
            <div className="text-center text-slate-700 py-12 italic dark:text-slate-700">
              No item selected.
            </div>
          )}
        </div>
      </div>
    </div>
  );
};