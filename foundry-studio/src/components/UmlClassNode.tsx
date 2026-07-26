import React from 'react';
import { Handle, Position } from '@xyflow/react';
import type { Property } from '../types';

export const UmlClassNode: React.FC<any> = ({ data, selected }) => {
  const entity = data?.entity || { name: 'UnnamedClass', properties: [] };
  const entityName = entity.name || 'UnnamedClass';
  const properties = entity.properties || [];

  // Parse stereotypes
  const getStereotype = () => {
    const nameLower = entityName.toLowerCase();
    if (nameLower.endsWith('dto')) return '«data transfer object»';
    if (nameLower.endsWith('request')) return '«request model»';
    if (nameLower.endsWith('command')) return '«command payload»';
    return '«entity»';
  };

  // Determine traits to render as badges in header
  const getTraitBadges = () => {
    const badges: string[] = [];
    const nameLower = entityName.toLowerCase();
    if (nameLower.includes('audit') || nameLower.includes('log')) badges.push('Auditable');
    if (properties.some((p: any) => p?.attributes?.some((a: any) => ['Encrypt', 'Mask', 'MaskEmail'].includes(a)))) {
      badges.push('Secured');
    }
    if (properties.some((p: any) => p?.name === 'Id' && p?.type === 'Guid')) badges.push('DDD Entity');
    return badges;
  };

  const getVisibilitySymbol = (isPublic: boolean) => {
    return isPublic ? '+' : '-';
  };

  const getPropertyBadges = (prop: Property) => {
    const badges: string[] = [];
    if (prop?.isKey || prop?.name === 'Id') badges.push('[Key]');
    if (prop?.attributes && prop.attributes.length > 0) {
      prop.attributes.forEach((attr: any) => {
        badges.push(`[${attr}]`);
      });
    }
    return badges;
  };

  const defaultMethods = [
    '+ SaveAsync()',
    '+ UpdateAsync()',
    '+ DeleteAsync()'
  ];

  const methods = defaultMethods;

  return (
    <div 
      className={`relative rounded-xl border-2 transition-all shadow-xl min-w-[240px] overflow-hidden ${
        selected 
          ? 'border-sky-500 shadow-lg ring-1 ring-sky-500 bg-white dark:bg-slate-900 scale-[1.02] shadow-sky-500/10' 
          : 'border-slate-200 dark:border-slate-800 bg-white/80 dark:bg-slate-900/60 backdrop-blur-md shadow-black/5 dark:shadow-black/40 hover:border-slate-300 dark:hover:border-slate-700 hover:bg-white/90 dark:hover:bg-slate-900/85'
      }`}
    >
      {/* Header */}
      <div className="flex flex-col items-center bg-gradient-to-b from-slate-50 to-slate-100/95 dark:from-slate-800 dark:to-slate-900/95 p-3 border-b border-slate-200 dark:border-slate-800">
        <div className="text-[9px] uppercase font-bold tracking-widest text-sky-550 dark:text-sky-400 font-mono mb-0.5">{getStereotype()}</div>
        <div className="font-bold text-sm text-slate-800 dark:text-white">{entityName}</div>
        <div className="flex flex-wrap justify-center gap-1 mt-1.5">
          {getTraitBadges().map((badge: string, index: number) => (
            <span key={index} className="text-[8px] font-bold bg-indigo-50 dark:bg-indigo-950/45 border border-indigo-200 dark:border-indigo-900/30 text-indigo-650 dark:text-indigo-300 px-1.5 py-0.5 rounded">
              {badge}
            </span>
          ))}
        </div>
      </div>

      {/* Attributes */}
      <div className="p-3 bg-slate-50/50 dark:bg-slate-900/30">
        <div className="text-[9px] font-bold text-slate-400 dark:text-slate-500 uppercase tracking-widest mb-2 font-mono">Attributes</div>
        <div className="flex flex-col gap-2 text-xs">
          {properties.map((prop: Property, index: number) => (
            <div key={index} className="flex flex-col bg-white dark:bg-slate-950/30 p-2 border border-slate-200 dark:border-slate-800/50 rounded-lg shadow-sm dark:shadow-none">
              <div className="flex justify-between items-center">
                <div className="flex gap-1.5 items-center">
                  <span className="text-sky-500 font-mono font-bold text-xs">{getVisibilitySymbol(true)}</span>
                  <span className="text-slate-800 dark:text-slate-100 font-semibold">{prop?.name || 'Unnamed'}</span>
                </div>
                <span className="text-sky-600 dark:text-sky-400 font-mono text-[10px] font-medium">{prop?.type || 'string'}</span>
              </div>
              {getPropertyBadges(prop).length > 0 && (
                <div className="flex flex-wrap gap-1 mt-1.5">
                  {getPropertyBadges(prop).map((badge: string, badgeIndex: number) => {
                    let badgeClass = "bg-slate-100 dark:bg-slate-800/80 text-slate-500 dark:text-slate-400 border-slate-200 dark:border-slate-700/50";
                    if (badge === '[Key]') badgeClass = "bg-amber-50 dark:bg-amber-950/40 text-amber-700 dark:text-amber-300 border-amber-200 dark:border-amber-900/30";
                    else if (badge === '[Required]') badgeClass = "bg-sky-50 dark:bg-sky-950/40 text-sky-700 dark:text-sky-300 border-sky-200 dark:border-sky-900/30";
                    else if (badge === '[Unique]') badgeClass = "bg-teal-50 dark:bg-teal-950/40 text-teal-700 dark:text-teal-300 border-teal-200 dark:border-teal-900/30";
                    else if (['[Encrypt]', '[Mask]', '[MaskEmail]'].includes(badge)) badgeClass = "bg-rose-50 dark:bg-rose-950/40 text-rose-700 dark:text-rose-300 border-rose-200 dark:border-rose-900/30";
                    return (
                      <span key={badgeIndex} className={`text-[8px] font-bold px-1.5 py-0.5 rounded border ${badgeClass}`}>
                        {badge.replace('[', '').replace(']', '')}
                      </span>
                    );
                  })}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>

      {/* Methods */}
      <div className="border-t border-slate-200 dark:border-slate-800 p-3 bg-slate-50/30 dark:bg-slate-900/10">
        <div className="text-[9px] font-bold text-slate-400 dark:text-slate-500 uppercase tracking-widest mb-1.5 font-mono">Repository APIs</div>
        <div className="flex flex-col gap-1 text-[11px] font-mono text-slate-600 dark:text-slate-400">
          {methods.map((method: string, index: number) => (
            <div key={index} className="flex items-center gap-1.5 hover:text-slate-800 dark:hover:text-slate-200 transition-colors">
              <span className="text-emerald-500 font-bold">+</span>
              <span>{method.substring(2)}</span>
            </div>
          ))}
        </div>
      </div>

      {/* Handles */}
      <Handle type="target" position={Position.Top} id="t" className="!w-3 !h-3 !bg-sky-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-sky-400 transition-all hover:scale-125" />
      <Handle type="source" position={Position.Bottom} id="b" className="!w-3 !h-3 !bg-indigo-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-indigo-400 transition-all hover:scale-125" />
      <Handle type="target" position={Position.Left} id="l" className="!w-3 !h-3 !bg-sky-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-sky-400 transition-all hover:scale-125" />
      <Handle type="source" position={Position.Right} id="r" className="!w-3 !h-3 !bg-indigo-500 !border-2 !border-white dark:!border-slate-950 rounded-full hover:!bg-indigo-400 transition-all hover:scale-125" />
    </div>
  );
};

export default UmlClassNode;