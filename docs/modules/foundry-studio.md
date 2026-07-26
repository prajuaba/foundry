# 🎨 Foundry Studio IDE Module Documentation

**Foundry Studio** is a standalone visual domain design environment built using **React 19**, **TypeScript**, **Vite**, **XYFlow (@xyflow/react)**, and **Tailwind CSS**.

---

## 💡 Key Features

1. **Visual Class & Enum Diagramming**: Drag-and-drop UML class and enum nodes, properties, attributes, and relationships.
2. **Relationship Manager**: Supports **Inheritance**, **Composition** (`List<T>`), and **Association** (`ObjectId`/Key references).
3. **DTO Designer**: Build Data Transfer Objects linked directly to source entities.
4. **Custom Endpoint Builder**: Configure custom API routes, HTTP methods (`GET`, `POST`, `PUT`, `DELETE`), RBAC roles, and business rules.
5. **UML Workflow Engine**: Visual state machine designer with choice nodes (decision gates) and transition conditions.
6. **Local Ollama AI Integration**: Connects to local Ollama endpoints (e.g. `http://edgexpert-c1ad.local:11434`) for AI-assisted domain schema generation.
7. **Theme & Canvas Utilities**: Dark/light mode toggle, undo/redo state history, navigable minimap, and schema export.

---

## 📁 Directory Location

```text
foundry-studio/
├── src/
│   ├── components/
│   │   ├── StudioWorkspace.tsx     # Main Studio Workspace Shell
│   │   ├── UmlClassNode.tsx        # ReactFlow UML Class Node Renderer
│   │   ├── UmlEnumNode.tsx         # ReactFlow UML Enum Node Renderer
│   │   ├── InspectorPanel.tsx      # Right-hand Properties Inspector
│   │   ├── ApiDesigner.tsx         # Custom Endpoints & DTOs Visual Builder
│   │   └── WorkflowDesigner.tsx    # UML State Machine Designer
│   ├── store.ts                    # Zustand Canvas & Schema Store
│   ├── types.ts                    # TypeScript Interface Definitions
│   └── vscode.ts                   # VS Code Webview IPC Bridge
├── package.json
└── vite.config.ts                  # Singlefile Vite Bundler Config
```

---

## ⚙️ Build Commands

```bash
cd foundry-studio

# Start Vite hot-reloading dev server
npm run dev

# Run oxlint linter
npm run lint

# Build production singlefile bundle (dist/index.html)
npm run build
```
