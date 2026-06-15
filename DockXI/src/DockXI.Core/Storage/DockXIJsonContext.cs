using System.Text.Json.Serialization;
using DockXI.Contracts;

namespace DockXI.Storage;

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DockConfigDocument))]
internal partial class DockXIJsonContext : JsonSerializerContext
{
}
