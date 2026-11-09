namespace RepoYak.Api.Tests;

public sealed class RepositoryKnowledgeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"repository-agent-tests-{Guid.NewGuid():N}");

    public RepositoryKnowledgeTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".git"));
    }

    [Fact]
    public void SkillDirectories_FindsRootsContainingSkills()
    {
        CreateFile(".github/skills/visualize/SKILL.md", "# Visualize");
        CreateFile(".agents/skills/review/SKILL.md", "# Review");

        var repository = new RepositoryKnowledge(_root);

        Assert.Contains(Path.Combine(_root, ".github", "skills"), repository.SkillDirectories);
        Assert.Contains(Path.Combine(_root, ".agents", "skills"), repository.SkillDirectories);
    }

    [Fact]
    public void AgentDirectories_FindsSupportedRepositoryAgentRoots()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".github", "agents"));
        Directory.CreateDirectory(Path.Combine(_root, ".agents", "agents"));

        var repository = new RepositoryKnowledge(_root);

        Assert.Equal(
            new[]
            {
                Path.Combine(_root, ".agents", "agents"),
                Path.Combine(_root, ".github", "agents")
            }.Order(StringComparer.OrdinalIgnoreCase),
            repository.AgentDirectories);
    }

    [Fact]
    public void BuildContext_IncludesRelevantDocumentsAndOkfRootIndex()
    {
        CreateFile("README.md", "# Example repository\n");
        CreateFile("knowledge/index.md", "---\nokf_version: \"0.2\"\n---\n# Knowledge\n");
        CreateFile(
            "knowledge/projects/alpha.md",
            "---\ntype: Project\ntitle: Alpha\n---\nAlpha tracks the current migration milestone.\n");

        var context = new RepositoryKnowledge(_root).BuildContext("alpha migration");

        Assert.Contains("Source: knowledge/index.md", context);
        Assert.Contains("Source: knowledge/projects/alpha.md", context);
        Assert.Contains("current migration milestone", context);
    }

    [Fact]
    public void Identity_UsesReadmeHeadingAndOpeningDescription()
    {
        CreateFile(
            "README.md",
            "# Acme API\n\nA small API for managing useful things.\n\n## Details\nMore information.\n");

        var identity = new RepositoryKnowledge(_root).Identity;

        Assert.Equal("Acme API", identity.Name);
        Assert.Equal("A small API for managing useful things.", identity.Description);
    }

    [Fact]
    public void Identity_FallsBackToGenericAgentNameWhenReadmeIsMissing()
    {
        var identity = new RepositoryKnowledge(_root).Identity;

        Assert.Equal("Agent", identity.Name);
        Assert.Equal("An agentic assistant grounded in configured workspace content.", identity.Description);
    }

    [Fact]
    public void Identity_PrefersAgentProfileOverReadme()
    {
        CreateFile("README.md", "# README Name\n\nREADME description.\n");
        CreateFile(
            ".agent/agent-profile.json",
            """{"name":"Configured Agent","description":"Uses curated portfolio knowledge."}""");

        var identity = new RepositoryKnowledge(_root).Identity;

        Assert.Equal("Configured Agent", identity.Name);
        Assert.Equal("Uses curated portfolio knowledge.", identity.Description);
    }

    [Fact]
    public void Constructor_RejectsDirectoriesThatAreNotGitRepositories()
    {
        var nonRepository = Path.Combine(Path.GetTempPath(), $"not-a-repository-{Guid.NewGuid():N}");
        Directory.CreateDirectory(nonRepository);

        try
        {
            Assert.Throws<InvalidOperationException>(() => new RepositoryKnowledge(nonRepository));
        }
        finally
        {
            Directory.Delete(nonRepository, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string CreateFile(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }
}
