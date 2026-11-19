namespace Chaos.Desktop.Platform;

/// <summary>One background component's pause: run <c>inner</c> while the component is stopped, then let it resume.</summary>
internal delegate Task PauseStep(Func<Task> inner, CancellationToken ct);

/// <summary>
/// Composes the pause steps that restore, reset and other file-replacing work runs inside. The order is explicit:
/// the first step is outermost (pauses first, resumes last), the last step is innermost and runs right around the work.
/// </summary>
internal static class PausePipeline
{
    public static Func<Func<Task>, CancellationToken, Task> Compose(params PauseStep[] outermostFirst) =>
        (work, ct) =>
        {
            var next = work;
            for (var index = outermostFirst.Length - 1; index >= 0; index--)
            {
                var step = outermostFirst[index];
                var inner = next;
                next = () => step(inner, ct);
            }

            return next();
        };
}
