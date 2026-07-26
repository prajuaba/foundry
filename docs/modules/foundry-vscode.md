# 🔌 Foundry VS Code Extension Module Documentation

**foundry-vscode** is a native Visual Studio Code Extension (`foundry-vscode-1.0.0.vsix`) providing a custom editor provider for `.foundry.json` manifest files inside VS Code.

---

## 💡 Architecture & IPC Protocol

```mermaid
sequenceDiagram
    participant User
    participant VSCode as VS Code Editor Panel
    participant Provider as FoundryEditorProvider.ts
    participant Webview as Studio Webview (index.html)
    participant Compiler as CompilerService.ts (.NET CLI)

    User->>VSCode: Open *.foundry.json
    VSCode->>Provider: resolveCustomTextEditor()
    Provider->>Webview: Load dist-studio/index.html
    Provider->>Webview: postMessage({ type: 'loadProject', data })
    Webview->>User: Render Visual Canvas
    User->>Webview: Edit Diagram / Save
    Webview->>Provider: postMessage({ type: 'updateProject', data })
    Provider->>VSCode: applyEdit(WorkspaceEdit)
    User->>Webview: Click "Compile Schema"
    Webview->>Provider: postMessage({ type: 'compileSchema', schema })
    Provider->>Compiler: dotnet run --project Foundry.Schema.Compiler.csproj
    Compiler->>User: C# POCOs Generated & Saved!
```

---

## 📁 Directory Location

```text
foundry-vscode/
├── src/
│   ├── extension.ts              # Extension activation & command registration
│   ├── FoundryEditorProvider.ts  # Custom Editor Provider serving Webview
│   └── CompilerService.ts        # Service invoking C# Compiler backend
├── scripts/
│   └── verify-extension.js       # Automated VSIX extension test suite
├── package.json
└── esbuild.js                    # Esbuild Extension Bundler
```

---

## ⚙️ Packaging & Verification Commands

```bash
cd foundry-vscode

# Build studio singlefile, copy assets, and bundle extension
npm run build:all

# Package .vsix installer package
npm run package

# Run automated verification suite
node scripts/verify-extension.js
```
