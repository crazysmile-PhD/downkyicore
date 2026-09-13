namespace DownKyi.Architecture.Tests;

public sealed class SqliteDownloadTaskStoreArchitectureTests
{
    private const string FacadeFile = "SqliteDownloadTaskStore.cs";
    private const string Facade = "SqliteDownloadTaskStore";
    private const string Database = "SqliteDownloadStoreDatabase";
    private static readonly string RepositoryRoot = FindRepositoryRoot();
    private static readonly string[] SqlMarkers = ["SELECT ", "INSERT ", "UPDATE ", "DELETE ", "PRAGMA "];
    private static readonly string[] ForbiddenOwnerTokens =
    [
        Facade,
        "IServiceProvider",
        "IServiceScope",
        "IServiceScopeFactory",
        "ServiceProvider",
        "GetService",
        "GetRequiredService",
        "CreateScope",
        "ActivatorUtilities",
        "Reflection",
        "BindingFlags",
        "MethodInfo",
        "PropertyInfo",
        "FieldInfo",
        "ConstructorInfo",
        "Assembly",
        "Activator",
        "dynamic"
    ];
    private static readonly HashSet<string> ReflectionLookups =
    [
        "GetMethod",
        "GetProperty",
        "GetField",
        "GetConstructor",
        "GetEvent",
        "GetMember"
    ];
    private static readonly HashSet<string> ReflectionInvocations =
    [
        "Invoke",
        "GetValue",
        "SetValue",
        "CreateDelegate"
    ];
    private static readonly Dictionary<string, string> ExpectedFacadeDelegations = new(StringComparer.Ordinal)
    {
        ["InitializeAsync"] = "_database.InitializeAsync(cancellationToken)",
        ["AddAsync"] = "await _outputReservations.AddAsync(task, cancellationToken).ConfigureAwait(false)",
        ["UpdateAsync"] =
            "await _commands.UpdateAsync(task, expectedVersion, cancellationToken).ConfigureAwait(false)",
        ["UpdateProgressAsync"] =
            "await _commands.UpdateProgressAsync(progressWrite, cancellationToken).ConfigureAwait(false)",
        ["FindAsync"] = "await _queries.FindAsync(taskId, cancellationToken).ConfigureAwait(false)",
        ["GetUnfinishedAsync"] =
            "await _queries.GetUnfinishedAsync(cancellationToken).ConfigureAwait(false)",
        ["IsOutputPathReservedAsync"] =
            "await _outputReservations.IsOutputPathReservedAsync(basePath, ignoreCase, cancellationToken)" +
            ".ConfigureAwait(false)",
        ["GetActiveOutputReservationKeysAsync"] =
            "await _outputReservations.GetActiveOutputReservationKeysAsync(ignoreCase, cancellationToken)" +
            ".ConfigureAwait(false)",
        ["GetHistoryPageAsync"] =
            "await _queries.GetHistoryPageAsync(cursor, pageSize, cancellationToken).ConfigureAwait(false)",
        ["DeleteAsync"] = "await _commands.DeleteAsync(taskId, cancellationToken).ConfigureAwait(false)",
        ["ClearHistoryAsync"] =
            "await _commands.ClearHistoryAsync(cancellationToken).ConfigureAwait(false)",
        ["GetQuarantinedRecordsAsync"] =
            "await _quarantine.GetRecordsAsync(cancellationToken).ConfigureAwait(false)",
        ["IsLegacyUpgradeAdmissionBlockedAsync"] =
            "await _quarantine.IsLegacyUpgradeAdmissionBlockedAsync(cancellationToken)" +
            ".ConfigureAwait(false)",
        ["ConfirmLegacyRemoteTasksStoppedAsync"] =
            "await _quarantine.ConfirmLegacyRemoteTasksStoppedAsync(cancellationToken)" +
            ".ConfigureAwait(false)",
        ["Dispose"] = "_database.Dispose()"
    };
    private static readonly string[] Collaborators =
    [
        "SqliteDownloadStoreQueries",
        "SqliteDownloadStoreCommands",
        "SqliteDownloadStoreOutputReservations",
        "SqliteDownloadStoreQuarantine"
    ];

