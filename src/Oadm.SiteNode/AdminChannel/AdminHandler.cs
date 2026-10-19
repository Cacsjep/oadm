using System.Globalization;

using Oadm.SiteNode.Admin;
using Oadm.SiteNode.Config;
using Oadm.SiteNode.Links;
using Oadm.SiteNode.Logging;
using Oadm.SiteNode.Networks;
using Oadm.SiteNode.State;
using Oadm.SiteNode.Feed;

namespace Oadm.SiteNode.AdminChannel;

/// <summary>
/// The commands of the app and the command line (docs/sites.md, "App"). Every local user may read the state and the
/// connection log; the pairing code and every change need an administrator of the machine.
/// </summary>
public sealed class AdminHandler(
    NodeStore store,
    BindingState binding,
    PortPolicy ports,
    NetworkScope networks,
    NodeStatusFeed feed,
    ConnectionLog log,
    LinkRegistry links)
{
    public const int MaxExtraSubnets = 64;

    public AdminResponse Handle(AdminRequest request, bool isAdmin)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!AdminCommands.IsOpen(request.Command) && !isAdmin)
            return AdminResponse.Fail(AdminTexts.NeedsAdministrator);
        try
        {
            switch (request.Command)
            {
                case AdminCommands.State:
                    return Ok();
                case AdminCommands.Log:
                    var count = int.TryParse(request.Arg("count"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : ConnectionLog.DefaultCapacity;
                    return new AdminResponse { Ok = true, Log = log.Newest(count) };
                case AdminCommands.PairingCode:
                    return binding.ShowsPairingCode
                        ? new AdminResponse { Ok = true, PairingCode = Sites.Binding.PairingCode.Format(binding.PairingCode), State = feed.View() }
                        : AdminResponse.Fail(binding.Binding == NodeBinding.Bound ? AdminTexts.AlreadyBound : "Binding is closed. Click Allow binding.");
                case AdminCommands.SetServer:
                    return SetServer(request);
                case AdminCommands.AllowBinding:
                    if (binding.Binding == NodeBinding.Bound)
                        return AdminResponse.Fail(AdminTexts.AlreadyBound);
                    binding.AllowBinding();
                    return Ok();
                case AdminCommands.SetCard:
                    var name = request.Arg("id");
                    var card = networks.Cards().FirstOrDefault(c => c.Id == name) ?? networks.Cards().FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                    if (card is null)
                        return AdminResponse.Fail("This network card is not on this computer.");
                    var id = card.Id;
                    var reached = Bool(request.Arg("reached"));
                    store.Update(c =>
                    {
                        c.AllCards = false;
                        c.Cards[id] = reached;
                    });
                    return Ok();
                case AdminCommands.AllCards:
                    var all = Bool(request.Arg("on"));
                    store.Update(c => c.AllCards = all);
                    return Ok();
                case AdminCommands.AddSubnet:
                    if (Subnet.Parse(request.Arg("subnet")) is not { } subnet)
                        return AdminResponse.Fail("Enter a network like 10.20.0.0/16.");
                    var added = store.TryUpdate(c =>
                    {
                        if (c.ExtraSubnets.Count >= MaxExtraSubnets || c.ExtraSubnets.Contains(subnet.ToString(), StringComparer.OrdinalIgnoreCase))
                            return false;
                        c.ExtraSubnets.Add(subnet.ToString());
                        return true;
                    });
                    return added || store.Read(c => c.ExtraSubnets.Contains(subnet.ToString(), StringComparer.OrdinalIgnoreCase))
                        ? Ok()
                        : AdminResponse.Fail($"At most {MaxExtraSubnets} extra networks.");
                case AdminCommands.RemoveSubnet:
                    var text = request.Arg("subnet");
                    var normalized = Subnet.Parse(text)?.ToString() ?? text;
                    store.TryUpdate(c => c.ExtraSubnets.RemoveAll(s => string.Equals(s, normalized, StringComparison.OrdinalIgnoreCase)) > 0);
                    return Ok();
                case AdminCommands.AddPort:
                case AdminCommands.AllowRequest:
                    if (Port(request) is not { } allow)
                        return AdminResponse.Fail("Enter a port from 1 to 65535.");
                    ports.Allow(allow);
                    return Ok();
                case AdminCommands.RemovePort:
                    if (Port(request) is not { } remove)
                        return AdminResponse.Fail("Enter a port from 1 to 65535.");
                    ports.Remove(remove);
                    return Ok();
                case AdminCommands.DismissRequest:
                    if (Port(request) is not { } dismiss)
                        return AdminResponse.Fail("Enter a port from 1 to 65535.");
                    ports.Dismiss(dismiss);
                    return Ok();
                case AdminCommands.AcceptServer:
                    // The new server binds with the pairing code: the app shows it now.
                    var code = binding.AcceptNewServerIdentity();
                    return new AdminResponse { Ok = true, PairingCode = Sites.Binding.PairingCode.Format(code), State = feed.View() };
                case AdminCommands.Reset:
                    binding.Reset();
                    links.CloseAll();
                    return Ok();
                default:
                    return AdminResponse.Fail("Unknown command: " + request.Command);
            }
        }
        catch (NodeRefusalException e)
        {
            return AdminResponse.Fail(e.Message);
        }
    }

    private AdminResponse SetServer(AdminRequest request)
    {
        var mode = request.Arg("mode") switch
        {
            "" => store.Read(c => c.Mode),
            var m when m.Equals("wait", StringComparison.OrdinalIgnoreCase) => NodeLinkMode.Wait,
            var m when m.Equals("connect", StringComparison.OrdinalIgnoreCase) => NodeLinkMode.Connect,
            _ => (NodeLinkMode?)null,
        };
        if (mode is null)
            return AdminResponse.Fail("Choose connect or wait.");
        var address = request.Args.TryGetValue("address", out var typed) ? typed.Trim() : store.Read(c => c.ServerAddress);
        if (address.Length > 0 && OutboundConnector.ParseAddress(address, NodeConfig.DefaultPort) is null)
            return AdminResponse.Fail("Enter the server's name or IP address, like oadm.example.com or 10.0.0.5.");
        if (mode == NodeLinkMode.Connect && address.Length == 0)
            return AdminResponse.Fail("Enter the server's name or IP address.");
        store.Update(c =>
        {
            c.Mode = mode.Value;
            c.ServerAddress = address;
        });
        return Ok();
    }

    private AdminResponse Ok() => new() { Ok = true, State = feed.View() };

    private static bool Bool(string value) =>
        value.Equals("true", StringComparison.OrdinalIgnoreCase) || value.Equals("on", StringComparison.OrdinalIgnoreCase) || value == "1";

    private static PortEntry? Port(AdminRequest request)
    {
        var use = request.Arg("use").Equals("listen", StringComparison.OrdinalIgnoreCase) ? PortUse.Listen : PortUse.Connect;
        var protocol = request.Arg("protocol").Equals("udp", StringComparison.OrdinalIgnoreCase) ? PortProtocol.Udp : PortProtocol.Tcp;
        if (!int.TryParse(request.Arg("port"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port is <= 0 or > 65535)
            return null;
        return new PortEntry(use, protocol, port, request.Arg("reason"));
    }
}
