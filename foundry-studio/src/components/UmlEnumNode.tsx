import React from 'react';
import { Handle, Position, type NodeProps } from '@xyflow/react';
import type { EnumNode } from '../types';

export const UmlEnumNode: React.FC<NodeProps<EnumNode>> = ({ data, selected }) => {
  const enumData = data?.enum || { name: 'UnnamedEnum', values: [] };
  const enumName = enumData.name || 'UnnamedEnum';
  const values = enumData.values || [];

  return (
    <div 
      className={`relative min-w-[220px] rounded-xl border-2 transition-all shadow-xl overflow-hidden ${
        selected 
          ? 'border-purple-500 shadow-lg ring-1 ring-purple-500 bg-white dark:bg-slate-900 scale-[1.02] shadow-purple-500/10' 
          : 'border-slate-200 dark:border-slate-800 bg-white/80 dark:bg-slate-900/60 backdrop-blur-md shadow-black/5 dark:shadow-black/40 hover:border-slate-300 dark:hover:border-slate-700 hover:bg-white/90 dark:hover:bg-slate-900/85'
      }`}
    >
      {/* Header compartment */}
      <div className="bg-gradient-to-b from-slate-50 to-slate-100/95 dark:from-slate-800 dark:to-slate-900/95 p-3 border-b border-slate-200 dark:border-slate-800">
        <div className="text-center text-[9px] uppercase font-bold tracking-widest text-purple-600 dark:text-purple-400 font-mono mb-0.5">«Enum»</div>
        <div className="text-center font-bold text-sm text-slate-800 dark:text-white">{enumName}</div>
      </div>
      
      {/* Values compartment */}
      <div className="p-3 bg-slate-50/50 dark:bg-slate-900/30">
        {values.length > 0 ? (
          <ul className="list-none text-xs text-slate-700 dark:text-slate-300 font-mono flex flex-col gap-1">
            {values.map((value, index) => (
              <li key={index} className="px-2 py-1 bg-white dark:bg-slate-950/40 border border-slate-200 dark:border-slate-800/50 rounded-lg text-slate-700 dark:text-slate-300 hover:text-slate-900 dark:hover:text-slate-100 hover:bg-slate-100 dark:hover:bg-slate-950/80 transition-all shadow-sm dark:shadow-none">{value}</li>
            ))}
          </ul>
        ) : (
          <div className="text-center text-xs text-slate-400 dark:text-slate-500 italic py-2">No values</div>
        )}
      </div>
      
      {/* Handles */}
      <Handle type="target" position={Position.Top} id="t" className="!w-3 !h-3 !bg-sky-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-sky-400 transition-all hover:scale-125" />
      <Handle type="source" position={Position.Bottom} id="b" className="!w-3 !h-3 !bg-indigo-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-indigo-400 transition-all hover:scale-125" />
      <Handle type="target" position={Position.Left} id="l" className="!w-3 !h-3 !bg-sky-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-sky-400 transition-all hover:scale-125" />
      <Handle type="source" position={Position.Right} id="r" className="!w-3 !h-3 !bg-indigo-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-indigo-400 transition-all hover:scale-125" />
    </div>
  );
};