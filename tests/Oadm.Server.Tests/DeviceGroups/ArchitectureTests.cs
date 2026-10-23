using System.Reflection;
using System.Text.RegularExpressions;

using Grpc.Core;

using Oadm.Core.DeviceGroups;
using Oadm.Core.Devices;
using Oadm.Tests.Shared;

namespace Oadm.Server.Tests.DeviceGroups;

/// <summary>
/// The capability type (docs/spec/device-scope.md, Tests): the gRPC classes get their devices from a <see cref="DeviceView"/>,
/// never from the global repository, and the services they use take <see cref="ViewedDevices"/>, never raw device ids.
/// Service methods that take raw ids are on an allow-list with a reason.
/// </summary>
public sealed partial class ArchitectureTests
{
    /// <summary>The types that read devices by id without a view (system work only).</summary>
    private static readonly Type[] GlobalDeviceAccess =
    [
        typeof(DeviceRepository), typeof(Sdk.Devices.IDeviceRepository), typeof(IDeviceChangeFeed), typeof(DeviceChangeFeed),
    ];

    /// <summary>Service methods behind the gRPC classes that take raw device ids, with the reason (type.method).</summary>
    internal static readonly Dictionary<string, string> ServiceAllowList = new(StringComparer.Ordinal)
    {
        ["TaskPluginQueries.QueryAsync"] = "the device id is resolved through the PluginCaller's view (outside = NOT_FOUND); the overload without a caller is system work and tests",
        ["EventLog.Write"] = "the writer of the rule engine and the action runner (system work); EventService is for users with all devices only (AccessRule.AllDevices)",
        ["ActionRunner.TestAsync"] = "Test of an action: EventService is for users with all devices only (AccessRule.AllDevices)",
        ["TaskEngine.RunAsync"] = "the raw-id form is the system ITaskRunner (ctx.System.Tasks, event rule actions; a group caller is refused outside the view); Run uses the ViewedDevices overload (converted in tasks)",
    };

    private static IEnumerable<Type> GrpcServiceClasses() =>
        typeof(Auth.AuthInterceptor).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t.BaseType?.GetCustomAttribute<BindServiceMethodAttribute>() is not null);

    [Fact]
    public void TheGrpcClassesGetTheirDevicesFromAViewNotFromTheGlobalRepository()
    {
        var found = new List<string>();
        foreach (var type in GrpcServiceClasses())
        {
            foreach (var parameter in type.GetConstructors().SelectMany(c => c.GetParameters()))
            {
                if (GlobalDeviceAccess.Contains(parameter.ParameterType))
                {
                    found.Add($"{type.Name}({parameter.ParameterType.Name})");
                }
            }
        }

        Assert.True(found.Count == 0, "gRPC classes that take the global device access: " + string.Join(", ", found));
    }

    [Fact]
    public void TheServicesBehindTheGrpcClassesTakeViewedDevicesNotRawDeviceIds()
    {
        var services = GrpcServiceClasses()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()))
            .Select(p => p.ParameterType)
            .Where(t => t.Assembly == typeof(DeviceRepository).Assembly || t.Assembly == typeof(Auth.AuthInterceptor).Assembly)
            .Where(t => !GlobalDeviceAccess.Contains(t))
            .Distinct()
            .ToList();

        var found = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var service in services)
        {
            foreach (var method in service.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.GetParameters().Any(IsRawDeviceIds))
                {
                    found.Add($"{service.Name}.{method.Name}");
                }
            }
        }

        var unlisted = found.Where(f => !ServiceAllowList.ContainsKey(f)).ToList();
        Assert.True(unlisted.Count == 0, "Service methods that take raw device ids without a reason on the allow-list: " + string.Join(", ", unlisted));
        var stale = ServiceAllowList.Keys.Where(k => !found.Contains(k)).ToList();
        Assert.True(stale.Count == 0, "Allow-list entries that are no longer needed (remove them): " + string.Join(", ", stale));
    }

    [Fact]
    public void ViewedDevicesAreMadeOnlyByTheView()
    {
        var sources = Directory.EnumerateFiles(TestRepository.Path("src"), "*.cs", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(TestRepository.Path("plugins"), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal));
        var makers = sources.Where(f => NewViewedDevices().IsMatch(File.ReadAllText(f))).Select(Path.GetFileName).ToList();
        Assert.Equal(["DeviceView.cs"], makers);

        // Its constructor is not public: only the core (DeviceView) can call it.
        Assert.All(typeof(ViewedDevices).GetConstructors(BindingFlags.Public | BindingFlags.Instance), _ => Assert.Fail("ViewedDevices has a public constructor."));
    }

    private static bool IsRawDeviceIds(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        var isIds = type == typeof(Guid) || type == typeof(Guid?)
            || (type != typeof(string) && type.GetInterfaces().Concat([type]).Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>) && i.GetGenericArguments()[0] == typeof(Guid)));
        return isIds && (parameter.Name ?? string.Empty).Contains("device", StringComparison.OrdinalIgnoreCase);
    }

    [GeneratedRegex(@"new\s+ViewedDevices\s*\(")]
    private static partial Regex NewViewedDevices();
}
