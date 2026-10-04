namespace DownKyi.CentralTestRunner;

internal sealed record CommandOptions(
    string RepositoryRoot,
    string? Project,
    string Configuration,
    bool NoRestore,
    bool NoBuild,
    string? ResultsDirectory,
    string? TrxName,
    string[] Classes,
    string[] ExcludedClasses,
    string? Filter,
    string? EvidenceDirectory)
{
    public static CommandOptions Parse(string[] args)
    {
        var repositoryRoot = Directory.GetCurrentDirectory();
        string? project = null;
        var configuration = "Release";
        var noRestore = false;
        var noBuild = false;
        string? resultsDirectory = null;
        string? trxName = null;
        var classes = new List<string>();
        var excludedClasses = new List<string>();
        string? filter = null;
        string? evidenceDirectory = null;

        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--repository-root":
                    repositoryRoot = ReadValue(args, ref index);
                    break;
                case "--project":
                    project = ReadValue(args, ref index);
                    break;
                case "--configuration":
                    configuration = ReadValue(args, ref index);
                    break;
                case "--no-restore":
                    noRestore = true;
                    break;
                case "--no-build":
                    noBuild = true;
                    break;
                case "--results-directory":
                    resultsDirectory = ReadValue(args, ref index);
                    break;
                case "--trx-name":
                    trxName = ReadValue(args, ref index);
                    break;
                case "--class":
                    classes.Add(ReadValue(args, ref index));
                    break;
                case "--exclude-class":
                    excludedClasses.Add(ReadValue(args, ref index));
                    break;
                case "--filter":
                    filter = ReadValue(args, ref index);
                    break;
                case "--evidence-directory":
                    evidenceDirectory = ReadValue(args, ref index);
                    break;
                default:
                    throw new ArgumentException($"Unknown option: {args[index]}", nameof(args));
            }
        }

        if (configuration is not ("Debug" or "Release"))
        {
            throw new ArgumentOutOfRangeException(nameof(args), "Configuration must be Debug or Release.");
        }
        return new CommandOptions(
            repositoryRoot,
            project,
            configuration,
            noRestore,
            noBuild,
            resultsDirectory,
            trxName,
            classes.ToArray(),
            excludedClasses.ToArray(),
            filter,
            evidenceDirectory);
    }

    private static string ReadValue(string[] args, ref int index)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index]))
        {
            throw new ArgumentException("A command option is missing its value.", nameof(args));
        }

        return args[index];
    }
}
