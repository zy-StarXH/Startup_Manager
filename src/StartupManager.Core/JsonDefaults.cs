using System.Text.Json;
using System.Text.Json.Serialization;

namespace StartupManager.Core;

public static class JsonDefaults
{
    /// <summary>
    /// Shared by the bridge and persistence. Enums become lower camelCase strings:
    /// registry, folder, task, service / currentUser, allUsers, system.
    /// </summary>
    public static JsonSerializerOptions Options { get; } = Create();

    public static JsonSerializerOptions Indented { get; } = Create(writeIndented: true);

    private static JsonSerializerOptions Create(bool writeIndented = false) => new(JsonSerializerDefaults.Web)
    {
        WriteIndented = writeIndented,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };
}
