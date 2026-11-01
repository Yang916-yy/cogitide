# Restricted tool worker

`ToolWorker` is an early Host entry point, before account stores, databases, normal Host locks or model initialization. `ToolWorkerDispatcher` reuses Tools adapters and owns a single chat/workspace. The explicit StreamJsonRpc interface permits only catalog, execute, bounded raw-byte read, run end and session end; it has no Host inference/authentication methods.

The ordinary Host broker launches this entry point through the official native MXC SDK. Starting `--tool-worker` directly in a shell is an ordinary process and is **not** evidence of isolation. Application approval modes remain enforced by the parent kernel; the native policy supplies the separate OS boundary. Standard-input loss disposes command and integration ownership; native container termination handles worker/descendant lifetime when the broker disappears.

See [policy, fixed upstream references, licenses and acceptance boundaries](../../Workbench.Tools/Isolation/README.md). No model SDK, account implementation, HTTP/SSE stack, alternate tool implementation or custom native sandbox engine is added here.
