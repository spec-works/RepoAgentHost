namespace RepoAgentHost.Api;

public sealed class PersonaSelectionException(string persona, Exception innerException)
    : Exception($"Unable to select custom agent '{persona}'. Check that it is installed and enabled for this repository. No work was executed.", innerException);
