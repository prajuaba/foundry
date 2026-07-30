import * as vscode from 'vscode';
import * as path from 'path';
import * as fs from 'fs';
import * as child_process from 'child_process';
import { CompilePlan, planCompile, resolveCliCommand } from './invocation';

export { CompilePlan, planCompile, resolveCliCommand };

export interface CompileResult {
  readonly ok: boolean;
  readonly exitCode: number;
  readonly output: string;
}

/** Runs a plan and reports what actually happened. */
export function runCompile(plan: CompilePlan): Promise<CompileResult> {
  return new Promise((resolve) => {
    child_process.execFile(
      plan.command,
      [...plan.args],
      { cwd: plan.cwd, maxBuffer: 10 * 1024 * 1024 },
      (error, stdout, stderr) => {
        const output = [stdout, stderr].filter(Boolean).join('\n').trim();
        const exitCode = error ? ((error as unknown as { code?: number }).code ?? 1) : 0;
        resolve({ ok: !error, exitCode, output });
      },
    );
  });
}

export class CompilerService {
  private static instance: CompilerService;
  private readonly channel: vscode.OutputChannel;

  private constructor() {
    this.channel = vscode.window.createOutputChannel('Foundry');
  }

  public static getInstance(): CompilerService {
    if (!CompilerService.instance) {
      CompilerService.instance = new CompilerService();
    }
    return CompilerService.instance;
  }

  /**
   * Compiles a schema document and reports the result honestly.
   *
   * The previous version caught every failure and answered it with
   * `showInformationMessage("Foundry: Schema compiled! Saved manifest successfully.")`, discarding
   * stdout and stderr. Since the invocation itself was malformed, that success notice was the only
   * thing the user ever saw: the compiler failed every time and said so to nobody.
   */
  public async compileSchema(documentUri: vscode.Uri): Promise<void> {
    const workspaceFolder = vscode.workspace.getWorkspaceFolder(documentUri);
    const workspaceRoot = workspaceFolder ? workspaceFolder.uri.fsPath : path.dirname(documentUri.fsPath);

    // The document on disk is what the compiler reads, so what is on screen has to be there first.
    // Writing a temporary file beside it -- as this used to -- compiled a copy under a name the
    // diagnostics then referred to, and left it behind whenever the run died mid-way.
    const document = await vscode.workspace.openTextDocument(documentUri);
    if (document.isDirty) {
      await document.save();
    }

    const schemaPath = documentUri.fsPath;
    const plan = planCompile(workspaceRoot, schemaPath);

    const result = await vscode.window.withProgress(
      { location: vscode.ProgressLocation.Notification, title: 'Foundry: compiling schema…', cancellable: false },
      () => runCompile(plan),
    );

    this.channel.appendLine(`$ ${plan.command} ${plan.args.join(' ')}`);
    if (result.output) this.channel.appendLine(result.output);

    if (!result.ok) {
      this.channel.show(true);
      void vscode.window.showErrorMessage(
        `Foundry: schema compilation failed (exit ${result.exitCode}). See the Foundry output channel.`,
      );
      return;
    }

    const written = countGeneratedFiles(plan.outDir);
    void vscode.window.showInformationMessage(
      `Foundry: compiled ${path.basename(schemaPath)} — ${written} file(s) in ${path.basename(plan.outDir)}/.`,
    );
  }

  /** Surfaces the compiler's own output, for a caller that wants to explain a failure. */
  public show(): void {
    this.channel.show(true);
  }

  public log(message: string): void {
    this.channel.appendLine(message);
  }
}

/** Counts what the compiler left behind, so the success message reports a fact rather than a hope. */
export function countGeneratedFiles(dir: string, io = fs): number {
  if (!io.existsSync(dir)) return 0;

  let count = 0;
  for (const entry of io.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, entry.name);
    count += entry.isDirectory() ? countGeneratedFiles(full, io) : 1;
  }
  return count;
}

export const compilerService = CompilerService.getInstance();
