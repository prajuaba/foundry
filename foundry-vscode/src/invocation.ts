import * as path from 'path';
import * as fs from 'fs';

/**
 * How the Foundry toolchain is invoked from the extension.
 *
 * Deliberately free of any `vscode` import, so it can be tested outside an editor. The bug this
 * file exists to prevent was in exactly this code and was invisible without running VS Code: the
 * compiler was invoked with positional arguments — `dotnet run --project ... -- <in> <out>` — while
 * it declares `--input` and `--output`. Every invocation exited with "Both --input and --output are
 * required" having written nothing, and the caller reported success regardless.
 */

/** Where the framework's own compiler project lives, relative to a workspace root. */
export const COMPILER_PROJECT = path.join('foundry-schema', 'compiler', 'Foundry.Schema.Compiler.csproj');

/** Where a built CLI lives, relative to a workspace root. */
export function cliDllPath(configuration: 'Release' | 'Debug' = 'Release'): string {
  return path.join('foundry-cli', 'src', 'Foundry.Cli', 'bin', configuration, 'net10.0', 'foundry.dll');
}

export type Exists = (p: string) => boolean;

export interface Invocation {
  readonly command: string;
  readonly args: readonly string[];
}

export interface CompilePlan extends Invocation {
  readonly cwd: string;
  readonly outDir: string;
  readonly manifestPath: string;
}

/**
 * How to invoke the Foundry CLI: its built dll, or a `foundry` on PATH.
 *
 * Shared by every command that shells out, so they cannot disagree about where the tool is.
 */
export function resolveCliCommand(workspaceRoot: string, exists: Exists = fs.existsSync): Invocation {
  for (const configuration of ['Release', 'Debug'] as const) {
    const dll = path.join(workspaceRoot, cliDllPath(configuration));
    if (exists(dll)) return { command: 'dotnet', args: [dll] };
  }

  return { command: 'foundry', args: [] };
}

/**
 * Builds the compiler invocation for a schema file.
 *
 * Prefers a built CLI, falls back to `dotnet run` against the compiler project, and finally to a
 * `foundry` on PATH. Whichever is used, the flags are the ones the compiler declares.
 */
export function planCompile(
  workspaceRoot: string,
  schemaPath: string,
  exists: Exists = fs.existsSync,
): CompilePlan {
  const outDir = path.join(path.dirname(schemaPath), 'Generated');
  const manifestPath = path.join(path.dirname(schemaPath), 'api-manifest.json');

  const compilerArgs = [
    '--input', schemaPath,
    '--output', outDir,
    '--manifest', manifestPath,
  ];

  for (const configuration of ['Release', 'Debug'] as const) {
    const dll = path.join(workspaceRoot, cliDllPath(configuration));
    if (exists(dll)) {
      return {
        command: 'dotnet',
        args: [dll, 'schema', 'build', ...compilerArgs],
        cwd: workspaceRoot, outDir, manifestPath,
      };
    }
  }

  const compilerProject = path.join(workspaceRoot, COMPILER_PROJECT);
  if (exists(compilerProject)) {
    return {
      command: 'dotnet',
      args: ['run', '--project', compilerProject, '--', ...compilerArgs],
      cwd: workspaceRoot, outDir, manifestPath,
    };
  }

  return {
    command: 'foundry',
    args: ['schema', 'build', ...compilerArgs],
    cwd: workspaceRoot, outDir, manifestPath,
  };
}

/**
 * How to launch the language server.
 *
 * The README advertises LSP integration and there was no client at all, so `foundry lsp` — which
 * speaks the protocol and had its framing bug fixed — was connected to nothing.
 */
export function resolveServerCommand(
  workspaceRoot: string | undefined,
  exists: Exists = fs.existsSync,
): Invocation {
  if (workspaceRoot) {
    for (const configuration of ['Release', 'Debug'] as const) {
      const dll = path.join(workspaceRoot, cliDllPath(configuration));
      if (exists(dll)) return { command: 'dotnet', args: [dll, 'lsp'] };
    }
  }

  return { command: 'foundry', args: ['lsp'] };
}

/**
 * Names the IR file beside a canvas file: <c>orders.foundry.json</c> becomes <c>orders.ir.json</c>.
 *
 * Mirrors `Foundry.Cli.Program.DefaultIrPathFor`, and the two are pinned to each other by a test —
 * the extension has to open the file the CLI just wrote, and guessing a different name would show
 * the user "migration succeeded" followed by nothing opening.
 */
export function defaultIrPathFor(canvasPath: string): string {
  const dir = path.dirname(canvasPath);
  let name = path.basename(canvasPath);

  for (const suffix of ['.foundry.json', '.canvas.json', '.foundry', '.json']) {
    if (name.toLowerCase().endsWith(suffix)) {
      name = name.slice(0, name.length - suffix.length);
      break;
    }
  }

  return path.join(dir, `${name}.ir.json`);
}
