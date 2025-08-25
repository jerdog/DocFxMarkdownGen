using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Log73;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

// Class definitions
class Config
{
    public string YamlPath { get; set; } = string.Empty;
    public string OutputPath { get; set; } = string.Empty;
    public string IndexSlug { get; set; } = "/api";
    public ConfigTypesGrouping? TypesGrouping { get; set; }
    public string BrNewline { get; set; } = "\n\n";
    public bool ForceNewline { get; set; } = false;
    public string ForcedNewline { get; set; } = "  \n";
    public bool RewriteInterlinks { get; set; } = false;
}

public class ConfigTypesGrouping
{
    public bool Enabled { get; set; }
    public int MinCount { get; set; } = 12;
}

class DocFxFile
{
    public Item[] Items { get; set; } = Array.Empty<Item>();
}

class Item
{
    public string Uid { get; set; } = string.Empty;
    public string CommentId { get; set; } = string.Empty;
    public string Id { get; set; } = string.Empty;
    public string Parent { get; set; } = string.Empty;
    public string[] Children { get; set; } = Array.Empty<string>();
    public string[] Langs { get; set; } = Array.Empty<string>();
    public string Definition { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string NameWithType { get; set; } = string.Empty;
    public string FullName { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public Source? Source { get; set; }
    public string[] Assemblies { get; set; } = Array.Empty<string>();
    public string Namespace { get; set; } = string.Empty;
    public string? Summary { get; set; }
    public string? Remarks { get; set; }
    public string[]? Example { get; set; }
    public Syntax? Syntax { get; set; }
    public string[]? Inheritance { get; set; }
    public string[]? InheritedMembers { get; set; }
    public string[]? DerivedClasses { get; set; }
    public string[]? Implements { get; set; }
    public string[]? ExtensionMethods { get; set; }
    public ThrowsException[]? Exceptions { get; set; }
}

class ThrowsException
{
    public string Type { get; set; } = string.Empty;
    public string CommentId { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

class Syntax
{
    public string Content { get; set; } = string.Empty;
    [YamlMember(Alias = "content.vb")]
    public string ContentVb { get; set; } = string.Empty;
    public Parameter[]? Parameters { get; set; }
    public TypeParameter[]? TypeParameters { get; set; }
    public SyntaxReturn? Return { get; set; }
}

class Source
{
    public Remote? Remote { get; set; }
    public string Id { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public int StartLine { get; set; }
}

class Remote
{
    public string Path { get; set; } = string.Empty;
    public string Branch { get; set; } = string.Empty;
    public string Repo { get; set; } = string.Empty;
}

class Parameter
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
}

class TypeParameter
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

class SyntaxReturn
{
    public string Type { get; set; } = string.Empty;
    public string? Description { get; set; }
}

// ReSharper disable ClassNeverInstantiated.Global
// ReSharper disable UnusedAutoPropertyAccessor.Global
#pragma warning disable CS8618

class Program
{
    static async Task Main()
    {
        if (Environment.GetEnvironmentVariable("JAN_DEBUG") == "1")
            Log73.Console.Options.LogLevel = LogLevel.Debug;

        Log73.Console.WriteLine($"Running on: {RuntimeInformation.FrameworkDescription}");

        var versionString = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString();
        Log73.Console.WriteLine($"DocFxMarkdownGen v{versionString} running...");

        var xrefRegex = new Regex("<xref href=\"(.+?)\" data-throw-if-not-resolved=\"false\"></xref>", RegexOptions.Compiled);
        var langwordXrefRegex =
            new Regex("<xref uid=\"langword_csharp_.+?\" name=\"(.+?)\" href=\"\"></xref>", RegexOptions.Compiled);
        var codeBlockRegex = new Regex("<pre><code class=\"lang-csharp\">((.|\n)+?)</code></pre>", RegexOptions.Compiled);
        var codeRegex = new Regex("<code>(.+?)</code>", RegexOptions.Compiled);
        var linkRegex = new Regex("<a href=\"(.+?)\">(.+?)</a>", RegexOptions.Compiled);
        var brRegex = new Regex("<br */?>", RegexOptions.Compiled);
        var yamlDeserializer = new DeserializerBuilder().WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties().Build();
        var config =
            yamlDeserializer.Deserialize<Config>(
                await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("DFMG_CONFIG") ?? "./config.yaml"));
        if (Environment.GetEnvironmentVariable("DFMG_OUTPUT_PATH") is { } outputPath and not "")
        {
            config.OutputPath = outputPath;
            Log73.Console.Info($"Output path overriden by env: {config.OutputPath}");
        }

        if (Environment.GetEnvironmentVariable("DFMG_YAML_PATH") is { } yamlPath and not "")
        {
            config.YamlPath = yamlPath;
            Log73.Console.Info($"YAML path overriden by env: {config.YamlPath}");
        }

        if (Directory.Exists(config.OutputPath))
            Directory.Delete(config.OutputPath, true);
        Directory.CreateDirectory(config.OutputPath);

        var stopwatch = Stopwatch.StartNew();
        List<Item> items = new();

        #region read all yaml and create directory structure

        await Parallel.ForEachAsync(Directory.GetFiles(config.YamlPath, "*.yml"), async (file, _) =>
        {
            if (file.EndsWith("toc.yml"))
                return;
            Log73.Console.Debug(file);
            var obj = yamlDeserializer.Deserialize<DocFxFile>(await File.ReadAllTextAsync(file));
            lock (items)
            {
                items.AddRange(obj.Items);
            }
        });
        Log73.Console.Info($"Read all YAML in {stopwatch.ElapsedMilliseconds}ms.");
        // create namespace directories
        Parallel.ForEach(items, (item, _) =>
        {
            if (item.Type == "Namespace")
            {
                Log73.Console.Debug(item.Type + ": " + item.Name);
                var dir = Path.Combine(config.OutputPath, NamespaceFolder(item.Name));
                Directory.CreateDirectory(dir);
            }
        });

        #endregion

        // util methods
        static string NamespaceFolder(string @namespace)
        {
            const string prefix = "Workspace.XBR.";
            return @namespace.StartsWith(prefix, StringComparison.Ordinal)
                ? @namespace[prefix.Length..]
                : @namespace;
        }
        static string GetTypePathPart(string type)
            => type switch
            {
                "Class" => "Classes",
                "Struct" => "Structs",
                "Interface" => "Interfaces",
                "Enum" => "Enums",
                "Delegate" => "Delegates",
                _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
            };

        Item[] GetProperties(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Property").ToArray();

        Item[] GetFields(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Field").ToArray();

        Item[] GetMethods(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Method").ToArray();

        Item[] GetEvents(string uid)
            => items.Where(i => i.Parent == uid && i.Type == "Event").ToArray();

        string? HtmlEscape(string? str)
        {
            if (str == null) return null;
            return str.Replace("&", "&amp;"); // Only escape ampersands, no need to escape quotes here
        }

        string LintMarkdown(string markdown)
        {
            if (string.IsNullOrEmpty(markdown))
                return markdown;

            var lines = markdown.Split('\n');
            var result = new List<string>();
            var inCodeBlock = false;
            var codeBlockLanguage = "";

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmedLine = line.TrimEnd();

                                // Handle code blocks
                if (trimmedLine.StartsWith("```"))
                {
                    if (!inCodeBlock)
                    {
                        // Starting a code block - ensure blank line before
                        if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[result.Count - 1]))
                        {
                            result.Add("");
                        }

                        inCodeBlock = true;
                        codeBlockLanguage = trimmedLine.Substring(3).Trim();

                        // Ensure code block has language specified
                        if (string.IsNullOrWhiteSpace(codeBlockLanguage))
                        {
                            line = "```csharp";
                        }

                        result.Add(line);
                    }
                    else
                    {
                        // Ending a code block - ensure blank line after
                        // Remove any language specification from closing fence
                        result.Add("```");
                        result.Add("");
                        inCodeBlock = false;
                        codeBlockLanguage = "";
                    }
                    continue;
                }

                // Fix indented code blocks
                if (line.StartsWith("    ```"))
                {
                    line = line.Substring(4); // Remove indentation
                }

                // Fix malformed code block closing fences (remove language from closing fence)
                if (line.StartsWith("```") && line != "```" && inCodeBlock)
                {
                    line = "```";
                }

                // Fix incomplete code blocks (missing closing fence)
                if (inCodeBlock && line.StartsWith("```") && line.Length > 3)
                {
                    // This looks like a malformed closing fence with language
                    line = "```";
                }

                // Fix orphaned closing fences (``` without opening)
                if (line == "```" && !inCodeBlock)
                {
                    // Skip orphaned closing fences
                    continue;
                }

                // Skip processing inside code blocks
                if (inCodeBlock)
                {
                    result.Add(line);
                    continue;
                }

                // Fix trailing spaces
                if (trimmedLine != line)
                {
                    line = trimmedLine;
                }

                // Fix multiple consecutive blank lines (keep max 2)
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (result.Count > 0 && string.IsNullOrWhiteSpace(result[result.Count - 1]))
                    {
                        // Skip this line if previous was also blank
                        continue;
                    }
                }

                                // Fix heading levels (MD001) - ensure proper increment
                if (line.StartsWith("#"))
                {
                    var headingLevel = 0;
                    for (int j = 0; j < line.Length && line[j] == '#'; j++)
                    {
                        headingLevel++;
                    }

                    // Ensure there's a blank line before headings (except at start of file)
                    if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[result.Count - 1]))
                    {
                        result.Add("");
                    }

                    // Add the heading
                    result.Add(line);

                    // Ensure there's a blank line after headings
                    result.Add("");

                    // Skip the normal result.Add(line) at the end
                    continue;
                }

                // Fix list item indentation (MD007) - use 2 spaces instead of 4
                if (line.StartsWith("* ") || line.StartsWith("- ") || line.StartsWith("+ "))
                {
                    // Ensure there's a blank line before list items if not preceded by another list item
                    if (result.Count > 0 &&
                        !string.IsNullOrWhiteSpace(result[result.Count - 1]) &&
                        !result[result.Count - 1].StartsWith("* ") &&
                        !result[result.Count - 1].StartsWith("- ") &&
                        !result[result.Count - 1].StartsWith("+ "))
                    {
                        result.Add("");
                    }
                }
                else if (line.StartsWith("    * ") || line.StartsWith("    - ") || line.StartsWith("    + "))
                {
                    // Fix indented list items to use 2 spaces instead of 4
                    line = "  " + line.Substring(4);
                }
                else if (line.StartsWith("      * ") || line.StartsWith("      - ") || line.StartsWith("      + "))
                {
                    // Fix deeper indented list items to use 4 spaces instead of 6
                    line = "    " + line.Substring(6);
                }

                // Fix table formatting
                if (line.Trim().StartsWith("|") && line.Trim().EndsWith("|"))
                {
                    var parts = line.Trim().Split('|');
                    var contentParts = parts.Skip(1).Take(parts.Length - 2).Select(p => p.Trim());
                    line = $"| {string.Join(" | ", contentParts)} |";
                }

                // Fix link formatting
                line = Regex.Replace(line, @"\[([^\]]+)\]\(([^)]+)\)", match =>
                {
                    var text = match.Groups[1].Value;
                    var url = match.Groups[2].Value;

                    // Clean up the text and URL
                    text = text.Trim();
                    url = url.Trim();

                    return $"[{text}]({url})";
                });

                // Split inline headings that appear mid-line (e.g., "... text ### Heading")
                if (!inCodeBlock && line.Contains("### ") && !line.TrimStart().StartsWith("### "))
                {
                    var idx = line.IndexOf("### ", StringComparison.Ordinal);
                    if (idx > 0)
                    {
                        var before = line[..idx].TrimEnd();
                        var heading = line[idx..].TrimStart();
                        if (!string.IsNullOrWhiteSpace(before))
                        {
                            result.Add(before);
                        }
                        // Ensure blank line before heading
                        if (result.Count > 0 && !string.IsNullOrWhiteSpace(result[^1]))
                            result.Add("");
                        result.Add(heading);
                        result.Add(""); // blank after heading
                        continue;
                    }
                }

                // Fix bold and italic formatting
                line = Regex.Replace(line, @"\*\*([^*]+)\*\*", "**$1**");
                line = Regex.Replace(line, @"\*([^*]+)\*", "*$1*");

                // Fix code formatting
                line = Regex.Replace(line, @"`([^`]+)`", "`$1`");

                // Wrap placeholder tokens like {appToken} with backticks, but only outside inline/code blocks
                static string WrapPlaceholdersOutsideCode(string input)
                {
                    if (string.IsNullOrEmpty(input)) return input;
                    var sb = new StringBuilder(input.Length + 16);
                    bool inCode = false;
                    for (int i = 0; i < input.Length; i++)
                    {
                        var ch = input[i];
                        if (ch == '`')
                        {
                            inCode = !inCode;
                            sb.Append(ch);
                            continue;
                        }

                        if (!inCode && ch == '{')
                        {
                            var end = input.IndexOf('}', i + 1);
                            if (end > i + 1)
                            {
                                var inner = input.Substring(i + 1, end - i - 1);
                                if (Regex.IsMatch(inner, @"^[A-Za-z0-9_.\-]+$"))
                                {
                                    sb.Append('`').Append('{').Append(inner).Append('}').Append('`');
                                    i = end; // advance past '}'
                                    continue;
                                }
                            }
                        }

                        sb.Append(ch);
                    }
                    return sb.ToString();
                }

                line = WrapPlaceholdersOutsideCode(line);

                // Fix HTML entities
                line = line.Replace("&amp;", "&")
                          .Replace("&lt;", "<")
                          .Replace("&gt;", ">");

                // Fix malformed code block patterns that appear in content
                line = Regex.Replace(line, @"```csharp\s*$", "```");
                line = Regex.Replace(line, @"```\s*```csharp", "```");
                line = Regex.Replace(line, @"```\s*```", "```");

                result.Add(line);
            }

            // Remove trailing blank lines
            while (result.Count > 0 && string.IsNullOrWhiteSpace(result[result.Count - 1]))
            {
                result.RemoveAt(result.Count - 1);
            }

            var finalMarkdown = string.Join("\n", result);

                        // Post-process to fix only the most obvious malformed patterns
            // Be very conservative to avoid breaking valid code blocks
            finalMarkdown = Regex.Replace(finalMarkdown, @"```csharp\s*$", "```");
            finalMarkdown = Regex.Replace(finalMarkdown, @"```\s*```csharp\s*$", "```");

            return finalMarkdown;
        }

        string SafeFrontmatterValue(string? str)
        {
            if (str == null) return "";

            // Remove HTML tags first
            str = Regex.Replace(str, "<[^>]*>", ""); // More robust HTML tag removal

            // Truncate long descriptions and clean up for frontmatter
            var maxLength = 150;
            str = str.Length > maxLength ? str[..maxLength] + "..." : str;

            return str
                .Replace("\n", " ")
                .Replace("\"", "'")
                .Replace("\\", "")
                .Replace(":", "-")
                .Replace("<", "")
                .Replace(">", "")
                .Trim();
        }

        string? FileEscape(string? str)
            => str?.Replace("<", "`").Replace(">", "`").Replace(" ", "%20");

        void Declaration(StringBuilder str, Item item)
        {
            // Do not convert or surface YAML `source:` keys; omit any source link output
            if (item.Syntax != null)
            {
                str.AppendLine("```csharp title=\"Declaration\"");
                // Ensure curly brace snippets are properly formatted
                var content = item.Syntax.Content;
                if (content.TrimStart().StartsWith("{"))
                    content = $"    {content}"; // Add indentation for standalone blocks
                str.AppendLine(content);
                str.AppendLine("```");
            }
        }

        // Ensure table cells contain inline-safe markdown only:
        // - Convert HTML to markdown via GetSummary
        // - Collapse newlines to <br />
        // - Escape pipe characters
        // - Trim whitespace
        string FormatTableCell(string? htmlOrMarkdown, bool linkFromGroupedType)
        {
            var converted = GetSummary(htmlOrMarkdown, linkFromGroupedType) ?? string.Empty;
            // Normalize newlines and collapse to <br /> for table cells
            converted = converted.Replace("\r\n", "\n");
            converted = Regex.Replace(converted, @"\s*\n\s*", " <br /> ");
            // Escape table pipe characters
            converted = converted.Replace("|", "\\|");
            return converted.Trim();
        }

        static string FormatTypeName(string name)
        {
            // Wrap names containing type parameters, curly braces, or dictionary-like content in backticks
            if (name.Contains('<') ||
                name.Contains('{') ||
                name.Contains('}') ||
                (name.Contains(':') && name.Contains('[')) ||  // Likely a dictionary
                Regex.IsMatch(name, @"\{.*:.*\}")) // Match dictionary-like patterns
                return $"`{name}`";
            return name;
        }

        string? GetSummary(string? summary, bool linkFromGroupedType)
        {
            if (summary == null)
                return null;

            // Remove nested p tags and replace with line breaks
            summary = Regex.Replace(summary, @"<p>\s*", "\n\n");
            summary = Regex.Replace(summary, @"\s*</p>", "");

            // Handle example blocks
            summary = Regex.Replace(summary, @"<example>(.*?)</example>", m =>
            {
                var content = m.Groups[1].Value.Trim();
                return $"\n\n**Example:**\n```csharp\n{content}\n```\n";
            });

            // Handle pre/code blocks with language specification
            summary = Regex.Replace(summary, @"<pre><code class=""lang-csharp"">(.*?)</code></pre>", m =>
            {
                var content = m.Groups[1].Value.Trim();
                return $"\n```csharp\n{content}\n```\n";
            });

            // Handle pre/code blocks without language specification
            summary = Regex.Replace(summary, @"<pre><code>(.*?)</code></pre>", m =>
            {
                var content = m.Groups[1].Value.Trim();
                return $"\n```csharp\n{content}\n```\n";
            });

            // Fix malformed code blocks that might be created during HTML conversion
            summary = Regex.Replace(summary, @"```\s*```csharp", "```");
            summary = Regex.Replace(summary, @"```csharp\s*```", "```");
            summary = Regex.Replace(summary, @"```\s*```", "```");

            // Replace other HTML tags with markdown - ensure tags are properly closed
            summary = xrefRegex.Replace(summary, match =>
            {
                var uid = match.Groups[1].Value;
                return Link(uid, linkFromGroupedType);
            });
            summary = langwordXrefRegex.Replace(summary, match => $"`{match.Groups[1].Value}`");
            summary = codeBlockRegex.Replace(summary, match => $"```csharp\n{match.Groups[1].Value.Trim()}\n```");

            // Ensure inline code tags are properly closed
            summary = Regex.Replace(summary, @"<code>([^<]*)</code>", match => $"`{match.Groups[1].Value}`");
            summary = Regex.Replace(summary, @"<code>([^<]*)", match => $"`{match.Groups[1].Value}`"); // Handle unclosed tags

            summary = linkRegex.Replace(summary, match => $"[{match.Groups[2].Value}]({match.Groups[1].Value})");
            summary = brRegex.Replace(summary, _ => "\n\n");

            // Handle HTML entities
            summary = summary.Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");

            // Handle bold tags
            summary = Regex.Replace(summary, @"<b>([^<]*)</b>", "**$1**");
            summary = Regex.Replace(summary, @"<strong>([^<]*)</strong>", "**$1**");

            // Handle italic tags
            summary = Regex.Replace(summary, @"<i>([^<]*)</i>", "*$1*");
            summary = Regex.Replace(summary, @"<em>([^<]*)</em>", "*$1*");

            // Handle span tags with class="term"
            summary = Regex.Replace(summary, @"<span class=""term"">([^<]*)</span>", "**$1**");

            // Handle ul/li tags for lists (ensure each item is on its own line)
            summary = Regex.Replace(summary, @"<ul\b[^>]*>", "\n");
            summary = Regex.Replace(summary, @"</ul>", "\n");
            summary = Regex.Replace(summary, @"<li>([\s\S]*?)</li>", m => "\n* " + m.Groups[1].Value.Trim() + "\n");

            // Clean up any remaining HTML tags
            summary = Regex.Replace(summary, @"<[^>]*>", "");

            // Handle dictionary-like content
            summary = Regex.Replace(summary, @"\{[^}]*:[^}]*\}", match => $"`{match.Value}`");

            // Clean up multiple newlines
            summary = Regex.Replace(summary, @"\n{3,}", "\n\n");

            if (config.ForceNewline)
                summary = summary.Replace("\n", config.ForcedNewline);

            return HtmlEscape(summary)?.Trim();
        }

        string BuildRemarksAdmonitions(string? remarks, bool linkFromGroupedType)
        {
            if (string.IsNullOrWhiteSpace(remarks)) return string.Empty;

            // Extract <p>...</p> segments; fallback to whole content if none
            var matches = Regex.Matches(remarks, @"<p>([\s\S]*?)</p>", RegexOptions.IgnoreCase);
            var paragraphs = new List<string>();
            if (matches.Count > 0)
            {
                foreach (Match m in matches)
                {
                    paragraphs.Add(m.Groups[1].Value);
                }
            }
            else
            {
                paragraphs.Add(remarks);
            }

            static bool IsCautionParagraph(string html)
            {
                var plain = Regex.Replace(html ?? string.Empty, @"<[^>]*>", string.Empty).TrimStart();
                return plain.StartsWith("Use with caution.", StringComparison.OrdinalIgnoreCase);
            }

            var sb = new StringBuilder();
            foreach (var para in paragraphs)
            {
                var content = GetSummary(para, linkFromGroupedType)?.Trim();
                if (string.IsNullOrWhiteSpace(content)) continue;
                if (IsCautionParagraph(para))
                {
                    sb.AppendLine($":::caution");
                    sb.AppendLine();
                    sb.AppendLine(content);
                    sb.AppendLine();
                    sb.AppendLine(":::");
                    sb.AppendLine();
                }
                else
                {
                    sb.AppendLine(content);
                    sb.AppendLine();
                }
            }

            return sb.ToString();
        }

        Log73.Console.Info("Generating and writing markdown...");

        // if grouping types, count types in each namespace, for minCount
        // we have to make it a local method like this because of the ref there(cannot be in async method)
        static void DoTypeCounts(List<Item> items, Dictionary<string, int> typeCounts)
        {
            foreach (var item in items)
            {
                if (item.Type is not ("Class" or "Interface" or "Enum" or "Struct" or "Delegate")) continue;
                ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(typeCounts, item.Namespace, out var exists);
                if (exists)
                    count++;
                else
                    count = 1;
            }
        }

        Dictionary<string, int>? typeCounts = null;
        if (config.TypesGrouping?.Enabled ?? false)
        {
            typeCounts = new();
            DoTypeCounts(items, typeCounts);
        }

        bool NamespaceHasTypeGrouping(string @namespace)
            => typeCounts is not null && typeCounts.TryGetValue(@namespace, out var count) &&
               count >= config.TypesGrouping!.MinCount;

        // Build a canonical name map to avoid case-collision filenames on case-insensitive filesystems
        // Key: namespace + "|" + type + "|" + lower(name) -> canonical Name chosen first-seen
        var canonicalTypeNameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items)
        {
            if (it.Type is "Class" or "Interface" or "Enum" or "Struct" or "Delegate")
            {
                var key = $"{it.Namespace}|{it.Type}|{it.Name.ToLowerInvariant()}";
                if (!canonicalTypeNameMap.ContainsKey(key))
                    canonicalTypeNameMap[key] = it.Name; // first wins
            }
        }

