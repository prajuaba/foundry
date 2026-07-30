import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { FoundryEditorProvider } from './FoundryEditorProvider';
import { FoundrySidebarProvider } from './FoundrySidebarProvider';
import { compilerService, runCompile } from './CompilerService';
import { defaultIrPathFor, resolveCliCommand } from './invocation';
import { startLanguageClient, stopLanguageClient } from './LanguageClient';

export function activate(context: vscode.ExtensionContext) {
  // The language server, which the README has advertised since this extension was written and which
  // nothing had ever connected to. Started without awaiting: a server that is slow to come up must
  // not delay the editor, and the client reports its own failure.
  void startLanguageClient(context);

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

      const fileName = `${domainName.toLowerCase()}.ir.json`;
      let filePath: string;

      const workspaceFolders = vscode.workspace.workspaceFolders;
      if (workspaceFolders && workspaceFolders.length > 0) {
        filePath = path.join(workspaceFolders[0].uri.fsPath, fileName);
      } else {
        const saveUri = await vscode.window.showSaveDialog({
          defaultUri: vscode.Uri.file(fileName),
          filters: { 'Foundry IR': ['ir.json', 'json'] },
          saveLabel: 'Create Foundry Schema'
        });
        if (!saveUri) return;
        filePath = saveUri.fsPath;
      }

      // Normative IR, which is what the compiler consumes.
      //
      // This used to write a Studio *canvas* document -- nodes, edges, positions -- and the compiler
      // rejects those outright: `foundry validate` answers FDY1010 ("Document is in Studio canvas
      // format, which the compiler does not consume. No code would be generated") and FDY1002. So
      // the command named "Create New Schema Manifest" produced a file the rest of the toolchain
      // refused, and it had been that way since the IR became normative.
      //
      // A canvas file is still a legitimate thing to hold -- it is the layout Studio draws from --
      // but it is not the schema, and `foundry migrate` converts one to the other.
      const initialSchema = {
        namespace: domainName,
        version: '1.0.0',
        entities: [
          {
            name: 'User',
            softDelete: true,
            apiEnabledMethods: ['GET', 'GET_BY_ID', 'POST', 'PUT', 'DELETE'],
            properties: [
              { name: 'Id', type: 'ObjectId', isKey: true },
              { name: 'Email', type: 'string', attributes: ['Unique', 'Required'] },
              { name: 'FullName', type: 'string', attributes: ['Required'] }
            ]
          }
        ]
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

  // Command: Migrate a Studio canvas document to IR
  context.subscriptions.push(
    vscode.commands.registerCommand('foundry.migrateSchema', async () => {
      const activeEditor = vscode.window.activeTextEditor;
      if (!activeEditor) {
        vscode.window.showErrorMessage('Foundry: no active file to migrate.');
        return;
      }

      await runFoundry(activeEditor.document.uri, ['migrate', activeEditor.document.uri.fsPath],
        'migrate', async () => {
          const irPath = defaultIrPathFor(activeEditor.document.uri.fsPath);
          if (fs.existsSync(irPath)) {
            const doc = await vscode.workspace.openTextDocument(vscode.Uri.file(irPath));
            await vscode.window.showTextDocument(doc);
          }
        });
    })
  );

  // Command: Validate the active IR document
  context.subscriptions.push(
    vscode.commands.registerCommand('foundry.validateSchema', async () => {
      const activeEditor = vscode.window.activeTextEditor;
      if (!activeEditor) {
        vscode.window.showErrorMessage('Foundry: no active file to validate.');
        return;
      }

      await runFoundry(activeEditor.document.uri, ['validate', activeEditor.document.uri.fsPath], 'validate');
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
        // Parsed only to fail early on malformed JSON with a better message than the compiler's.
        // The compiler reads the file itself, so what is on disk is what gets compiled.
        JSON.parse(activeEditor.document.getText());
      } catch (err: any) {
        vscode.window.showErrorMessage(`Foundry: invalid JSON in the active file: ${err.message}`);
        return;
      }

      await compilerService.compileSchema(activeEditor.document.uri);
    })
  );
}

/**
 * Runs a `foundry` subcommand and reports the outcome from its exit code.
 *
 * Never reports success on failure. The compile command used to answer every error with a success
 * notification, so a malformed invocation that wrote nothing looked exactly like a good build.
 */
async function runFoundry(
  documentUri: vscode.Uri,
  args: string[],
  label: string,
  onSuccess?: () => Promise<void>,
): Promise<void> {
  const folder = vscode.workspace.getWorkspaceFolder(documentUri);
  const workspaceRoot = folder ? folder.uri.fsPath : path.dirname(documentUri.fsPath);
  const resolved = resolveCliCommand(workspaceRoot);

  const result = await vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: `Foundry: ${label}…`, cancellable: false },
    () => runCompile({
      command: resolved.command,
      args: [...resolved.args, ...args],
      cwd: workspaceRoot,
      outDir: workspaceRoot,
      manifestPath: '',
    }),
  );

  compilerService.log(`$ ${resolved.command} ${[...resolved.args, ...args].join(' ')}`);
  if (result.output) compilerService.log(result.output);

  if (!result.ok) {
    compilerService.show();
    void vscode.window.showErrorMessage(
      `Foundry: ${label} failed (exit ${result.exitCode}). See the Foundry output channel.`);
    return;
  }

  void vscode.window.showInformationMessage(`Foundry: ${label} succeeded.`);
  await onSuccess?.();
}

export function deactivate(): Thenable<void> {
  return stopLanguageClient();
}
