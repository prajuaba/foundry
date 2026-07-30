import * as path from 'path';
import { describe, expect, it } from 'vitest';
import {
  cliDllPath,
  defaultIrPathFor,
  planCompile,
  resolveCliCommand,
  resolveServerCommand,
} from './invocation';

/**
 * How the extension invokes the toolchain.
 *
 * The extension had no tests of any kind and was in no CI job, and the defect that hid there was
 * exactly the sort a typecheck cannot see: the compiler was invoked with positional arguments while
 * it declares `--input` and `--output`, so every compile exited with "Both --input and --output are
 * required" having written nothing — and the caller answered every failure with a success
 * notification, so nobody ever saw it.
 */

const ROOT = path.join('/', 'workspace');
const SCHEMA = path.join(ROOT, 'domain', 'orders.ir.json');

/** Pretends the given paths exist and nothing else does. */
const only = (...present: string[]) => (p: string) => present.includes(p);
const nothing = () => false;

describe('planCompile', () => {
  it('passes the schema and output as named flags, not positionally', () => {
    // The regression guard. `dotnet run --project X -- <in> <out>` is what this used to emit, and
    // the compiler rejects it outright.
    const plan = planCompile(ROOT, SCHEMA, nothing);

    expect(plan.args).toContain('--input');
    expect(plan.args[plan.args.indexOf('--input') + 1]).toBe(SCHEMA);
    expect(plan.args).toContain('--output');
    expect(plan.args[plan.args.indexOf('--output') + 1]).toBe(plan.outDir);
  });

  it('asks for the manifest, without which the app serves no routes', () => {
    const plan = planCompile(ROOT, SCHEMA, nothing);

    expect(plan.args).toContain('--manifest');
    expect(plan.manifestPath).toBe(path.join(path.dirname(SCHEMA), 'api-manifest.json'));
  });

  it('writes generated code beside the schema rather than over it', () => {
    const plan = planCompile(ROOT, SCHEMA, nothing);

    expect(plan.outDir).toBe(path.join(path.dirname(SCHEMA), 'Generated'));
    expect(plan.outDir).not.toBe(path.dirname(SCHEMA));
  });

  it('prefers a built CLI when the workspace has one', () => {
    const dll = path.join(ROOT, cliDllPath('Release'));

    const plan = planCompile(ROOT, SCHEMA, only(dll));

    expect(plan.command).toBe('dotnet');
    expect(plan.args.slice(0, 3)).toEqual([dll, 'schema', 'build']);
  });

  it('falls back to the compiler project when there is no built CLI', () => {
    const project = path.join(ROOT, 'foundry-schema', 'compiler', 'Foundry.Schema.Compiler.csproj');

    const plan = planCompile(ROOT, SCHEMA, only(project));

    expect(plan.command).toBe('dotnet');
    expect(plan.args.slice(0, 4)).toEqual(['run', '--project', project, '--']);
  });

  it('falls back to a foundry on PATH outside the framework repository', () => {
    // The ordinary case for someone using the extension on their own project.
    const plan = planCompile(ROOT, SCHEMA, nothing);

    expect(plan.command).toBe('foundry');
    expect(plan.args.slice(0, 2)).toEqual(['schema', 'build']);
  });
});

describe('resolveCliCommand', () => {
  it('uses a Release build when both configurations are present', () => {
    const release = path.join(ROOT, cliDllPath('Release'));
    const debug = path.join(ROOT, cliDllPath('Debug'));

    expect(resolveCliCommand(ROOT, only(release, debug)).args[0]).toBe(release);
  });

  it('accepts a Debug build, which is what a developer has just built', () => {
    const debug = path.join(ROOT, cliDllPath('Debug'));

    expect(resolveCliCommand(ROOT, only(debug)).args[0]).toBe(debug);
  });

  it('falls back to PATH', () => {
    expect(resolveCliCommand(ROOT, nothing)).toEqual({ command: 'foundry', args: [] });
  });
});

describe('resolveServerCommand', () => {
  it('runs the language server the CLI provides', () => {
    const dll = path.join(ROOT, cliDllPath('Release'));

    expect(resolveServerCommand(ROOT, only(dll))).toEqual({ command: 'dotnet', args: [dll, 'lsp'] });
  });

  it('falls back to PATH with no workspace at all', () => {
    expect(resolveServerCommand(undefined, nothing)).toEqual({ command: 'foundry', args: ['lsp'] });
  });
});

describe('defaultIrPathFor', () => {
  // Pinned to Foundry.Cli.Program.DefaultIrPathFor. The extension opens the file the CLI just
  // wrote, so a different guess would report "migration succeeded" and open nothing.
  it.each([
    ['orders.foundry.json', 'orders.ir.json'],
    ['orders.canvas.json', 'orders.ir.json'],
    ['orders.foundry', 'orders.ir.json'],
    ['orders.json', 'orders.ir.json'],
    ['orders', 'orders.ir.json'],
  ])('%s becomes %s', (input, expected) => {
    expect(defaultIrPathFor(path.join(ROOT, input))).toBe(path.join(ROOT, expected));
  });

  it('does not double up a compound extension', () => {
    expect(defaultIrPathFor(path.join(ROOT, 'orders.foundry.json')))
      .not.toContain('foundry.ir.json');
  });
});
