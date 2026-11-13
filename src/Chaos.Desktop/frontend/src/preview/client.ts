/**
 * Replaces `@neoastra/client` in the browser preview build (see vite.preview.config.ts):
 * the generated RPC objects call `invoke`, which goes to the in-memory backend instead of
 * the desktop host.
 */
import { handle } from "./backend";

export type NeoRpcCallOptions = { readonly signal?: AbortSignal; readonly contractHash?: string; readonly timeoutMilliseconds?: number };
export type NeoRpcUnsubscribe = () => void;

export function invoke<TRequest, TResult>(command: string, args: TRequest, _options?: NeoRpcCallOptions): Promise<TResult> {
  return handle(command, args) as Promise<TResult>;
}

export async function invokeChannel<TRequest, TItem>(_command: string, _args: TRequest, _options?: NeoRpcCallOptions): Promise<AsyncIterable<TItem>> {
  return { async *[Symbol.asyncIterator]() { /* no streams in the preview */ } };
}

export async function subscribe<T>(_event: string, _handler: (payload: T) => void, _options?: NeoRpcCallOptions): Promise<NeoRpcUnsubscribe> {
  return () => {};
}
