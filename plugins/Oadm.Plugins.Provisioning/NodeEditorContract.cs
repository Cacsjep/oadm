using System.Text.Json;

using Oadm.Plugins.Provisioning.Model;

namespace Oadm.Plugins.Provisioning;

// The contract records the node editor reads (NodeEditorViewModel, NodeFields): their own file, linked into the client part
// of Schedules too, whose wizard uses the same node editor (docs/spec/schedules.md "Actions").

/// <summary>A command of the VAPIX Commander library for a VAPIX commands node: its id, name, category and fields.</summary>
public sealed record ProfileCommand(string Id, string Name, string Category, string? Description, IReadOnlyList<VapixCommander.CommandField> Fields);

/// <summary>A kind of node for the editor's list.</summary>
public sealed record NodeKindInfo(NodeKind Kind, string Name, bool IsFixed, bool AlwaysStops, JsonElement DefaultSettings);