        string GetCanonicalTypeName(string @namespace, string type, string name)
        {
            var key = $"{@namespace}|{type}|{name.ToLowerInvariant()}";
            return canonicalTypeNameMap.TryGetValue(key, out var canon) ? canon : name;
        }

        string Link(string uid, bool linkFromGroupedType, bool nameOnly = false, bool linkFromIndex = false)
        {
            var reference = items.FirstOrDefault(i => i.Uid == uid);
            if (uid.Contains('{') && reference == null)
            {
                // try to resolve single type argument references
                var replaced = uid.Replace(uid[uid.IndexOf('{')..(uid.LastIndexOf('}') + 1)], "`1");
                reference = items.FirstOrDefault(i => i.Uid == replaced);
            }

            if (reference == null)
                return $"`{uid}`"; // Ensure unknown references are code-wrapped

            var name = nameOnly ? reference.Name : reference.FullName;
            // Wrap type parameters in backticks
            if (name.Contains('<'))
                name = $"`{name}`";

            var dots = linkFromIndex ? "./" : linkFromGroupedType ? "../../" : "../";
            var extension = linkFromIndex ? ".md" : "";
            if (reference.Type is "Class" or "Interface" or "Enum" or "Struct" or "Delegate")
            {
                var canonicalName = GetCanonicalTypeName(reference.Namespace, reference.Type, reference.Name);
                var nsFolder = NamespaceFolder(reference.Namespace);
                if (NamespaceHasTypeGrouping(reference.Namespace))
                    return
                        $"[{HtmlEscape(name)}]({FileEscape($"{dots}{nsFolder}/{GetTypePathPart(reference.Type)}/{canonicalName}{extension}")})";
                return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{nsFolder}/{canonicalName}{extension}")})";
            }
            else if (reference.Type is "Namespace")
            {
                // Always link to the namespace index file to satisfy Docusaurus resolver
                return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{NamespaceFolder(reference.Name)}/index.md")})";
            }
            else
            {
                var parent = items.FirstOrDefault(i => i.Uid == reference.Parent);
                if (parent == null)
                    return $"`{uid}`"; // Ensure unknown references are code-wrapped
                var anchor = Regex.Replace(reference.Name.ToLowerInvariant(), "[^a-z0-9]", "");
                var parentNsFolder = NamespaceFolder(parent.Namespace);
                // Place the anchor inside the link target
                return $"[{HtmlEscape(name)}]({FileEscape($"{dots}{parentNsFolder}{(NamespaceHasTypeGrouping(parent.Namespace) ? $"/{GetTypePathPart(parent.Type)}" : "")}/{parent.Name}{extension}#{anchor}")})";
            }
        }

