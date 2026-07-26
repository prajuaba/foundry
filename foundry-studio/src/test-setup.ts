/**
 * Minimal browser globals required to import the Zustand store outside a browser.
 *
 * `store.ts` reads `localStorage` at module scope to restore the configured Ollama host and model,
 * so the module cannot be imported at all without it.
 */
class MemoryStorage implements Storage {
  private readonly entries = new Map<string, string>();

  get length(): number {
    return this.entries.size;
  }

  clear(): void {
    this.entries.clear();
  }

  getItem(key: string): string | null {
    return this.entries.has(key) ? this.entries.get(key)! : null;
  }

  key(index: number): string | null {
    return [...this.entries.keys()][index] ?? null;
  }

  removeItem(key: string): void {
    this.entries.delete(key);
  }

  setItem(key: string, value: string): void {
    this.entries.set(key, String(value));
  }
}

globalThis.localStorage = new MemoryStorage();
