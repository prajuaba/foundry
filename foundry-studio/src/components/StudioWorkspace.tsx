import React, { useState, useCallback, useMemo, useRef, type DragEvent } from 'react';
import {
  ReactFlow,
  Controls,
  Background,
  MiniMap,
  type NodeTypes,
  type EdgeTypes,
  ConnectionLineType,
  useReactFlow
} from '@xyflow/react';
import { useStore } from '../store';
import { deriveApiManifest } from '../manifest';
import type { ClassNode } from '../types';
import UmlClassNode from './UmlClassNode';
import { UmlEnumNode } from './UmlEnumNode';
import { OrthogonalEdge } from './OrthogonalEdge';
import { InspectorPanel } from './InspectorPanel';
import { ApiDesigner } from './ApiDesigner';
import { WorkflowDesigner } from './WorkflowDesigner';
import { ApiPlayground } from './ApiPlayground';
import { DiagnosticsPanel } from './DiagnosticsPanel';
import { Play, Download, Upload, Code, Plus, Check, Settings, Sparkles, Database, Globe, Plug, Undo2, Redo2, Sun, Moon, GitBranch, Terminal } from 'lucide-react';
import { initVsCodeBridge, isVsCodeEnvironment, postMessageToVsCode } from '../vscode';

const nodeTypes: NodeTypes = {
  classNode: UmlClassNode,
  enumNode: UmlEnumNode,
  domainGroup: ({ data }: any) => (
    <div
      style={{
        width: data.width || 400,
        height: data.height || 300,
        border: '2px dashed',
        borderColor: data.color || '#64748b',
        borderRadius: 12,
        background: (data.color || '#64748b') + '08',
        padding: 12,
        pointerEvents: 'none',
      }}
    >
      <div style={{ fontSize: 11, fontWeight: 700, textTransform: 'uppercase', letterSpacing: '0.05em', color: data.color || '#64748b', pointerEvents: 'auto' }}>
        {data.label || 'Domain Group'}
      </div>
    </div>
  ),
};

const edgeTypes: EdgeTypes = {
  orthogonal: OrthogonalEdge,
};

// Frontend implementation of C# code generator to allow direct export from schema JSON keys (Capitalized)
const generateCsCode = (entity: any, namespace: string) => {
  const keyProperty = entity.Properties.find((p: any) => p.IsKey);
  const keyType = keyProperty?.Type || 'ObjectId';
  
  const interfaces = [];
  if (entity.BaseClass) {
    interfaces.push(entity.BaseClass);
  } else {
    interfaces.push('BaseEntity<' + keyType + '>');
  }
  interfaces.push('IVersionable');
  if (entity.SoftDelete) interfaces.push('ISoftDelete');
  
  const interfaceList = interfaces.join(', ');
  const propertiesLines: string[] = [];

  entity.Properties.forEach((prop: any) => {
    if (prop.IsKey) return;
    
    const type = prop.Type;
    const requiredKeyword = prop.Attributes.includes('Required') ? 'required ' : '';
    const initKeyword = 'get; init';
    
    const attributes: string[] = [];
    if (prop.Attributes.includes('UniqueIndex') || prop.Attributes.includes('Unique')) {
      attributes.push('[Indexed(Unique = true)]');
    } else if (prop.Attributes.includes('Index')) {
      attributes.push('[Indexed]');
    }
    
    if (prop.Attributes.includes('TextIndex')) attributes.push('[TextIndexed]');
    if (prop.Attributes.includes('Encrypt')) attributes.push('[SensitiveData(Protection = ProtectionType.Encrypt)]');
    if (prop.Attributes.includes('Mask')) attributes.push('[SensitiveData(Protection = ProtectionType.Mask)]');
    if (prop.Attributes.includes('MaskEmail')) attributes.push('[SensitiveData(Protection = ProtectionType.Mask, MaskingType = MaskingType.Email)]');

    const attrPrefix = attributes.length > 0 ? '    ' + attributes.join('\n    ') + '\n' : '';
    let defaultValue = '';
    if (type === 'string') defaultValue = ' = string.Empty;';
    else if (type === 'bool') defaultValue = ' = false;';
    else if (['int', 'decimal', 'double', 'float'].includes(type)) defaultValue = ' = 0;';
    else if (prop.IsEnum) defaultValue = ` = default(${type});`;

    propertiesLines.push(`${attrPrefix}    public ${requiredKeyword}${type} ${prop.Name} { ${initKeyword}; }${defaultValue}`);
  });

  if (entity.SoftDelete) {
    propertiesLines.push('    [Indexed]\n    public bool IsDeleted { get; init; } = false;');
    propertiesLines.push('    public DateTime? DeletedAt { get; init; }');
  }

  const propertiesBody = propertiesLines.length > 0 ? '\n' + propertiesLines.join('\n\n') + '\n' : '';

  return `using System;
using MongoDB.Bson;
using FoundryMongo.Domain.Entities;
using FoundryMongo.Domain.Filters;

namespace ${namespace};

public record ${entity.Name} : ${interfaceList}
{${propertiesBody}}`;
};

const generateEnumCode = (enumDef: any, namespace: string) => {
  const values = enumDef.Values.join(',\n    ');
  return `namespace ${namespace};

public enum ${enumDef.Name}
{
    ${values}
}`;
};

const generateDtoCsCode = (dto: any, namespace: string) => {
  const propertiesLines: string[] = [];
  (dto.Properties || dto.properties || []).forEach((prop: any) => {
    const requiredKeyword = prop.IsRequired || prop.isRequired ? 'required ' : '';
    const initKeyword = 'get; init;';
    let defaultValue = '';
    if (prop.Type === 'string' || prop.type === 'string') defaultValue = ' = string.Empty;';
    else if (prop.Type === 'bool' || prop.type === 'bool') defaultValue = ' = false;';
    else if (['int', 'decimal', 'double', 'float'].includes(prop.Type || prop.type)) defaultValue = ' = 0;';

    const attributes: string[] = [];
    const attrs = prop.Attributes || prop.attributes || [];
    if (attrs.includes('Email')) attributes.push('[EmailAddress]');
    if (attrs.includes('Phone')) attributes.push('[Phone]');
    if (attrs.includes('Url')) attributes.push('[Url]');
    const attrPrefix = attributes.length > 0 ? '    ' + attributes.join('\n    ') + '\n' : '';

    propertiesLines.push(`${attrPrefix}    public ${requiredKeyword}${prop.Type || prop.type} ${prop.Name || prop.name} { ${initKeyword} }${defaultValue}`);
  });

  const propertiesBody = propertiesLines.length > 0 ? '\n' + propertiesLines.join('\n\n') + '\n' : '';

  return `using System;
using System.ComponentModel.DataAnnotations;
using MongoDB.Bson;

namespace ${namespace};

public record ${dto.Name || dto.name}
{${propertiesBody}}`;
};

