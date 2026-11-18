using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

/// <summary>
/// Observes the app from inside, without any change to its code, and streams what it sees to RepoRunner:
/// every traced operation (requests, outgoing HTTP calls, database commands), controller inputs and results,
/// and every exception at the moment it is thrown. Nothing is written to disk; if RepoRunner is not listening
/// the events are dropped.
/// </summary>
internal static class FlowRecorder
{
    private const int MaxBody = 32_000;

    private static readonly BlockingCollection<byte[]> Queue = new(4000);
    private static readonly ConditionalWeakTable<Exception, object> Seen = new();
    private static readonly JsonSerializerOptions ObjectJson = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        MaxDepth = 24,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    [ThreadStatic] private static bool _busy;
    private static long _exceptionWindow;
    private static int _exceptionsInWindow;
    private static ActivityListener? _listener;
    private static IDisposable? _subscription;

    public static void Start(string pipeName)
    {
        new Thread(() => Pump(pipeName)) { IsBackground = true, Name = "rr-flow" }.Start();

        _listener = new ActivityListener
        {
            ShouldListenTo = _ => true,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = (ref ActivityCreationOptions<string> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = OnActivityStopped,
        };
        ActivitySource.AddActivityListener(_listener);
        _subscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver());
        AppDomain.CurrentDomain.FirstChanceException += (_, e) => OnException(e.Exception, unhandled: false);

        Emit(w =>
        {
            w.WriteString("type", "hello");
            w.WriteNumber("pid", Environment.ProcessId);
            w.WriteString("runtime", Environment.Version.ToString());
        });
    }

    // ---------- what is observed ----------

    private static void OnActivityStopped(Activity a) => Emit(w =>
    {
        w.WriteString("type", "span");
        WriteIds(w, a);
        w.WriteString("source", a.Source.Name);
        w.WriteString("name", a.DisplayName);
        w.WriteString("kind", a.Kind.ToString());
        w.WriteString("start", a.StartTimeUtc);
        w.WriteNumber("ms", a.Duration.TotalMilliseconds);
        w.WriteString("status", a.Status.ToString());
        if (a.StatusDescription is not null) w.WriteString("statusText", Cut(a.StatusDescription, 2000));
        w.WriteStartObject("tags");
        foreach (var tag in a.TagObjects)
            if (tag.Value is not null) w.WriteString(tag.Key, Cut(tag.Value.ToString() ?? "", 4000));
        w.WriteEndObject();
    });

