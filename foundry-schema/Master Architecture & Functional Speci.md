# Master Architecture & Functional Specification: Foundry.Schema Visual & AI Designer

## 1. Goal & Philosophy
Design and implement a responsive, high-fidelity web-based UML Class Diagram editor and workspace environment named `Foundry.Schema.Studio`. The primary objective is to allow users (or an AI agent) to visually architect domain entity models down to precise specifications, automatically compiling the resulting layout into a clean, platform-agnostic intermediate JSON Schema. This JSON Schema will be fed into a design-time compiler engine to emit production-ready C# POCO classes optimized to plug directly into the `Foundry.Mongo` runtime framework without manual coding.

## 2. Technical Stack & Layout Architecture
- **Frontend Core:** React 19+ (or Blazor WebAssembly) styled with TailwindCSS for a minimal, clean developer IDE theme.
- **Canvas Engine:** React Flow (XYFlow) or GoJS utilizing customized HTML/SVG components to strictly enforce rigid, non-curved UML shapes.
- **State Management:** Zustand, Redux Toolkit, or a native state container managing the canvas nodes, relational coordinates, and property state.
- **Target C# Framework:** .NET 8+ / C# matching modern language paradigms (File-scoped namespaces, Primary Constructors/Records, and Attribute-driven configurations).

## 3. High-Fidelity UML Canvas Component Specifications

### A. The StarUML-Style Class Node Component
Implement a custom canvas node component designed with sharp corners (`rounded-none`) and distinct charcoal borders (`border-slate-800`). The node must feature three strictly separated structural compartments:
1. **Header Compartment:** Displays the class stereotype center-aligned (e.g., `«Entity»` or `«ValueObject»`), the bolded Class Name, and quick-toggle indicator badges for global framework traits (e.g., `[SoftDelete]`, `[Auditable]`).
2. **Attributes Compartment:** Standardized grid listing entity properties using exact UML visibility notation (`+` for public, `-` for private), the property name, data type mappings (e.g., `string`, `ObjectId`, `int`), and frame annotations for index rules (`[Index]`, `[Unique]`).
3. **Methods/Actions Compartment:** Lists entity domain behaviors or domain query targets.
- *UX Interaction:* Double-clicking any specific element row or header instantly enables an inline edit field or pulls open a sidebar management utility pane to modify the object data properties live.

### B. Orthogonal Relationship Edges
- **Rigid Orthogonal Routing:** Connections between class boxes must automatically align to sharp $90^\circ$ angles. Curved or straight diagonal links are strictly disallowed.
- **UML-Standard Arrowheads:** The layout system must draw precise SVG connectors reflecting real relationship data rules:
  - **Generalization / Inheritance:** A solid line terminating with an open, hollow triangle arrowhead (\u25B3) pointing to the parent base class.
  - **Composition (Embedded Document):** A solid line terminating with a filled diamond arrowhead (\u25C6) anchored to the parent container document.
  - **Association (Referenced Document / ID Lookup):** A simple directed line terminating with a standard open arrow pointing to the foreign reference target.

### C. The Unified IDE Screen Workspace Layout
- **Left Toolbox Panel:** Drag-and-drop tool palette featuring: "Class Box", "Enum Box", "Inheritance Link", "Composition Link", and "Association Link".
- **Center Visual Canvas:** The grid-backed, infinite panning interactive canvas managing the UML assembly.
- **Right Property Inspector Sidebar:** Contextually displays extensive detailed parameters for the currently active node or highlighted property row. Features clear interface fields for:
  - BSON Custom Parameter Mappings
  - Index Names and composite multi-key indexes arrays
  - Data validation annotations (e.g., `[BsonRequired]`, String Lengths)

## 4. The Intermediate JSON Schema Specification
The engine must serialize the active canvas layout state into a clean, detached intermediate format. The generator backend must not process raw graphical XML/JSON coordinate blocks directly. The format must match this structure:

```json
{
  "Namespace": "Paperclip.OrderingSystem.Domain",
  "Entities": [
    {
      "Name": "Order",
      "SoftDelete": true,
      "Auditable": true,
      "Indexes": [ { "Fields": ["CustomerId"], "Unique": false } ],
      "Properties": [
        { "Name": "Id", "Type": "ObjectId", "IsKey": true },
        { "Name": "OrderNumber", "Type": "string", "Attributes": ["Required"] },
        { "Name": "TotalAmount", "Type": "decimal" },
        { "Name": "Status", "Type": "OrderStatus", "IsEnum": true }
      ]
    }
  ]
}