const generateHandlerCsCode = (ep: any, namespace: string) => {
  const reqType = ep.RequestType || ep.requestType;
  const targetEntity = ep.TargetEntity || ep.targetEntity;
  const operationType = ep.OperationType || ep.operationType || 'Custom';
  const filterField = ep.FilterField || ep.filterField;
  const filterSourceValue = ep.FilterSourceValue || ep.filterSourceValue;
  const assignments = ep.Assignments || ep.assignments || [];

  const handlerName = reqType + "Handler";
  const responseType = ep.Method === 'GET' || ep.method === 'GET' ? (reqType.replace("Query", "Response").replace("Request", "Response")) : "bool";
  const repoType = targetEntity ? `IRepository<${targetEntity}>` : null;

  let body = "";
  if (operationType === 'Query') {
    body = `        var items = await _repository.FindAsync(
            x => x.${filterField || 'Id'} == request.${filterSourceValue || 'Id'},
            cancellationToken: cancellationToken);

        return new ${responseType}
        {
            // Auto-projected from ${targetEntity} records
            Items = items.Select(x => new ${targetEntity}Dto
            {
                // Property mappings appear here
            }).ToList()
        };`;
  } else if (operationType === 'Update') {
    body = `        var entity = await _repository.GetByIdAsync(request.${filterSourceValue || 'Id'});
        if (entity == null)
        {
            return false;
        }

        // Apply visual assignments
` + assignments.map((a: any) => `        entity.${a.entityProperty || a.EntityProperty} = request.${a.sourceValue || a.SourceValue};`).join('\n') + `

        await _repository.UpdateAsync(entity);
        return true;`;
  } else if (operationType === 'Insert') {
    body = `        var entity = new ${targetEntity}
        {
            // Auto-mapped from request payload properties
        };

        await _repository.AddAsync(entity);
        return true;`;
  } else {
    body = `        // Write your custom MediatR query/command logic here
        throw new NotImplementedException("Custom logic handler.");`;
  }

  const constructor = repoType ? `    private readonly ${repoType} _repository;

    public ${handlerName}(${repoType} repository)
    {
        _repository = repository;
    }
` : `    public ${handlerName}()
    {
    }`;

  return `using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediatR;
using MongoDB.Bson;
using FoundryCore.Entities;
using FoundryMongo.Repositories;
using ${namespace};

namespace ${namespace}.Handlers;

public class ${handlerName} : IRequestHandler<${reqType}, ${responseType}>
{
${constructor}
    public async Task<${responseType}> Handle(${reqType} request, CancellationToken cancellationToken)
    {
${body}
    }
}`;
};