        stopwatch.Restart();
        // create type files finally
        await Parallel.ForEachAsync(items, async (item, _) =>
        {
            // for global namespace?
            // ReSharper disable once ConditionIsAlwaysTrueOrFalseAccordingToNullableAPIContract
            if (item.CommentId == null)
            {
                if (item.Type == "Namespace")
                    return;
                // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
                Log73.Console.Warn($"Missing commentId for {item.Uid ?? item.Id ?? "(can't get uid or id)"}");
                return;
            }

            if (item.CommentId.StartsWith("T:"))
            {
                var isGroupedType = typeCounts != null && typeCounts[item.Namespace] >= config.TypesGrouping!.MinCount;
                var str = new StringBuilder();
                str.AppendLine("---");
                str.AppendLine($"title: {item.Type} {item.Name}");
                str.AppendLine($"sidebar_label: {item.Name}");
                if (item.Summary != null)
                    str.AppendLine($"description: \"{SafeFrontmatterValue(item.Summary)}\"");
                str.AppendLine("---");
                str.AppendLine($"# {item.Type} {FormatTypeName(item.Name)}");
                str.AppendLine(GetSummary(item.Summary, isGroupedType)?.Trim());
                str.AppendLine();
                str.AppendLine($"**Namespace:** {item.Namespace}");
                str.AppendLine();
                str.AppendLine($"**Assembly:** {item.Assemblies[0]}.dll");
                Declaration(str, item);
                // do not when it is only System.Object
                if (item.Inheritance?.Length > 1)
                {
                    str.Append("**Inheritance:** ");
                    for (int i = 0; i < item.Inheritance.Length; i++)
                    {
                        str.Append(Link(item.Inheritance[i], isGroupedType));
                        if (i != item.Inheritance.Length - 1)
                            str.Append(" -> ");
                    }

                    str.Append("\n\n");
                }

                // Examples
                if (item.Example?.Length > 0)
                {
                    str.AppendLine("## Examples");
                    foreach (var example in item.Example)
                    {
                        if (!string.IsNullOrWhiteSpace(example))
                        {
                            str.AppendLine(GetSummary(example, isGroupedType)?.Trim());
                            str.AppendLine();
                        }
                    }
                }

                // Remarks (parent type: keep original formatting, no admonitions)
                if (!string.IsNullOrWhiteSpace(item.Remarks))
                {
                    str.AppendLine("## Remarks");
                    str.AppendLine(GetSummary(item.Remarks, isGroupedType)?.Trim());
                    str.AppendLine();
                }

                if (item.DerivedClasses != null)
                {
                    str.AppendLine("**Derived:**  ");
                    if (item.DerivedClasses.Length > 8)
                        str.AppendLine("\n<details>\n<summary>Expand</summary>\n");

                    for (var i = 0; i < item.DerivedClasses.Length; i++)
                    {
                        str.Append(Link(item.DerivedClasses[i], isGroupedType));
                        if (i != item.DerivedClasses.Length - 1)
                            str.Append(", ");
                    }

                    if (item.DerivedClasses.Length > 8)
                        str.AppendLine("\n</details>\n");
                    str.Append("\n\n");
                }

                if (item.Implements != null)
                {
                    str.AppendLine("**Implements:**  ");
                    if (item.Implements.Length > 8)
                        str.AppendLine("\n<details>\n<summary>Expand</summary>\n");

                    for (var i = 0; i < item.Implements.Length; i++)
                    {
                        str.Append(Link(item.Implements[i], isGroupedType));
                        if (i != item.Implements.Length - 1)
                            str.Append(", ");
                    }

                    if (item.Implements.Length > 8)
                        str.AppendLine("\n</details>\n");
                    str.Append("\n\n");
                }

                // Properties
                var properties = GetProperties(item.Uid);
                if (properties.Length != 0)
                {
                    str.AppendLine("## Properties");
                    foreach (var property in properties)
                    {
                        str.AppendLine($"### {FormatTypeName(property.Name)}");
                        str.AppendLine(GetSummary(property.Summary, isGroupedType)?.Trim());
                        Declaration(str, property);
                        if (!string.IsNullOrWhiteSpace(property.Remarks))
                        {
                            str.AppendLine("##### Remarks");
                            var remarksBlock = BuildRemarksAdmonitions(property.Remarks, isGroupedType);
                            if (!string.IsNullOrWhiteSpace(remarksBlock))
                                str.AppendLine(remarksBlock.TrimEnd());
                        }
                    }
                }

                // Fields
                var fields = GetFields(item.Uid);
                if (fields.Length != 0)
                {
                    str.AppendLine("## Fields");
                    foreach (var field in fields)
                    {
                        str.AppendLine($"### {FormatTypeName(field.Name)}");
                        str.AppendLine(GetSummary(field.Summary, isGroupedType)?.Trim());
                        Declaration(str, field);
                        if (!string.IsNullOrWhiteSpace(field.Remarks))
                        {
                            str.AppendLine("##### Remarks");
                            var remarksBlock = BuildRemarksAdmonitions(field.Remarks, isGroupedType);
                            if (!string.IsNullOrWhiteSpace(remarksBlock))
                                str.AppendLine(remarksBlock.TrimEnd());
                        }
                    }
                }

                // Methods
                var methods = GetMethods(item.Uid);
                if (methods.Length != 0)
                {
                    str.AppendLine("## Methods");
                    foreach (var method in methods)
                    {
                        str.AppendLine($"### {FormatTypeName(method.Name)}");
                        str.AppendLine(GetSummary(method.Summary, isGroupedType)?.Trim());
                        Declaration(str, method);
                        if (!string.IsNullOrWhiteSpace(method.Remarks))
                        {
                            str.AppendLine("##### Remarks");
                            var remarksBlock = BuildRemarksAdmonitions(method.Remarks, isGroupedType);
                            if (!string.IsNullOrWhiteSpace(remarksBlock))
                                str.AppendLine(remarksBlock.TrimEnd());
                        }
                        if (!string.IsNullOrWhiteSpace(method.Syntax!.Return?.Type))
                        {
                            str.AppendLine();
                            str.AppendLine("##### Returns");
                            str.AppendLine();
                            str.Append(Link(method.Syntax.Return.Type, isGroupedType).Trim());
                            if (string.IsNullOrWhiteSpace(method.Syntax.Return?.Description))
                                str.AppendLine();
                            else
                                str.Append(": " + GetSummary(method.Syntax.Return.Description, isGroupedType));
                        }

                        if (method.Syntax.Parameters is { Length: > 0 })
                        {
                            str.AppendLine();
                            str.AppendLine("##### Parameters");
                            str.AppendLine();
                            if (method.Syntax.Parameters.Any(p => !string.IsNullOrWhiteSpace(p.Description)))
                            {
                                str.AppendLine("| Type | Name | Description |");
                                str.AppendLine("|:--- |:--- |:--- |");
                                foreach (var parameter in method.Syntax.Parameters)
                                {
                                    var desc = FormatTableCell(parameter.Description, isGroupedType);
                                    str.AppendLine($"| {Link(parameter.Type, isGroupedType)} | *{parameter.Id}* | {desc} |");
                                }
                            }
                            else
                            {
                                str.AppendLine("| Type | Name |");
                                str.AppendLine("|:--- |:--- |");
                                foreach (var parameter in method.Syntax.Parameters)
                                    str.AppendLine(
                                        $"| {Link(parameter.Type, isGroupedType)} | *{parameter.Id}* |");
                            }

                            str.AppendLine();
                        }

                        if (method.Syntax.TypeParameters is { Length: > 0 })
                        {
                            str.AppendLine("##### Type Parameters");
                            if (method.Syntax.TypeParameters.Any(tp => !string.IsNullOrWhiteSpace(tp.Description)))
                            {
                                str.AppendLine("| Name | Description |");
                                str.AppendLine("|:--- |:--- |");
                                foreach (var typeParameter in method.Syntax.TypeParameters)
                                {
                                    var tpDesc = FormatTableCell(typeParameter.Description, isGroupedType);
                                    str.AppendLine($"| {Link(typeParameter.Id, isGroupedType)} | {tpDesc} |");
                                }
                            }
                            else
                                foreach (var typeParameter in method.Syntax.TypeParameters)
                                    str.AppendLine($"* {Link(typeParameter.Id, isGroupedType)}");
                        }

                        if (method.Exceptions is { Length: > 0 })
                        {
                            str.AppendLine();
                            str.AppendLine("##### Exceptions");
                            str.AppendLine();
                            foreach (var exception in method.Exceptions)
                            {
                                // those two spaces are there so that we can have a line break without too much spacing
                                // before the next line
                                str.AppendLine($"{Link(exception.Type, isGroupedType)}  ");
                                str.AppendLine(GetSummary(exception.Description, isGroupedType)?.Trim());
                            }
                        }
                    }
                }

                // Events
                var events = GetEvents(item.Uid);
                if (events.Length != 0)
                {
                    str.AppendLine("## Events");
                    foreach (var @event in events)
                    {
                        str.AppendLine($"### {FormatTypeName(@event.Name)}");
                        str.AppendLine(GetSummary(@event.Summary, isGroupedType)?.Trim());
                        Declaration(str, @event);
                        if (!string.IsNullOrWhiteSpace(@event.Remarks))
                        {
                            str.AppendLine("##### Remarks");
                            var remarksBlock = BuildRemarksAdmonitions(@event.Remarks, isGroupedType);
                            if (!string.IsNullOrWhiteSpace(remarksBlock))
                                str.AppendLine(remarksBlock.TrimEnd());
                        }
                        str.AppendLine("##### Event Type");
                        if (@event.Syntax!.Return!.Description == null)
                            str.AppendLine(Link(@event.Syntax.Return.Type, isGroupedType).Trim());
                        else
                            str.AppendLine(Link(@event.Syntax.Return.Type, isGroupedType).Trim() + ": " +
                                           @event.Syntax.Return.Description);
                    }
                }

                // Implements
                if (item.Implements?.Any() ?? false)
                {
                    str.AppendLine();
                    str.AppendLine("## Implements");
                    str.AppendLine();
                    foreach (var implemented in item.Implements)
                    {
                        str.AppendLine($"* {Link(implemented, isGroupedType)}");
                    }
                }

                // Inherited Members
                if (item.InheritedMembers?.Length > 0)
                {
                    str.AppendLine();
                    str.AppendLine("## Inherited Members");
                    str.AppendLine();
                    foreach (var inheritedMember in item.InheritedMembers)
                    {
                        str.AppendLine($"* {Link(inheritedMember, isGroupedType)}");
                    }
                }

                // Extension methods
                if (item.ExtensionMethods is { Length: > 1 })
                {
                    str.AppendLine("## Extension Methods");
                    foreach (var extMethod in item.ExtensionMethods!)
                    {
                        // ReSharper disable once SimplifyConditionalTernaryExpression
                        // todo: wont link if other args are present
                        var method = items.FirstOrDefault(i =>
                            ((i.Syntax?.Parameters?.Any() ?? false)
                                ? (i.Syntax.Parameters[0].Type + '.' +
                                   i.FullName
                                       [..(i.FullName.IndexOf('(') == -1 ? i.FullName.Length : i.FullName.IndexOf('('))] ==
                                   extMethod)
                                : false));
                        if (method == null)
                            str.AppendLine($"* {extMethod.Replace("{", "&#123;").Replace("}", "&#125;")}");
                        else
                            str.AppendLine($"* {Link(method.Uid, isGroupedType)}");
                    }
                }

                var canonicalNameForWrite = GetCanonicalTypeName(item.Namespace, item.Type, item.Name);
                var safeName = canonicalNameForWrite.Replace('<', '`').Replace('>', '`');
                var path = !isGroupedType
                    ? Path.Join(config.OutputPath, NamespaceFolder(item.Namespace), safeName) + ".md"
                    : Path.Join(config.OutputPath, NamespaceFolder(item.Namespace), GetTypePathPart(item.Type), safeName) + ".md";

                // create directory if it doesn't exist
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                await File.WriteAllTextAsync(path, LintMarkdown(str.ToString()));
            }
            else if (item.Type == "Namespace")
            {
                var str = new StringBuilder();
                str.AppendLine("---");
                str.AppendLine($"title: {item.Type} {item.Name}");
                str.AppendLine($"sidebar_label: {NamespaceFolder(item.Name)}");
                str.AppendLine("---");
                str.AppendLine($"# Namespace {HtmlEscape(item.Name)}");

                void Do(string type, string header)
                {
                    var where = items.Where(i => i.Namespace == item.Name && i.Type == type).ToArray();
                    if (where.Length != 0)
                    {
                        str.AppendLine($"## {header}");
                        foreach (var item1 in where.OrderBy(i => i.Name))
                        {
                            str.AppendLine($"### {HtmlEscape(Link(item1.Uid, false, nameOnly: true))}");
                            str.AppendLine(GetSummary(item1.Summary, false)?.Trim());
                        }
                    }
                }

                Do("Class", "Classes");
                Do("Struct", "Structs");
                Do("Interface", "Interfaces");
                Do("Enum", "Enums");
                Do("Delegate", "Delegates");

                await File.WriteAllTextAsync(Path.Join(config.OutputPath, NamespaceFolder(item.Name), $"index.md"), LintMarkdown(str.ToString()));
            }
        });

        // Generate root index.md with links to all namespaces
        {
            var namespaces = items
                .Where(i => i.Type == "Namespace")
                .Select(i => i.Name)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();

            var root = new StringBuilder();
            root.AppendLine("---");
            root.AppendLine("title: API Namespaces");
            root.AppendLine("sidebar_label: Namespaces");
            root.AppendLine("---");
            root.AppendLine();
            root.AppendLine("# API Namespaces");
            root.AppendLine();
            foreach (var ns in namespaces)
            {
                // Link to each namespace folder/index.md
                root.AppendLine($"* [{HtmlEscape(ns)}](./{NamespaceFolder(ns)}/index.md)");
            }

            await File.WriteAllTextAsync(Path.Join(config.OutputPath, "index.md"), LintMarkdown(root.ToString()));
        }
        Log73.Console.Info($"Generated markdown in {stopwatch.ElapsedMilliseconds}ms.");
    }
}