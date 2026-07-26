import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { FoundryEditorProvider } from './FoundryEditorProvider';

export class FoundrySidebarProvider implements vscode.TreeDataProvider<FoundryTreeItem> {
  private _onDidChangeTreeData: vscode.EventEmitter<FoundryTreeItem | undefined | null | void> = new vscode.EventEmitter<FoundryTreeItem | undefined | null | void>();
  readonly onDidChangeTreeData: vscode.Event<FoundryTreeItem | undefined | null | void> = this._onDidChangeTreeData.event;

  constructor(private context: vscode.ExtensionContext) {
    vscode.workspace.onDidSaveTextDocument(() => this.refresh());
    vscode.workspace.onDidCreateFiles(() => this.refresh());
    vscode.workspace.onDidDeleteFiles(() => this.refresh());
  }

  refresh(): void {
    this._onDidChangeTreeData.fire();
  }

  getTreeItem(element: FoundryTreeItem): vscode.TreeItem {
    return element;
  }

  async getChildren(element?: FoundryTreeItem): Promise<FoundryTreeItem[]> {
    if (element) {
      return [];
    }

    const items: FoundryTreeItem[] = [];

    // Action 1: Create New Schema
    items.push(
      new FoundryTreeItem(
        'Create New Schema Manifest',
        'Click to create a new domain schema JSON file',
        vscode.TreeItemCollapsibleState.None,
        {
          command: 'foundry.newSchema',
          title: 'Create New Schema'
        },
        new vscode.ThemeIcon('add')
      )
    );

    // Action 2: Compile Active Schema
    items.push(
      new FoundryTreeItem(
        'Compile Schema to C# Code',
        'Click to generate C# POCOs and API routes',
        vscode.TreeItemCollapsibleState.None,
        {
          command: 'foundry.compileSchema',
          title: 'Compile Schema'
        },
        new vscode.ThemeIcon('gear')
      )
    );

    // List all .foundry.json files in open workspace
    const workspaceFolders = vscode.workspace.workspaceFolders;
    if (workspaceFolders && workspaceFolders.length > 0) {
      const foundryFiles = await vscode.workspace.findFiles('**/*.foundry.json');
      if (foundryFiles.length > 0) {
        items.push(
          new FoundryTreeItem(
            `Workspace Schemas (${foundryFiles.length})`,
            'Foundry domain schemas in workspace',
            vscode.TreeItemCollapsibleState.Expanded,
            undefined,
            new vscode.ThemeIcon('database')
          )
        );

        for (const file of foundryFiles) {
          const fileName = path.basename(file.fsPath);
          items.push(
            new FoundryTreeItem(
              fileName,
              file.fsPath,
              vscode.TreeItemCollapsibleState.None,
              {
                command: 'vscode.openWith',
                title: 'Open in Foundry Studio',
                arguments: [file, FoundryEditorProvider.viewType]
              },
              new vscode.ThemeIcon('symbol-structure')
            )
          );
        }
      }
    }

    return items;
  }
}

export class FoundryTreeItem extends vscode.TreeItem {
  constructor(
    public readonly label: string,
    public readonly descriptionStr: string,
    public readonly collapsibleState: vscode.TreeItemCollapsibleState,
    public readonly command?: vscode.Command,
    public readonly customIcon?: vscode.ThemeIcon
  ) {
    super(label, collapsibleState);
    this.tooltip = `${this.label} - ${this.descriptionStr}`;
    this.description = descriptionStr;
    if (customIcon) {
      this.iconPath = customIcon;
    }
  }
}
