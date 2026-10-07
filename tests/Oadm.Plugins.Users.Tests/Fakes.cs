using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Oadm.Sdk.Devices;
using Oadm.Sdk.Plugins;
using Oadm.Sdk.Vapix;

namespace Oadm.Plugins.Users.Tests;

internal static class Fixtures
{
    public static string Read(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>API list recorded from AXIS P3265-V, AXIS OS 12.11 (lists user-management 1.2).</summary>
    public static IReadOnlyList<DeviceApi> P3265ApiList() => ParseApiList(Read("apilist-p3265v-12.11.json"));

    public static string P3265Users => Read("pwdgrp-get-p3265v-12.11.txt");

    public static string P3265UserGroup => Read("usergroup-p3265v-12.11.txt");

    public static string P3265SystemReady => Read("systemready-p3265v-12.11.json");

    public static IReadOnlyList<DeviceApi> ParseApiList(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("data").GetProperty("apiList").EnumerateArray()
            .Select(a => new DeviceApi(a.GetProperty("id").GetString()!, a.GetProperty("version").GetString()!))
            .ToList();
    }

    public static IReadOnlyList<DeviceApi> Apis(params (string Id, string Version)[] apis) =>
        apis.Select(a => new DeviceApi(a.Id, a.Version)).ToList();
}

internal sealed class FakeDevice : IDeviceInfo
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Serial { get; init; } = "B8A44F631339";
    public string Address { get; init; } = "10.0.0.48";
    public string? HostName { get; init; }
    public string? Model { get; init; } = "AXIS P3265-V";
    public string? FirmwareVersion { get; init; } = "12.11.77";
    public DeviceStatus Status { get; init; } = DeviceStatus.Ok;
    public DeviceCategory Category { get; init; } = DeviceCategory.Camera;
    public bool HasVideo => true;
    public IReadOnlyList<DeviceApi> Apis { get; init; } = Fixtures.P3265ApiList();
}

/// <summary>A request as the device would see it. The body is captured at send time because the plugin disposes requests.</summary>
internal sealed record RecordedRequest(HttpMethod Method, string Uri, string? ContentType, string Body);

/// <summary>
/// Fake device for pwdgrp.cgi and friends: keeps a mutable "action=get" body and answers writes like
/// AXIS OS does. Unexpected requests fail the test.
/// </summary>
internal sealed class FakeVapix : IVapixClient
{
    private readonly Dictionary<string, (string Admin, string Ptz, string Operator, string Viewer)> _users = new(StringComparer.Ordinal);

    public FakeVapix(string? usersBody = null)
    {
        LoadUsers(usersBody ?? Fixtures.P3265Users);
    }

    public Uri BaseAddress { get; } = new("http://10.0.0.48/");
    public IReadOnlyList<DeviceApi> ApiList { get; set; } = Fixtures.P3265ApiList();
    public string UserGroupBody { get; set; } = Fixtures.P3265UserGroup;
    public HttpStatusCode UserGroupStatus { get; set; } = HttpStatusCode.OK;
    public string SystemReadyBody { get; set; } = Fixtures.P3265SystemReady;

    /// <summary>Overrides the answer to the next write (e.g. "Error: invalid password.").</summary>
    public string? WriteAnswer { get; set; }

    /// <summary>When false, a write is answered as success but not applied (to test the read-back check).</summary>
    public bool ApplyWrites { get; set; } = true;

    public List<RecordedRequest> Requests { get; } = [];
    public int ApiListCalls { get; private set; }

    public IEnumerable<RecordedRequest> Writes => Requests.Where(r => r.Uri == "axis-cgi/pwdgrp.cgi" && !r.Body.StartsWith("action=get", StringComparison.Ordinal));

    public Task<IReadOnlyList<DeviceApi>> GetApiListAsync(CancellationToken ct)
    {
        ApiListCalls++;
        return Task.FromResult(ApiList);
    }

