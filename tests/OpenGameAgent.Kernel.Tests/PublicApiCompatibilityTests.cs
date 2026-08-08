using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using OpenGameAgent.Kernel;
using Xunit;

namespace OpenGameAgent.Kernel.Tests;

public sealed class PublicApiCompatibilityTests
{
    private const string ApprovedApiHash = "D1FB0A1E25CE92F44DEFDC7E04D297EB4A9779A24EBF2FA72D85E25B917DEC04";

    [Fact]
    public void KernelPublicApiMatchesTheApprovedStableSurface()
    {
        var surface = DescribeAssembly(typeof(Agent).Assembly);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(surface)));

        Assert.True(
            string.Equals(ApprovedApiHash, hash, StringComparison.Ordinal),
            $"The Kernel public API changed. Review the complete surface below, then update the approved hash intentionally.\nHash: {hash}\n\n{surface}");
    }

    private static string DescribeAssembly(Assembly assembly)
    {
        var lines = new List<string>();
        foreach (var type in assembly.GetExportedTypes().OrderBy(TypeName, StringComparer.Ordinal))
        {
            var kind = type.IsEnum
                ? "enum"
                : type.IsInterface
                    ? "interface"
                    : typeof(MulticastDelegate).IsAssignableFrom(type.BaseType)
                        ? "delegate"
                        : type.IsValueType ? "struct" : "class";
            lines.Add($"{kind} {TypeName(type)} base={TypeName(type.BaseType)} interfaces={string.Join(',', type.GetInterfaces().Select(TypeName).OrderBy(value => value, StringComparer.Ordinal))}");

            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                var constant = field.IsLiteral ? FormatConstant(field.GetRawConstantValue()) : string.Empty;
                lines.Add($"  field {(field.IsStatic ? "static " : string.Empty)}{TypeName(field.FieldType)} {field.Name}{constant}");
            }

            foreach (var constructor in type.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(DescribeParameters, StringComparer.Ordinal))
            {
                lines.Add($"  ctor {type.Name}({DescribeParameters(constructor)})");
            }

            foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                var accessor = property.GetMethod ?? property.SetMethod;
                lines.Add($"  property {(accessor?.IsStatic == true ? "static " : string.Empty)}{TypeName(property.PropertyType)} {property.Name} get={property.GetMethod is not null} set={property.SetMethod is not null} index=({DescribeParameters(property.GetIndexParameters())})");
            }

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(method => !method.IsSpecialName)
                         .OrderBy(method => method.Name, StringComparer.Ordinal)
                         .ThenBy(DescribeParameters, StringComparer.Ordinal))
            {
                lines.Add($"  method {(method.IsStatic ? "static " : string.Empty)}{TypeName(method.ReturnType)} {method.Name}`{method.GetGenericArguments().Length}({DescribeParameters(method)})");
            }

            foreach (var eventInfo in type.GetEvents(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .OrderBy(eventInfo => eventInfo.Name, StringComparer.Ordinal))
            {
                lines.Add($"  event {TypeName(eventInfo.EventHandlerType)} {eventInfo.Name}");
            }
        }

        return string.Join("\n", lines);
    }

    private static string DescribeParameters(MethodBase method) => DescribeParameters(method.GetParameters());

    private static string DescribeParameters(IEnumerable<ParameterInfo> parameters) =>
        string.Join(",", parameters.Select(parameter =>
            $"{(parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty)}{TypeName(parameter.ParameterType.IsByRef ? parameter.ParameterType.GetElementType() : parameter.ParameterType)} {parameter.Name}{(parameter.HasDefaultValue ? "=" + FormatConstant(parameter.DefaultValue) : string.Empty)}"));

    private static string TypeName(Type? type)
    {
        if (type is null)
        {
            return "-";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
        }

        if (type.IsGenericParameter)
        {
            return "!" + type.GenericParameterPosition + ":" + type.Name;
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definition = type.GetGenericTypeDefinition();
        var name = (definition.FullName ?? definition.Name).Split('`')[0];
        return name + "<" + string.Join(",", type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private static string FormatConstant(object? value) => value switch
    {
        null => "null",
        string text => "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"",
        char character => "'" + character + "'",
        bool boolean => boolean ? "true" : "false",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
    };
}
