using System;
using System.Threading.Tasks;

namespace AsyncFixture;

public class Violations
{
    // ============ POSITIVE CASES (each must produce exactly one violation) ============

    // 1. SyncOverAsyncResult: .Result on Task<T>
    public string GetResultViolation()
    {
        var task = Task.FromResult("hello");
        return task.Result;
    }

    // 2. SyncOverAsyncWait: .Wait() on Task
    public void WaitViolation()
    {
        var task = Task.Delay(10);
        task.Wait();
    }

    // 3. SyncOverAsyncGetAwaiterGetResult
    public string GetAwaiterGetResultViolation()
    {
        var task = Task.FromResult("hello");
        return task.GetAwaiter().GetResult();
    }

    // 4. AsyncVoid (NOT event-handler shaped)
    public async void AsyncVoidViolation()
    {
        await Task.Delay(10);
    }

    // 5. MissingAwait: bare Task call inside async method
    public async Task MissingAwaitViolation()
    {
        Task.Delay(10);
        await Task.CompletedTask;
    }

    // 6. FireAndForget: bare Task call in non-async method
    public void FireAndForgetViolation()
    {
        Task.Delay(10);
    }

    // ============ NEGATIVE CASES (must NOT be flagged) ============

    public async Task ProperAwait()
    {
        await Task.Delay(10);
    }

    public async void EventHandler(object sender, EventArgs e)
    {
        await Task.Delay(10);
    }

    public void DiscardFireAndForget()
    {
        _ = Task.Delay(10);
    }

    public void AssignedFireAndForget()
    {
        var t = Task.Delay(10);
        _ = t;
    }

    public Task ForwardingMethod()
    {
        return Task.Delay(10);
    }
}