    public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
        var recorded = new RecordedRequest(request.Method, request.RequestUri!.ToString(), request.Content?.Headers.ContentType?.MediaType, body);
        Requests.Add(recorded);
        return recorded.Uri switch
        {
            "axis-cgi/systemready.cgi" => Text(SystemReadyBody),
            "axis-cgi/usergroup.cgi" => new HttpResponseMessage(UserGroupStatus) { Content = new StringContent(UserGroupBody) },
            "axis-cgi/pwdgrp.cgi" => Pwdgrp(body),
            _ => throw new InvalidOperationException("Unexpected request " + recorded.Uri),
        };
    }

    public Task<BasicDeviceInfo> GetBasicDeviceInfoAsync(CancellationToken ct) => throw new NotSupportedException();

    public Task<IReadOnlyDictionary<string, string>> ListParametersAsync(IEnumerable<string> groups, CancellationToken ct) => throw new NotSupportedException();

    public Task RestartAsync(CancellationToken ct) => throw new NotSupportedException();

    public string UsersBody()
    {
        string Csv(Func<KeyValuePair<string, (string Admin, string Ptz, string Operator, string Viewer)>, bool> pick) =>
            string.Join(',', _users.Where(pick).Select(u => u.Key));
        return $"admin=\"{Csv(u => u.Value.Admin == "1")}\"\noperator=\"{Csv(u => u.Value.Operator == "1")}\"\nviewer=\"{Csv(u => u.Value.Viewer == "1")}\"\nptz=\"{Csv(u => u.Value.Ptz == "1")}\"\ndigusers=\"{string.Join(',', _users.Keys)}\"\n";
    }

    private HttpResponseMessage Pwdgrp(string body)
    {
        var form = body.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => WebUtility.UrlDecode(p.Length > 1 ? p[1] : string.Empty));
        var action = form["action"];
        if (action == "get")
        {
            return Text(UsersBody());
        }

        if (WriteAnswer is { } answer)
        {
            return Text(answer);
        }

        var user = form["user"];
        if (ApplyWrites)
        {
            switch (action)
            {
                case "add":
                    _users[user] = Groups(form["sgrp"]);
                    break;
                case "update" when form.TryGetValue("sgrp", out var sgrp):
                    _users[user] = Groups(sgrp);
                    break;
                case "remove":
                    _users.Remove(user);
                    break;
            }
        }

        return Text(action switch
        {
            "add" => $"Created account {user}.",
            "update" => $"Modified account {user}.",
            "remove" => $"Removed account {user}.",
            _ => "Error: action operation type.",
        });
    }

    private static (string, string, string, string) Groups(string sgrp)
    {
        var g = sgrp.Split(':');
        string Has(string name) => g.Contains(name) ? "1" : "0";
        return (Has("admin"), Has("ptz"), Has("operator"), Has("viewer"));
    }

    private void LoadUsers(string body)
    {
        foreach (var user in PwdgrpApi.ParseUsers(body))
        {
            _users[user.Name] = (
                user.Role == UserRole.Administrator ? "1" : "0",
                user.Ptz ? "1" : "0",
                user.Role >= UserRole.Operator ? "1" : "0",
                user.Role >= UserRole.Viewer ? "1" : "0");
        }
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "text/plain") };
}

internal sealed class FakeTaskContext : ITaskExecutionContext, ITaskQueryContext
{
    public FakeTaskContext(IVapixClient vapix)
    {
        Vapix = vapix;
        Steps = new TaskStepList(onWarning: Warnings.Add);
    }

    /// <summary>The steps exactly as the server's task engine records them.</summary>
    public TaskStepList Steps { get; }

    public void PlanSteps(params string[] names) => Steps.Plan(names);

    public ITaskStep BeginStep(string name) => Steps.Begin(name);

    public Guid TaskId { get; } = Guid.NewGuid();
    public IVapixClient Vapix { get; }
    public ILogger Logger => CapturingLogger;
    public CapturingLogger CapturingLogger { get; } = new();
    public ICorePlugin? Owner => null;
    public IUploadedFiles Files => throw new NotSupportedException();
    public List<(int Percent, string? Message)> Progress { get; } = [];
    public List<string> Warnings { get; } = [];
    public List<(TaskLogLevel Level, string Message)> Log { get; } = [];

    public void ReportProgress(int percent, string? message = null) => Progress.Add((percent, message));

    public void ReportWarning(string message) => Warnings.Add(message);

    void ITaskExecutionContext.Log(TaskLogLevel level, string message) => Log.Add((level, message));

    /// <summary>Everything the task wrote anywhere that could be persisted or shown.</summary>
    public IEnumerable<string> AllText() => Progress.Select(p => p.Message ?? string.Empty)
        .Concat(Warnings).Concat(Log.Select(l => l.Message)).Concat(CapturingLogger.Messages)
        .Concat(Steps.Snapshot().SelectMany(s => new[] { s.Name, s.Detail ?? string.Empty }));
}

internal sealed class CapturingLogger : ILogger
{
    public List<string> Messages { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NullLogger.Instance.BeginScope(state);

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Messages.Add(formatter(state, exception));
}