    [Fact]
    public void FacadeIsNonPartialAndContainsNoSql()
    {
        var source = ReadDownloadSource(FacadeFile);

        Assert.Contains("public sealed class SqliteDownloadTaskStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("partial class SqliteDownloadTaskStore", source, StringComparison.Ordinal);
        Assert.DoesNotContain("CommandText", source, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", source, StringComparison.Ordinal);
        Assert.All(
            SqlMarkers,
            sql => Assert.DoesNotContain(sql, source, StringComparison.OrdinalIgnoreCase));
        Assert.False(File.Exists(Path.Combine(DownloadSourceRoot, "SqliteDownloadTaskStore.OutputReservations.cs")));
    }

    [Fact]
    public void FacadeDelegatesToExplicitCollaborators()
    {
        var source = ReadDownloadSource(FacadeFile);

        Assert.Contains($"new {Database}", source, StringComparison.Ordinal);
        Assert.All(
            Collaborators,
            collaborator => Assert.Contains($"new {collaborator}", source, StringComparison.Ordinal));
        Assert.Empty(FindFacadeDelegationViolations(source, ExpectedFacadeDelegations));
    }

    [Fact]
    public void CollaboratorsDependOnDatabaseWithoutPeerCoupling()
    {
        foreach (var collaborator in Collaborators)
        {
            var source = ReadDownloadSource($"{collaborator}.cs");
            Assert.Contains($"internal sealed class {collaborator}", source, StringComparison.Ordinal);
            Assert.Contains(Database, source, StringComparison.Ordinal);
            Assert.DoesNotContain($"partial class {collaborator}", source, StringComparison.Ordinal);

            var forbiddenPeers = Collaborators.Where(peer =>
                peer != collaborator &&
                !(collaborator == "SqliteDownloadStoreQueries" &&
                  peer == "SqliteDownloadStoreQuarantine"));
            Assert.All(
                forbiddenPeers,
                peer => Assert.DoesNotContain(peer, source, StringComparison.Ordinal));
        }

        var database = ReadDownloadSource($"{Database}.cs");
        Assert.All(
            Collaborators,
            collaborator => Assert.DoesNotContain(collaborator, database, StringComparison.Ordinal));

        foreach (var owner in Collaborators.Append(Database))
        {
            Assert.Empty(FindForbiddenOwnerDependencyViolations(
                owner,
                ReadDownloadSource($"{owner}.cs")));
        }
    }

    [Fact]
    public void FacadeDelegationRuleRejectsPolicyControlFlow()
    {
        const string source = """
            public sealed class SqliteDownloadTaskStore
            {
                public async Task AddAsync(object task, CancellationToken cancellationToken) =>
                    task is null
                        ? throw new ArgumentNullException(nameof(task))
                        : await _outputReservations.AddAsync(task, cancellationToken).ConfigureAwait(false);
            }
            """;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AddAsync"] = ExpectedFacadeDelegations["AddAsync"]
        };

        var violations = FindFacadeDelegationViolations(source, expected);

        Assert.Contains(violations, violation => violation.Contains("AddAsync", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("SqliteDownloadTaskStore", "public facade")]
    [InlineData("IServiceProvider", "service resolution")]
    [InlineData("GetRequiredService", "service resolution")]
    [InlineData("BindingFlags", "reflection")]
    [InlineData("DownloadStoreContext", "general context")]
    public void OwnerDependencyRuleRejectsForbiddenTypeTokens(string token, string expectedReason)
    {
        var source = $"internal sealed class Owner {{ private {token} _dependency; }}";

        var violations = FindForbiddenOwnerDependencyViolations("Owner", source);

        Assert.Contains(violations, violation =>
            violation.Contains(token, StringComparison.Ordinal) &&
            violation.Contains(expectedReason, StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerDependencyRuleRejectsReflectionLookupInvocationSequence()
    {
        const string source = """
            internal sealed class Owner
            {
                public object? Build(object target) =>
                    target.GetType().GetMethod("Build")!.Invoke(target, parameters: null);
            }
            """;

        var violations = FindForbiddenOwnerDependencyViolations("Owner", source);

        Assert.Contains(violations, violation =>
            violation.Contains("GetType().GetMethod(...).Invoke", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerDependencyRuleRejectsNullConditionalReflectionInvocationSequence()
    {
        const string source = """
            internal sealed class Owner
            {
                public object? Build(object target) =>
                    target.GetType().GetMethod("Build")?.Invoke(target, parameters: null);
            }
            """;

        var violations = FindForbiddenOwnerDependencyViolations("Owner", source);

        Assert.Contains(violations, violation =>
            violation.Contains("GetType().GetMethod(...).Invoke", StringComparison.Ordinal));
    }

    [Fact]
    public void OwnerDependencyRuleIgnoresReflectionWordsInCommentsAndLiterals()
    {
        const string source = """"
            internal sealed class Owner
            {
                // target.GetType().GetMethod("Build")!.Invoke(...)
                private const string Text = "GetType GetMethod Invoke";
                private const string RawText = """target.GetType().GetMethod("Build")!.Invoke(...)""";
            }
            """";

        Assert.Empty(FindForbiddenOwnerDependencyViolations("Owner", source));
    }

    [Fact]
    public void DatabaseOwnsConnectionsInitializationAndStoreTransactionBoundaries()
    {
        var database = ReadDownloadSource($"{Database}.cs");
        Assert.Contains("new SqliteConnection", database, StringComparison.Ordinal);
        Assert.Contains("SemaphoreSlim", database, StringComparison.Ordinal);
        Assert.Contains("DownloadStoreSchema.InitializeAsync", database, StringComparison.Ordinal);
        Assert.Contains("RemoveOrphanedDownloadingRecordsAsync", database, StringComparison.Ordinal);
        Assert.Contains("BeginTransaction", database, StringComparison.Ordinal);
        Assert.Contains("CommitAsync", database, StringComparison.Ordinal);
        Assert.Contains("RollbackAsync", database, StringComparison.Ordinal);

        foreach (var file in new[] { FacadeFile }.Concat(Collaborators.Select(name => $"{name}.cs")))
        {
            var source = ReadDownloadSource(file);
            Assert.DoesNotContain("new SqliteConnection", source, StringComparison.Ordinal);
            Assert.DoesNotContain("BeginTransaction", source, StringComparison.Ordinal);
            Assert.DoesNotContain("CommitAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("RollbackAsync", source, StringComparison.Ordinal);
            Assert.DoesNotContain("SemaphoreSlim", source, StringComparison.Ordinal);
            Assert.DoesNotContain("DownloadStoreSchema.InitializeAsync", source, StringComparison.Ordinal);
        }
    }

    private static string DownloadSourceRoot => Path.Combine(
        RepositoryRoot,
        "src",
        "DownKyi.Infrastructure",
        "Downloads");

    private static string ReadDownloadSource(string fileName) =>
        File.ReadAllText(Path.Combine(DownloadSourceRoot, fileName));

    private static List<string> FindFacadeDelegationViolations(
        string source,
        IReadOnlyDictionary<string, string> expectedDelegations)
    {
        var tokens = Tokenize(source);
        var violations = new List<string>();
        var found = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index] != "public")
            {
                continue;
            }

            var delimiter = FindHeaderDelimiter(tokens, index + 1);
            if (delimiter < 0 || tokens[delimiter] != "(")
            {
                continue;
            }

            var methodName = tokens[delimiter - 1];
            if (methodName == Facade)
            {
                continue;
            }

            if (!expectedDelegations.TryGetValue(methodName, out var expectedExpression))
            {
                violations.Add($"Facade has unexpected public method '{methodName}'.");
                continue;
            }

            found.Add(methodName);
            var closeParenthesis = FindMatchingParenthesis(tokens, delimiter);
            if (closeParenthesis < 0 || closeParenthesis + 1 >= tokens.Count ||
                tokens[closeParenthesis + 1] != "=>")
            {
                violations.Add($"Facade method '{methodName}' must be expression-bodied delegation.");
                continue;
            }

            var semicolon = tokens.IndexOf(";", closeParenthesis + 2);
            if (semicolon < 0)
            {
                violations.Add($"Facade method '{methodName}' has no terminating semicolon.");
                continue;
            }

            var actual = tokens.GetRange(closeParenthesis + 2, semicolon - closeParenthesis - 2);
            var expected = Tokenize(expectedExpression);
            if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
            {
                violations.Add(
                    $"Facade method '{methodName}' must only delegate to its assigned owner. " +
                    $"Expected '{string.Join(' ', expected)}'; actual '{string.Join(' ', actual)}'.");
            }
        }

        foreach (var missing in expectedDelegations.Keys.Except(found, StringComparer.Ordinal))
        {
            violations.Add($"Facade method '{missing}' is missing.");
        }

        return violations;
    }

    private static List<string> FindForbiddenOwnerDependencyViolations(
        string owner,
        string source)
    {
        var tokens = Tokenize(source);
        var identifiers = tokens
            .Where(token => token.Length > 0 && (char.IsLetter(token[0]) || token[0] == '_'))
            .ToHashSet(StringComparer.Ordinal);
        var violations = new List<string>();
        foreach (var token in ForbiddenOwnerTokens.Where(identifiers.Contains))
        {
            var reason = token switch
            {
                Facade => "public facade",
                "IServiceProvider" or "IServiceScope" or "IServiceScopeFactory" or "ServiceProvider" or
                    "GetService" or "GetRequiredService" or "CreateScope" or "ActivatorUtilities" =>
                    "service resolution",
                _ => "reflection"
            };
            violations.Add($"{owner} references forbidden {reason} token '{token}'.");
        }

        foreach (var context in identifiers.Where(identifier => identifier.EndsWith("Context", StringComparison.Ordinal)))
        {
            violations.Add($"{owner} references forbidden general context token '{context}'.");
        }

        AddReflectionSequenceViolations(owner, tokens, violations);

        return violations;
    }

    private static void AddReflectionSequenceViolations(
        string owner,
        List<string> tokens,
        List<string> violations)
    {
        for (var index = 0; index + 5 < tokens.Count; index++)
        {
            if (tokens[index] != "GetType" || tokens[index + 1] != "(" || tokens[index + 2] != ")" ||
                tokens[index + 3] != "." || !ReflectionLookups.Contains(tokens[index + 4]) ||
                tokens[index + 5] != "(")
            {
                continue;
            }

            var lookupClose = FindMatchingParenthesis(tokens, index + 5);
            if (lookupClose < 0)
            {
                continue;
            }

            var cursor = lookupClose + 1;
            while (cursor < tokens.Count && tokens[cursor] == "!")
            {
                cursor++;
            }

            if (cursor + 1 < tokens.Count && tokens[cursor] == "?" && tokens[cursor + 1] == ".")
            {
                cursor++;
            }

            if (cursor + 2 < tokens.Count && tokens[cursor] == "." &&
                ReflectionInvocations.Contains(tokens[cursor + 1]) && tokens[cursor + 2] == "(")
            {
                violations.Add(
                    $"{owner} uses forbidden reflection call sequence " +
                    $"'GetType().{tokens[index + 4]}(...).{tokens[cursor + 1]}(...)'.");
            }
        }
    }

    private static int FindHeaderDelimiter(List<string> tokens, int start)
    {
        for (var index = start; index < tokens.Count; index++)
        {
            if (tokens[index] is "(" or "{" or ";" or "=>")
            {
                return index;
            }
        }

        return -1;
    }

    private static int FindMatchingParenthesis(List<string> tokens, int openParenthesis)
    {
        var depth = 0;
        for (var index = openParenthesis; index < tokens.Count; index++)
        {
            if (tokens[index] == "(")
            {
                depth++;
            }
            else if (tokens[index] == ")" && --depth == 0)
            {
                return index;
            }
        }

        return -1;
    }

    private static List<string> Tokenize(string source)
    {
        var tokens = new List<string>();
        for (var index = 0; index < source.Length;)
        {
            if (char.IsWhiteSpace(source[index]))
            {
                index++;
                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                index = source.IndexOf('\n', index + 2);
                if (index < 0)
                {
                    break;
                }

                continue;
            }

            if (source[index] == '/' && index + 1 < source.Length && source[index + 1] == '*')
            {
                var endComment = source.IndexOf("*/", index + 2, StringComparison.Ordinal);
                index = endComment < 0 ? source.Length : endComment + 2;
                continue;
            }

            if (source[index] is '"' or '\'')
            {
                index = SkipLiteral(source, index, source[index]);
                continue;
            }

            if (char.IsLetter(source[index]) || source[index] == '_')
            {
                var start = index++;
                while (index < source.Length &&
                       (char.IsLetterOrDigit(source[index]) || source[index] == '_'))
                {
                    index++;
                }

                tokens.Add(source[start..index]);
                continue;
            }

            if (source[index] == '=' && index + 1 < source.Length && source[index + 1] == '>')
            {
                tokens.Add("=>");
                index += 2;
                continue;
            }

            tokens.Add(source[index].ToString());
            index++;
        }

        return tokens;
    }

    private static int SkipLiteral(string source, int start, char delimiter)
    {
        var quoteCount = delimiter == '"'
            ? source.AsSpan(start).IndexOfAnyExcept('"')
            : 1;
        if (quoteCount >= 3)
        {
            var rawDelimiter = new string('"', quoteCount);
            var rawEnd = source.IndexOf(rawDelimiter, start + quoteCount, StringComparison.Ordinal);
            return rawEnd < 0 ? source.Length : rawEnd + quoteCount;
        }

        for (var index = start + 1; index < source.Length; index++)
        {
            if (source[index] == '\\')
            {
                index++;
            }
            else if (source[index] == delimiter)
            {
                return index + 1;
            }
        }

        return source.Length;
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DownKyi.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root.");
    }
}
