# Foundry.Schema.Studio

Foundry.Schema.Studio is a high-fidelity visual and AI-driven domain model architecting tool. It features a StarUML-style Class Diagram canvas and a design-time C# compiler engine to compile diagrams into MongoDB-ready C# POCO records optimized for the `Foundry.Mongo` framework.

---

## Workspace Structure

* **`/studio`**: React + TypeScript + TailwindCSS v4 visual workspace built with **React Flow (XYFlow)** and **Zustand**. Enforces rigid 90-degree orthogonal edges and class/enum compartments.
* **`/compiler`**: C# .NET 10 console application compiling intermediate JSON schemas into required C# records. Packable as a global dotnet tool.
* **`/backend`**: ASP.NET Core Minimal API server acting as an integration broker for schema compilation, direct code-saving disk exports, and local Ollama proxying.

---

## Quick Start (Visual Studio & AI Agent)

### Prerequisites
1. **.NET 10 SDK** (installed at `~/.dotnet/dotnet` or globally)
2. **Node.js** (v18+)
3. **Local Ollama Model** (such as `qwen3-coder:30b`) running at `http://edgexpert-c1ad.local:11434` (configurable via `OLLAMA_HOST` env variable).

### 1-Click Launch
To start both the visual editor and Minimal API backend server simultaneously, run:
```bash
./start-studio.sh
```
This script boots the servers and opens `http://localhost:5173/` in your browser. Press `Ctrl+C` in the terminal to clean up and shut down both servers.

---

## Features

### 1. Canvas Relationship-to-Property Synchronization
Edges drawn on the UML canvas automatically alter the logical domain structures:
* **Inheritance ($\u25B3$):** Updates the subclass `baseClass` metadata, compiling records to inherit from the target parent record.
* **Composition ($\u25C6$):** Automatically injects a collection property `List<Target>` named `[Target]s` inside the container class.
* **Association ($\rightarrow$):** Automatically injects a foreign reference `ObjectId` property named `[Target]Id` (with the `[Indexed]` trait) inside the source class.
* **Deletion Cleanup:** Removing edge links on the canvas (via selecting and pressing `Backspace`/`Delete` on your keyboard) instantly cleans up the associated property fields or base class configurations.

### 2. AI Designer Assistant
Use the collapsible AI Prompt box on the left toolbar to describe your domain adjustments in natural language (e.g. *"add Customer entity with a description string"*). The backend proxies this request to Ollama and reconciles the logical response onto the visual canvas without resetting your manual layout coordinates.

### 3. Save to Workspace
Inside the C# POCO modal, specify an output directory path (e.g. `../foundry-mongo/src/Domain/Entities`) and click **"Save to Workspace"** to write all generated C# record files directly to disk.

### 4. Undo / Redo Operational History
Tracks all operations (dragging nodes, adding properties, customizing endpoints) in an undo/redo stack in Zustand state. Micro-dragging movements are automatically ignored to optimize the history stack.

### 5. Print-to-Page Class Diagram Export
Ensures the class diagram canvas scales and fits onto a single landscape page for clean exporting to PDF/image formats. Controls, headers, sidebars, and dialogue widgets are automatically hidden during print rendering.

### 6. Interactive Navigable MiniMap
Allows panning and zooming directly inside the minimap mask to instantly navigate massive diagrams.

### 7. Light / Dark Theme Custom Toggling
Toggles the entire canvas, UML node cards, textboxes, and sidebars between light and dark modes with a dedicated button in the header toolbar.

---

## Packaging the Compiler CLI Globally

You can package and install the compiler as a global command-line tool on your machine:

1. **Pack the package:**
   ```bash
   dotnet pack compiler/Foundry.Schema.Compiler.csproj
   ```
2. **Install globally from local output:**
   ```bash
   dotnet tool install -g --add-source ./compiler/nupkg Foundry.Schema.Compiler
   ```
3. **Run compiler CLI:**
   ```bash
   # Ensure DOTNET_ROOT is configured if .NET is installed in home directories
   export DOTNET_ROOT="$HOME/.dotnet"
   
   foundry-compile --input schema.json --output ./Entities
   ```