    private sealed class ListenerObserver : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name is "Microsoft.AspNetCore" or "HttpHandlerDiagnosticListener")
                listener.Subscribe(new EventObserver());
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private sealed class EventObserver : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> e)
        {
            if (_busy || e.Value is null) return;
            try
            {
                switch (e.Key)
                {
                    case "Microsoft.AspNetCore.Mvc.BeforeControllerActionMethod":
                        Data("Action input", ActionName(e.Value), Get(e.Value, "ActionArguments"));
                        break;
                    case "Microsoft.AspNetCore.Mvc.AfterControllerActionMethod":
                    {
                        var result = Get(e.Value, "Result");
                        var status = Get(result, "StatusCode");
                        var label = status is null ? "Action result" : "Action result (" + status + ")";
                        Data(label, ActionName(e.Value), Get(result, "Value") ?? result?.GetType().Name);
                        break;
                    }
                    case "Microsoft.AspNetCore.Diagnostics.UnhandledException":
                    case "Microsoft.AspNetCore.Hosting.UnhandledException":
                        if (Get(e.Value, "exception") is Exception ex) OnException(ex, unhandled: true);
                        break;
                    case "System.Net.Http.HttpRequestOut.Start":
                        CallStack();
                        break;
                    case "System.Net.Http.HttpRequestOut.Stop":
                        OutgoingHttp(e.Value);
                        break;
                    case "Microsoft.AspNetCore.Hosting.HttpRequestIn.Stop":
                        IncomingHttp(e.Value);
                        break;
                }
            }
            catch { /* observing must never disturb the app */ }
        }

        public void OnCompleted() { }
        public void OnError(Exception error) { }
    }

    private static string? ActionName(object payload) =>
        Get(Get(Get(payload, "ActionContext"), "ActionDescriptor"), "DisplayName") as string;

    /// <summary>Outgoing calls on runtimes whose HttpClient is not yet traced through an ActivitySource.</summary>
    private static void OutgoingHttp(object payload)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        var request = Get(payload, "Request");
        var response = Get(payload, "Response");
        Emit(w =>
        {
            w.WriteString("type", "span");
            WriteIds(w, activity);
            w.WriteString("source", "System.Net.Http");
            w.WriteString("name", "HTTP " + Get(request, "Method"));
            w.WriteString("kind", "Client");
            w.WriteString("start", activity.StartTimeUtc);
            w.WriteNumber("ms", (DateTime.UtcNow - activity.StartTimeUtc).TotalMilliseconds);
            w.WriteString("status", "Unset");
            w.WriteStartObject("tags");
            w.WriteString("http.request.method", Get(request, "Method")?.ToString());
            w.WriteString("url.full", Get(request, "RequestUri")?.ToString());
            if (Get(response, "StatusCode") is { } code) w.WriteString("http.response.status_code", Convert.ToInt32(code).ToString());
            w.WriteEndObject();
        });
    }

    /// <summary>The incoming request: method, path and status, read from the HttpContext the event carries.</summary>
    private static void IncomingHttp(object httpContext)
    {
        var activity = Activity.Current;
        if (activity is null) return;
        var request = Get(httpContext, "Request");
        var status = Get(Get(httpContext, "Response"), "StatusCode");
        Emit(w =>
        {
            w.WriteString("type", "span");
            WriteIds(w, activity);
            w.WriteString("source", "Microsoft.AspNetCore");
            w.WriteString("name", "HTTP request");
            w.WriteString("kind", "Server");
            w.WriteString("start", activity.StartTimeUtc);
            w.WriteNumber("ms", (DateTime.UtcNow - activity.StartTimeUtc).TotalMilliseconds);
            w.WriteString("status", "Unset");
            w.WriteStartObject("tags");
            w.WriteString("http.request.method", Get(request, "Method")?.ToString());
            w.WriteString("url.path", Get(request, "Path")?.ToString());
            if (Get(request, "QueryString")?.ToString() is { Length: > 0 } query) w.WriteString("url.query", Cut(query, 2000));
            if (status is not null) w.WriteString("http.response.status_code", Convert.ToInt32(status).ToString());
            w.WriteEndObject();
        });
    }

    private static void Data(string label, string? title, object? value) => Emit(w =>
    {
        w.WriteString("type", "data");
        WriteIds(w, Activity.Current);
        w.WriteString("at", DateTime.UtcNow);
        w.WriteString("label", label);
        if (title is not null) w.WriteString("title", title);
        w.WriteString("body", Serialize(value));
    });

    private static void OnException(Exception ex, bool unhandled)
    {
        if (_busy || ex is OperationCanceledException) return;
        // The same exception surfaces at every catch-and-rethrow; report it once, unless it turns out to be unhandled.
        var first = !Seen.TryGetValue(ex, out _);
        if (!first && !unhandled) return;
        if (first)
        {
            try { Seen.Add(ex, ex); } catch (ArgumentException) { }
            // An app throwing in a tight loop must not flood the pipe.
            var second = Stopwatch.GetTimestamp() / Stopwatch.Frequency;
            if (second != Interlocked.Read(ref _exceptionWindow)) { Interlocked.Exchange(ref _exceptionWindow, second); _exceptionsInWindow = 0; }
            if (Interlocked.Increment(ref _exceptionsInWindow) > 40) return;
        }

        // At throw time the exception's own trace holds one frame; the live call stack shows how the code got here.
        var frames = first ? new StackTrace(2, true).GetFrames() : null;
        Emit(w =>
        {
            w.WriteString("type", "exception");
            WriteIds(w, Activity.Current);
            w.WriteString("at", DateTime.UtcNow);
            w.WriteString("exType", ex.GetType().FullName);
            w.WriteString("message", Cut(ex.Message, 4000));
            w.WriteBoolean("unhandled", unhandled);
            w.WriteNumber("key", RuntimeHelpers.GetHashCode(ex));
            if (frames is not null) WriteFrames(w, frames, sourceOnly: false);
        });
    }

    /// <summary>The app's own methods that led to the operation now starting, innermost first.</summary>
    private static void CallStack()
    {
        var activity = Activity.Current;
        if (activity is null) return;
        var frames = new StackTrace(1, true).GetFrames();
        Emit(w =>
        {
            w.WriteString("type", "stack");
            WriteIds(w, activity);
            WriteFrames(w, frames, sourceOnly: true);
        });
    }

    private static void WriteFrames(Utf8JsonWriter w, StackFrame[] frames, bool sourceOnly)
    {
        w.WriteStartArray("frames");
        var written = 0;
        string? previous = null;
        foreach (var frame in frames)
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type is null || type == typeof(FlowRecorder)) continue;
            var file = frame.GetFileName();
            // Frames with source are the developer's code. Keep a few others only when the full picture is wanted.
            if (file is null && (sourceOnly || written >= 12)) continue;

            // Async methods and lambdas run inside compiler-made types; report the method the developer wrote.
            var name = method.Name;
            if (type.Name.StartsWith("<", StringComparison.Ordinal) && type.DeclaringType is not null)
            {
                name = Written(type.Name) ?? name;
                type = type.DeclaringType;
            }
            if (name.StartsWith("<", StringComparison.Ordinal)) name = Written(name) ?? name;

            var line = frame.GetFileLineNumber();
            var id = type.FullName + "." + name + ":" + line;
            if (id == previous) continue;
            previous = id;

            w.WriteStartObject();
            w.WriteString("m", type.FullName + "." + name);
            if (file is not null)
            {
                w.WriteString("f", file);
                w.WriteNumber("l", line);
            }
            w.WriteEndObject();
            if (++written >= 40) break;
        }
        w.WriteEndArray();
    }

    /// <summary>"&lt;SearchAsync&gt;d__5" → "SearchAsync". Null when the brackets hold no name.</summary>
    private static string? Written(string compilerName)
    {
        var close = compilerName.IndexOf('>');
        return close > 1 ? compilerName.Substring(1, close - 1) : null;
    }

    // ---------- plumbing ----------

    private static void WriteIds(Utf8JsonWriter w, Activity? a)
    {
        if (a is null) return;
        var w3c = a.IdFormat == ActivityIdFormat.W3C;
        w.WriteString("trace", w3c ? a.TraceId.ToString() : a.RootId);
        w.WriteString("id", w3c ? a.SpanId.ToString() : a.Id);
        if (w3c ? a.ParentSpanId != default : a.ParentId is not null)
            w.WriteString("parent", w3c ? a.ParentSpanId.ToString() : a.ParentId);
    }

    private static object? Get(object? target, string property) =>
        target?.GetType().GetProperty(property, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase)?.GetValue(target);

    private static string Serialize(object? value)
    {
        if (value is null) return "null";
        try { return Cut(JsonSerializer.Serialize(value, value.GetType(), ObjectJson), MaxBody); }
        catch { return Cut(JsonSerializer.Serialize(value.ToString()), MaxBody); }
    }

    private static string Cut(string text, int max) => text.Length <= max ? text : text.Substring(0, max) + "…";

    private static void Emit(Action<Utf8JsonWriter> write)
    {
        if (_busy) return;
        _busy = true; // anything thrown or traced while reporting is not reported again
        try
        {
            using var buffer = new MemoryStream(512);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                write(writer);
                writer.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');
            Queue.TryAdd(buffer.ToArray()); // full queue: drop rather than slow the app
        }
        catch { }
        finally { _busy = false; }
    }

    private static void Pump(string pipeName)
    {
        _busy = true; // this thread only ships events
        try
        {
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out);
            pipe.Connect(5000);
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                pipe.Write(line, 0, line.Length);
                if (Queue.Count == 0) pipe.Flush();
            }
        }
        catch
        {
            // RepoRunner went away: stop recording quietly.
            _listener?.Dispose();
            _subscription?.Dispose();
            while (Queue.TryTake(out _)) { }
        }
    }
}