export const StudioWorkspace: React.FC = () => {
  const { 
    namespace, 
    nodes, 
    edges, 
    activeTool,
    onNodesChange,
    onEdgesChange,
    onConnect,
    setActiveTool,
    setNamespace,
    addClassNode,
    addEnumNode,
    exportToSchema,
    importFromSchema,
    exportProject,
    importProject,
    ollamaHost,
    ollamaModel,
    customEndpoints,
    undo,
    redo,
    canUndo,
    canRedo
  } = useStore();
  const { fitView } = useReactFlow();

  const [aiPrompt, setAiPrompt] = useState<string>('');
  const [isAiLoading, setIsAiLoading] = useState<boolean>(false);
  const [aiError, setAiError] = useState<string | null>(null);
  const [showPreviewModal, setShowPreviewModal] = useState<boolean>(false);
  const [previewTab, setPreviewTab] = useState<'JSON' | 'C#'>('JSON');
  const [selectedPreviewFile, setSelectedPreviewFile] = useState<string>('');
  const [outputPath, setOutputPath] = useState<string>('./exported-pocos');
  const [manifestPath, setManifestPath] = useState<string>('../../samples/Foundry.Api.Sample/api-manifest.json');
  const [saveMessage, setSaveMessage] = useState<string | null>(null);
  const [isSaving, setIsSaving] = useState<boolean>(false);
  const [activeView, setActiveView] = useState<'schema' | 'api' | 'workflow' | 'playground'>('schema');
  const [isFullScreen, setIsFullScreen] = useState<boolean>(false);
  const [theme, setTheme] = useState<'light' | 'dark'>(() => {
    const saved = localStorage.getItem('theme');
    if (saved === 'light' || saved === 'dark') return saved;
    return 'dark';
  });

  React.useEffect(() => {
    const root = window.document.documentElement;
    if (theme === 'dark') {
      root.classList.add('dark');
    } else {
      root.classList.remove('dark');
    }
    localStorage.setItem('theme', theme);
  }, [theme]);

  React.useEffect(() => {
    if (!isVsCodeEnvironment()) return;

    const cleanup = initVsCodeBridge({
      onLoadProject: (projectData) => {
        if (projectData) {
          importProject(projectData);
        }
      },
      onCompileRequest: () => {
        const projectData = exportProject();
        postMessageToVsCode({ type: 'compileSchema', schema: projectData });
      }
    });

    return cleanup;
  }, [importProject, exportProject]);

  React.useEffect(() => {
    if (isVsCodeEnvironment() && nodes.length > 0) {
      const projectData = exportProject();
      postMessageToVsCode({ type: 'updateProject', data: projectData });
    }
  }, [nodes, edges, customEndpoints, namespace]);

  const handleExportPdf = useCallback(() => {
    // 1. Force the diagram to fit within the viewport dimensions
    fitView({ padding: 0.08, includeHiddenNodes: true });

    // 2. Trigger printing after UI transitions finish
    setTimeout(() => {
      const style = document.createElement('style');
      style.id = 'print-diagram-style';
      style.innerHTML = `
        @media print {
          @page {
            size: landscape;
            margin: 0 !important;
          }
          body, html, #root {
            width: 100vw !important;
            height: 100vh !important;
            margin: 0 !important;
            padding: 0 !important;
            overflow: hidden !important;
            background-color: white !important;
            color: black !important;
          }
          .react-flow-wrapper {
            position: fixed !important;
            left: 0 !important;
            top: 0 !important;
            width: 100vw !important;
            height: 100vh !important;
            z-index: 99999 !important;
            background-color: white !important;
          }
          .react-flow__controls,
          .react-flow__minimap,
          .react-flow__attribution,
          button,
          input,
          label,
          .absolute,
          .fixed {
            display: none !important;
          }
        }
      `;
      document.head.appendChild(style);
      window.print();
      const styleToRemove = document.getElementById('print-diagram-style');
      if (styleToRemove) {
        document.head.removeChild(styleToRemove);
      }
    }, 250);
  }, [fitView]);

  const handleNodeClick = useCallback((_event: React.MouseEvent, _node: any) => {
    // Selection is handled automatically by React Flow state updates
  }, []);

  const handleCanvasClick = useCallback(() => {
    // Deselection is handled automatically by React Flow state updates
  }, []);

  const { screenToFlowPosition } = useReactFlow();
  const reactFlowWrapper = useRef<HTMLDivElement>(null);

  const handleAddClass = useCallback(() => {
    const x = 200 + Math.random() * 100;
    const y = 150 + Math.random() * 100;
    addClassNode(x, y);
  }, [addClassNode]);

  const handleAddEnum = useCallback(() => {
    const x = 200 + Math.random() * 100;
    const y = 150 + Math.random() * 100;
    addEnumNode(x, y);
  }, [addEnumNode]);

  const handleAddDomainGroup = useCallback(() => {
    const id = `domain-group-${Date.now()}`;
    const x = 100 + Math.random() * 100;
    const y = 100 + Math.random() * 100;
    const newNode = {
      id,
      type: 'domainGroup' as const,
      position: { x, y },
      data: { label: 'New Domain', color: '#6366f1', width: 400, height: 300 },
      style: { zIndex: -1 },
      draggable: true,
      selectable: true,
    };
    onNodesChange([{ type: 'add' as const, item: newNode }]);
  }, [onNodesChange]);

  const onDragStart = useCallback((event: DragEvent, nodeType: string) => {
    event.dataTransfer.setData('application/foundry-node-type', nodeType);
    event.dataTransfer.effectAllowed = 'move';
  }, []);

  const onDragOver = useCallback((event: DragEvent) => {
    event.preventDefault();
    event.dataTransfer.dropEffect = 'move';
  }, []);

  const onDrop = useCallback((event: DragEvent) => {
    event.preventDefault();
    const type = event.dataTransfer.getData('application/foundry-node-type');
    if (!type) return;

    const position = screenToFlowPosition({
      x: event.clientX,
      y: event.clientY,
    });

    if (type === 'classNode') {
      addClassNode(position.x, position.y);
    } else if (type === 'enumNode') {
      addEnumNode(position.x, position.y);
    } else if (type === 'domainGroup') {
      const id = `domain-group-${Date.now()}`;
      const newNode = {
        id,
        type: 'domainGroup' as const,
        position,
        data: { label: 'New Domain', color: '#6366f1', width: 400, height: 300 },
        style: { zIndex: -1 },
        draggable: true,
        selectable: true,
      };
      onNodesChange([{ type: 'add' as const, item: newNode }]);
    }
  }, [screenToFlowPosition, addClassNode, addEnumNode, onNodesChange]);

  const schemaJson = useMemo(() => {
    return exportToSchema();
  }, [exportToSchema, nodes, edges, namespace]);

  const generatedCsFiles = useMemo(() => {
    const files: Record<string, string> = {};
    
    schemaJson.Entities.forEach((entity: any) => {
      if (entity.Name) {
        files[`${entity.Name}.cs`] = generateCsCode(entity, namespace);
      }
    });

    schemaJson.Enums.forEach((enumDef: any) => {
      if (enumDef.Name) {
        files[`${enumDef.Name}.cs`] = generateEnumCode(enumDef, namespace);
      }
    });

    (schemaJson.Dtos || schemaJson.dtos || []).forEach((dto: any) => {
      if (dto.Name) {
        files[`${dto.Name}.cs`] = generateDtoCsCode(dto, namespace);
      }
    });

    (schemaJson.CustomEndpoints || schemaJson.customEndpoints || []).forEach((ep: any) => {
      const reqType = ep.RequestType || ep.requestType;
      if (reqType && reqType !== 'Void') {
        files[`Handlers/${reqType}Handler.cs`] = generateHandlerCsCode(ep, namespace);
      }
    });

    return files;
  }, [schemaJson, namespace]);

  const apiMetrics = useMemo(() => {
    const entities = (nodes || [])
      .filter(n => n && n.type === 'classNode')
      .map(n => (n as ClassNode)?.data?.entity)
      .filter(Boolean);
    
    let totalRoutes = 0;
    let securedMethods = 0;
    let cachedRoutes = 0;

    entities.forEach(entity => {
      if (!entity) return;
      const enabledMethods = entity.apiEnabledMethods || ['GET', 'POST', 'GET_BY_ID', 'PUT', 'DELETE'];
      totalRoutes += enabledMethods.length;
      
      enabledMethods.forEach(method => {
        if (entity.apiRoles && entity.apiRoles[method] && entity.apiRoles[method].length > 0) {
          securedMethods++;
        }
        if (entity.apiCaching && entity.apiCaching[method] && entity.apiCaching[method].enabled) {
          cachedRoutes++;
        }
      });
    });

    totalRoutes += customEndpoints.length;
    customEndpoints.forEach((ep: any) => {
      if (ep.roles && ep.roles.length > 0) {
        securedMethods++;
      }
    });

    return {
      totalRoutes,
      customRoutes: customEndpoints.length,
      securedMethods,
      cachedRoutes
    };
  }, [nodes, customEndpoints]);

  // Set default preview file
  const previewFiles = Object.keys(generatedCsFiles);
  if (previewFiles.length > 0 && !selectedPreviewFile) {
    setSelectedPreviewFile(previewFiles[0]);
  }

  const downloadPocos = () => {
    previewFiles.forEach(file => {
      const blob = new Blob([generatedCsFiles[file]], { type: 'text/plain' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = file;
      a.click();
    });
  };

  const downloadManifest = async () => {
    // Derived by the compiler, not here. See src/manifest.ts for why there is only one producer.
    try {
      const manifestJson = await deriveApiManifest(schemaJson);
      const blob = new Blob([manifestJson], { type: 'application/json' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = 'api-manifest.json';
      a.click();
      URL.revokeObjectURL(url);
    } catch (error) {
      setSaveMessage(error instanceof Error ? error.message : String(error));
    }
  };

  const downloadAllWithManifest = async () => {
    // Download POCOs
    previewFiles.forEach(file => {
      const blob = new Blob([generatedCsFiles[file]], { type: 'text/plain' });
      const url = URL.createObjectURL(blob);
      const a = document.createElement('a');
      a.href = url;
      a.download = file;
      a.click();
    });
    // Download manifest, derived by the compiler.
    await downloadManifest();
  };

  const downloadJson = () => {
    const blob = new Blob([JSON.stringify(schemaJson, null, 2)], { type: 'application/json' });
    const url = URL.createObjectURL(blob);
    const a = document.createElement('a');
    a.href = url;
    a.download = 'schema.json';
    a.click();
  };

  const handleSaveProject = () => {
    const projectData = exportProject();
    const dataStr = "data:text/json;charset=utf-8," + encodeURIComponent(JSON.stringify(projectData, null, 2));
    const downloadAnchor = document.createElement('a');
    downloadAnchor.setAttribute("href", dataStr);
    downloadAnchor.setAttribute("download", `${namespace.toLowerCase() || 'schema'}.foundry`);
    document.body.appendChild(downloadAnchor);
    downloadAnchor.click();
    downloadAnchor.remove();
  };

  const handleLoadProject = (event: React.ChangeEvent<HTMLInputElement>) => {
    const fileReader = new FileReader();
    const files = event.target.files;
    if (files && files.length > 0) {
      fileReader.readAsText(files[0], "UTF-8");
      fileReader.onload = (e) => {
        try {
          const target = e.target;
          if (target && typeof target.result === 'string') {
            const projectObj = JSON.parse(target.result);
            importProject(projectObj);
          }
        } catch (err) {
          alert('Failed to parse foundry project file. Make sure it is a valid project JSON.');
        }
      };
    }
  };

  const handleAiSubmit = async () => {
    if (!aiPrompt.trim()) return;
    setIsAiLoading(true);
    setAiError(null);
    try {
      const response = await fetch('http://localhost:5100/api/ai/prompt', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          Prompt: aiPrompt,
          CurrentSchema: schemaJson,
          OllamaHost: ollamaHost,
          OllamaModel: ollamaModel,
        }),
      });
      if (!response.ok) {
        throw new Error(`AI Request failed: ${response.statusText}`);
      }
      const updatedSchema = await response.json();
      const entities = updatedSchema.Entities || updatedSchema.entities;
      const enums = updatedSchema.Enums || updatedSchema.enums;
      if (updatedSchema && (entities || enums)) {
        importFromSchema(updatedSchema);
        setAiPrompt('');
      } else {
        throw new Error('AI returned an invalid schema structure.');
      }
    } catch (err: any) {
      setAiError(err.message || 'An error occurred.');
    } finally {
      setIsAiLoading(false);
    }
  };

  const handleSaveToWorkspace = async () => {
    setIsSaving(true);
    setSaveMessage(null);
    try {
      const response = await fetch('http://localhost:5100/api/save-pocos', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          Files: generatedCsFiles,
          OutputPath: outputPath,
        }),
      });
      if (!response.ok) {
        throw new Error(`Failed to save C# classes: ${response.statusText}`);
      }

      // The IR is posted and the server derives the manifest with ApiManifestGenerator, so a manifest
      // written from Studio is identical to one written by `foundry compile`.
      const manifestResponse = await fetch('http://localhost:5100/api/save-manifest', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          Schema: schemaJson,
          OutputPath: manifestPath,
        }),
      });
      if (!manifestResponse.ok) {
        throw new Error(`Failed to save API manifest: ${manifestResponse.statusText}`);
      }

      // Keep tests in sync as well
      await fetch('http://localhost:5100/api/save-manifest', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          Schema: schemaJson,
          OutputPath: '../../tests/Foundry.Api.Tests/api-manifest.json',
        }),
      });

      const entityCount = nodes.filter((n) => n.type === 'classNode').length;
      const enumCount = nodes.filter((n) => n.type === 'enumNode').length;
      setSaveMessage(
        `✓ ${entityCount} entity${entityCount !== 1 ? 'ies' : 'y'} + ${enumCount} enum\n  POCOs → ${outputPath}\n  Manifest → ${manifestPath}`
      );
      setTimeout(() => setSaveMessage(null), 8000);
    } catch (err: any) {
      setSaveMessage(`Error: ${err.message || 'Failed to save'}`);
    } finally {
      setIsSaving(false);
    }
  };

  return (
    <div className="flex flex-col h-screen bg-slate-50 text-slate-900 dark:bg-slate-950 dark:text-slate-100 font-sans transition-colors duration-200">
      {/* Top Bar */}
      <div className="flex items-center justify-between px-6 py-3 glass-panel border-b z-10 print-hide transition-colors duration-200 shadow-sm">
        <div className="flex items-center gap-3">
          <Settings className="w-5.5 h-5.5 text-sky-500 animate-spin-slow" />
          <h1 className="text-base font-extrabold tracking-tight bg-gradient-to-r from-sky-400 via-sky-500 to-indigo-500 bg-clip-text text-transparent">
            Foundry Schema Studio
          </h1>
          <button
            onClick={() => setTheme(theme === 'dark' ? 'light' : 'dark')}
            className="p-1.5 rounded-lg text-slate-500 dark:text-slate-400 hover:text-sky-500 dark:hover:text-sky-400 hover:bg-slate-100 dark:hover:bg-slate-800/80 transition-all cursor-pointer hover-lift"
            title={theme === 'dark' ? 'Switch to Light Mode' : 'Switch to Dark Mode'}
          >
            {theme === 'dark' ? <Sun className="w-4 h-4 text-amber-400 animate-pulse" /> : <Moon className="w-4 h-4 text-slate-600" />}
          </button>
        </div>

        <div className="flex items-center gap-2">
          {/* Designer Tabs */}
          <div className="flex bg-slate-100/80 dark:bg-slate-950/80 border border-slate-200 dark:border-slate-800 p-0.5 rounded-lg shadow-inner transition-colors duration-200">
            <button
              onClick={() => setActiveView('schema')}
              className={`px-4 py-1.75 text-xs rounded-md transition-all font-semibold flex items-center gap-1.5 cursor-pointer select-none hover-lift ${
                activeView === 'schema' 
                  ? 'bg-gradient-to-r from-sky-500 to-indigo-600 text-white font-bold shadow-md shadow-sky-500/25 glow-sky' 
                  : 'text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-slate-200'
              }`}
            >
              <Database className="w-3.5 h-3.5" /> Schema Designer
            </button>
            <button
              onClick={() => setActiveView('api')}
              className={`px-4 py-1.75 text-xs rounded-md transition-all font-semibold flex items-center gap-1.5 cursor-pointer select-none hover-lift ${
                activeView === 'api' 
                  ? 'bg-gradient-to-r from-sky-500 to-indigo-600 text-white font-bold shadow-md shadow-sky-500/25 glow-sky' 
                  : 'text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-slate-200'
              }`}
            >
              <Globe className="w-3.5 h-3.5" /> API Designer
            </button>
            <button
              onClick={() => setActiveView('workflow')}
              className={`px-4 py-1.75 text-xs rounded-md transition-all font-semibold flex items-center gap-1.5 cursor-pointer select-none hover-lift ${
                activeView === 'workflow' 
                  ? 'bg-gradient-to-r from-sky-500 to-indigo-600 text-white font-bold shadow-md shadow-sky-500/25 glow-sky' 
                  : 'text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-slate-200'
              }`}
            >
              <GitBranch className="w-3.5 h-3.5" /> Workflow Designer
            </button>
            <button
              onClick={() => setActiveView('playground')}
              className={`px-4 py-1.75 text-xs rounded-md transition-all font-semibold flex items-center gap-1.5 cursor-pointer select-none hover-lift ${
                activeView === 'playground' 
                  ? 'bg-gradient-to-r from-sky-500 to-indigo-600 text-white font-bold shadow-md shadow-sky-500/25 glow-sky' 
                  : 'text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-slate-200'
              }`}
            >
              <Terminal className="w-3.5 h-3.5" /> API Playground
            </button>
          </div>

          {/* Undo / Redo Actions */}
          <div className="flex items-center bg-slate-100 dark:bg-slate-950/80 border border-slate-200 dark:border-slate-800 p-0.5 rounded shadow-inner transition-colors duration-200">
            <button
              onClick={undo}
              disabled={!canUndo}
              className="p-1.5 text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white disabled:opacity-25 disabled:hover:text-slate-500 dark:disabled:hover:text-slate-400 rounded transition-all cursor-pointer select-none"
              title="Undo Last Action"
            >
              <Undo2 className="w-3.5 h-3.5" />
            </button>
            <div className="w-[1px] h-3.5 bg-slate-200 dark:bg-slate-800" />
            <button
              onClick={redo}
              disabled={!canRedo}
              className="p-1.5 text-slate-500 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white disabled:opacity-25 disabled:hover:text-slate-500 dark:disabled:hover:text-slate-400 rounded transition-all cursor-pointer select-none"
              title="Redo Action"
            >
              <Redo2 className="w-3.5 h-3.5" />
            </button>
          </div>
        </div>
        
        <div className="flex items-center gap-4">
          <div className="flex items-center gap-2">
            <span className="text-xs text-slate-500 dark:text-slate-400 font-mono">NAMESPACE:</span>
            <input
              type="text"
              value={namespace}
              onChange={(e) => setNamespace(e.target.value)}
              className="px-3 py-1 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded font-mono text-xs focus:outline-none focus:border-sky-500 focus:ring-1 focus:ring-sky-500 w-44 text-slate-800 dark:text-sky-300 transition-colors duration-200"
            />
          </div>

          <div className="flex items-center gap-3">
            {/* Project Storage Group */}
            <div className="flex items-center bg-slate-100 dark:bg-slate-950/40 p-0.5 border border-slate-200 dark:border-slate-800 rounded-lg shadow-sm transition-colors duration-200">
              <button 
                onClick={() => document.getElementById('load-project-input')?.click()}
                className="flex items-center gap-1 px-2.5 py-1 text-slate-600 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white rounded-md text-xs transition-all font-semibold cursor-pointer select-none"
                title="Load Project"
              >
                <Upload className="w-3.5 h-3.5 text-purple-500 dark:text-purple-400" />
                <span>Load</span>
              </button>
              <div className="w-[1px] h-3.5 bg-slate-200 dark:bg-slate-800" />
              <button 
                onClick={handleSaveProject}
                className="flex items-center gap-1 px-2.5 py-1 text-slate-600 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white rounded-md text-xs transition-all font-semibold cursor-pointer select-none"
                title="Save Project"
              >
                <Download className="w-3.5 h-3.5 text-sky-500 dark:text-sky-400" />
                <span>Save</span>
              </button>
            </div>
            <input 
              id="load-project-input"
              type="file" 
              accept=".foundry" 
              onChange={handleLoadProject} 
              style={{ display: 'none' }} 
            />
            
            {/* Preview Group */}
            <div className="flex items-center bg-slate-100 dark:bg-slate-950/40 p-0.5 border border-slate-200 dark:border-slate-800 rounded-lg shadow-sm transition-colors duration-200">
              <button 
                onClick={() => { setPreviewTab('JSON'); setShowPreviewModal(true); }}
                className="flex items-center gap-1 px-2.5 py-1 text-slate-600 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white rounded-md text-xs transition-all font-semibold cursor-pointer select-none"
                title="Preview JSON Schema"
              >
                <Code className="w-3.5 h-3.5 text-slate-500 dark:text-slate-400" />
                <span>JSON</span>
              </button>
              <div className="w-[1px] h-3.5 bg-slate-200 dark:bg-slate-800" />
              <button 
                onClick={() => { setPreviewTab('C#'); setShowPreviewModal(true); }}
                className="flex items-center gap-1 px-2.5 py-1 text-slate-600 dark:text-slate-400 hover:text-slate-800 dark:hover:text-white rounded-md text-xs transition-all font-semibold cursor-pointer select-none"
                title="Preview C# POCOs"
              >
                <Play className="w-3.5 h-3.5 text-emerald-500 dark:text-emerald-400" />
                <span>C# POCOs</span>
              </button>
            </div>

            {/* Primary Action */}
            <button 
              onClick={downloadJson}
              className="flex items-center gap-1.5 px-3 py-1.5 bg-sky-600 hover:bg-sky-500 text-white rounded-lg text-xs font-semibold transition-all shadow-md shadow-sky-600/10 cursor-pointer select-none"
            >
              <Download className="w-3.5 h-3.5" />
              <span>Download Schema</span>
            </button>
          </div>
        </div>
      </div>

      <div className="flex flex-1 overflow-hidden relative">
        {activeView === 'schema' ? (
          <>
            {/* Left Panel - Toolbox */}
            {!isFullScreen && (
              <div className="w-64 glass-panel border-r p-4 flex flex-col gap-6 z-10 print-hide transition-colors duration-200 shadow-sm">
              <div>
                <h2 className="text-xs font-semibold text-slate-500 uppercase tracking-wider mb-3">Editor Tools</h2>
                <div className="flex flex-col gap-1.5">
                  {(['Select', 'Association', 'Composition', 'Inheritance'] as const).map(tool => (
                    <button
                      key={tool}
                      onClick={() => setActiveTool(tool)}
                      className={`px-3 py-2 rounded text-left text-xs transition-all flex items-center justify-between ${
                        activeTool === tool 
                          ? 'bg-sky-500/10 border border-sky-500/30 text-sky-500 dark:text-sky-400 font-semibold' 
                          : 'bg-slate-100 dark:bg-slate-800/20 border border-slate-200/50 dark:border-transparent hover:bg-slate-200 dark:hover:bg-slate-800/40 text-slate-600 dark:text-slate-400'
                      }`}
                    >
                      <span>{tool === 'Select' ? 'Pointer/Select' : `${tool} Link`}</span>
                      {activeTool === tool && <Check className="w-3.5 h-3.5 text-sky-500 dark:text-sky-400" />}
                    </button>
                  ))}
                </div>
              </div>

              <div>
                <h2 className="text-xs font-semibold text-slate-500 uppercase tracking-wider mb-3">Add Elements</h2>
                <p className="text-[10px] text-slate-400 dark:text-slate-500 mb-2 italic">Drag onto canvas or click to add</p>
                <div className="grid grid-cols-2 gap-2">
                  <button
                    draggable
                    onDragStart={(e) => onDragStart(e, 'classNode')}
                    onClick={handleAddClass}
                    className="flex flex-col items-center justify-center p-3 bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/40 dark:hover:bg-slate-800 border border-slate-200 dark:border-slate-800 rounded transition-all group cursor-grab active:cursor-grabbing"
                  >
                    <Plus className="w-5 h-5 text-sky-500 dark:text-sky-400 mb-1.5 group-hover:scale-110 transition-transform" />
                    <span className="text-xs font-medium text-slate-700 dark:text-slate-200">Class Node</span>
                  </button>
                  
                  <button
                    draggable
                    onDragStart={(e) => onDragStart(e, 'enumNode')}
                    onClick={handleAddEnum}
                    className="flex flex-col items-center justify-center p-3 bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/40 dark:hover:bg-slate-800 border border-slate-200 dark:border-slate-800 rounded transition-all group cursor-grab active:cursor-grabbing"
                  >
                    <Plus className="w-5 h-5 text-purple-500 dark:text-purple-400 mb-1.5 group-hover:scale-110 transition-transform" />
                    <span className="text-xs font-medium text-slate-700 dark:text-slate-200">Enum Node</span>
                  </button>

                  <button
                    draggable
                    onDragStart={(e) => onDragStart(e, 'domainGroup')}
                    onClick={handleAddDomainGroup}
                    className="flex flex-col items-center justify-center p-3 bg-slate-50 hover:bg-slate-100 dark:bg-slate-800/40 dark:hover:bg-slate-800 border border-slate-200 dark:border-slate-800 rounded transition-all group cursor-grab active:cursor-grabbing col-span-2"
                  >
                    <Database className="w-5 h-5 text-indigo-500 dark:text-indigo-400 mb-1.5 group-hover:scale-110 transition-transform" />
                    <span className="text-xs font-medium text-slate-700 dark:text-slate-200">Domain Group</span>
                  </button>
                </div>
              </div>

              <div className="mt-auto border-t border-slate-200 dark:border-slate-800 pt-4 flex flex-col gap-3">
                <div className="flex items-center gap-1.5 text-xs font-semibold text-slate-600 dark:text-slate-400 uppercase tracking-wider">
                  <Sparkles className="w-4 h-4 text-sky-500 dark:text-sky-400 animate-pulse" />
                  <span>AI Designer Assistant</span>
                </div>
                
                <textarea
                  value={aiPrompt}
                  onChange={(e) => setAiPrompt(e.target.value)}
                  placeholder="Ask AI to add Customer class, link composition, etc..."
                  className="w-full px-2.5 py-2 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-slate-300 resize-none h-20 placeholder:text-slate-400 dark:placeholder:text-slate-650 font-sans transition-colors duration-200"
                />

                {aiError && (
                  <div className="text-[10px] text-red-500 dark:text-red-400 bg-red-50 dark:bg-red-950/20 border border-red-200 dark:border-red-900/30 p-2 rounded">
                    {aiError}
                  </div>
                )}

                <button
                  onClick={handleAiSubmit}
                  disabled={isAiLoading || !aiPrompt.trim()}
                  className="w-full flex items-center justify-center gap-1.5 px-3 py-1.5 bg-sky-600 hover:bg-sky-500 disabled:bg-slate-200 dark:disabled:bg-slate-800 disabled:text-slate-400 dark:disabled:text-slate-500 text-white rounded text-xs font-semibold transition-all shadow-md shadow-sky-600/10 cursor-pointer"
                >
                  {isAiLoading ? (
                    <span className="flex items-center gap-1">
                      <svg className="animate-spin h-3.5 w-3.5 text-white" fill="none" viewBox="0 0 24 24">
                        <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4" />
                        <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z" />
                      </svg>
                      Thinking...
                    </span>
                  ) : (
                    <>Submit Prompt</>
                  )}
                </button>
              </div>
            </div>
            )}

            {/* Center Canvas */}
            <div className="flex-1 relative h-full react-flow-wrapper" ref={reactFlowWrapper}>
              <ReactFlow
                nodes={nodes}
                edges={edges}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onConnect={onConnect}
                onNodeClick={handleNodeClick}
                onClick={handleCanvasClick}
                onDragOver={onDragOver}
                onDrop={onDrop}
                nodeTypes={nodeTypes}
                edgeTypes={edgeTypes}
                connectionLineStyle={{ stroke: '#38bdf8', strokeWidth: 2 }}
                connectionLineType={ConnectionLineType.SmoothStep}
                fitView
                className="bg-slate-50 dark:bg-slate-950 transition-colors duration-200"
              >
                <Background color={theme === 'dark' ? '#1e293b' : '#cbd5e1'} />
                <Controls className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800 text-slate-800 dark:text-white rounded-none" />
                <MiniMap 
                  nodeColor={(node) => {
                    if (node.type === 'classNode') return '#38bdf8';
                    if (node.type === 'enumNode') return '#c084fc';
                    return '#64748b';
                  }}
                  className="bg-white dark:bg-slate-900 border border-slate-200 dark:border-slate-800"
                  pannable={true}
                  zoomable={true}
                />
                
                {/* Custom SVG marker definitions for sharp orthogonal arrowheads */}
                <svg style={{ position: 'absolute', width: 0, height: 0 }}>
                  <defs>
                    <marker 
                      id="uml-inheritance" 
                      viewBox="0 0 10 10" 
                      refX="8" 
                      refY="5" 
                      markerWidth="8" 
                      markerHeight="8" 
                      orient="auto-start-reverse"
                    >
                      <path d="M 0 0 L 8 5 L 0 10 z" fill="#0f172a" stroke="#475569" strokeWidth="1.5" />
                    </marker>
                    
                    <marker 
                      id="uml-composition" 
                      viewBox="0 0 12 12" 
                      refX="10" 
                      refY="6" 
                      markerWidth="10" 
                      markerHeight="10" 
                      orient="auto-start-reverse"
                    >
                      <path d="M 0 6 L 5 1 L 10 6 L 5 11 z" fill="#475569" stroke="#475569" strokeWidth="1.5" />
                    </marker>
                    
                    <marker 
                      id="uml-association" 
                      viewBox="0 0 10 10" 
                      refX="8" 
                      refY="5" 
                      markerWidth="7" 
                      markerHeight="7" 
                      orient="auto-start-reverse"
                    >
                      <path d="M 0 1 L 8 5 L 0 9" fill="none" stroke="#475569" strokeWidth="1.5" />
                    </marker>
                  </defs>
                </svg>
              </ReactFlow>

              <DiagnosticsPanel />

              {/* Floating Canvas Action Overlays */}
              <div className="absolute top-4 right-4 z-20 flex gap-2 print-hide">
                <button
                  onClick={handleExportPdf}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-white/90 dark:bg-slate-900/90 hover:bg-slate-100 dark:hover:bg-slate-800 border border-slate-200 dark:border-slate-800 rounded-lg text-xs font-semibold text-slate-700 dark:text-slate-300 hover:text-slate-900 dark:hover:text-white transition-all cursor-pointer backdrop-blur shadow-md dark:shadow-lg select-none"
                  title="Export class diagram to a printable PDF / Image"
                >
                  <Download className="w-3.5 h-3.5 text-emerald-500 dark:text-emerald-400" />
                  <span>Export Diagram</span>
                </button>

                <button
                  onClick={() => setIsFullScreen(!isFullScreen)}
                  className="flex items-center gap-1.5 px-3 py-1.5 bg-white/90 dark:bg-slate-900/90 hover:bg-slate-100 dark:hover:bg-slate-800 border border-slate-200 dark:border-slate-800 rounded-lg text-xs font-semibold text-slate-700 dark:text-slate-300 hover:text-slate-900 dark:hover:text-white transition-all cursor-pointer backdrop-blur shadow-md dark:shadow-lg select-none"
                  title="Toggle full screen visual designer focus"
                >
                  {isFullScreen ? 'Exit Full Screen' : 'Full Screen'}
                </button>
              </div>
            </div>

            {/* Right Panel - Property Inspector */}
            {!isFullScreen && <InspectorPanel />}
          </>
        ) : activeView === 'api' ? (
          <>
            {/* Left Panel - API Overview */}
            <div className="w-64 bg-white/80 dark:bg-slate-900/50 border-r border-slate-200 dark:border-slate-800 p-4 flex flex-col gap-6 z-10 backdrop-blur-md transition-colors duration-200">
              <div>
                <h2 className="text-xs font-semibold text-slate-500 dark:text-slate-500 uppercase tracking-wider mb-3">API Overview</h2>
                <div className="flex flex-col gap-3 bg-slate-50 dark:bg-slate-950/40 p-4 border border-slate-200 dark:border-slate-800 rounded transition-colors duration-200">
                  <div className="flex justify-between items-center text-xs">
                    <span className="text-slate-500 dark:text-slate-400">Total Routes:</span>
                    <span className="font-mono font-bold text-sky-600 dark:text-sky-400">{apiMetrics.totalRoutes}</span>
                  </div>
                  <div className="flex justify-between items-center text-xs">
                    <span className="text-slate-500 dark:text-slate-400">Custom Routes:</span>
                    <span className="font-mono font-bold text-purple-600 dark:text-purple-400">{apiMetrics.customRoutes}</span>
                  </div>
                  <div className="flex justify-between items-center text-xs border-t border-slate-200 dark:border-slate-800 pt-2 mt-1">
                    <span className="text-slate-500 dark:text-slate-400">Secured Methods:</span>
                    <span className="font-mono font-bold text-amber-600 dark:text-amber-400">{apiMetrics.securedMethods}</span>
                  </div>
                  <div className="flex justify-between items-center text-xs">
                    <span className="text-slate-400">Cached Routes:</span>
                    <span className="font-mono font-bold text-emerald-400">{apiMetrics.cachedRoutes}</span>
                  </div>
                </div>
              </div>

              <div className="mt-auto border-t border-slate-200 dark:border-slate-800 pt-4 flex flex-col gap-3 transition-colors duration-200">
                <div className="flex items-center gap-1.5 text-xs font-semibold text-slate-600 dark:text-slate-400 uppercase tracking-wider">
                  <Sparkles className="w-4 h-4 text-sky-500 dark:text-sky-400 animate-pulse" />
                  <span>AI API Assistant</span>
                </div>
                
                <textarea
                  value={aiPrompt}
                  onChange={(e) => setAiPrompt(e.target.value)}
                  placeholder="Ask AI to configure cache, add custom endpoints..."
                  className="w-full px-2.5 py-2 bg-slate-100 dark:bg-slate-950 border border-slate-200 dark:border-slate-800 rounded text-xs focus:outline-none focus:border-sky-500 text-slate-800 dark:text-slate-300 resize-none h-20 placeholder:text-slate-400 dark:placeholder:text-slate-600 font-sans transition-colors duration-200"
                />

                {aiError && (
                  <div className="text-[10px] text-red-500 dark:text-red-400 bg-red-50 dark:bg-red-950/20 border border-red-200 dark:border-red-900/30 p-2 rounded">
                    {aiError}
                  </div>
                )}

                <button
                  onClick={handleAiSubmit}
                  disabled={isAiLoading || !aiPrompt.trim()}
                  className="w-full flex items-center justify-center gap-1.5 px-3 py-1.5 bg-sky-600 hover:bg-sky-500 disabled:bg-slate-200 dark:disabled:bg-slate-800 disabled:text-slate-400 dark:disabled:text-slate-500 text-white rounded text-xs font-semibold transition-all shadow-md shadow-sky-600/10 cursor-pointer"
                >
                  {isAiLoading ? 'Thinking...' : 'Submit Prompt'}
                </button>
              </div>
            </div>

            {/* API Designer Panel */}
            <ApiDesigner />
          </>
        ) : activeView === 'workflow' ? (
          <WorkflowDesigner />
        ) : (
          <ApiPlayground />
        )}
      </div>

      {/* Code / JSON Preview Modal */}
      {showPreviewModal && (
        <div className="fixed inset-0 bg-black/70 backdrop-blur-sm z-50 flex items-center justify-center p-6 animate-fadeIn">
          <div className="w-full max-w-4xl h-[80vh] bg-slate-900 border border-slate-800 rounded-lg shadow-2xl flex flex-col overflow-hidden">
            {/* Modal Header */}
            <div className="flex items-center justify-between px-6 py-4 border-b border-slate-800 bg-slate-950/80">
              <div className="flex items-center gap-4">
                <h3 className="font-bold text-lg">Visual Output Preview</h3>
                <div className="flex bg-slate-900 border border-slate-800 p-0.5 rounded">
                  <button
                    onClick={() => setPreviewTab('JSON')}
                    className={`px-3 py-1 text-xs rounded transition-all ${
                      previewTab === 'JSON' ? 'bg-sky-500 text-white font-medium' : 'text-slate-400 hover:text-white'
                    }`}
                  >
                    JSON Schema
                  </button>
                  <button
                    onClick={() => setPreviewTab('C#')}
                    className={`px-3 py-1 text-xs rounded transition-all ${
                      previewTab === 'C#' ? 'bg-sky-500 text-white font-medium' : 'text-slate-400 hover:text-white'
                    }`}
                  >
                    C# POCO classes
                  </button>
                </div>
              </div>
              <button 
                onClick={() => setShowPreviewModal(false)}
                className="text-slate-400 hover:text-white font-bold"
              >
                ✕
              </button>
            </div>

            {/* Modal Content */}
            <div className="flex-1 flex overflow-hidden">
              {/* C# Files sidebar */}
              {previewTab === 'C#' && (
                <div className="w-64 border-r border-slate-800 bg-slate-950/40 p-4 overflow-y-auto flex flex-col gap-1">
                  <span className="text-[10px] text-slate-500 font-mono block mb-2 uppercase tracking-wider">Generated files</span>
                  {previewFiles.map(file => (
                    <button
                      key={file}
                      onClick={() => setSelectedPreviewFile(file)}
                      className={`px-3 py-2 rounded text-left text-xs font-mono transition-all ${
                        selectedPreviewFile === file 
                          ? 'bg-slate-800 text-white font-medium border border-slate-700' 
                          : 'text-slate-400 hover:bg-slate-800/40 hover:text-slate-200'
                      }`}
                    >
                      {file}
                    </button>
                  ))}
                </div>
              )}

              {/* Code viewer */}
              <div className="flex-1 p-6 overflow-auto bg-slate-950 font-mono text-sm text-slate-300 relative">
                {previewTab === 'C#' && selectedPreviewFile && (
                  <div className="absolute top-4 right-4 flex items-center gap-3 bg-slate-900 border border-slate-800 p-2 rounded shadow-xl z-20">
                    <div className="flex flex-col">
                      <span className="text-[9px] text-slate-500 font-mono block mb-0.5 uppercase">Output Dir</span>
                      <input
                        type="text"
                        value={outputPath}
                        onChange={(e) => setOutputPath(e.target.value)}
                        className="px-2 py-0.5 bg-slate-950 border border-slate-800 rounded text-[10px] text-sky-400 font-mono focus:outline-none focus:border-sky-500 w-36"
                      />
                    </div>
                    <div className="flex flex-col">
                      <span className="text-[9px] text-slate-500 font-mono block mb-0.5 uppercase">Manifest Path</span>
                      <input
                        type="text"
                        value={manifestPath}
                        onChange={(e) => setManifestPath(e.target.value)}
                        className="px-2 py-0.5 bg-slate-950 border border-slate-800 rounded text-[10px] text-sky-400 font-mono focus:outline-none focus:border-sky-500 w-48"
                      />
                    </div>
                    <button
                      onClick={handleSaveToWorkspace}
                      disabled={isSaving}
                      className="flex items-center gap-1.5 px-3 py-1.5 bg-sky-600 hover:bg-sky-500 text-white rounded text-xs font-semibold transition-all shadow-md disabled:bg-slate-800 disabled:text-slate-500 cursor-pointer"
                    >
                      {isSaving ? 'Saving...' : (
                        <>
                          <Plug className="w-3.5 h-3.5" /> Publish API
                        </>
                      )}
                    </button>
                    <div className="flex gap-1">
                      <button
                        onClick={downloadPocos}
                        className="flex items-center gap-1 px-2 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-350 rounded text-xs cursor-pointer"
                      >
                        <Code className="w-3.5 h-3.5" /> Code
                      </button>
                      <button
                        onClick={downloadManifest}
                        className="flex items-center gap-1 px-2 py-1.5 bg-slate-800 hover:bg-slate-700 text-slate-350 rounded text-xs cursor-pointer"
                      >
                        <Database className="w-3.5 h-3.5" /> Manifest
                      </button>
                      <button
                        onClick={downloadAllWithManifest}
                        className="flex items-center gap-1 px-2 py-1.5 bg-slate-800 hover:bg-slate-700 text-sky-400 rounded text-xs cursor-pointer font-semibold"
                      >
                        <Download className="w-3.5 h-3.5" /> Download All
                      </button>
                    </div>
                    {saveMessage && (
                      <div className="absolute right-0 top-full mt-2 bg-slate-900 border border-slate-800 p-2.5 rounded text-[10px] text-sky-400 shadow-2xl max-w-xs text-right whitespace-pre-wrap font-mono">
                        {saveMessage}
                      </div>
                    )}
                  </div>
                )}
                
                <pre className="whitespace-pre">
                  {previewTab === 'JSON' 
                    ? JSON.stringify(schemaJson, null, 2)
                    : generatedCsFiles[selectedPreviewFile] || '// No POCO classes generated yet.'
                  }
                </pre>
              </div>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};