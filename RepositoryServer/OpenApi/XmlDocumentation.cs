using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Serilog;

namespace OpenShock.RepositoryServer.OpenApi;

/// <summary>
/// Reads the XML documentation file the build writes next to the assembly, for what the built-in OpenAPI XML comment support
/// leaves out: controller summaries as tag descriptions. Each file is loaded once per process. Documentation is optional: a
/// missing or unreadable file is logged and treated as empty.
/// </summary>
internal static partial class XmlDocumentation
{
    private static readonly ConcurrentDictionary<Assembly, Lazy<IReadOnlyDictionary<string, XElement>>> Files = new();

    /// <summary>
    /// The &lt;member&gt; element documenting <paramref name="type"/>, if any.
    /// </summary>
    public static XElement? Find(Type type) => Members(type.Assembly).GetValueOrDefault("T:" + (type.FullName ?? type.Name).Replace('+', '.'));

    /// <summary>
    /// The text of a documentation element, formatted the way the built-in XML comment support formats it: <c>&lt;c&gt;</c> as code,
    /// a <c>&lt;see cref&gt;</c> as the name it refers to, paragraphs separated by a blank line and the source indentation removed.
    /// </summary>
    public static string Text(XElement? element)
    {
        if (element is null) return "";

        var builder = new StringBuilder();
        AppendNodes(builder, element.Nodes());

        var lines = builder.ToString().Split('\n').Select(line => line.Trim());
        return BlankLines().Replace(string.Join('\n', lines), "\n\n").Trim();
    }

    private static void AppendNodes(StringBuilder builder, IEnumerable<XNode> nodes)
    {
        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText text:
                    builder.Append(text.Value.Replace("\r", ""));
                    break;
                case XElement { Name.LocalName: "c" } code:
                    builder.Append('`').Append(code.Value).Append('`');
                    break;
                case XElement { Name.LocalName: "para" } para:
                    builder.Append("\n\n");
                    AppendNodes(builder, para.Nodes());
                    builder.Append("\n\n");
                    break;
                case XElement { Name.LocalName: "br" }:
                    builder.Append('\n');
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } see:
                    builder.Append(Reference(see));
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } reference:
                    builder.Append('`').Append(reference.Attribute("name")?.Value).Append('`');
                    break;
                case XElement other:
                    AppendNodes(builder, other.Nodes());
                    break;
            }
        }
    }

    private static string Reference(XElement see)
    {
        if (!see.IsEmpty) return see.Value;
        if (see.Attribute("langword")?.Value is { } langword) return $"`{langword}`";
        if (see.Attribute("href")?.Value is { } href) return href;
        if (see.Attribute("cref")?.Value is not { Length: > 2 } cref) return "";

        // "T:Ns.Type", "P:Ns.Type.Member" or "M:Ns.Type.Member(args)": keep the type and member name, like "Type.Member"
        var name = cref[2..];
        var parameters = name.IndexOf('(');
        if (parameters >= 0) name = name[..parameters];

        var segments = name.Split('.');
        return cref[0] == 'T' || segments.Length < 2 ? segments[^1] : $"{segments[^2]}.{segments[^1]}";
    }

    private static IReadOnlyDictionary<string, XElement> Members(Assembly assembly) =>
        Files.GetOrAdd(assembly, a => new Lazy<IReadOnlyDictionary<string, XElement>>(() => Load(a))).Value;

    private static IReadOnlyDictionary<string, XElement> Load(Assembly assembly)
    {
        var path = Path.Combine(AppContext.BaseDirectory, assembly.GetName().Name + ".xml");
        try
        {
            if (!File.Exists(path))
            {
                Log.Warning("OpenAPI XML documentation file {Path} was not found, its descriptions are left out", path);
                return new Dictionary<string, XElement>();
            }

            return XDocument.Load(path, LoadOptions.PreserveWhitespace)
                .Descendants("member")
                .Where(member => member.Attribute("name") is not null)
                .GroupBy(member => member.Attribute("name")!.Value, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);
        }
        catch (Exception e)
        {
            Log.Warning(e, "Failed to load OpenAPI XML documentation file {Path}, its descriptions are left out", path);
            return new Dictionary<string, XElement>();
        }
    }

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex BlankLines();
}
