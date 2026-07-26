import React, { useState, useMemo } from 'react';
import { useStore } from '../store';
import type { Entity, Property, Enum } from '../types';
import { AlertTriangle, Info, CheckCircle2, ChevronDown, ChevronUp, ShieldAlert } from 'lucide-react';

export interface DiagnosticItem {
  id: string;
  severity: 'error' | 'warning' | 'info';
  category: 'Schema' | 'API' | 'Workflow';
  message: string;
}

export const DiagnosticsPanel: React.FC = () => {
  const [isOpen, setIsOpen] = useState(false);
  const { nodes, edges, customEndpoints, workflows } = useStore();

  const diagnostics = useMemo<DiagnosticItem[]>(() => {
    const items: DiagnosticItem[] = [];

    // 1. Schema Validations
    const classNodes = nodes.filter((n) => n.type === 'classNode');
    classNodes.forEach((node) => {
      const entity: Entity = (node.data as any).entity || node.data;
      if (!entity?.properties?.some((p: Property) => p.isKey)) {
        items.push({
          id: `no-key-${node.id}`,
          severity: 'error',
          category: 'Schema',
          message: `Entity '${entity?.name || node.id}' has no Key ([Key]) property defined.`,
        });
      }
      if (entity?.realTime && (!entity.realTimeRoles || entity.realTimeRoles.length === 0)) {
        items.push({
          id: `rt-roles-${node.id}`,
          severity: 'info',
          category: 'Schema',
          message: `Entity '${entity.name}' has RealTime enabled with open (public) subscriber access.`,
        });
      }
    });

    const enumNodes = nodes.filter((n) => n.type === 'enumNode');
    enumNodes.forEach((node) => {
      const enumDef: Enum = (node.data as any).enum || node.data;
      if (!enumDef?.values || enumDef.values.length === 0) {
        items.push({
          id: `enum-empty-${node.id}`,
          severity: 'warning',
          category: 'Schema',
          message: `Enum '${enumDef?.name || node.id}' has no values defined.`,
        });
      }
    });

    // 2. Custom Endpoint Validations
    customEndpoints.forEach((ep) => {
      if (!ep.targetEntity && ep.operationType !== 'Custom') {
        items.push({
          id: `ep-no-target-${ep.requestType}`,
          severity: 'warning',
          category: 'API',
          message: `Custom Endpoint '${ep.requestType}' is missing a target entity binding.`,
        });
      }
    });

    // 3. Workflow Validations
    workflows.forEach((wf) => {
      wf.states.forEach((st) => {
        const hasTransition = wf.transitions.some(
          (tr) => tr.fromState === st.name || tr.toState === st.name
        );
        if (!hasTransition && !st.isInitial) {
          items.push({
            id: `wf-unlinked-${wf.id}-${st.name}`,
            severity: 'warning',
            category: 'Workflow',
            message: `Workflow '${wf.name}' state '${st.name}' is unlinked with no transitions.`,
          });
        }
      });
    });

    return items;
  }, [nodes, edges, customEndpoints, workflows]);

  const errorCount = diagnostics.filter((d) => d.severity === 'error').length;
  const warningCount = diagnostics.filter((d) => d.severity === 'warning').length;

  return (
    <div className="absolute bottom-3 left-1/2 -translate-x-1/2 z-20 w-[90%] max-w-3xl bg-white/95 dark:bg-slate-900/95 backdrop-blur border border-slate-200 dark:border-slate-800 rounded-lg shadow-xl overflow-hidden transition-all">
      {/* Header Bar */}
      <button
        onClick={() => setIsOpen(!isOpen)}
        className="w-full flex items-center justify-between px-4 py-2 bg-slate-100/80 dark:bg-slate-950/80 hover:bg-slate-200/80 dark:hover:bg-slate-900 text-xs font-semibold text-slate-700 dark:text-slate-200 cursor-pointer select-none"
      >
        <div className="flex items-center gap-3">
          <ShieldAlert className="w-4 h-4 text-sky-500" />
          <span>Schema & System Diagnostics</span>
          <div className="flex items-center gap-2">
            {errorCount > 0 ? (
              <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-red-500/20 text-red-600 dark:text-red-400 border border-red-500/30">
                {errorCount} {errorCount === 1 ? 'Error' : 'Errors'}
              </span>
            ) : (
              <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-emerald-500/20 text-emerald-600 dark:text-emerald-400 border border-emerald-500/30 flex items-center gap-1">
                <CheckCircle2 className="w-3 h-3" /> Valid
              </span>
            )}
            {warningCount > 0 && (
              <span className="px-2 py-0.5 rounded-full text-[10px] font-bold bg-amber-500/20 text-amber-600 dark:text-amber-400 border border-amber-500/30">
                {warningCount} {warningCount === 1 ? 'Warning' : 'Warnings'}
              </span>
            )}
          </div>
        </div>

        <div className="flex items-center gap-1 text-slate-400">
          {isOpen ? <ChevronDown className="w-4 h-4" /> : <ChevronUp className="w-4 h-4" />}
        </div>
      </button>

      {/* Expanded Details List */}
      {isOpen && (
        <div className="max-h-48 overflow-y-auto p-3 flex flex-col gap-2 divide-y divide-slate-100 dark:divide-slate-800/60">
          {diagnostics.length === 0 ? (
            <div className="text-xs text-slate-500 dark:text-slate-400 text-center py-4 flex items-center justify-center gap-2">
              <CheckCircle2 className="w-4 h-4 text-emerald-500" />
              <span>No design diagnostics or warnings detected. All entities and workflows are valid!</span>
            </div>
          ) : (
            diagnostics.map((item) => (
              <div key={item.id} className="pt-2 first:pt-0 flex items-start gap-2.5 text-xs">
                {item.severity === 'error' && (
                  <AlertTriangle className="w-4 h-4 text-red-500 shrink-0 mt-0.5" />
                )}
                {item.severity === 'warning' && (
                  <AlertTriangle className="w-4 h-4 text-amber-500 shrink-0 mt-0.5" />
                )}
                {item.severity === 'info' && (
                  <Info className="w-4 h-4 text-sky-500 shrink-0 mt-0.5" />
                )}
                <div className="flex-1">
                  <div className="flex items-center gap-2">
                    <span className="font-semibold text-[10px] uppercase tracking-wider px-1.5 py-0.2 rounded bg-slate-100 dark:bg-slate-800 text-slate-600 dark:text-slate-300">
                      {item.category}
                    </span>
                    <span className="text-slate-800 dark:text-slate-200 font-medium">{item.message}</span>
                  </div>
                </div>
              </div>
            ))
          )}
        </div>
      )}
    </div>
  );
};
