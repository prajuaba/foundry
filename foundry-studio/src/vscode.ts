// VS Code Webview Messaging API Bridge
declare function acquireVsCodeApi(): VsCodeApi;

export interface VsCodeApi {
  postMessage: (message: any) => void;
  setState: (state: any) => void;
  getState: () => any;
}

let vsCodeApi: VsCodeApi | null = null;
let isAcquired = false;

export function getVsCodeApi(): VsCodeApi | null {
  if (!vsCodeApi && !isAcquired) {
    if (typeof acquireVsCodeApi === 'function') {
      try {
        vsCodeApi = acquireVsCodeApi();
        isAcquired = true;
      } catch (e) {
        console.warn('VS Code API acquisition error:', e);
      }
    }
  }
  return vsCodeApi;
}

export function isVsCodeEnvironment(): boolean {
  if (vsCodeApi || isAcquired) return true;
  if (typeof acquireVsCodeApi === 'function') {
    getVsCodeApi();
    return vsCodeApi !== null;
  }
  return false;
}

export function postMessageToVsCode(message: any): void {
  const vscode = getVsCodeApi();
  if (vscode) {
    vscode.postMessage(message);
  }
}

export function initVsCodeBridge(callbacks: { 
  onLoadProject: (data: any) => void; 
  onCompileRequest?: () => void;
}): () => void {
  const handleMessage = (event: MessageEvent) => {
    const message = event.data;
    if (!message || !message.type) return;
    
    switch (message.type) {
      case 'loadProject':
        if (message.data && callbacks.onLoadProject) {
          callbacks.onLoadProject(message.data);
        }
        break;
      case 'compileSchema':
        if (callbacks.onCompileRequest) {
          callbacks.onCompileRequest();
        }
        break;
    }
  };

  window.addEventListener('message', handleMessage);
  
  // Notify VS Code extension host that Webview is ready
  postMessageToVsCode({ type: 'webviewReady' });

  return () => {
    window.removeEventListener('message', handleMessage);
  };
}
