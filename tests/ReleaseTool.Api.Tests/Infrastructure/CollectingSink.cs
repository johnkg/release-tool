using Serilog.Core;
using Serilog.Events;

namespace ReleaseTool.Api.Tests.Infrastructure;

/// <summary>
/// Captures log events in memory. Picked up by the ReadFrom.Services call in
/// Program.cs, so the tests observe the real logging pipeline without depending
/// on file sinks or configuration.
/// </summary>
public sealed class CollectingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = [];

    public void Emit(LogEvent logEvent)
    {
        lock (_events)
        {
            _events.Add(logEvent);
        }
    }

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_events)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>The completion event Serilog's request logging writes per request.</summary>
    public LogEvent? RequestCompletionFor(string path) =>
        Events.FirstOrDefault(e =>
            e.MessageTemplate.Text.Contains("responded", StringComparison.OrdinalIgnoreCase)
            && e.Properties.TryGetValue("RequestPath", out var value)
            && value.ToString().Contains(path, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The client's response can arrive before the logging middleware has
    /// unwound on the server, so wait rather than sampling once.
    /// </summary>
    public async Task<LogEvent?> WaitForRequestCompletion(string path, int timeoutMs = 2000)
    {
        for (var waited = 0; waited < timeoutMs; waited += 25)
        {
            if (RequestCompletionFor(path) is { } completion)
            {
                return completion;
            }

            await Task.Delay(25);
        }

        return null;
    }

    public string RenderAll() =>
        string.Join('\n', Events.Select(e => e.RenderMessage() + " " + e.Exception));
}
