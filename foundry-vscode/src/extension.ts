import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { FoundryEditorProvider } from './FoundryEditorProvider';
import { FoundrySidebarProvider } from './FoundrySidebarProvider';
import { compilerService } from './CompilerService';

export function activate(context: vscode.ExtensionContext) {
  console.log('Foundry Studio IDE extension activated.');

  // Register Custom Text Editor Provider
  context.subscriptions.push(FoundryEditorProvider.register(context));

  // Register Activity Bar Sidebar Provider
  const sidebarProvider = new FoundrySidebarProvider(context);
  context.subscriptions.push(
    vscode.window.registerTreeDataProvider('foundry.explorerView', sidebarProvider)
  );

  // Command: Create New Schema Manifest
  context.subscriptions.push(
    vscode.commands.registerCommand('foundry.newSchema', async () => {
      const domainName = await vscode.window.showInputBox({
        prompt: 'Enter Domain Namespace (e.g. ECommerce, Inventory, Identity)',
        value: 'MyDomain'
      });

      if (!domainName) return;

      const fileName = `${domainName.toLowerCase()}.foundry.json`;
      let filePath: string;

      const workspaceFolders = vscode.workspace.workspaceFolders;
      if (workspaceFolders && workspaceFolders.length > 0) {
        filePath = path.join(workspaceFolders[0].uri.fsPath, fileName);
      } else {
        const saveUri = await vscode.window.showSaveDialog({
          defaultUri: vscode.Uri.file(fileName),
          filters: { 'Foundry Schema': ['foundry.json', 'foundry', 'json'] },
          saveLabel: 'Create Foundry Schema'
        });
        if (!saveUri) return;
        filePath = saveUri.fsPath;
      }

      const initialSchema = {
        namespace: domainName,
        nodes: [
          {
            id: 'node-1',
            type: 'classNode',
            position: { x: 250, y: 150 },
            data: {
              Name: 'User',
              BaseClass: '',
              SoftDelete: true,
              Properties: [
                { Name: 'Id', Type: 'ObjectId', IsKey: true, Attributes: [] },
                { Name: 'Email', Type: 'string', IsKey: false, Attributes: ['Unique', 'Required'] },
                { Name: 'FullName', Type: 'string', IsKey: false, Attributes: ['Required'] }
              ],
              Indexes: []
            }
          }
        ],
        edges: [],
        customEndpoints: [],
        dtos: [],
        workflows: []
      };

      fs.writeFileSync(filePath, JSON.stringify(initialSchema, null, 2));

      const docUri = vscode.Uri.file(filePath);
      await vscode.commands.executeCommand('vscode.openWith', docUri, FoundryEditorProvider.viewType);
      vscode.window.showInformationMessage(`Created ${fileName} and opened in Foundry Studio.`);
      sidebarProvider.refresh();
    })
  );

  // Command: Open Active Schema in Foundry Studio
  context.subscriptions.push(
    vscode.commands.registerCommand('foundry.openStudio', async () => {
      const activeEditor = vscode.window.activeTextEditor;
      if (activeEditor) {
        await vscode.commands.executeCommand('vscode.openWith', activeEditor.document.uri, FoundryEditorProvider.viewType);
      } else {
        await vscode.commands.executeCommand('foundry.newSchema');
      }
    })
  );

  // Command: Compile Active Schema
  context.subscriptions.push(
    vscode.commands.registerCommand('foundry.compileSchema', async () => {
      const activeEditor = vscode.window.activeTextEditor;
      if (!activeEditor) {
        vscode.window.showErrorMessage('Foundry: No active schema file open to compile.');
        return;
      }

      try {
        const schemaData = JSON.parse(activeEditor.document.getText());
        await compilerService.compileSchema(activeEditor.document.uri, schemaData);
      } catch (err: any) {
        vscode.window.showErrorMessage(`Foundry: Invalid JSON in active file: ${err.message}`);
      }
    })
  );
}

export function deactivate() {}
