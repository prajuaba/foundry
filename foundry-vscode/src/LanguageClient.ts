import * as vscode from 'vscode';
import {
  LanguageClient,
  LanguageClientOptions,
  ServerOptions,
  TransportKind,
} from 'vscode-languageclient/node';
import { resolveServerCommand } from './invocation';

/**
 * Starts `foundry lsp` and connects it to IR documents.
 *
 * The README has advertised "Native VS Code Extension & LSP Server integration" since the extension
 * was written. There was no language client in it: no `vscode-languageclient` dependency, and no
 * reference to the LSP anywhere in the source. `foundry lsp` exists, speaks the protocol, and had
 * its framing bug fixed — and nothing had ever connected to it, so the diagnostics and completions
 * it produces reached no editor.
 */

let client: LanguageClient | undefined;

export async function startLanguageClient(context: vscode.ExtensionContext): Promise<void> {
  const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  const resolved = resolveServerCommand(workspaceRoot);

  const serverOptions: ServerOptions = {
    run: { command: resolved.command, args: [...resolved.args], transport: TransportKind.stdio },
    debug: { command: resolved.command, args: [...resolved.args], transport: TransportKind.stdio },
  };

  const clientOptions: LanguageClientOptions = {
    // IR documents only. A canvas file is not IR, and reporting "this declares no entities" against
    // a diagram would be noise on a file that is not meant to have any.
    documentSelector: [
      { scheme: 'file', pattern: '**/*.ir.json' },
    ],
    synchronize: {
      fileEvents: vscode.workspace.createFileSystemWatcher('**/*.ir.json'),
    },
    outputChannelName: 'Foundry Language Server',
  };

  client = new LanguageClient('foundryLsp', 'Foundry Language Server', serverOptions, clientOptions);

  try {
    await client.start();
    context.subscriptions.push({ dispose: () => { void client?.stop(); } });
  } catch (error) {
    // Said out loud rather than swallowed. An extension that quietly runs without its language
    // server looks identical to one whose schema has no problems.
    void vscode.window.showWarningMessage(
      `Foundry: the language server could not start (${resolved.command}). IR diagnostics are `
      + `unavailable. Build the CLI, or put 'foundry' on your PATH. ${error instanceof Error ? error.message : ''}`,
    );
    client = undefined;
  }
}

export async function stopLanguageClient(): Promise<void> {
  await client?.stop();
  client = undefined;
}
