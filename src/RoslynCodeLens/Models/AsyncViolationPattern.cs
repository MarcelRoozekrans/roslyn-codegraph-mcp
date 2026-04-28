namespace RoslynCodeLens.Models;

public enum AsyncViolationPattern
{
    SyncOverAsyncResult,
    SyncOverAsyncWait,
    SyncOverAsyncGetAwaiterGetResult,
    AsyncVoid,
    MissingAwait,
    FireAndForget
}
