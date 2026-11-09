using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace RepoYak.Api;

public sealed class RepositoryKnowledge
{
    private const int MaxFiles = 5000;
    private const int MaxFileCharacters = 150_000;
    private const int MaxContextCharacters = 16_000;
    private const int MaxExcerptCharacters = 3_500;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".adoc", ".cddl", ".cfg", ".cs", ".csproj", ".css", ".feature", ".go", ".html", ".ini",
        ".java", ".js", ".json", ".md", ".mdx", ".mjs", ".props", ".ps1", ".py", ".rst", ".rs",
        ".sln", ".sql", ".sh", ".toml", ".ts", ".tsx", ".txt", ".xml", ".yaml", ".yml"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".vs", ".idea", ".venv", "bin", "build", "dist", "node_modules",
        "obj", "packages", "previews", "venv"
    };

    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "about", "after", "also", "and", "are", "based", "could", "does", "from", "have", "into",
        "more", "our", "repo", "repository", "that", "their", "there", "these", "this", "those",
        "using", "what", "when", "where", "which", "with", "would", "your"
    };

    private readonly string _rootPath;

    public RepositoryKnowledge(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        if (!Directory.Exists(_rootPath))
        {
            throw new DirectoryNotFoundException($"Repository directory does not exist: {_rootPath}");
        }

        if (!File.Exists(Path.Combine(_rootPath, ".git")) && !Directory.Exists(Path.Combine(_rootPath, ".git")))
        {
            throw new InvalidOperationException($"Configured path is not a supported repository folder: {_rootPath}");
        }
    }

    public string RootPath => _rootPath;

    public RepositoryIdentity Identity
    {
        get
        {
            var fallback = ReadmeIdentity;
            var profilePath = Path.Combine(_rootPath, ".agent", "agent-profile.json");
            if (!File.Exists(profilePath))
            {
                return fallback;
            }

            var profile = JsonSerializer.Deserialize<AgentProfile>(File.ReadAllText(profilePath), new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            }) ?? throw new InvalidOperationException($"Agent profile is empty: {profilePath}");

            return new RepositoryIdentity(
                string.IsNullOrWhiteSpace(profile.Name) ? fallback.Name : profile.Name.Trim(),
                string.IsNullOrWhiteSpace(profile.Description) ? fallback.Description : profile.Description.Trim());
        }
    }

    private RepositoryIdentity ReadmeIdentity
    {
        get
        {
            var readmePath = Path.Combine(_rootPath, "README.md");
            if (!File.Exists(readmePath))
            {
                return new RepositoryIdentity("Agent", "An agentic assistant grounded in configured workspace content.");
            }

            var readme = File.ReadAllText(readmePath);
            var heading = Regex.Match(readme, @"(?m)^#\s+(.+?)\s*#*\s*$");
            var name = heading.Success ? heading.Groups[1].Value.Trim() : "Agent";
            var description = FindReadmeDescription(readme, heading);
            return new RepositoryIdentity(name, description ?? "An agentic assistant grounded in configured workspace content.");
        }
    }

    public List<string> SkillDirectories
    {
        get
        {
            var skillRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in EnumerateRepositoryFiles().Where(path => Path.GetFileName(path).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase)))
            {
                var skillDirectory = Path.GetDirectoryName(file);
                var skillsRoot = FindSkillsRoot(skillDirectory);
                if (skillsRoot is not null && IsWithinRoot(skillsRoot))
                {
                    skillRoots.Add(skillsRoot);
                }
            }

            return skillRoots.Order(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    public List<string> AgentDirectories =>
        new[]
        {
            Path.Combine(_rootPath, ".github", "agents"),
            Path.Combine(_rootPath, ".agents", "agents")
        }
        .Where(Directory.Exists)
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToList();

    public string BuildContext(string question)
    {
        var terms = Regex.Matches(question.ToLowerInvariant(), @"[\p{L}\p{N}_-]{2,}")
            .Select(match => match.Value)
            .Where(term => !StopWords.Contains(term))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var documents = EnumerateRepositoryFiles()
            .Where(path => TextExtensions.Contains(Path.GetExtension(path)))
            .Select(ReadDocument)
            .Where(document => document is not null)
            .Select(document => document!)
            .ToArray();

        var selected = documents
            .Select(document => new
            {
                Document = document,
                Score = Score(document, terms)
            })
            .Where(result => result.Score > 0 || IsRepositoryGuide(result.Document.Path) || IsOkfRootIndex(result.Document))
            .OrderByDescending(result => IsRepositoryGuide(result.Document.Path) || IsOkfRootIndex(result.Document) ? 1 : 0)
            .ThenByDescending(result => result.Score)
            .ThenBy(result => result.Document.Path, StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();

        if (selected.Length == 0)
        {
            return "No workspace documents matched the question. The configured workspace contains "
                + $"{documents.Length} readable text files.";
        }

        var output = new StringBuilder();
        output.AppendLine($"Repository root: {_rootPath}");
        output.AppendLine($"Searchable text files: {documents.Length}");
        var remaining = MaxContextCharacters - output.Length;

        foreach (var result in selected)
        {
            if (remaining <= 0)
            {
                break;
            }

            var excerpt = SelectExcerpt(result.Document.Content, terms, Math.Min(MaxExcerptCharacters, remaining - 100));
            if (string.IsNullOrWhiteSpace(excerpt))
            {
                continue;
            }

            var block = $"\n--- Source: {result.Document.Path} ---\n{excerpt}\n";
            if (block.Length > remaining)
            {
                block = block[..remaining];
            }

            output.Append(block);
            remaining -= block.Length;
        }

        return output.ToString();
    }

    private Document? ReadDocument(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileCharacters * 4L)
        {
            return null;
        }

        var content = File.ReadAllText(path);
        if (content.Length > MaxFileCharacters)
        {
            content = content[..MaxFileCharacters];
        }

        var relativePath = Path.GetRelativePath(_rootPath, path)
            .Replace(Path.DirectorySeparatorChar, '/')
            .Replace(Path.AltDirectorySeparatorChar, '/');
        return new Document(relativePath, content);
    }

    private IEnumerable<string> EnumerateRepositoryFiles()
    {
        var pending = new Stack<string>();
        pending.Push(_rootPath);
        var count = 0;

        while (pending.Count > 0 && count < MaxFiles)
        {
            var current = pending.Pop();
            foreach (var directory in Directory.EnumerateDirectories(current))
            {
                var info = new DirectoryInfo(directory);
                if (ExcludedDirectories.Contains(info.Name) || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                pending.Push(directory);
            }

            foreach (var file in Directory.EnumerateFiles(current))
            {
                var info = new FileInfo(file);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }

                count++;
                yield return file;
                if (count >= MaxFiles)
                {
                    yield break;
                }
            }
        }
    }

    private bool IsWithinRoot(string path)
    {
        var relative = Path.GetRelativePath(_rootPath, path);
        return relative != ".."
            && !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
            && !Path.IsPathRooted(relative);
    }

    private static string? FindSkillsRoot(string? skillDirectory)
    {
        if (skillDirectory is null)
        {
            return null;
        }

        var current = new DirectoryInfo(skillDirectory);
        while (current is not null)
        {
            if (current.Name.Equals("skills", StringComparison.OrdinalIgnoreCase))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return skillDirectory;
    }

    private static string? FindReadmeDescription(string readme, Match heading)
    {
        if (!heading.Success)
        {
            return null;
        }

        var afterHeading = readme[(heading.Index + heading.Length)..];
        foreach (var paragraph in Regex.Split(afterHeading, @"\r?\n\s*\r?\n"))
        {
            var text = paragraph.Trim();
            if (text.Length == 0 || text[0] == '#' || text.StartsWith("![", StringComparison.Ordinal))
            {
                continue;
            }

            if (text[0] == '<' || text.StartsWith("```", StringComparison.Ordinal))
            {
                continue;
            }

            text = Regex.Replace(text, @"\[([^\]]+)\]\([^)]+\)", "$1");
            text = Regex.Replace(text, @"[*_`~]", string.Empty).Trim();
            if (text.Length > 300)
            {
                text = text[..297].TrimEnd() + "...";
            }

            return text;
        }

        return null;
    }

    private static bool IsRepositoryGuide(string path)
    {
        var fileName = Path.GetFileName(path);
        return fileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("README.md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsOkfRootIndex(Document document) =>
        Path.GetFileName(document.Path).Equals("index.md", StringComparison.OrdinalIgnoreCase)
        && Regex.IsMatch(document.Content, @"(?m)^okf_version:\s*[""']?0\.2\b");

    private static int Score(Document document, string[] terms)
    {
        if (terms.Length == 0)
        {
            return 0;
        }

        var content = document.Content.ToLowerInvariant();
        var path = document.Path.ToLowerInvariant();
        var score = 0;
        foreach (var term in terms)
        {
            score += CountOccurrences(content, term);
            if (path.Contains(term, StringComparison.Ordinal))
            {
                score += 4;
            }
        }

        return score;
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(term, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += term.Length;
        }

        return count;
    }

    private static string SelectExcerpt(string content, string[] terms, int limit)
    {
        if (limit <= 0)
        {
            return string.Empty;
        }

        if (content.Length <= limit)
        {
            return content;
        }

        var paragraphs = Regex.Split(content, @"\r?\n\s*\r?\n")
            .Select((text, index) => new
            {
                Text = text.Trim(),
                Index = index,
                Score = terms.Sum(term => CountOccurrences(text, term))
            })
            .Where(paragraph => paragraph.Text.Length > 0)
            .OrderByDescending(paragraph => paragraph.Score)
            .ThenBy(paragraph => paragraph.Index);

        var chosen = new List<string>();
        var length = 0;
        foreach (var paragraph in paragraphs)
        {
            var addition = paragraph.Text.Length + 2;
            if (length + addition > limit)
            {
                continue;
            }

            chosen.Add(paragraph.Text);
            length += addition;
        }

        return chosen.Count == 0 ? content[..limit] : string.Join("\n\n", chosen);
    }

    private sealed record Document(string Path, string Content);
    private sealed record AgentProfile(string? Name, string? Description);
}
