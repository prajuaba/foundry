import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import { compilerService } from './CompilerService';

export class FoundryEditorProvider implements vscode.CustomTextEditorProvider {
  public static readonly viewType = 'foundry.studioEditor';

  constructor(private readonly context: vscode.ExtensionContext) {}

  public static register(context: vscode.ExtensionContext): vscode.Disposable {
    const provider = new FoundryEditorProvider(context);
    return vscode.window.registerCustomEditorProvider(
      FoundryEditorProvider.viewType,
      provider,
      {
        webviewOptions: {
          retainContextWhenHidden: true
        },
        supportsMultipleEditorsPerDocument: false
      }
    );
  }

  async resolveCustomTextEditor(
    document: vscode.TextDocument,
    webviewPanel: vscode.WebviewPanel,
    _token: vscode.CancellationToken
  ): Promise<void> {
    const localStudioDir = path.resolve(this.context.extensionPath, 'dist-studio');
    const devStudioDir = path.resolve(this.context.extensionPath, '../foundry-studio/dist');
    const distDir = fs.existsSync(localStudioDir) ? localStudioDir : devStudioDir;

    webviewPanel.webview.options = {
      enableScripts: true,
      localResourceRoots: [
        vscode.Uri.file(distDir),
        vscode.Uri.file(this.context.extensionPath)
      ]
    };

    webviewPanel.webview.html = this.getHtmlContent(webviewPanel, distDir);

    let isUpdatingFromWebview = false;

    const sendInitialData = () => {
      let projectData = {};
      try {
        const text = document.getText();
        if (text.trim()) {
          projectData = JSON.parse(text);
        }
      } catch {}

      webviewPanel.webview.postMessage({
        type: 'loadProject',
        data: projectData
      });
    };

    // Send immediately after webview panel initialization
    setTimeout(sendInitialData, 300);

    // Handle incoming messages from the React Webview
    webviewPanel.webview.onDidReceiveMessage(async (message) => {
      switch (message.type) {
        case 'webviewReady': {
          sendInitialData();
          return;
        }

        case 'updateProject': {
          isUpdatingFromWebview = true;
          const edit = new vscode.WorkspaceEdit();
          const fullRange = new vscode.Range(
            document.positionAt(0),
            document.positionAt(document.getText().length)
          );
          edit.replace(document.uri, fullRange, JSON.stringify(message.data, null, 2));
          await vscode.workspace.applyEdit(edit);
          isUpdatingFromWebview = false;
          return;
        }

        case 'compileSchema': {
          await compilerService.compileSchema(document.uri, message.schema || JSON.parse(document.getText() || '{}'));
          return;
        }
      }
    });

    // Listen for external changes to the text document
    const changeDocumentListener = vscode.workspace.onDidChangeTextDocument((e) => {
      if (e.document.uri.toString() === document.uri.toString() && !isUpdatingFromWebview) {
        try {
          const updatedData = JSON.parse(e.document.getText());
          webviewPanel.webview.postMessage({
            type: 'loadProject',
            data: updatedData
          });
        } catch {}
      }
    });

    webviewPanel.onDidDispose(() => {
      changeDocumentListener.dispose();
    });
  }

  private getHtmlContent(webviewPanel: vscode.WebviewPanel, distDir: string): string {
    const indexPath = path.join(distDir, 'index.html');

    if (!fs.existsSync(indexPath)) {
      return `<!DOCTYPE html>
      <html>
        <body style="padding: 2rem; color: #f87171; font-family: sans-serif;">
          <h2>Foundry Studio Build Artifacts Not Found</h2>
          <p>Please run <code>npm run build</code> inside <code>foundry-studio</code> to compile the visual UI.</p>
        </body>
      </html>`;
    }

    let html = fs.readFileSync(indexPath, 'utf8');

    // Inject permissive Content-Security-Policy for VS Code Webview Engine
    const cspMeta = `<meta http-equiv="Content-Security-Policy" content="default-src * 'unsafe-inline' 'unsafe-eval' data: blob:; style-src * 'unsafe-inline'; script-src * 'unsafe-inline' 'unsafe-eval' data: blob:;">`;

    if (!html.includes('Content-Security-Policy')) {
      html = html.replace('<head>', `<head>\n    ${cspMeta}`);
    }

    return html;
  }
}
