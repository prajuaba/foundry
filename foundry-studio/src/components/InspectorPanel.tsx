import React from 'react';
import { createPortal } from 'react-dom';
import { useStore } from '../store';
import type { Property, Index, ClassNode, EnumNode } from '../types';
import { Plus, Trash2, Edit, Globe, Clock, Zap } from 'lucide-react';

export const InspectorPanel: React.FC = () => {
  const {
    nodes,
    edges,
    namespace,
    setNamespace,
    deleteNode,
    deleteEdge,
    updateClassNode,
    updateEnumNode,
    updateProperty,
    addProperty,
    deleteProperty,
    addIndex,
    deleteIndex,
    updateIndex,
    updateEdgeRelationship,
    customEndpoints,
    addCustomEndpoint,
    updateCustomEndpoint,
    deleteCustomEndpoint,
    ollamaHost,
    ollamaModel,
    setOllamaHost,
    setOllamaModel,
    ollamaModels,
    fetchOllamaModels
  } = useStore();

  const [isValidating, setIsValidating] = React.useState(false);
  const [validationStatus, setValidationStatus] = React.useState<'idle' | 'success' | 'error'>('idle');
  const [editingPropIdx, setEditingPropIdx] = React.useState<number | null>(null);

  const handleValidateEndpoint = async () => {
    setIsValidating(true);
    setValidationStatus('idle');
    try {
      await fetchOllamaModels();
      setValidationStatus('success');
      setTimeout(() => setValidationStatus('idle'), 3000);
    } catch (err) {
      setValidationStatus('error');
      setTimeout(() => setValidationStatus('idle'), 5000);
    } finally {
      setIsValidating(false);
    }
  };

  const [confirmConfig, setConfirmConfig] = React.useState<{
    isOpen: boolean;
    title: string;
    message: string;
    onConfirm: () => void;
  }>({
    isOpen: false,
    title: '',
    message: '',
    onConfirm: () => {},
  });

  const triggerConfirm = (title: string, message: string, onConfirm: () => void) => {
    setConfirmConfig({
      isOpen: true,
      title,
      message,
      onConfirm: () => {
        onConfirm();
        setConfirmConfig(prev => ({ ...prev, isOpen: false }));
      }
    });
  };

  const isCustomMode = React.useMemo(() => {
    const defaults = ['qwen3-coder:30b', 'qwen2.5-coder:14b', 'qwen2.5-coder:7b', 'llama3.1:latest'];
    const base = ollamaModels.length > 0 ? ollamaModels : defaults;
    return !base.includes(ollamaModel);
  }, [ollamaModels, ollamaModel]);

  const baseList = React.useMemo(() => {
    const defaults = ['qwen3-coder:30b', 'qwen2.5-coder:14b', 'qwen2.5-coder:7b', 'llama3.1:latest'];
    return ollamaModels.length > 0 ? ollamaModels : defaults;
  }, [ollamaModels]);

  const selectedNode = React.useMemo(() => {
    return nodes.find(n => n.selected) || null;
  }, [nodes]);

  const selectedEdge = React.useMemo(() => {
    return edges.find(e => e.selected) || null;
  }, [edges]);

  const classNodeSelected = React.useMemo(() => {
    return (selectedNode && selectedNode.type === 'classNode') ? (selectedNode as ClassNode) : null;
  }, [selectedNode]);

  const enumNodeSelected = React.useMemo(() => {
    return (selectedNode && selectedNode.type === 'enumNode') ? (selectedNode as EnumNode) : null;
  }, [selectedNode]);

  const edgeSourceNode = React.useMemo(() => {
    if (!selectedEdge) return null;
    return nodes.find(n => n.id === selectedEdge.source) || null;
  }, [selectedEdge, nodes]);

  const edgeTargetNode = React.useMemo(() => {
    if (!selectedEdge) return null;
    return nodes.find(n => n.id === selectedEdge.target) || null;
  }, [selectedEdge, nodes]);

  return (
    <div className="w-80 glass-panel border-l p-5 overflow-y-auto z-10 flex flex-col gap-6 shadow-sm">
      <div className="flex items-center justify-between border-b border-slate-200 dark:border-slate-800 pb-3">
        <h2 className="text-xs font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Property Inspector</h2>
        {selectedNode && (
          <button 
            onClick={() => {
              const nodeName = classNodeSelected?.data.entity.name || enumNodeSelected?.data.enum.name || 'this element';
              triggerConfirm(
                'Delete Element',
                `Are you sure you want to delete '${nodeName}' and all its associated connections?`,
                () => deleteNode(selectedNode.id)
              );
            }}
            className="p-1 hover:bg-red-500/20 text-slate-500 dark:text-slate-400 hover:text-red-400 rounded transition-colors cursor-pointer"
            title="Delete node"
          >
            <Trash2 className="w-4 h-4" />
          </button>
        )}
        {selectedEdge && (
          <button 
            onClick={() => {
              triggerConfirm(
                'Delete Relationship',
                'Are you sure you want to delete this relationship link and its mapped POCO attributes?',
                () => deleteEdge(selectedEdge.id)
              );
            }}
            className="p-1 hover:bg-red-500/20 text-slate-500 dark:text-slate-400 hover:text-red-400 rounded transition-colors cursor-pointer"
            title="Delete relationship"
          >
            <Trash2 className="w-4 h-4" />
          </button>
        )}
      </div>

      {!selectedNode && !selectedEdge ? (
        <div className="flex flex-col gap-6">
          {/* Namespace Config */}
          <div className="flex flex-col gap-2">
            <label className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">C# NAMESPACE</label>
            <input
              type="text"
              value={namespace}
              onChange={(e) => setNamespace(e.target.value)}
              className="w-full px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-sm text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-semibold font-sans"
            />
          </div>

          {/* AI Helper Settings */}
          <div className="flex flex-col gap-3 border-t border-slate-200 dark:border-slate-800 pt-4">
            <h3 className="text-xs font-semibold text-slate-500 dark:text-slate-400">AI Helper Settings</h3>
            
            <div className="flex flex-col gap-1.5">
              <label className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">OLLAMA HOST URL</label>
              <div className="flex gap-2">
                <input
                  type="text"
                  value={ollamaHost}
                  onChange={(e) => setOllamaHost(e.target.value)}
                  className="flex-1 px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-mono"
                  placeholder="http://localhost:11434"
                />
                <button
                  onClick={handleValidateEndpoint}
                  disabled={isValidating}
                  className="px-2.5 py-1.5 bg-slate-200 dark:bg-slate-800 hover:bg-slate-300 dark:hover:bg-slate-700 disabled:bg-slate-100 dark:disabled:bg-slate-900 disabled:text-slate-600 dark:disabled:text-slate-600 border border-slate-300 dark:border-slate-700 text-xs font-semibold text-slate-800 dark:text-white rounded transition-all cursor-pointer flex items-center justify-center shrink-0"
                  title="Validate endpoint & fetch models"
                >
                  {isValidating ? '...' : 'Fetch'}
                </button>
              </div>
              {validationStatus === 'success' && (
                <span className="text-[10px] text-emerald-400 font-medium mt-0.5">✓ Connected! Models loaded.</span>
              )}
              {validationStatus === 'error' && (
                <span className="text-[10px] text-rose-400 font-medium mt-0.5">✗ Connection failed.</span>
              )}
            </div>

            <div className="flex flex-col gap-1.5">
              <label className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">OLLAMA MODEL NAME</label>
              
              <select
                value={isCustomMode ? 'custom' : ollamaModel}
                onChange={(e) => {
                  const val = e.target.value;
                  if (val === 'custom') {
                    setOllamaModel('custom-model');
                  } else {
                    setOllamaModel(val);
                  }
                }}
                className="w-full px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-sans font-semibold cursor-pointer"
              >
                {baseList.map((model) => (
                  <option key={model} value={model}>{model}</option>
                ))}
                <option value="custom">Custom Model Name...</option>
              </select>

              {isCustomMode && (
                <input
                  type="text"
                  value={ollamaModel === 'custom-model' ? '' : ollamaModel}
                  onChange={(e) => setOllamaModel(e.target.value)}
                  className="w-full px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-mono mt-2"
                  placeholder="Enter custom model name..."
                />
              )}
            </div>
          </div>

          {/* Custom Endpoints Config */}
          <div className="flex flex-col gap-3 border-t border-slate-200 dark:border-slate-800 pt-4">
            <div className="flex items-center justify-between">
              <h3 className="text-xs font-semibold text-slate-500 dark:text-slate-400">Custom Endpoints</h3>
              <button 
                onClick={addCustomEndpoint}
                className="flex items-center gap-1 text-[10px] text-sky-400 hover:text-sky-300 font-bold cursor-pointer"
              >
                <Plus className="w-3.5 h-3.5" /> ADD
              </button>
            </div>

            <div className="flex flex-col gap-3">
              {customEndpoints.length === 0 ? (
                <div className="text-center text-slate-600 dark:text-slate-600 text-xs py-6 italic border border-dashed border-slate-200 dark:border-slate-800 rounded">
                  No custom endpoints configured. Click ADD to add.
                </div>
              ) : (
                customEndpoints.map((ep, idx) => (
                  <div key={idx} className="flex flex-col gap-2 p-2.5 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded relative group">
                    <button
                      onClick={() => {
                        triggerConfirm(
                          'Delete Custom Endpoint',
                          `Are you sure you want to delete the endpoint '${ep.route || 'this endpoint'}'?`,
                          () => deleteCustomEndpoint(idx)
                        );
                      }}
                      className="absolute top-2 right-2 p-1 text-slate-600 dark:text-slate-600 hover:text-red-400 opacity-0 group-hover:opacity-100 transition-opacity cursor-pointer"
                      title="Delete endpoint"
                    >
                      <Trash2 className="w-3.5 h-3.5" />
                    </button>

                    <div>
                      <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono block">ROUTE PATH</label>
                      <input
                        type="text"
                        value={ep.route}
                        onChange={(e) => updateCustomEndpoint(idx, { route: e.target.value })}
                        className="w-full px-2 py-1 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none"
                        placeholder="/api/v1/checkout"
                      />
                    </div>

                    <div className="flex gap-2">
                      <div className="flex-1">
                        <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono block">METHOD</label>
                        <select
                          value={ep.method}
                          onChange={(e) => updateCustomEndpoint(idx, { method: e.target.value })}
                          className="w-full px-2 py-1 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none bg-slate-900"
                        >
                          <option value="GET">GET</option>
                          <option value="POST">POST</option>
                          <option value="PUT">PUT</option>
                          <option value="DELETE">DELETE</option>
                        </select>
                      </div>

                      <div className="flex-[2]">
                        <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono block">REQUEST TYPE (COMMAND)</label>
                        <input
                          type="text"
                          value={ep.requestType}
                          onChange={(e) => updateCustomEndpoint(idx, { requestType: e.target.value })}
                          className="w-full px-2 py-1 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none"
                          placeholder="PlaceOrderCommand"
                        />
                      </div>
                    </div>

                    <div className="flex gap-2">
                      <div className="flex-1">
                        <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono block">ROLES (COMMA SEPARATED)</label>
                        <input
                          type="text"
                          value={ep.roles.join(', ')}
                          onChange={(e) => updateCustomEndpoint(idx, { roles: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })}
                          className="w-full px-2 py-1 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none"
                          placeholder="User, Admin"
                        />
                      </div>
                      <div className="flex-1">
                        <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono block">BUSINESS RULES (COMMA SEPARATED)</label>
                        <input
                          type="text"
                          value={(ep.businessRules || []).join(', ')}
                          onChange={(e) => updateCustomEndpoint(idx, { businessRules: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })}
                          className="w-full px-2 py-1 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none"
                          placeholder="e.g. LimitRule"
                        />
                      </div>
                    </div>
                  </div>
                ))
              )}
            </div>
          </div>
        </div>
      ) : (classNodeSelected && classNodeSelected.data?.entity) ? (
        <div className="flex flex-col gap-5">
          {/* Entity Name */}
          <div className="flex flex-col gap-1.5">
            <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono">CLASS NAME</label>
            <input
              type="text"
              value={classNodeSelected?.data?.entity?.name || ''}
              onChange={(e) => updateClassNode(classNodeSelected.id, { name: e.target.value })}
              className="w-full px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-sm text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-bold"
            />
          </div>

          {/* Configuration switches */}
          <div className="flex flex-col gap-2 p-3 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded">
            <div className="flex items-center justify-between">
              <span className="text-xs text-slate-600 dark:text-slate-350">Soft Delete</span>
              <input 
                type="checkbox"
                checked={!!classNodeSelected?.data?.entity?.softDelete}
                onChange={(e) => updateClassNode(classNodeSelected.id, { softDelete: e.target.checked })}
                className="w-4 h-4 rounded text-sky-500 border-slate-200 dark:border-slate-800 focus:ring-sky-500 bg-white dark:bg-slate-900 cursor-pointer"
              />
            </div>
            <div className="flex items-center justify-between">
              <span className="text-xs text-slate-600 dark:text-slate-350">Auditable (History)</span>
              <input 
                type="checkbox"
                checked={!!classNodeSelected?.data?.entity?.auditable}
                onChange={(e) => updateClassNode(classNodeSelected.id, { auditable: e.target.checked })}
                className="w-4 h-4 rounded text-sky-500 border-slate-200 dark:border-slate-800 focus:ring-sky-500 bg-white dark:bg-slate-900 cursor-pointer"
              />
            </div>
            <div className="flex items-center justify-between">
              <span className="text-xs text-slate-600 dark:text-slate-350">Real-Time Sync</span>
              <input 
                type="checkbox"
                checked={classNodeSelected.data?.entity?.realTime !== false}
                onChange={(e) => updateClassNode(classNodeSelected.id, { realTime: e.target.checked })}
                className="w-4 h-4 rounded text-sky-500 border-slate-200 dark:border-slate-800 focus:ring-sky-500 bg-white dark:bg-slate-900 cursor-pointer"
              />
            </div>
          </div>

          {/* Real-Time Security Settings */}
          {(classNodeSelected.data?.entity?.realTime !== false) && (
            <div className="flex flex-col gap-2 p-3 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded">
              <div className="flex items-center gap-2 mb-1">
                <Zap className="w-3.5 h-3.5 text-amber-500" />
                <span className="text-[9px] text-slate-600 dark:text-slate-350 font-semibold tracking-wide">REAL-TIME SECURITY</span>
              </div>
              <div className="flex flex-col gap-1">
                <label className="text-[9px] text-slate-500 font-mono">AUTHORIZED ROLES</label>
                <input
                  type="text"
                  placeholder="e.g. Admin, Manager (leave empty for anonymous)"
                  value={(classNodeSelected.data?.entity?.realTimeRoles || []).join(', ')}
                  onChange={(e) => {
                    const roles = e.target.value.split(',').map(r => r.trim()).filter(Boolean);
                    updateClassNode(classNodeSelected.id, { realTimeRoles: roles });
                  }}
                  className="w-full px-2.5 py-1 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-slate-200 focus:outline-none focus:border-sky-500"
                />
              </div>
            </div>
          )}

          {/* API Configuration */}
          <div className="flex flex-col gap-2 p-3 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded">
            <div className="flex items-center gap-2 mb-1">
              <Globe className="w-3.5 h-3.5 text-sky-400" />
              <span className="text-[9px] text-slate-600 dark:text-slate-350 font-semibold tracking-wide">API ENDPOINT</span>
            </div>

            {/* HTTP Methods */}
            <div className="flex flex-col gap-1">
              <label className="text-[8px] text-slate-500 dark:text-slate-500 font-mono uppercase">HTTP Methods</label>
              <div className="flex flex-wrap gap-1.5">
                {['GET', 'GET_BY_ID', 'POST', 'PUT', 'DELETE'].map((method) => {
                  const methods = classNodeSelected.data?.entity?.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'];
                  const enabled = methods.includes(method);
                  return (
                    <button
                      key={method}
                      onClick={() => {
                        const next = enabled
                          ? methods.filter((m) => m !== method)
                          : [...methods, method];
                        updateClassNode(classNodeSelected.id, { apiEnabledMethods: next });
                      }}
                      className={`px-2 py-0.5 rounded text-[9px] font-mono font-bold transition-all cursor-pointer ${
                        enabled
                          ? 'bg-sky-600 text-white shadow-sm'
                          : 'bg-slate-100 dark:bg-slate-900 text-slate-500 border border-slate-200 dark:border-slate-800 hover:border-slate-600 dark:hover:border-slate-600'
                      }`}
                    >
                      {method}
                    </button>
                  );
                })}
              </div>
            </div>

            {/* Roles per method */}
            <div className="flex flex-col gap-1 mt-1">
              <label className="text-[8px] text-slate-500 dark:text-slate-500 font-mono uppercase">RBAC Roles</label>
              {(classNodeSelected.data?.entity?.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE']).map((method) => {
                const roles = classNodeSelected.data?.entity?.apiRoles?.[method] || ['Admin'];
                return (
                  <div key={method} className="flex items-center gap-1.5">
                    <span className="text-[9px] text-sky-400 font-mono font-bold w-24">{method}</span>
                    <input
                      type="text"
                      value={roles.join(',')}
                      onChange={(e) => {
                        const newRoles = e.target.value.split(',').map((s) => s.trim()).filter(Boolean);
                        const apiRoles = { ...(classNodeSelected.data?.entity?.apiRoles || {}), [method]: newRoles };
                        updateClassNode(classNodeSelected.id, { apiRoles });
                      }}
                      className="flex-1 px-2 py-0.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-[9px] text-slate-800 dark:text-white font-mono focus:outline-none focus:border-sky-500"
                      placeholder="Admin,User"
                    />
                  </div>
                );
              })}
            </div>

            {/* Business Rules per method */}
            <div className="flex flex-col gap-1 mt-2 pt-2 border-t border-slate-200 dark:border-slate-800">
              <label className="text-[8px] text-slate-500 dark:text-slate-500 font-mono uppercase">Business Rules</label>
              {(classNodeSelected.data?.entity?.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE']).map((method) => {
                const rules = classNodeSelected.data?.entity?.apiBusinessRules?.[method] || [];
                return (
                  <div key={method} className="flex items-center gap-1.5">
                    <span className="text-[9px] text-sky-400 font-mono font-bold w-24">{method}</span>
                    <input
                      type="text"
                      value={rules.join(',')}
                      onChange={(e) => {
                        const newRules = e.target.value.split(',').map((s) => s.trim()).filter(Boolean);
                        const apiBusinessRules = { ...(classNodeSelected.data?.entity?.apiBusinessRules || {}), [method]: newRules };
                        updateClassNode(classNodeSelected.id, { apiBusinessRules });
                      }}
                      className="flex-1 px-2 py-0.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-[9px] text-slate-800 dark:text-white font-mono focus:outline-none focus:border-sky-500"
                      placeholder="e.g. CreditLimitRule"
                    />
                  </div>
                );
              })}
            </div>

            {/* Caching config */}
            <div className="flex flex-col gap-1 mt-1 pt-2 border-t border-slate-200 dark:border-slate-800">
              <div className="flex items-center gap-1.5">
                <Clock className="w-3 h-3 text-emerald-400" />
                <span className="text-[8px] text-slate-500 dark:text-slate-500 font-mono uppercase">Caching</span>
              </div>
              {(classNodeSelected.data?.entity?.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'])
                .filter((m) => m === 'GET' || m === 'GET_BY_ID')
                .map((method) => {
                  const cacheConfig = classNodeSelected.data?.entity?.apiCaching?.[method];
                  const enabled = cacheConfig?.enabled ?? (method === 'GET_BY_ID' ? true : false);
                  const ttl = cacheConfig?.ttlSeconds ?? (method === 'GET_BY_ID' ? 120 : 60);
                  return (
                    <div key={method} className="flex items-center gap-1.5">
                      <span className="text-[9px] text-sky-400 font-mono w-24">{method}</span>
                      <label className="flex items-center gap-1 cursor-pointer">
                        <input
                          type="checkbox"
                          checked={enabled}
                          onChange={(e) => {
                            const apiCaching = { ...(classNodeSelected.data?.entity?.apiCaching || {}) };
                            apiCaching[method] = { enabled: e.target.checked, ttlSeconds: ttl };
                            updateClassNode(classNodeSelected.id, { apiCaching });
                          }}
                          className="w-3 h-3 rounded text-emerald-500 border-slate-200 dark:border-slate-800 focus:ring-emerald-500 bg-white dark:bg-slate-900 cursor-pointer"
                        />
                      </label>
                      <input
                        type="number"
                        value={ttl}
                        onChange={(e) => {
                          const ttlVal = parseInt(e.target.value, 10) || 60;
                          const apiCaching = { ...(classNodeSelected.data?.entity?.apiCaching || {}) };
                          apiCaching[method] = { enabled: true, ttlSeconds: ttlVal };
                          updateClassNode(classNodeSelected.id, { apiCaching });
                        }}
                        className="w-16 px-1 py-0.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-[9px] text-slate-800 dark:text-white font-mono focus:outline-none focus:border-emerald-500"
                        placeholder="sec"
                      />
                      <span className="text-[8px] text-slate-600 dark:text-slate-600">s</span>
                    </div>
                  );
                })}
            </div>

            {/* Enterprise Integrations Section */}
            <div className="flex flex-col gap-2 mt-2 pt-2 border-t border-slate-200 dark:border-slate-800">
              <label className="text-[8px] text-slate-500 dark:text-slate-500 font-mono uppercase font-bold text-sky-400">Enterprise Engines & Protocols</label>
              
              {/* Kafka Outbox */}
              <div className="flex flex-col gap-1 p-1.5 bg-slate-100/50 dark:bg-slate-900/50 border border-slate-200 dark:border-slate-800 rounded">
                <label className="flex items-center justify-between cursor-pointer text-[9px] font-mono font-bold text-slate-800 dark:text-white">
                  <span>Kafka Transactional Outbox</span>
                  <input
                    type="checkbox"
                    checked={classNodeSelected.data?.entity?.kafkaOutboxEnabled ?? false}
                    onChange={(e) => updateClassNode(classNodeSelected.id, { kafkaOutboxEnabled: e.target.checked })}
                    className="w-3.5 h-3.5 rounded text-amber-500 border-slate-300 dark:border-slate-700 focus:ring-amber-500"
                  />
                </label>
                {classNodeSelected.data?.entity?.kafkaOutboxEnabled && (
                  <input
                    type="text"
                    value={classNodeSelected.data?.entity?.kafkaTopic || ''}
                    onChange={(e) => updateClassNode(classNodeSelected.id, { kafkaTopic: e.target.value })}
                    placeholder="Topic e.g. orders-events-topic"
                    className="px-2 py-0.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-[9px] font-mono text-slate-800 dark:text-white focus:outline-none focus:border-amber-500"
                  />
                )}
              </div>

              {/* GraphQL Gateway */}
              <div className="flex items-center justify-between p-1.5 bg-slate-100/50 dark:bg-slate-900/50 border border-slate-200 dark:border-slate-800 rounded">
                <span className="text-[9px] font-mono font-bold text-slate-800 dark:text-white">GraphQL Queries & Mutations</span>
                <input
                  type="checkbox"
                  checked={classNodeSelected.data?.entity?.graphQlEnabled ?? true}
                  onChange={(e) => updateClassNode(classNodeSelected.id, { graphQlEnabled: e.target.checked })}
                  className="w-3.5 h-3.5 rounded text-purple-500 border-slate-300 dark:border-slate-700 focus:ring-purple-500"
                />
              </div>

              {/* FileIO Storage */}
              <div className="flex flex-col gap-1 p-1.5 bg-slate-100/50 dark:bg-slate-900/50 border border-slate-200 dark:border-slate-800 rounded">
                <label className="flex items-center justify-between cursor-pointer text-[9px] font-mono font-bold text-slate-800 dark:text-white">
                  <span>FileIO Upload Pipeline</span>
                  <input
                    type="checkbox"
                    checked={classNodeSelected.data?.entity?.fileIoEnabled ?? false}
                    onChange={(e) => updateClassNode(classNodeSelected.id, { fileIoEnabled: e.target.checked })}
                    className="w-3.5 h-3.5 rounded text-emerald-500 border-slate-300 dark:border-slate-700 focus:ring-emerald-500"
                  />
                </label>
                {classNodeSelected.data?.entity?.fileIoEnabled && (
                  <input
                    type="text"
                    value={(classNodeSelected.data?.entity?.fileIoAllowedExtensions || []).join(',')}
                    onChange={(e) => updateClassNode(classNodeSelected.id, { fileIoAllowedExtensions: e.target.value.split(',').map(s => s.trim()).filter(Boolean) })}
                    placeholder="Allowed e.g. .pdf,.png,.jpg"
                    className="px-2 py-0.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-[9px] font-mono text-slate-800 dark:text-white focus:outline-none focus:border-emerald-500"
                  />
                )}
              </div>
            </div>
          </div>

          {/* Properties list */}
          <div className="flex flex-col gap-2.5">
            <div className="flex items-center justify-between border-b border-slate-200 dark:border-slate-800 pb-1.5">
              <h3 className="text-xs font-semibold text-slate-600 dark:text-slate-350">Properties</h3>
              <button
                onClick={() => addProperty(classNodeSelected.id)}
                className="flex items-center gap-1 text-[10px] text-sky-400 hover:text-sky-300 font-bold cursor-pointer"
              >
                <Plus className="w-3.5 h-3.5" /> ADD
              </button>
            </div>

            <div className="flex flex-col gap-2 max-h-[30vh] overflow-y-auto pr-1">
              {(classNodeSelected.data?.entity?.properties || []).map((prop: Property, idx: number) => {
                const isIdField = prop.name.toLowerCase() === 'id' || prop.isKey;
                const isRequired = prop.attributes?.includes('Required');
                const isProtected = prop.attributes?.some(a => ['Encrypt', 'Mask', 'MaskEmail'].includes(a));
                const hasValidation = prop.attributes?.some(a => !['Required', 'Encrypt', 'Mask', 'MaskEmail', 'Index', 'UniqueIndex', 'TextIndex', 'Unique'].includes(a));
                
                return (
                  <div key={idx} className="flex items-center justify-between p-2 bg-slate-100/45 dark:bg-slate-950/45 hover:bg-slate-100 dark:hover:bg-slate-950 border border-slate-200 dark:border-slate-800/80 rounded relative group/prop">
                    <div className="flex flex-col gap-0.5">
                      <div className="flex items-center gap-1.5">
                        <span className="text-xs text-slate-800 dark:text-white font-mono font-semibold">
                          {isIdField ? '🔑 ' : ''}{prop.name || 'unnamed'}
                        </span>
                        <span className="text-[9px] text-slate-500 dark:text-slate-500 font-mono">({prop.type})</span>
                      </div>
                      
                      {/* Badge flags */}
                      <div className="flex gap-1">
                        {isRequired && (
                          <span className="text-[8px] font-bold text-sky-400 bg-sky-950/40 px-1 rounded border border-sky-900/30">REQ</span>
                        )}
                        {prop.isEnum && (
                          <span className="text-[8px] font-bold text-purple-400 bg-purple-950/40 px-1 rounded border border-purple-900/30">ENUM</span>
                        )}
                        {isProtected && (
                          <span className="text-[8px] font-bold text-amber-400 bg-amber-950/40 px-1 rounded border border-amber-900/30">SEC</span>
                        )}
                        {hasValidation && (
                          <span className="text-[8px] font-bold text-emerald-400 bg-emerald-950/40 px-1 rounded border border-emerald-900/30">VAL</span>
                        )}
                      </div>
                    </div>

                    <div className="flex items-center gap-1 opacity-0 group-hover/prop:opacity-100 transition-opacity">
                      <button
                        onClick={() => setEditingPropIdx(idx)}
                        className="p-1 text-slate-500 dark:text-slate-500 hover:text-sky-400 transition-colors cursor-pointer hover:bg-slate-100 dark:hover:bg-slate-900 rounded"
                        title="Edit property details"
                      >
                        <Edit className="w-3.5 h-3.5" />
                      </button>
                      {!isIdField && (
                        <button
                          onClick={() => {
                            triggerConfirm(
                              'Delete Property',
                              `Are you sure you want to delete the property '${prop.name || 'this property'}'?`,
                              () => deleteProperty(classNodeSelected.id, idx)
                            );
                          }}
                          className="p-1 text-slate-500 dark:text-slate-500 hover:text-red-400 transition-colors cursor-pointer hover:bg-slate-100 dark:hover:bg-slate-900 rounded"
                          title="Delete property"
                        >
                          <Trash2 className="w-3.5 h-3.5" />
                        </button>
                      )}
                    </div>
                  </div>
                );
              })}
            </div>
          </div>

          {/* Indexes */}
          <div className="flex flex-col gap-2 border-t border-slate-200 dark:border-slate-800 pt-4">
            <div className="flex items-center justify-between border-b border-slate-200 dark:border-slate-800 pb-1.5">
              <h3 className="text-xs font-semibold text-slate-600 dark:text-slate-350">Indexes</h3>
              <button 
                onClick={() => addIndex(classNodeSelected.id)}
                className="flex items-center gap-1 text-[10px] text-sky-400 hover:text-sky-300 font-bold cursor-pointer"
              >
                <Plus className="w-3.5 h-3.5" /> ADD
              </button>
            </div>

            <div className="flex flex-col gap-2 max-h-[20vh] overflow-y-auto pr-1">
              {(classNodeSelected.data?.entity?.indexes || []).map((idxObj: Index, idxIdx: number) => (
                <div key={idxIdx} className="flex items-center gap-2 p-2 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded relative group/idx">
                  <button
                    onClick={() => {
                      const fieldsStr = idxObj.fields.join(', ') || 'this index';
                      triggerConfirm(
                        'Delete Index',
                        `Are you sure you want to delete the index defined on fields [${fieldsStr}]?`,
                        () => deleteIndex(classNodeSelected.id, idxIdx)
                      );
                    }}
                    className="absolute top-1 right-1 p-0.5 text-slate-600 dark:text-slate-600 hover:text-red-400 opacity-0 group-hover/idx:opacity-100 transition-opacity cursor-pointer"
                    title="Delete index"
                  >
                    <Trash2 className="w-3 h-3" />
                  </button>

                  <div className="flex-1">
                    <label className="text-[8px] text-slate-500 dark:text-slate-500 font-mono block">FIELDS (COMMA SEPARATED)</label>
                    <input
                      type="text"
                      value={idxObj.fields.join(', ')}
                      onChange={(e) => updateIndex(classNodeSelected.id, idxIdx, e.target.value.split(',').map(s => s.trim()).filter(Boolean), idxObj.unique)}
                      className="w-full px-2 py-0.5 bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none font-mono"
                      placeholder="e.g. Email"
                    />
                  </div>

                  <div className="flex flex-col items-center justify-center min-w-[50px] border-l border-slate-200 dark:border-slate-800/80 pl-2">
                    <label className="text-[8px] text-slate-500 dark:text-slate-500 font-mono block mb-1">UNIQUE</label>
                    <input 
                      type="checkbox"
                      checked={idxObj.unique}
                      onChange={(e) => updateIndex(classNodeSelected.id, idxIdx, idxObj.fields, e.target.checked)}
                      className="w-3 h-3 text-sky-500 bg-white dark:bg-slate-900 border-slate-200 dark:border-slate-800 cursor-pointer"
                    />
                  </div>
                </div>
              ))}
            </div>
          </div>
        </div>
      ) : (enumNodeSelected && enumNodeSelected.data?.enum) ? (
        <div className="flex flex-col gap-5">
          {/* Enum Name */}
          <div className="flex flex-col gap-1.5">
            <label className="text-[9px] text-slate-500 dark:text-slate-500 font-mono">ENUM NAME</label>
            <input
              type="text"
              value={enumNodeSelected?.data?.enum?.name || ''}
              onChange={(e) => updateEnumNode(enumNodeSelected.id, { name: e.target.value, values: enumNodeSelected?.data?.enum?.values || [] })}
              className="w-full px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-sm text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-bold"
            />
          </div>

          {/* Enum Values */}
          <div className="flex flex-col gap-2.5">
            <div className="flex items-center justify-between border-b border-slate-200 dark:border-slate-800 pb-1.5">
              <h3 className="text-xs font-semibold text-slate-600 dark:text-slate-350">Enum Values</h3>
              <button 
                onClick={() => {
                  const values = [...(enumNodeSelected?.data?.enum?.values || []), ''];
                  updateEnumNode(enumNodeSelected.id, { name: enumNodeSelected?.data?.enum?.name || '', values });
                }}
                className="flex items-center gap-1 text-[10px] text-sky-400 hover:text-sky-300 font-bold cursor-pointer"
              >
                <Plus className="w-3.5 h-3.5" /> ADD VALUE
              </button>
            </div>

            <div className="flex flex-col gap-2 max-h-[40vh] overflow-y-auto pr-1">
              {(enumNodeSelected?.data?.enum?.values || []).map((val: string, idx: number) => (
                <div key={idx} className="flex items-center gap-2 relative group/enum">
                  <input
                    type="text"
                    value={val}
                    onChange={(e) => {
                      const values = [...(enumNodeSelected?.data?.enum?.values || [])];
                      values[idx] = e.target.value;
                      updateEnumNode(enumNodeSelected.id, { name: enumNodeSelected?.data?.enum?.name || '', values });
                    }}
                    className="w-full px-2.5 py-1 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-mono"
                    placeholder={`Value ${idx + 1}`}
                  />
                  <button
                    onClick={() => {
                      const values = (enumNodeSelected?.data?.enum?.values || []).filter((_, i) => i !== idx);
                      updateEnumNode(enumNodeSelected.id, { name: enumNodeSelected?.data?.enum?.name || '', values });
                    }}
                    className="p-1 hover:bg-red-500/20 text-slate-500 dark:text-slate-500 hover:text-red-400 rounded transition-colors cursor-pointer"
                    title="Remove value"
                  >
                    <Trash2 className="w-3.5 h-3.5" />
                  </button>
                </div>
              ))}
            </div>
          </div>
        </div>
      ) : selectedEdge ? (
        <div className="flex flex-col gap-5">
          <div className="flex flex-col gap-1.5">
            <span className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block font-sans">RELATIONSHIP DETAILS</span>
            <div className="p-3 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded flex flex-col gap-3 text-xs text-slate-600 dark:text-slate-350">
              <div className="flex justify-between">
                <span className="text-slate-500 dark:text-slate-500 font-mono">Source Node:</span>
                <span className="font-semibold text-slate-800 dark:text-white">{edgeSourceNode?.type === 'classNode' ? (edgeSourceNode as ClassNode)?.data?.entity?.name || 'Unknown' : 'Unknown'}</span>
              </div>
              <div className="flex justify-between">
                <span className="text-slate-500 dark:text-slate-500 font-mono">Target Node:</span>
                <span className="font-semibold text-slate-800 dark:text-white">{edgeTargetNode?.type === 'classNode' ? (edgeTargetNode as ClassNode)?.data?.entity?.name || 'Unknown' : edgeTargetNode?.type === 'enumNode' ? (edgeTargetNode as EnumNode)?.data?.enum?.name || 'Unknown' : 'Unknown'}</span>
              </div>
            </div>
          </div>

          <div className="flex flex-col gap-2">
            <label className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">RELATIONSHIP TYPE</label>
            <select
              value={((selectedEdge.data as any)?.relationshipType as string) || 'Association'}
              onChange={(e) => {
                const newType = e.target.value as 'Association' | 'Composition' | 'Inheritance';
                updateEdgeRelationship(selectedEdge.id, newType);
              }}
              className="w-full px-3 py-1.5 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-sm text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-semibold"
            >
              <option value="Association">Association Link</option>
              <option value="Composition">Composition Link</option>
              <option value="Inheritance">Inheritance Link</option>
            </select>
          </div>
        </div>
      ) : null}

      {editingPropIdx !== null && classNodeSelected && (
        (() => {
          const prop = classNodeSelected.data.entity.properties[editingPropIdx];
          if (!prop) return null;
          
          return createPortal(
            <div className="fixed inset-0 bg-slate-950/80 backdrop-blur-sm z-50 flex items-center justify-center p-4">
              <div className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded max-w-md w-full shadow-2xl flex flex-col overflow-hidden animate-in fade-in zoom-in duration-200">
                {/* Header */}
                <div className="flex items-center justify-between px-5 py-4 border-b border-slate-200 dark:border-slate-800 bg-slate-100/50 dark:bg-slate-950/50">
                  <div className="flex items-center gap-2">
                    <span className="text-sky-400 font-mono text-[11px] uppercase tracking-wider font-bold">Configure Property:</span>
                    <span className="text-slate-800 dark:text-white font-bold text-xs font-mono">{prop.name || 'unnamed'}</span>
                  </div>
                  <button 
                    onClick={() => setEditingPropIdx(null)}
                    className="text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white text-xs font-semibold cursor-pointer"
                  >
                    ✕
                  </button>
                </div>
                
                {/* Body Form */}
                <div className="p-5 flex flex-col gap-4">
                  {/* General Config - Row */}
                  <div className="grid grid-cols-2 gap-4">
                    <div className="flex flex-col gap-1">
                      <label className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">PROPERTY NAME</label>
                      <input
                        type="text"
                        value={prop.name}
                        onChange={(e) => updateProperty(classNodeSelected.id, editingPropIdx, { name: e.target.value })}
                        className="w-full px-3 py-2 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-mono font-semibold"
                        placeholder="e.g. TotalAmount"
                      />
                    </div>

                    <div className="flex flex-col gap-1">
                      <label className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">DATA TYPE</label>
                      <select
                        value={prop.type}
                        onChange={(e) => updateProperty(classNodeSelected.id, editingPropIdx, { type: e.target.value })}
                        className="w-full px-3 py-2 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-sans font-semibold cursor-pointer"
                      >
                        <option value="string">string</option>
                        <option value="int">int</option>
                        <option value="long">long</option>
                        <option value="double">double</option>
                        <option value="decimal">decimal</option>
                        <option value="bool">bool</option>
                        <option value="datetime">datetime</option>
                        <option value="objectid">objectid</option>
                        {/* Map other custom enums in schema */}
                        {nodes
                          .filter(n => n.type === 'enumNode')
                          .map(n => {
                            const name = n.data.enum?.name;
                            return name ? <option key={name} value={name}>{name} (Enum)</option> : null;
                          })}
                      </select>
                    </div>
                  </div>

                  {/* Modifiers Checkboxes */}
                  <div className="grid grid-cols-3 gap-3 p-3 bg-slate-100/40 dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800 rounded text-xs mt-1">
                    <label className="flex items-center gap-2 text-slate-600 dark:text-slate-400 cursor-pointer select-none">
                      <input 
                        type="checkbox"
                        checked={prop.isKey}
                        onChange={(e) => updateProperty(classNodeSelected.id, editingPropIdx, { isKey: e.target.checked })}
                        className="w-3.5 h-3.5 text-sky-500 bg-white dark:bg-slate-900 border-slate-200 dark:border-slate-800 rounded focus:ring-0 focus:ring-offset-0 cursor-pointer"
                      />
                      <span className="font-mono text-[9px] font-bold">KEY FIELD</span>
                    </label>

                    <label className="flex items-center gap-2 text-slate-600 dark:text-slate-400 cursor-pointer select-none">
                      <input 
                        type="checkbox"
                        checked={prop.isEnum}
                        onChange={(e) => updateProperty(classNodeSelected.id, editingPropIdx, { isEnum: e.target.checked })}
                        className="w-3.5 h-3.5 text-sky-500 bg-white dark:bg-slate-900 border-slate-200 dark:border-slate-800 rounded focus:ring-0 focus:ring-offset-0 cursor-pointer"
                      />
                      <span className="font-mono text-[9px] font-bold">IS ENUM</span>
                    </label>

                    <label className="flex items-center gap-2 text-slate-600 dark:text-slate-400 cursor-pointer select-none">
                      <input 
                        type="checkbox"
                        checked={prop.attributes.includes('Required')}
                        onChange={(e) => {
                          const newAttrs = e.target.checked
                            ? [...prop.attributes, 'Required']
                            : prop.attributes.filter(a => a !== 'Required');
                          updateProperty(classNodeSelected.id, editingPropIdx, { attributes: newAttrs });
                        }}
                        className="w-3.5 h-3.5 text-sky-500 bg-white dark:bg-slate-900 border-slate-200 dark:border-slate-800 rounded focus:ring-0 focus:ring-offset-0 cursor-pointer"
                      />
                      <span className="font-mono text-[9px] font-bold text-sky-450">REQUIRED</span>
                    </label>
                  </div>

                  {/* Security & Masking */}
                  <div className="flex flex-col gap-1.5 border-t border-slate-200 dark:border-slate-800/60 pt-3">
                    <span className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">SECURITY & DATA PROTECTION</span>
                    <div className="flex items-center gap-3 bg-slate-100/40 dark:bg-slate-950/40 p-2 border border-slate-200 dark:border-slate-800 rounded">
                      <span className="text-[10px] text-slate-600 dark:text-slate-400 font-semibold font-mono">PROTECT:</span>
                      <select
                        value={prop.attributes.includes('Encrypt') ? 'Encrypt' : prop.attributes.includes('Mask') ? 'Mask' : prop.attributes.includes('MaskEmail') ? 'MaskEmail' : 'None'}
                        onChange={(e) => {
                          const selectedVal = e.target.value;
                          const baseAttrs = prop.attributes.filter(a => a !== 'Encrypt' && a !== 'Mask' && a !== 'MaskEmail');
                          const newAttrs = selectedVal !== 'None' ? [...baseAttrs, selectedVal] : baseAttrs;
                          updateProperty(classNodeSelected.id, editingPropIdx, { attributes: newAttrs });
                        }}
                        className="bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs px-2 py-1 text-slate-800 dark:text-slate-200 focus:outline-none cursor-pointer flex-1"
                      >
                        <option value="None">None (Clear Text)</option>
                        <option value="Encrypt">Encrypt (AES-256 Storage)</option>
                        <option value="Mask">Mask (Generic Character Masking)</option>
                        <option value="MaskEmail">MaskEmail (Email Format Masking)</option>
                      </select>
                    </div>
                  </div>

                  {/* Validation Rules Section */}
                  <div className="flex flex-col gap-1.5 border-t border-slate-200 dark:border-slate-800/60 pt-3">
                    <span className="text-[10px] text-slate-500 dark:text-slate-500 font-mono block">VALIDATION CONTRAINTS</span>
                    <input
                      type="text"
                      value={prop.attributes.filter(a => !['Required', 'Encrypt', 'Mask', 'MaskEmail', 'Index', 'UniqueIndex', 'TextIndex', 'Unique'].includes(a)).join(', ')}
                      onChange={(e) => {
                        const reserved = ['Required', 'Encrypt', 'Mask', 'MaskEmail', 'Index', 'UniqueIndex', 'TextIndex', 'Unique'];
                        const baseAttrs = prop.attributes.filter(a => reserved.includes(a));
                        const customRules = e.target.value.split(',').map(s => s.trim()).filter(Boolean);
                        const newAttrs = [...baseAttrs, ...customRules];
                        updateProperty(classNodeSelected.id, editingPropIdx, { attributes: newAttrs });
                      }}
                      className="w-full px-3 py-2 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs text-slate-800 dark:text-white focus:outline-none focus:border-sky-500 font-mono placeholder:text-slate-700 dark:placeholder:text-slate-700"
                      placeholder="e.g. MinLength(3), MaxLength(50)"
                    />
                    
                    {/* Presets Helper */}
                    <div className="flex flex-wrap gap-1.5 mt-1">
                      <span className="text-[9px] text-slate-500 dark:text-slate-500 font-mono self-center mr-1">PRESETS:</span>
                      {[
                        { label: '+ MinLength(3)', rule: 'MinLength(3)' },
                        { label: '+ MaxLength(100)', rule: 'MaxLength(100)' },
                        { label: '+ Range(0, 1000)', rule: 'Range(0, 1000)' },
                        { label: '+ Email', rule: 'Email' },
                        { label: '+ Url', rule: 'Url' },
                        { label: '+ Phone', rule: 'Phone' }
                      ].map((preset) => {
                        const valRules = prop.attributes.filter(a => !['Required', 'Encrypt', 'Mask', 'MaskEmail', 'Index', 'UniqueIndex', 'TextIndex', 'Unique'].includes(a));
                        const hasRule = valRules.includes(preset.rule);
                        return (
                          <button
                            key={preset.rule}
                            onClick={() => {
                              if (!hasRule) {
                                const newAttrs = [...prop.attributes, preset.rule];
                                updateProperty(classNodeSelected.id, editingPropIdx, { attributes: newAttrs });
                              } else {
                                const newAttrs = prop.attributes.filter(a => a !== preset.rule);
                                updateProperty(classNodeSelected.id, editingPropIdx, { attributes: newAttrs });
                              }
                            }}
                            className={`px-2 py-0.5 rounded text-[9px] font-mono transition-colors cursor-pointer border ${
                              hasRule 
                                ? 'bg-sky-500/20 text-sky-400 border-sky-500/30 font-bold' 
                                : 'bg-slate-100/60 dark:bg-slate-950/60 hover:bg-slate-200 dark:hover:bg-slate-800 text-slate-600 dark:text-slate-400 border-slate-200 dark:border-slate-800'
                            }`}
                          >
                            {preset.label}
                          </button>
                        );
                      })}
                    </div>
                  </div>
                </div>

                {/* Footer */}
                <div className="flex justify-end gap-3 px-5 py-3 border-t border-slate-200 dark:border-slate-800 bg-slate-100/40 dark:bg-slate-950/40">
                  <button
                    onClick={() => setEditingPropIdx(null)}
                    className="px-4 py-1.5 bg-sky-600 hover:bg-sky-500 text-xs font-semibold text-white rounded transition-colors cursor-pointer shadow-md shadow-sky-600/10"
                  >
                    Save & Close
                  </button>
                </div>
              </div>
            </div>,
            document.body
          );
        })()
      )}

      {confirmConfig.isOpen && createPortal(
        <div className="fixed inset-0 bg-slate-950/80 backdrop-blur-sm z-50 flex items-center justify-center p-4">
          <div className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 rounded p-5 max-w-sm w-full shadow-2xl flex flex-col gap-4 animate-in fade-in zoom-in duration-200">
            <div className="flex flex-col gap-1">
              <h3 className="text-xs font-semibold text-slate-800 dark:text-white uppercase tracking-wider font-mono border-b border-slate-200 dark:border-slate-800 pb-2">{confirmConfig.title}</h3>
              <p className="text-xs text-slate-600 dark:text-slate-400 mt-2">{confirmConfig.message}</p>
            </div>
            <div className="flex justify-end gap-3 pt-2">
              <button
                onClick={() => setConfirmConfig(prev => ({ ...prev, isOpen: false }))}
                className="px-3 py-1.5 bg-slate-200 dark:bg-slate-800 hover:bg-slate-300 dark:hover:bg-slate-800 text-xs font-semibold text-slate-600 dark:text-slate-300 rounded transition-colors cursor-pointer border border-slate-300 dark:border-slate-700/60"
              >
                Cancel
              </button>
              <button
                onClick={confirmConfig.onConfirm}
                className="px-3 py-1.5 bg-red-600 hover:bg-red-500 text-xs font-semibold text-white rounded transition-colors cursor-pointer border border-red-700/60"
              >
                Confirm Delete
              </button>
            </div>
          </div>
        </div>,
        document.body
      )}
    </div>
  );
};