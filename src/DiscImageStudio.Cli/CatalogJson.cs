using System.Reflection;
using System.Text;
using System.Text.Json;
using DiscImageStudio.Core;

namespace DiscImageStudio.Cli;

/// <summary>
/// Projects <see cref="DiscModuleCatalog"/> into the JSON contract consumed by GUI clients
/// (see <c>macos/DESIGN.md</c> section 1). This is a read-only view: the catalog never becomes
/// a module command and this file must not alter how any existing command runs.
/// </summary>
internal static class CatalogJson
{
    internal const int SchemaVersion = 1;

    /// <summary>
    /// <see cref="Enum.GetValues{TEnum}"/> yields flag values in ascending order, which matches the
    /// declaration order of the power-of-two members in <see cref="DiscModuleCapabilities"/>.
    /// </summary>
    private static readonly DiscModuleCapabilities[] CapabilityOrder =
        Enum.GetValues<DiscModuleCapabilities>()
            .Where(capability => capability != DiscModuleCapabilities.None)
            .ToArray();

    internal static int Run(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 1)
        {
            WriteSummary();
            return 0;
        }

        if (arguments.Count == 2 && arguments[1].Equals("--json", StringComparison.Ordinal))
        {
            WriteJson();
            return 0;
        }

        throw new ArgumentException("catalog accepts no arguments or exactly --json.");
    }

    private static void WriteJson()
    {
        byte[] payload = Serialize();
        using Stream standardOutput = Console.OpenStandardOutput();
        standardOutput.Write(payload, 0, payload.Length);
        standardOutput.WriteByte((byte)'\n');
        standardOutput.Flush();
    }

    /// <summary>Serializes the catalog to indented UTF-8 JSON without a BOM or trailing newline.</summary>
    internal static byte[] Serialize()
    {
        using MemoryStream buffer = new();
        using (Utf8JsonWriter writer = new(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", SchemaVersion);
            WriteNullableString(writer, "toolVersion", ReadToolVersion());
            writer.WriteStartArray("modules");
            foreach (IOpticalDiscModule module in ConsoleModuleRunner.Catalog.Modules)
            {
                WriteModule(writer, module.Descriptor);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void WriteModule(Utf8JsonWriter writer, DiscModuleDescriptor module)
    {
        writer.WriteStartObject();
        writer.WriteString("id", module.Id);
        writer.WriteString("displayName", module.DisplayName);
        writer.WriteString("family", module.Family.ToString());
        writer.WriteString("description", module.Description);
        writer.WriteBoolean("supportsCancellation", module.SupportsCancellation);
        writer.WriteStartArray("commands");
        foreach (DiscCommandDescriptor command in module.Commands)
        {
            WriteCommand(writer, command);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteCommand(Utf8JsonWriter writer, DiscCommandDescriptor command)
    {
        writer.WriteStartObject();
        writer.WriteString("name", command.Name);
        writer.WriteString("displayName", command.DisplayName);
        writer.WriteString("description", command.Description);
        writer.WriteStartArray("capabilities");
        foreach (DiscModuleCapabilities capability in CapabilityOrder)
        {
            if ((command.Capabilities & capability) == capability)
            {
                writer.WriteStringValue(capability.ToString());
            }
        }

        writer.WriteEndArray();
        writer.WriteStartArray("options");
        foreach (DiscOptionDefinition option in command.Options)
        {
            WriteOption(writer, option);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteOption(Utf8JsonWriter writer, DiscOptionDefinition option)
    {
        writer.WriteStartObject();
        writer.WriteString("name", option.Name);
        writer.WriteString("displayName", option.DisplayName);
        writer.WriteString("valueType", option.ValueType.ToString());
        writer.WriteBoolean("required", option.Required);
        WriteNullableString(writer, "defaultValue", option.DefaultValue);
        WriteNullableString(writer, "unit", option.Unit);
        if (option.Choices is null)
        {
            writer.WriteNull("choices");
        }
        else
        {
            writer.WriteStartArray("choices");
            foreach (string choice in option.Choices)
            {
                writer.WriteStringValue(choice);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string propertyName, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(propertyName);
        }
        else
        {
            writer.WriteString(propertyName, value);
        }
    }

    /// <summary>
    /// Reads the entry assembly's informational version and drops the <c>+&lt;commit hash&gt;</c> suffix.
    /// Returns <c>null</c> when the attribute is missing or blank.
    /// </summary>
    private static string? ReadToolVersion()
    {
        string? informational = Assembly.GetEntryAssembly()
            ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
        if (string.IsNullOrWhiteSpace(informational))
        {
            return null;
        }

        int separator = informational.IndexOf('+');
        string version = (separator >= 0 ? informational[..separator] : informational).Trim();
        return version.Length == 0 ? null : version;
    }

    private static void WriteSummary()
    {
        IReadOnlyList<IOpticalDiscModule> modules = ConsoleModuleRunner.Catalog.Modules;
        int commandCount = modules.Sum(module => module.Descriptor.Commands.Count);
        Console.WriteLine(
            $"Disc Image Studio command catalog (schema {SchemaVersion}, tool {ReadToolVersion() ?? "unknown"})");
        Console.WriteLine($"{modules.Count} modules, {commandCount} commands");
        StringBuilder summary = new();
        foreach (IOpticalDiscModule module in modules)
        {
            DiscModuleDescriptor descriptor = module.Descriptor;
            summary.Clear();
            summary.AppendLine();
            summary.AppendLine(
                $"{descriptor.Id} - {descriptor.DisplayName} ({descriptor.Family})");
            summary.AppendLine($"  {descriptor.Description}");
            summary.AppendLine($"  supportsCancellation: {(descriptor.SupportsCancellation ? "true" : "false")}");
            foreach (DiscCommandDescriptor command in descriptor.Commands)
            {
                summary.AppendLine($"  {command.Name} - {command.DisplayName}");
                summary.AppendLine($"    {command.Description}");
                foreach (DiscOptionDefinition option in command.Options)
                {
                    summary.Append("    --")
                        .Append(option.Name)
                        .Append(" <")
                        .Append(option.ValueType)
                        .Append('>');
                    if (option.Required)
                    {
                        summary.Append(" required");
                    }

                    if (option.DefaultValue is not null)
                    {
                        summary.Append(" default=").Append(option.DefaultValue);
                    }

                    if (option.Unit is not null)
                    {
                        summary.Append(" [").Append(option.Unit).Append(']');
                    }

                    if (option.Choices is not null)
                    {
                        summary.Append(" choices=").Append(string.Join('|', option.Choices));
                    }

                    summary.AppendLine();
                }
            }

            Console.Write(summary.ToString());
        }
    }
}
