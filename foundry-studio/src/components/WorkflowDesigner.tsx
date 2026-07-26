import React, { useState, useMemo, useEffect } from 'react';
import { 
  ReactFlow, 
  Controls, 
  Background, 
  MiniMap,
  Handle,
  Position,
  MarkerType,
  type Node,
  type Edge,
  type Connection,
  type NodeTypes
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { useStore } from '../store';
import type { 
  WorkflowDefinition, WorkflowState, WorkflowTransition, 
  WorkflowCondition, WorkflowAction, InternalApiAction, 
  ExternalApiAction, WorkflowChoiceNode, WorkflowChoiceBranch 
} from '../types';
import { 
  Plus, Trash2, Shield, Settings, GitMerge,
  Layers, Cpu
} from 'lucide-react';

// Custom Node Components (Theme Aware)
const StateNodeComponent: React.FC<{ data: any; selected: boolean }> = ({ data, selected }) => {
  return (
    <div className={`p-4 rounded-xl border bg-white dark:bg-slate-900 shadow-sm dark:shadow-md min-w-[160px] text-xs flex flex-col gap-2 transition-all ${
      selected 
        ? 'border-sky-500 shadow-lg ring-1 ring-sky-500 dark:ring-0 dark:shadow-sky-500/10' 
        : 'border-slate-200 dark:border-slate-800 hover:border-slate-300 dark:hover:border-slate-700'
    }`}>
      {/* Target handle on top and left */}
      <Handle type="target" position={Position.Top} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
      <Handle type="target" position={Position.Left} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
      
      <div className="flex justify-between items-center gap-2">
        <span className="font-semibold text-slate-800 dark:text-slate-100 truncate">{data.label}</span>
      </div>

      <div className="flex gap-1.5 mt-0.5">
        {data.isInitial && (
          <span className="text-[8px] font-bold px-1.5 py-0.5 bg-indigo-500/10 text-indigo-500 dark:text-indigo-400 border border-indigo-500/25 rounded uppercase">
            Initial
          </span>
        )}
        {data.isFinal && (
          <span className="text-[8px] font-bold px-1.5 py-0.5 bg-emerald-500/10 text-emerald-600 dark:text-emerald-400 border border-emerald-500/25 rounded uppercase">
            Final
          </span>
        )}
      </div>

      <div className="text-[9px] text-slate-500 dark:text-slate-400 border-t border-slate-100 dark:border-slate-800/80 pt-1.5 mt-0.5 flex items-center gap-1.5">
        <Shield className="w-3 h-3 text-slate-400 dark:text-slate-500" />
        <span className="truncate">Roles: {data.allowedRoles?.join(', ') || 'None'}</span>
      </div>

      {/* Source handle on bottom and right */}
      <Handle type="source" position={Position.Bottom} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
      <Handle type="source" position={Position.Right} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
    </div>
  );
};

const ChoiceNodeComponent: React.FC<{ data: any; selected: boolean }> = ({ data, selected }) => {
  return (
    <div className={`p-4 rounded-xl border bg-slate-50 dark:bg-slate-950 shadow-sm dark:shadow-md min-w-[160px] text-xs flex flex-col gap-2 transition-all ${
      selected 
        ? 'border-amber-500 shadow-lg ring-1 ring-amber-500 dark:ring-0 dark:shadow-amber-500/10' 
        : 'border-slate-200 dark:border-slate-800 hover:border-slate-300 dark:hover:border-slate-700'
    }`}>
      {/* Target handle on top and left */}
      <Handle type="target" position={Position.Top} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
      <Handle type="target" position={Position.Left} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />

      <div className="flex items-center gap-1.5 font-semibold text-amber-600 dark:text-amber-400">
        <GitMerge className="w-4 h-4" />
        <span className="truncate">{data.label}</span>
      </div>
      
      <span className="text-[8px] font-bold px-1.5 py-0.5 bg-amber-500/10 text-amber-600 dark:text-amber-400 border border-amber-500/25 rounded uppercase w-max">
        Decision Gate
      </span>

      <div className="text-[9px] text-slate-500 dark:text-slate-400 border-t border-slate-200 dark:border-slate-800/80 pt-1.5 mt-0.5 flex justify-between">
        <span>Branches: {data.branchesCount}</span>
        <span className="font-mono text-slate-400 dark:text-slate-500">Else: {data.defaultState || 'None'}</span>
      </div>

      {/* Source handle on bottom and right */}
      <Handle type="source" position={Position.Bottom} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
      <Handle type="source" position={Position.Right} className="!bg-slate-400 dark:!bg-slate-600 !w-2 !h-2" />
    </div>
  );
};

const nodeTypes: NodeTypes = {
  stateNode: StateNodeComponent,
  choiceNode: ChoiceNodeComponent
};

export const WorkflowDesigner: React.FC = () => {
  const { 
    workflows, addWorkflow, updateWorkflow, deleteWorkflow, nodes: domainNodes 
  } = useStore();

  const [selectedWorkflowIndex, setSelectedWorkflowIndex] = useState<number>(-1);
  const [selectedStateIndex, setSelectedStateIndex] = useState<number>(-1);
  const [selectedTransitionIndex, setSelectedTransitionIndex] = useState<number>(-1);
  const [selectedChoiceNodeIndex, setSelectedChoiceNodeIndex] = useState<number>(-1);

  const selectedWorkflow = selectedWorkflowIndex >= 0 ? workflows[selectedWorkflowIndex] : null;

  // React Flow Local Nodes & Edges
  const [flowNodes, setFlowNodes] = useState<Node[]>([]);
  const [flowEdges, setFlowEdges] = useState<Edge[]>([]);

  // Check if HTML document root has dark class active to adapt edge color styling dynamically
  const [isDarkMode, setIsDarkMode] = useState<boolean>(
    document.documentElement.classList.contains('dark')
  );

  useEffect(() => {
    const observer = new MutationObserver(() => {
      setIsDarkMode(document.documentElement.classList.contains('dark'));
    });
    observer.observe(document.documentElement, { attributes: true, attributeFilter: ['class'] });
    return () => observer.disconnect();
  }, []);

  // Sync React Flow nodes & edges with Zustand state
  useEffect(() => {
    if (!selectedWorkflow) {
      setFlowNodes([]);
      setFlowEdges([]);
      return;
    }

    const newNodes: Node[] = [
      ...selectedWorkflow.states.map((s, idx) => ({
        id: s.name,
        type: 'stateNode',
        position: { x: s.x ?? (150 + idx * 240), y: s.y ?? 100 },
        selected: selectedStateIndex === idx,
        data: { label: s.name, isInitial: s.isInitial, isFinal: s.isFinal, allowedRoles: s.allowedRoles },
      })),
      ...(selectedWorkflow.choiceNodes || []).map((c, idx) => ({
        id: c.id,
        type: 'choiceNode',
        position: { x: c.x ?? (150 + idx * 240), y: c.y ?? 280 },
        selected: selectedChoiceNodeIndex === idx,
        data: { label: c.name, branchesCount: (c.branches || []).length, defaultState: c.defaultState },
      }))
    ];

    const defaultEdgeColor = isDarkMode ? '#38bdf8' : '#0284c7';
    const labelColor = isDarkMode ? '#94a3b8' : '#475569';

    const newEdges: Edge[] = selectedWorkflow.transitions.map((t, idx) => {
      const isSelected = selectedTransitionIndex === idx;
      return {
        id: t.id,
        source: t.fromState,
        target: t.toState,
        label: t.name || t.trigger,
        type: 'smoothstep',
        selected: isSelected,
        markerEnd: { type: MarkerType.ArrowClosed, color: isSelected ? '#a855f7' : defaultEdgeColor },
        style: { stroke: isSelected ? '#a855f7' : defaultEdgeColor, strokeWidth: isSelected ? 3 : 1.5, cursor: 'pointer' },
        labelStyle: { fill: isSelected ? '#a855f7' : labelColor, fontSize: '9px', fontFamily: 'monospace', fontWeight: isSelected ? 'bold' : 'normal' }
      };
    });

    setFlowNodes(newNodes);
    setFlowEdges(newEdges);
  }, [
    selectedWorkflowIndex, 
    selectedWorkflow?.states, 
    selectedWorkflow?.choiceNodes, 
    selectedWorkflow?.transitions,
    selectedStateIndex,
    selectedChoiceNodeIndex,
    selectedTransitionIndex,
    isDarkMode
  ]);

  // Extract all entity names from class nodes on the canvas
  const availableEntities = useMemo(() => {
    return domainNodes
      .filter(n => n.type === 'classNode')
      .map(n => n.data.entity.name);
  }, [domainNodes]);

  const handleUpdateSelectedWorkflow = (updated: Partial<WorkflowDefinition>) => {
    if (selectedWorkflowIndex >= 0) {
      updateWorkflow(selectedWorkflowIndex, updated);
    }
  };

  // Add State
  const handleAddState = () => {
    if (!selectedWorkflow) return;
    const name = `State_${selectedWorkflow.states.length + 1}`;
    const newState: WorkflowState = {
      name,
      isInitial: selectedWorkflow.states.length === 0,
      isFinal: false,
      allowedRoles: ['Admin'],
      x: 100 + Math.random() * 80,
      y: 100 + Math.random() * 80
    };
    handleUpdateSelectedWorkflow({
      states: [...selectedWorkflow.states, newState]
    });
  };

  // Add Decision Gate (Choice Node)
  const handleAddChoiceNode = () => {
    if (!selectedWorkflow) return;
    const name = `Check_Gate_${(selectedWorkflow.choiceNodes || []).length + 1}`;
    const newChoice: WorkflowChoiceNode = {
      id: name.toLowerCase().replace(/\s+/g, '_'),
      name,
      branches: [],
      defaultState: selectedWorkflow.states[0]?.name || '',
      x: 100 + Math.random() * 80,
      y: 200 + Math.random() * 80
    };
    handleUpdateSelectedWorkflow({
      choiceNodes: [...(selectedWorkflow.choiceNodes || []), newChoice]
    });
  };

  // Delete Selected State
  const handleDeleteState = (stateIndex: number) => {
    if (!selectedWorkflow) return;
    const stateName = selectedWorkflow.states[stateIndex].name;
    const states = selectedWorkflow.states.filter((_, idx) => idx !== stateIndex);
    const transitions = selectedWorkflow.transitions.filter(
      t => t.fromState !== stateName && t.toState !== stateName
    );
    handleUpdateSelectedWorkflow({ states, transitions });
    setSelectedStateIndex(-1);
  };

  // Delete Selected Choice Node
  const handleDeleteChoiceNode = (choiceIndex: number) => {
    if (!selectedWorkflow) return;
    const choice = selectedWorkflow.choiceNodes[choiceIndex];
    const choiceNodes = selectedWorkflow.choiceNodes.filter((_, idx) => idx !== choiceIndex);
    const transitions = selectedWorkflow.transitions.filter(
      t => t.fromState !== choice.id && t.toState !== choice.id
    );
    handleUpdateSelectedWorkflow({ choiceNodes, transitions });
    setSelectedChoiceNodeIndex(-1);
  };

  // Delete Selected Transition Edge
  const handleDeleteTransition = (transIndex: number) => {
    if (!selectedWorkflow) return;
    const transitions = selectedWorkflow.transitions.filter((_, idx) => idx !== transIndex);
    handleUpdateSelectedWorkflow({ transitions });
    setSelectedTransitionIndex(-1);
  };

  // Drag stop: Update positions in store
  const onNodeDragStop = (_event: any, node: Node) => {
    if (!selectedWorkflow) return;
    if (node.type === 'stateNode') {
      const states = selectedWorkflow.states.map(s => {
        if (s.name === node.id) {
          return { ...s, x: Math.round(node.position.x), y: Math.round(node.position.y) };
        }
        return s;
      });
      handleUpdateSelectedWorkflow({ states });
    } else if (node.type === 'choiceNode') {
      const choiceNodes = selectedWorkflow.choiceNodes.map(c => {
        if (c.id === node.id) {
          return { ...c, x: Math.round(node.position.x), y: Math.round(node.position.y) };
        }
        return c;
      });
      handleUpdateSelectedWorkflow({ choiceNodes });
    }
  };

  // Handle visual connections -> transition stubs
  const onConnect = (connection: Connection) => {
    if (!selectedWorkflow) return;
    const fromState = connection.source;
    const toState = connection.target;
    if (!fromState || !toState) return;

    const transitionId = `${fromState.toLowerCase()}_to_${toState.toLowerCase()}`;
    if (selectedWorkflow.transitions.some(t => t.fromState === fromState && t.toState === toState)) {
      return;
    }

    const newTransition: WorkflowTransition = {
      id: transitionId,
      name: `Transition to ${toState}`,
      fromState,
      toState,
      trigger: `${fromState}To${toState}Command`,
      useCustomCommand: false,
      requiredRoles: ['Admin'],
      conditions: [],
      actions: []
    };

    handleUpdateSelectedWorkflow({
      transitions: [...selectedWorkflow.transitions, newTransition]
    });
  };

  // Selection click handlers
  const onNodeClick = (_event: any, node: Node) => {
    if (!selectedWorkflow) return;
    if (node.type === 'stateNode') {
      const idx = selectedWorkflow.states.findIndex(s => s.name === node.id);
      setSelectedStateIndex(idx);
      setSelectedChoiceNodeIndex(-1);
      setSelectedTransitionIndex(-1);
    } else if (node.type === 'choiceNode') {
      const idx = selectedWorkflow.choiceNodes.findIndex(c => c.id === node.id);
      setSelectedChoiceNodeIndex(idx);
      setSelectedStateIndex(-1);
      setSelectedTransitionIndex(-1);
    }
  };

  const onEdgeClick = (_event: any, edge: Edge) => {
    if (!selectedWorkflow) return;
    const idx = selectedWorkflow.transitions.findIndex(t => t.id === edge.id);
    setSelectedTransitionIndex(idx);
    setSelectedStateIndex(-1);
    setSelectedChoiceNodeIndex(-1);
  };

  const onPaneClick = () => {
    setSelectedStateIndex(-1);
    setSelectedChoiceNodeIndex(-1);
    setSelectedTransitionIndex(-1);
  };

  return (
    <div className="flex flex-1 overflow-hidden bg-slate-50 dark:bg-slate-900 text-slate-800 dark:text-slate-100 font-sans h-full transition-colors duration-200">
      {/* Left Sidebar - Workflows List */}
      <div className="w-64 border-r border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-950/40 p-4 flex flex-col gap-4 overflow-y-auto transition-colors duration-200">
        <div className="flex justify-between items-center">
          <h2 className="text-xs font-semibold text-slate-550 dark:text-slate-400 uppercase tracking-wider flex items-center gap-1.5">
            <Layers className="w-4 h-4 text-sky-500" />
            Workflows
          </h2>
          <button
            onClick={addWorkflow}
            className="p-1 text-slate-500 hover:text-slate-900 dark:text-slate-400 dark:hover:text-white bg-slate-100 hover:bg-slate-200 dark:bg-slate-800 dark:hover:bg-slate-700 rounded transition-all cursor-pointer"
            title="Create New Workflow"
          >
            <Plus className="w-4 h-4" />
          </button>
        </div>

        <div className="flex flex-col gap-1">
          {workflows.map((wf, idx) => (
            <div
              key={wf.id}
              onClick={() => {
                setSelectedWorkflowIndex(idx);
                setSelectedStateIndex(-1);
                setSelectedTransitionIndex(-1);
                setSelectedChoiceNodeIndex(-1);
              }}
              className={`p-3 rounded-lg text-left text-xs transition-all cursor-pointer border flex flex-col gap-1.5 ${
                selectedWorkflowIndex === idx
                  ? 'bg-sky-500/10 border-sky-500/50 text-sky-700 dark:text-white font-semibold shadow-sm dark:shadow-lg dark:shadow-sky-500/5'
                  : 'bg-slate-50 dark:bg-slate-900/40 border-slate-200 dark:border-slate-800/85 hover:bg-slate-100 dark:hover:bg-slate-800/50 text-slate-600 dark:text-slate-400'
              }`}
            >
              <div className="flex justify-between items-center">
                <span className="font-medium truncate max-w-[140px]">{wf.name || 'Unnamed'}</span>
                <span className={`text-[9px] px-1.5 py-0.5 rounded font-mono font-bold ${
                  wf.isActive 
                    ? 'bg-emerald-500/10 text-emerald-600 dark:text-emerald-400' 
                    : 'bg-slate-200 dark:bg-slate-800 text-slate-500'
                }`}>
                  {wf.version}
                </span>
              </div>
              <div className="flex justify-between items-center text-[10px]">
                <span className="font-mono text-slate-500 truncate max-w-[120px]">{wf.entity || 'No Entity Bound'}</span>
                <button
                  onClick={(e) => {
                    e.stopPropagation();
                    deleteWorkflow(idx);
                    if (selectedWorkflowIndex === idx) setSelectedWorkflowIndex(-1);
                  }}
                  className="text-slate-450 dark:text-slate-500 hover:text-red-500 dark:hover:text-red-400 p-0.5 rounded transition-all"
                >
                  <Trash2 className="w-3 h-3" />
                </button>
              </div>
            </div>
          ))}

          {workflows.length === 0 && (
            <div className="text-center py-8 text-xs text-slate-400 dark:text-slate-600 italic">
              No workflows defined.<br />Click the plus icon to start.
            </div>
          )}
        </div>
      </div>

      {/* Center Panel - UML Diagram Canvas */}
      <div className="flex-1 flex flex-col overflow-hidden bg-white dark:bg-slate-900/60 relative transition-colors duration-200">
        {selectedWorkflow ? (
          <>
            {/* Top Config Header */}
            <div className="p-4 border-b border-slate-200 dark:border-slate-800/80 bg-slate-50/50 dark:bg-slate-950/20 grid grid-cols-5 gap-4 items-center print-hide transition-colors duration-200">
              <div className="flex flex-col gap-1">
                <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Workflow Name</label>
                <input
                  type="text"
                  value={selectedWorkflow.name}
                  onChange={(e) => handleUpdateSelectedWorkflow({ name: e.target.value })}
                  className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-medium transition-colors duration-200"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Target Entity</label>
                <select
                  value={selectedWorkflow.entity}
                  onChange={(e) => handleUpdateSelectedWorkflow({ entity: e.target.value })}
                  className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-medium transition-colors duration-200"
                >
                  <option value="">-- Select Entity --</option>
                  {availableEntities.map(name => (
                    <option key={name} value={name}>{name}</option>
                  ))}
                </select>
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Version</label>
                <input
                  type="text"
                  value={selectedWorkflow.version}
                  onChange={(e) => handleUpdateSelectedWorkflow({ version: e.target.value })}
                  className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-medium font-mono transition-colors duration-200"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Effective Date</label>
                <input
                  type="date"
                  value={selectedWorkflow.effectiveDate}
                  onChange={(e) => handleUpdateSelectedWorkflow({ effectiveDate: e.target.value })}
                  className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-medium transition-colors duration-200"
                />
              </div>

              <div className="flex flex-col gap-1">
                <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Expiration Date</label>
                <input
                  type="date"
                  value={selectedWorkflow.expirationDate}
                  onChange={(e) => handleUpdateSelectedWorkflow({ expirationDate: e.target.value })}
                  className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-medium transition-colors duration-200"
                />
              </div>
            </div>

            {/* Interactive Canvas Controls */}
            <div className="p-3 bg-slate-50/40 dark:bg-slate-950/10 border-b border-slate-200 dark:border-slate-800/40 flex justify-between items-center px-6 transition-colors duration-200">
              <span className="text-xs text-slate-500">
                Drag handles to connect states. Drag nodes to layout the diagram.
              </span>
              <div className="flex gap-2">
                <button
                  onClick={handleAddState}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-sky-600 hover:bg-sky-500 text-white rounded text-xs font-semibold shadow-sm hover:shadow-md transition-all cursor-pointer"
                >
                  <Plus className="w-3.5 h-3.5" />
                  <span>Add State</span>
                </button>
                <button
                  onClick={handleAddChoiceNode}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-amber-600 hover:bg-amber-500 text-white rounded text-xs font-semibold shadow-sm hover:shadow-md transition-all cursor-pointer"
                >
                  <GitMerge className="w-3.5 h-3.5" />
                  <span>Add Decision Gate</span>
                </button>
              </div>
            </div>

            {/* React Flow Canvas Container */}
            <div className="flex-1 w-full relative min-h-[400px]">
              <ReactFlow
                nodes={flowNodes}
                edges={flowEdges}
                nodeTypes={nodeTypes}
                onConnect={onConnect}
                onNodeDragStop={onNodeDragStop}
                onNodeClick={onNodeClick}
                onEdgeClick={onEdgeClick}
                onPaneClick={onPaneClick}
                fitView
                className="bg-slate-50/40 dark:bg-slate-950/20"
              >
                <Background color={isDarkMode ? '#1e293b' : '#cbd5e1'} gap={16} size={1} />
                <Controls />
                <MiniMap 
                  style={{ height: 100, width: 140 }}
                  nodeColor={(n) => n.type === 'choiceNode' ? '#f59e0b' : '#0284c7'}
                />
              </ReactFlow>
            </div>
          </>
        ) : (
          <div className="flex-1 flex flex-col items-center justify-center text-slate-450 dark:text-slate-500 text-sm gap-2">
            <Layers className="w-8 h-8 text-slate-300 dark:text-slate-700" />
            <span>Select a workflow from the left sidebar to edit states and transitions.</span>
          </div>
        )}
      </div>

      {/* Right Sidebar - Properties Inspector */}
      {selectedWorkflow && (selectedStateIndex >= 0 || selectedTransitionIndex >= 0 || selectedChoiceNodeIndex >= 0) && (
        <div className="w-85 border-l border-slate-200 dark:border-slate-800 bg-white dark:bg-slate-950/40 p-5 flex flex-col gap-6 overflow-y-auto print-hide transition-colors duration-200">
          {/* STATE INSPECTION */}
          {selectedStateIndex >= 0 && (
            <>
              <div>
                <div className="flex justify-between items-center mb-4">
                  <h3 className="text-xs font-bold text-slate-800 dark:text-white uppercase tracking-wider flex items-center gap-1.5">
                    <Settings className="w-4 h-4 text-sky-500" />
                    State Parameters
                  </h3>
                  <button
                    onClick={() => handleDeleteState(selectedStateIndex)}
                    className="text-slate-550 hover:text-red-500 dark:text-slate-500 dark:hover:text-red-400 p-1 bg-slate-50 dark:bg-slate-900/60 rounded border border-slate-200 dark:border-slate-800 cursor-pointer"
                    title="Delete State Node"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
                </div>
                
                <div className="flex flex-col gap-4">
                  <div className="flex flex-col gap-1">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">State Name</label>
                    <input
                      type="text"
                      value={selectedWorkflow.states[selectedStateIndex].name}
                      onChange={(e) => {
                        const states = [...selectedWorkflow.states];
                        const oldName = states[selectedStateIndex].name;
                        const newName = e.target.value;
                        states[selectedStateIndex].name = newName;

                        // Also sync transition dependencies
                        const transitions = selectedWorkflow.transitions.map(t => {
                          let updated = { ...t };
                          if (t.fromState === oldName) updated.fromState = newName;
                          if (t.toState === oldName) updated.toState = newName;
                          return updated;
                        });

                        handleUpdateSelectedWorkflow({ states, transitions });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-medium transition-colors duration-200"
                    />
                  </div>

                  <div className="flex items-center gap-4">
                    <label className="flex items-center gap-2 text-xs text-slate-650 dark:text-slate-300 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={selectedWorkflow.states[selectedStateIndex].isInitial}
                        onChange={(e) => {
                          const states = selectedWorkflow.states.map((s, idx) => ({
                            ...s,
                            isInitial: idx === selectedStateIndex ? e.target.checked : false
                          }));
                          handleUpdateSelectedWorkflow({ states });
                        }}
                        className="rounded border-slate-300 dark:border-slate-800 text-sky-600 focus:ring-sky-500"
                      />
                      Is Initial State
                    </label>
                  </div>

                  <div className="flex items-center gap-4">
                    <label className="flex items-center gap-2 text-xs text-slate-655 dark:text-slate-300 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={selectedWorkflow.states[selectedStateIndex].isFinal}
                        onChange={(e) => {
                          const states = [...selectedWorkflow.states];
                          states[selectedStateIndex].isFinal = e.target.checked;
                          handleUpdateSelectedWorkflow({ states });
                        }}
                        className="rounded border-slate-300 dark:border-slate-800 text-sky-600 focus:ring-sky-500"
                      />
                      Is Final State
                    </label>
                  </div>

                  <div className="flex flex-col gap-2">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Authorized Security Roles</label>
                    <input
                      type="text"
                      placeholder="Comma-separated roles"
                      value={selectedWorkflow.states[selectedStateIndex].allowedRoles.join(', ')}
                      onChange={(e) => {
                        const states = [...selectedWorkflow.states];
                        states[selectedStateIndex].allowedRoles = e.target.value.split(',').map(s => s.trim()).filter(Boolean);
                        handleUpdateSelectedWorkflow({ states });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-medium transition-colors duration-200"
                    />
                  </div>
                </div>
              </div>
            </>
          )}

          {/* CHOICE NODE (DECISION GATE) INSPECTION */}
          {selectedChoiceNodeIndex >= 0 && (
            <>
              <div>
                <div className="flex justify-between items-center mb-4">
                  <h3 className="text-xs font-bold text-slate-800 dark:text-white uppercase tracking-wider flex items-center gap-1.5">
                    <GitMerge className="w-4 h-4 text-amber-600 dark:text-amber-500" />
                    Decision Gate Settings
                  </h3>
                  <button
                    onClick={() => handleDeleteChoiceNode(selectedChoiceNodeIndex)}
                    className="text-slate-555 hover:text-red-505 dark:text-slate-500 dark:hover:text-red-400 p-1 bg-slate-50 dark:bg-slate-900/60 rounded border border-slate-200 dark:border-slate-800 cursor-pointer"
                    title="Delete Decision Node"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
                </div>

                <div className="flex flex-col gap-4">
                  <div className="flex flex-col gap-1">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Gate ID</label>
                    <input
                      type="text"
                      value={selectedWorkflow.choiceNodes[selectedChoiceNodeIndex].name}
                      onChange={(e) => {
                        const choiceNodes = [...selectedWorkflow.choiceNodes];
                        const oldId = choiceNodes[selectedChoiceNodeIndex].id;
                        const newName = e.target.value;
                        const newId = newName.toLowerCase().replace(/\s+/g, '_');
                        
                        choiceNodes[selectedChoiceNodeIndex].name = newName;
                        choiceNodes[selectedChoiceNodeIndex].id = newId;

                        // Sync transitions referencing this choice node
                        const transitions = selectedWorkflow.transitions.map(t => {
                          let updated = { ...t };
                          if (t.fromState === oldId) updated.fromState = newId;
                          if (t.toState === oldId) updated.toState = newId;
                          return updated;
                        });

                        handleUpdateSelectedWorkflow({ choiceNodes, transitions });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-semibold transition-colors duration-200"
                    />
                  </div>

                  <div className="flex flex-col gap-1">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Default Target State (Else)</label>
                    <select
                      value={selectedWorkflow.choiceNodes[selectedChoiceNodeIndex].defaultState}
                      onChange={(e) => {
                        const choiceNodes = [...selectedWorkflow.choiceNodes];
                        choiceNodes[selectedChoiceNodeIndex].defaultState = e.target.value;
                        handleUpdateSelectedWorkflow({ choiceNodes });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white transition-colors duration-200"
                    >
                      <option value="">-- Select Target State --</option>
                      {selectedWorkflow.states.map(s => (
                        <option key={s.name} value={s.name}>{s.name}</option>
                      ))}
                    </select>
                  </div>

                  {/* Branches Section */}
                  <div className="border-t border-slate-200 dark:border-slate-800/80 pt-4 flex flex-col gap-3">
                    <div className="flex justify-between items-center">
                      <span className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Conditional Branches</span>
                      <button
                        onClick={() => {
                          const choiceNodes = [...selectedWorkflow.choiceNodes];
                          const newBranch: WorkflowChoiceBranch = {
                            toState: selectedWorkflow.states[0]?.name || '',
                            conditions: []
                          };
                          choiceNodes[selectedChoiceNodeIndex].branches = [
                            ...(choiceNodes[selectedChoiceNodeIndex].branches || []),
                            newBranch
                          ];
                          handleUpdateSelectedWorkflow({ choiceNodes });
                        }}
                        className="p-1 hover:text-slate-800 dark:hover:text-white text-slate-400 dark:text-slate-500 bg-slate-50 dark:bg-slate-950/40 rounded border border-slate-200 dark:border-slate-800 transition-all cursor-pointer"
                        title="Add Conditional Branch"
                      >
                        <Plus className="w-3.5 h-3.5" />
                      </button>
                    </div>

                    <div className="flex flex-col gap-3">
                      {(selectedWorkflow.choiceNodes[selectedChoiceNodeIndex].branches || []).map((br, bIdx) => (
                        <div key={bIdx} className="bg-slate-50 dark:bg-slate-950/50 border border-slate-200 dark:border-slate-850 p-3 rounded flex flex-col gap-2 text-[10px] transition-colors duration-200">
                          <div className="flex justify-between items-center border-b border-slate-100 dark:border-slate-800/60 pb-1.5">
                            <span className="font-mono text-amber-600 dark:text-amber-400">Branch #{bIdx + 1}</span>
                            <button
                              onClick={() => {
                                const choiceNodes = [...selectedWorkflow.choiceNodes];
                                choiceNodes[selectedChoiceNodeIndex].branches = choiceNodes[selectedChoiceNodeIndex].branches.filter((_, i) => i !== bIdx);
                                handleUpdateSelectedWorkflow({ choiceNodes });
                              }}
                              className="text-slate-400 dark:text-slate-500 hover:text-red-500 dark:hover:text-red-400 p-0.5 rounded transition-all cursor-pointer"
                            >
                              <Trash2 className="w-3.5 h-3.5" />
                            </button>
                          </div>

                          <div className="flex flex-col gap-1">
                            <label className="text-[9px] text-slate-400 dark:text-slate-500 uppercase">Target State</label>
                            <select
                              value={br.toState}
                              onChange={(e) => {
                                const choiceNodes = [...selectedWorkflow.choiceNodes];
                                choiceNodes[selectedChoiceNodeIndex].branches[bIdx].toState = e.target.value;
                                handleUpdateSelectedWorkflow({ choiceNodes });
                              }}
                              className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white transition-colors duration-200"
                            >
                              {selectedWorkflow.states.map(s => (
                                <option key={s.name} value={s.name}>{s.name}</option>
                              ))}
                            </select>
                          </div>

                          {/* Branch Conditions */}
                          <div className="flex flex-col gap-1.5 mt-1.5">
                            <div className="flex justify-between items-center text-[9px] text-slate-400 dark:text-slate-500 uppercase">
                              <span>Conditions</span>
                              <button
                                onClick={() => {
                                  const choiceNodes = [...selectedWorkflow.choiceNodes];
                                  choiceNodes[selectedChoiceNodeIndex].branches[bIdx].conditions.push({
                                    type: 'PropertyComparison',
                                    property: '',
                                    operator: 'Equal',
                                    value: ''
                                  });
                                  handleUpdateSelectedWorkflow({ choiceNodes });
                                }}
                                className="hover:text-slate-950 dark:hover:text-white cursor-pointer"
                              >
                                + Add Guard
                              </button>
                            </div>

                            {br.conditions.map((cond, cIdx) => (
                              <div key={cIdx} className="flex gap-1.5 items-center">
                                <input
                                  type="text"
                                  placeholder="Prop"
                                  value={cond.property}
                                  onChange={(e) => {
                                    const choiceNodes = [...selectedWorkflow.choiceNodes];
                                    choiceNodes[selectedChoiceNodeIndex].branches[bIdx].conditions[cIdx].property = e.target.value;
                                    handleUpdateSelectedWorkflow({ choiceNodes });
                                  }}
                                  className="w-1/3 px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-slate-800 dark:text-white font-mono transition-colors duration-200"
                                />
                                <select
                                  value={cond.operator}
                                  onChange={(e) => {
                                    const choiceNodes = [...selectedWorkflow.choiceNodes];
                                    choiceNodes[selectedChoiceNodeIndex].branches[bIdx].conditions[cIdx].operator = e.target.value as any;
                                    handleUpdateSelectedWorkflow({ choiceNodes });
                                  }}
                                  className="w-1/3 px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-slate-800 dark:text-white text-[9px] transition-colors duration-200"
                                >
                                  <option value="Equal">==</option>
                                  <option value="NotEqual">!=</option>
                                  <option value="LessThan">&lt;</option>
                                  <option value="LessThanOrEqual">&lt;=</option>
                                  <option value="GreaterThan">&gt;</option>
                                  <option value="GreaterThanOrEqual">&gt;=</option>
                                </select>
                                <input
                                  type="text"
                                  placeholder="Val"
                                  value={cond.value}
                                  onChange={(e) => {
                                    const choiceNodes = [...selectedWorkflow.choiceNodes];
                                    choiceNodes[selectedChoiceNodeIndex].branches[bIdx].conditions[cIdx].value = e.target.value;
                                    handleUpdateSelectedWorkflow({ choiceNodes });
                                  }}
                                  className="w-1/3 px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-slate-800 dark:text-white font-mono transition-colors duration-200"
                                />
                                <button
                                  onClick={() => {
                                    const choiceNodes = [...selectedWorkflow.choiceNodes];
                                    choiceNodes[selectedChoiceNodeIndex].branches[bIdx].conditions = 
                                      choiceNodes[selectedChoiceNodeIndex].branches[bIdx].conditions.filter((_, i) => i !== cIdx);
                                    handleUpdateSelectedWorkflow({ choiceNodes });
                                  }}
                                  className="text-slate-400 dark:text-slate-500 hover:text-red-505 dark:hover:text-red-400 cursor-pointer"
                                >
                                  ×
                                </button>
                              </div>
                            ))}
                          </div>
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              </div>
            </>
          )}

          {/* TRANSITION INSPECTION */}
          {selectedTransitionIndex >= 0 && (
            <>
              <div>
                <div className="flex justify-between items-center mb-4">
                  <h3 className="text-xs font-bold text-slate-800 dark:text-white uppercase tracking-wider flex items-center gap-1.5">
                    <Cpu className="w-4 h-4 text-sky-500" />
                    Transition Gate
                  </h3>
                  <button
                    onClick={() => handleDeleteTransition(selectedTransitionIndex)}
                    className="text-slate-555 hover:text-red-505 dark:text-slate-500 dark:hover:text-red-400 p-1 bg-slate-50 dark:bg-slate-900/60 rounded border border-slate-200 dark:border-slate-800 cursor-pointer"
                    title="Delete Transition Edge"
                  >
                    <Trash2 className="w-4 h-4" />
                  </button>
                </div>

                <div className="flex flex-col gap-4">
                  <div className="flex flex-col gap-1">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Transition Label</label>
                    <input
                      type="text"
                      value={selectedWorkflow.transitions[selectedTransitionIndex].name}
                      onChange={(e) => {
                        const transitions = [...selectedWorkflow.transitions];
                        transitions[selectedTransitionIndex].name = e.target.value;
                        handleUpdateSelectedWorkflow({ transitions });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-medium transition-colors duration-200"
                    />
                  </div>

                  <div className="flex items-center gap-4">
                    <label className="flex items-center gap-2 text-xs text-slate-650 dark:text-slate-300 cursor-pointer">
                      <input
                        type="checkbox"
                        checked={selectedWorkflow.transitions[selectedTransitionIndex].useCustomCommand}
                        onChange={(e) => {
                          const transitions = [...selectedWorkflow.transitions];
                          transitions[selectedTransitionIndex].useCustomCommand = e.target.checked;
                          handleUpdateSelectedWorkflow({ transitions });
                        }}
                        className="rounded border-slate-300 dark:border-slate-800 text-sky-600 focus:ring-sky-500"
                      />
                      Use Custom C# Command
                    </label>
                  </div>

                  <div className="flex flex-col gap-1">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Trigger Command Name</label>
                    <input
                      type="text"
                      value={selectedWorkflow.transitions[selectedTransitionIndex].trigger}
                      onChange={(e) => {
                        const transitions = [...selectedWorkflow.transitions];
                        transitions[selectedTransitionIndex].trigger = e.target.value;
                        handleUpdateSelectedWorkflow({ transitions });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-mono transition-colors duration-200"
                    />
                  </div>

                  <div className="flex flex-col gap-2">
                    <label className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Required Roles</label>
                    <input
                      type="text"
                      placeholder="Roles allowed to transition"
                      value={selectedWorkflow.transitions[selectedTransitionIndex].requiredRoles.join(', ')}
                      onChange={(e) => {
                        const transitions = [...selectedWorkflow.transitions];
                        transitions[selectedTransitionIndex].requiredRoles = e.target.value.split(',').map(s => s.trim()).filter(Boolean);
                        handleUpdateSelectedWorkflow({ transitions });
                      }}
                      className="px-2.5 py-1.5 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-medium transition-colors duration-200"
                    />
                  </div>

                  {/* Conditions List */}
                  <div className="border-t border-slate-200 dark:border-slate-800/80 pt-4 flex flex-col gap-3">
                    <div className="flex justify-between items-center">
                      <span className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Guard Conditions</span>
                      <button
                        onClick={() => {
                          const transitions = [...selectedWorkflow.transitions];
                          const conditions = [...transitions[selectedTransitionIndex].conditions, {
                            type: 'PropertyComparison',
                            property: '',
                            operator: 'Equal',
                            value: ''
                          } as WorkflowCondition];
                          transitions[selectedTransitionIndex].conditions = conditions;
                          handleUpdateSelectedWorkflow({ transitions });
                        }}
                        className="p-1 hover:text-slate-800 dark:hover:text-white text-slate-400 dark:text-slate-500 bg-slate-55 dark:bg-slate-950/40 rounded border border-slate-200 dark:border-slate-800 transition-all cursor-pointer"
                        title="Add Condition Guard"
                      >
                        <Plus className="w-3.5 h-3.5" />
                      </button>
                    </div>

                    <div className="flex flex-col gap-2">
                      {selectedWorkflow.transitions[selectedTransitionIndex].conditions.map((cond, cIdx) => (
                        <div key={cIdx} className="bg-slate-50 dark:bg-slate-950/50 border border-slate-200 dark:border-slate-800/60 p-2 rounded flex flex-col gap-1.5 text-[10px] transition-colors duration-200">
                          <div className="flex justify-between items-center">
                            <span className="font-mono text-sky-600 dark:text-sky-400">Rule #{cIdx + 1}</span>
                            <button
                              onClick={() => {
                                const transitions = [...selectedWorkflow.transitions];
                                transitions[selectedTransitionIndex].conditions = transitions[selectedTransitionIndex].conditions.filter((_, i) => i !== cIdx);
                                handleUpdateSelectedWorkflow({ transitions });
                              }}
                              className="text-slate-405 dark:text-slate-500 hover:text-red-500 dark:hover:text-red-400 p-0.5 rounded transition-all cursor-pointer"
                            >
                              <Trash2 className="w-3 h-3" />
                            </button>
                          </div>

                          <input
                            type="text"
                            placeholder="Property Name"
                            value={cond.property}
                            onChange={(e) => {
                              const transitions = [...selectedWorkflow.transitions];
                              transitions[selectedTransitionIndex].conditions[cIdx].property = e.target.value;
                              handleUpdateSelectedWorkflow({ transitions });
                            }}
                            className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-mono transition-colors duration-200"
                          />

                          <select
                            value={cond.operator}
                            onChange={(e) => {
                              const transitions = [...selectedWorkflow.transitions];
                              transitions[selectedTransitionIndex].conditions[cIdx].operator = e.target.value as any;
                              handleUpdateSelectedWorkflow({ transitions });
                            }}
                            className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white transition-colors duration-200"
                          >
                            <option value="Equal">==</option>
                            <option value="NotEqual">!=</option>
                            <option value="LessThan">&lt;</option>
                            <option value="LessThanOrEqual">&lt;=</option>
                            <option value="GreaterThan">&gt;</option>
                            <option value="GreaterThanOrEqual">&gt;=</option>
                          </select>

                          <input
                            type="text"
                            placeholder="Comparison Value"
                            value={cond.value}
                            onChange={(e) => {
                              const transitions = [...selectedWorkflow.transitions];
                              transitions[selectedTransitionIndex].conditions[cIdx].value = e.target.value;
                              handleUpdateSelectedWorkflow({ transitions });
                            }}
                            className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-850 dark:text-white font-mono transition-colors duration-200"
                          />
                        </div>
                      ))}
                    </div>
                  </div>

                  {/* Actions List */}
                  <div className="border-t border-slate-200 dark:border-slate-800/80 pt-4 flex flex-col gap-3">
                    <div className="flex justify-between items-center">
                      <span className="text-[10px] font-semibold text-slate-500 dark:text-slate-400 uppercase tracking-wider">Automated Actions (DAG)</span>
                      <div className="flex gap-1">
                        <button
                          onClick={() => {
                            const transitions = [...selectedWorkflow.transitions];
                            const actions = [...transitions[selectedTransitionIndex].actions, {
                              type: 'InternalApi',
                              requestType: 'NotifyUserCommand',
                              payloadTemplate: '{ "message": "Order approved" }'
                            } as WorkflowAction];
                            transitions[selectedTransitionIndex].actions = actions;
                            handleUpdateSelectedWorkflow({ transitions });
                          }}
                          className="px-1.5 py-0.5 hover:text-slate-800 dark:hover:text-white text-slate-400 dark:text-slate-500 bg-slate-50 dark:bg-slate-950/40 rounded border border-slate-200 dark:border-slate-800 transition-all text-[9px] font-mono cursor-pointer animate-pulse"
                          title="Add Internal MediatR Command Action"
                        >
                          + Internal
                        </button>
                        <button
                          onClick={() => {
                            const transitions = [...selectedWorkflow.transitions];
                            const actions = [...transitions[selectedTransitionIndex].actions, {
                              type: 'ExternalApi',
                              method: 'POST',
                              url: 'https://api.thirdparty.com',
                              headers: {},
                              bodyTemplate: '{}'
                            } as WorkflowAction];
                            transitions[selectedTransitionIndex].actions = actions;
                            handleUpdateSelectedWorkflow({ transitions });
                          }}
                          className="px-1.5 py-0.5 hover:text-slate-800 dark:hover:text-white text-slate-400 dark:text-slate-500 bg-slate-50 dark:bg-slate-950/40 rounded border border-slate-200 dark:border-slate-800 transition-all text-[9px] font-mono cursor-pointer animate-pulse"
                          title="Add External HTTP Action"
                        >
                          + External
                        </button>
                      </div>
                    </div>

                    <div className="flex flex-col gap-2">
                      {selectedWorkflow.transitions[selectedTransitionIndex].actions.map((act, aIdx) => (
                        <div key={aIdx} className="bg-slate-50 dark:bg-slate-950/50 border border-slate-200 dark:border-slate-800/60 p-2 rounded flex flex-col gap-1.5 text-[10px] transition-colors duration-200">
                          <div className="flex justify-between items-center">
                            <span className="font-mono text-emerald-605 dark:text-emerald-400">Action #{aIdx + 1} ({act.type})</span>
                            <button
                              onClick={() => {
                                const transitions = [...selectedWorkflow.transitions];
                                transitions[selectedTransitionIndex].actions = transitions[selectedTransitionIndex].actions.filter((_, i) => i !== aIdx);
                                handleUpdateSelectedWorkflow({ transitions });
                              }}
                              className="text-slate-400 dark:text-slate-500 hover:text-red-500 dark:hover:text-red-400 p-0.5 rounded transition-all cursor-pointer"
                            >
                              <Trash2 className="w-3.5 h-3.5" />
                            </button>
                          </div>

                          {act.type === 'InternalApi' ? (
                            <>
                              <input
                                type="text"
                                placeholder="Request Type"
                                value={act.requestType}
                                onChange={(e) => {
                                  const transitions = [...selectedWorkflow.transitions];
                                  const currentAct = transitions[selectedTransitionIndex].actions[aIdx] as InternalApiAction;
                                  currentAct.requestType = e.target.value;
                                  handleUpdateSelectedWorkflow({ transitions });
                                }}
                                className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-mono transition-colors duration-200"
                              />
                              <textarea
                                placeholder="Payload template (JSON)"
                                value={act.payloadTemplate}
                                onChange={(e) => {
                                  const transitions = [...selectedWorkflow.transitions];
                                  const currentAct = transitions[selectedTransitionIndex].actions[aIdx] as InternalApiAction;
                                  currentAct.payloadTemplate = e.target.value;
                                  handleUpdateSelectedWorkflow({ transitions });
                                }}
                                className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-mono h-16 resize-none transition-colors duration-200"
                              />
                            </>
                          ) : (
                            <>
                              <div className="flex gap-1.5">
                                <select
                                  value={act.method}
                                  onChange={(e) => {
                                    const transitions = [...selectedWorkflow.transitions];
                                    const currentAct = transitions[selectedTransitionIndex].actions[aIdx] as ExternalApiAction;
                                    currentAct.method = e.target.value;
                                    handleUpdateSelectedWorkflow({ transitions });
                                  }}
                                  className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white transition-colors duration-200"
                                >
                                  <option value="GET">GET</option>
                                  <option value="POST">POST</option>
                                  <option value="PUT">PUT</option>
                                  <option value="DELETE">DELETE</option>
                                </select>
                                <input
                                  type="text"
                                  placeholder="HTTP URL"
                                  value={act.url}
                                  onChange={(e) => {
                                    const transitions = [...selectedWorkflow.transitions];
                                    const currentAct = transitions[selectedTransitionIndex].actions[aIdx] as ExternalApiAction;
                                    currentAct.url = e.target.value;
                                    handleUpdateSelectedWorkflow({ transitions });
                                  }}
                                  className="flex-1 px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-mono transition-colors duration-200"
                                />
                              </div>
                              <textarea
                                placeholder="HTTP Body template (JSON)"
                                value={act.bodyTemplate}
                                onChange={(e) => {
                                  const transitions = [...selectedWorkflow.transitions];
                                  const currentAct = transitions[selectedTransitionIndex].actions[aIdx] as ExternalApiAction;
                                  currentAct.bodyTemplate = e.target.value;
                                  handleUpdateSelectedWorkflow({ transitions });
                                }}
                                className="px-2 py-1 bg-white dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded focus:outline-none focus:border-sky-500 text-slate-800 dark:text-white font-mono h-16 resize-none transition-colors duration-200"
                              />
                            </>
                          )}
                        </div>
                      ))}
                    </div>
                  </div>
                </div>
              </div>
            </>
          )}
        </div>
      )}
    </div>
  );
};
