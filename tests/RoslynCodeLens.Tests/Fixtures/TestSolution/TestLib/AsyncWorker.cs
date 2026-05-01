namespace TestLib;

public class AsyncWorker
{
    public Task DoAsync() => Task.CompletedTask;

    public Task<int> ComputeAsync() => Task.FromResult(7);
}
