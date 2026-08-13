using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace DownKyi.Architecture.Tests;

internal static class GitHubWorkflowReachability
{
    internal static WorkflowReachabilityResult SimulateTagPush(string workflow)
    {
        return Simulate(
            workflow,
            new WorkflowContext(
                "push",
                "refs/tags/v-test",
                new Dictionary<string, bool>(StringComparer.Ordinal)));
    }

    internal static bool HasSinglePullRequestPathsFilter(
        string workflow,
        string jobId)
    {
        Dictionary<string, WorkflowJob> jobs;
        try
        {
            jobs = ParseJobs(workflow);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        if (!jobs.TryGetValue(jobId, out var job) ||
            !string.IsNullOrWhiteSpace(job.Condition) ||
            job.Steps.Count != 1)
        {
            return false;
        }

        var step = job.Steps[0];
        return string.Equals(
                   NormalizeCondition(step.Condition),
                   "github.event_name == 'pull_request'",
                   StringComparison.Ordinal) &&
               string.Equals(step.Id, "filter", StringComparison.Ordinal) &&
               step.Uses?.StartsWith("dorny/paths-filter@", StringComparison.Ordinal) == true &&
               !step.ContinueOnError;
    }

    internal static string WithJobCondition(
        string workflow,
        string jobId,
        string condition)
    {
        var yaml = LoadYaml(workflow);
        var root = RequireMapping(yaml.Documents[0].RootNode, "workflow root");
        var jobs = RequireMapping(ReadRequired(root, "jobs"), "jobs");
        var job = RequireMapping(ReadRequired(jobs, jobId), $"job '{jobId}'");
        SetScalar(job, "if", condition);

        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        yaml.Save(writer, assignAnchors: false);
        return writer.ToString();
    }

    private static WorkflowReachabilityResult Simulate(
        string workflow,
        WorkflowContext context)
    {
        var jobs = ParseJobs(workflow);
        var states = new Dictionary<string, WorkflowJobState>(StringComparer.Ordinal);
        var remaining = new HashSet<string>(jobs.Keys, StringComparer.Ordinal);

        while (remaining.Count > 0)
        {
            var progressed = false;
            foreach (var jobId in remaining.ToArray())
            {
                var job = jobs[jobId];
                var missingDependency = job.Needs.FirstOrDefault(dependency => !jobs.ContainsKey(dependency));
                if (missingDependency is not null)
                {
                    states[jobId] = WorkflowJobState.Skipped(
                        $"missing dependency '{missingDependency}'");
                    remaining.Remove(jobId);
                    progressed = true;
                    continue;
                }

                if (job.Needs.Any(dependency => !states.ContainsKey(dependency)))
                {
                    continue;
                }

                states[jobId] = EvaluateJob(job, context, states);
                remaining.Remove(jobId);
                progressed = true;
            }

            if (progressed)
            {
                continue;
            }

            foreach (var jobId in remaining)
            {
                states[jobId] = WorkflowJobState.Skipped(
                    "dependency graph contains a cycle or unresolved dependency");
            }

            break;
        }

        return new WorkflowReachabilityResult(states);
    }

    private static WorkflowJobState EvaluateJob(
        WorkflowJob job,
        WorkflowContext context,
        Dictionary<string, WorkflowJobState> states)
    {
        var dependenciesSucceeded = job.Needs.All(dependency => states[dependency].Succeeded);
        if (string.IsNullOrWhiteSpace(job.Condition))
        {
            return dependenciesSucceeded
                ? WorkflowJobState.Success()
                : WorkflowJobState.Skipped("a required dependency did not succeed");
        }

        try
        {
            var evaluation = ConditionEvaluator.Evaluate(job.Condition, context, states);
            if (!dependenciesSucceeded && !evaluation.UsesAlways)
            {
                return WorkflowJobState.Skipped(
                    "GitHub's implicit success() check rejected a non-success dependency");
            }

            return evaluation.Value
                ? WorkflowJobState.Success()
                : WorkflowJobState.Skipped($"job condition evaluated false: {job.Condition}");
        }
        catch (InvalidDataException exception)
        {
            return WorkflowJobState.Skipped($"job condition could not be evaluated: {exception.Message}");
        }
    }

    private static Dictionary<string, WorkflowJob> ParseJobs(string workflow)
    {
        var yaml = LoadYaml(workflow);
        var root = RequireMapping(yaml.Documents[0].RootNode, "workflow root");
        var jobsNode = RequireMapping(ReadRequired(root, "jobs"), "jobs");
        var result = new Dictionary<string, WorkflowJob>(StringComparer.Ordinal);

        foreach (var pair in jobsNode.Children)
        {
            var jobId = RequireScalar(pair.Key, "job id");
            var jobNode = RequireMapping(pair.Value, $"job '{jobId}'");
            result.Add(
                jobId,
                new WorkflowJob(
                    jobId,
                    ReadNeeds(jobNode),
                    ReadScalar(jobNode, "if"),
                    ReadSteps(jobNode)));
        }

        return result;
    }

    private static string[] ReadNeeds(YamlMappingNode job)
    {
        var node = ReadOptional(job, "needs");
        return node switch
        {
            null => [],
            YamlScalarNode scalar when !string.IsNullOrWhiteSpace(scalar.Value) => [scalar.Value],
            YamlSequenceNode sequence => sequence.Children
                .Select(child => RequireScalar(child, "needs item"))
                .ToArray(),
            _ => throw new InvalidDataException("A workflow job has an invalid needs declaration.")
        };
    }

    private static WorkflowStep[] ReadSteps(YamlMappingNode job)
    {
        if (ReadOptional(job, "steps") is not YamlSequenceNode steps)
        {
            return [];
        }

        return steps.Children
            .Select((node, index) =>
            {
                var step = RequireMapping(node, $"step {index}");
                return new WorkflowStep(
                    ReadScalar(step, "id"),
                    ReadScalar(step, "if"),
                    ReadScalar(step, "uses"),
                    ReadBoolean(step, "continue-on-error"));
            })
            .ToArray();
    }

    private static YamlStream LoadYaml(string workflow)
    {
        var yaml = new YamlStream();
        try
        {
            yaml.Load(new StringReader(workflow));
        }
        catch (YamlDotNet.Core.YamlException exception)
        {
            throw new InvalidDataException("The workflow YAML is invalid.", exception);
        }

        if (yaml.Documents.Count != 1)
        {
            throw new InvalidDataException("A workflow must contain exactly one YAML document.");
        }

        return yaml;
    }

    private static YamlMappingNode RequireMapping(YamlNode node, string description)
    {
        return node as YamlMappingNode ??
               throw new InvalidDataException($"Expected {description} to be a mapping.");
    }

    private static string RequireScalar(YamlNode node, string description)
    {
        return node is YamlScalarNode { Value: { } value }
            ? value
            : throw new InvalidDataException($"Expected {description} to be a scalar.");
    }

    private static YamlNode ReadRequired(YamlMappingNode mapping, string key)
    {
        return ReadOptional(mapping, key) ??
               throw new InvalidDataException($"Required YAML key '{key}' is missing.");
    }

    private static YamlNode? ReadOptional(YamlMappingNode mapping, string key)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                return pair.Value;
            }
        }

        return null;
    }

    private static string? ReadScalar(YamlMappingNode mapping, string key)
    {
        return ReadOptional(mapping, key) is YamlScalarNode scalar
            ? scalar.Value
            : null;
    }

    private static bool ReadBoolean(YamlMappingNode mapping, string key)
    {
        return bool.TryParse(ReadScalar(mapping, key), out var value) && value;
    }

    private static void SetScalar(YamlMappingNode mapping, string key, string value)
    {
        foreach (var pair in mapping.Children)
        {
            if (pair.Key is YamlScalarNode scalar &&
                string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                mapping.Children[pair.Key] = new YamlScalarNode(value);
                return;
            }
        }

        mapping.Add(key, value);
    }

    private static string NormalizeCondition(string? condition)
    {
        var value = condition?.Trim() ?? string.Empty;
        if (value.StartsWith("${{", StringComparison.Ordinal) &&
            value.EndsWith("}}", StringComparison.Ordinal))
        {
            return value[3..^2].Trim();
        }

        return value;
    }

    private sealed record WorkflowJob(
        string Id,
        IReadOnlyList<string> Needs,
        string? Condition,
        IReadOnlyList<WorkflowStep> Steps);

    private sealed record WorkflowStep(
        string? Id,
        string? Condition,
        string? Uses,
        bool ContinueOnError);

    private sealed record WorkflowContext(
        string EventName,
        string Ref,
        IReadOnlyDictionary<string, bool> Inputs);

    private static class ConditionEvaluator
    {
        internal static ConditionEvaluation Evaluate(
            string condition,
            WorkflowContext context,
            IReadOnlyDictionary<string, WorkflowJobState> states)
        {
            var parser = new ExpressionParser(NormalizeCondition(condition), context, states);
            var value = parser.Parse();
            return new ConditionEvaluation(ToBoolean(value), parser.UsesAlways);
        }

        private static bool ToBoolean(object? value)
        {
            return value switch
            {
                null => false,
                bool boolean => boolean,
                string text => !string.IsNullOrEmpty(text),
                _ => throw new InvalidDataException(
                    $"Unsupported workflow condition value '{value.GetType().Name}'.")
            };
        }

        private sealed class ExpressionParser
        {
            private readonly WorkflowContext _context;
            private readonly IReadOnlyDictionary<string, WorkflowJobState> _states;
            private readonly List<Token> _tokens;
            private int _position;

            internal ExpressionParser(
                string expression,
                WorkflowContext context,
                IReadOnlyDictionary<string, WorkflowJobState> states)
            {
                _context = context;
                _states = states;
                _tokens = Tokenize(expression);
            }

            internal bool UsesAlways { get; private set; }

            internal object? Parse()
            {
                var value = ParseOr();
                Require(TokenKind.End);
                return value;
            }

            private object? ParseOr()
            {
                var value = ParseAnd();
                while (Match(TokenKind.Or))
                {
                    value = ToBoolean(value) | ToBoolean(ParseAnd());
                }

                return value;
            }

            private object? ParseAnd()
            {
                var value = ParseEquality();
                while (Match(TokenKind.And))
                {
                    value = ToBoolean(value) & ToBoolean(ParseEquality());
                }

                return value;
            }

            private object? ParseEquality()
            {
                var value = ParseUnary();
                while (Current.Kind is TokenKind.Equal or TokenKind.NotEqual)
                {
                    var operation = Advance().Kind;
                    var right = ParseUnary();
                    var equals = ValuesEqual(value, right);
                    value = operation == TokenKind.Equal ? equals : !equals;
                }

                return value;
            }

            private object? ParseUnary()
            {
                if (Match(TokenKind.Not))
                {
                    return !ToBoolean(ParseUnary());
                }

                return ParsePrimary();
            }

            private object? ParsePrimary()
            {
                if (Match(TokenKind.LeftParenthesis))
                {
                    var value = ParseOr();
                    Require(TokenKind.RightParenthesis);
                    return value;
                }

                if (Current.Kind == TokenKind.String)
                {
                    return Advance().Value;
                }

                if (Current.Kind != TokenKind.Identifier)
                {
                    throw new InvalidDataException(
                        $"Unexpected token '{Current.Value}' in workflow condition.");
                }

                var identifier = Advance().Value;
                if (Match(TokenKind.LeftParenthesis))
                {
                    return EvaluateFunction(identifier);
                }

                return ResolveIdentifier(identifier);
            }

            private bool EvaluateFunction(string name)
            {
                if (string.Equals(name, "always", StringComparison.Ordinal))
                {
                    Require(TokenKind.RightParenthesis);
                    UsesAlways = true;
                    return true;
                }

                if (string.Equals(name, "startsWith", StringComparison.Ordinal))
                {
                    var source = Convert.ToString(ParseOr(), CultureInfo.InvariantCulture) ?? string.Empty;
                    Require(TokenKind.Comma);
                    var prefix = Convert.ToString(ParseOr(), CultureInfo.InvariantCulture) ?? string.Empty;
                    Require(TokenKind.RightParenthesis);
                    return source.StartsWith(prefix, StringComparison.Ordinal);
                }

                throw new InvalidDataException($"Unsupported workflow function '{name}'.");
            }

            private object? ResolveIdentifier(string identifier)
            {
                if (string.Equals(identifier, "true", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                if (string.Equals(identifier, "false", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (string.Equals(identifier, "null", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                if (string.Equals(identifier, "github.event_name", StringComparison.Ordinal))
                {
                    return _context.EventName;
                }

                if (string.Equals(identifier, "github.ref", StringComparison.Ordinal))
                {
                    return _context.Ref;
                }

                if (identifier.StartsWith("inputs.", StringComparison.Ordinal))
                {
                    var input = identifier["inputs.".Length..];
                    return _context.Inputs.TryGetValue(input, out var value) && value;
                }

                if (identifier.StartsWith("needs.", StringComparison.Ordinal))
                {
                    return ResolveNeeds(identifier);
                }

                throw new InvalidDataException($"Unsupported workflow identifier '{identifier}'.");
            }

            private string ResolveNeeds(string identifier)
            {
                var remainder = identifier["needs.".Length..];
                const string resultSuffix = ".result";
                if (remainder.EndsWith(resultSuffix, StringComparison.Ordinal))
                {
                    var jobId = remainder[..^resultSuffix.Length];
                    return _states.TryGetValue(jobId, out var state) && state.Succeeded
                        ? "success"
                        : "skipped";
                }

                const string outputMarker = ".outputs.";
                var outputIndex = remainder.IndexOf(outputMarker, StringComparison.Ordinal);
                if (outputIndex > 0)
                {
                    var jobId = remainder[..outputIndex];
                    if (!_states.ContainsKey(jobId))
                    {
                        throw new InvalidDataException(
                            $"Workflow condition references unknown needs job '{jobId}'.");
                    }

                    return string.Empty;
                }

                throw new InvalidDataException($"Unsupported needs reference '{identifier}'.");
            }

            private static bool ValuesEqual(object? left, object? right)
            {
                if (left is bool leftBoolean && right is bool rightBoolean)
                {
                    return leftBoolean == rightBoolean;
                }

                return string.Equals(
                    Convert.ToString(left, CultureInfo.InvariantCulture) ?? string.Empty,
                    Convert.ToString(right, CultureInfo.InvariantCulture) ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);
            }

            private Token Current => _tokens[_position];

            private Token Advance()
            {
                return _tokens[_position++];
            }

            private bool Match(TokenKind kind)
            {
                if (Current.Kind != kind)
                {
                    return false;
                }

                _position++;
                return true;
            }

            private void Require(TokenKind kind)
            {
                if (!Match(kind))
                {
                    throw new InvalidDataException(
                        $"Expected {kind} in workflow condition, got '{Current.Value}'.");
                }
            }

            private static List<Token> Tokenize(string expression)
            {
                var result = new List<Token>();
                for (var index = 0; index < expression.Length;)
                {
                    if (char.IsWhiteSpace(expression[index]))
                    {
                        index++;
                        continue;
                    }

                    if (TryAddOperator(expression, ref index, result))
                    {
                        continue;
                    }

                    if (expression[index] == '\'')
                    {
                        result.Add(ReadString(expression, ref index));
                        continue;
                    }

                    if (IsIdentifierCharacter(expression[index]))
                    {
                        var start = index;
                        while (index < expression.Length && IsIdentifierCharacter(expression[index]))
                        {
                            index++;
                        }

                        result.Add(new Token(TokenKind.Identifier, expression[start..index]));
                        continue;
                    }

                    throw new InvalidDataException(
                        $"Unsupported character '{expression[index]}' in workflow condition.");
                }

                result.Add(new Token(TokenKind.End, string.Empty));
                return result;
            }

            private static bool TryAddOperator(
                string expression,
                ref int index,
                ICollection<Token> tokens)
            {
                foreach (var candidate in Operators)
                {
                    if (!expression.AsSpan(index).StartsWith(candidate.Text, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    tokens.Add(new Token(candidate.Kind, candidate.Text));
                    index += candidate.Text.Length;
                    return true;
                }

                return false;
            }

            private static Token ReadString(string expression, ref int index)
            {
                index++;
                var start = index;
                while (index < expression.Length && expression[index] != '\'')
                {
                    index++;
                }

                if (index >= expression.Length)
                {
                    throw new InvalidDataException("Workflow condition contains an unterminated string.");
                }

                var value = expression[start..index];
                index++;
                return new Token(TokenKind.String, value);
            }

            private static bool IsIdentifierCharacter(char value)
            {
                return char.IsLetterOrDigit(value) || value is '_' or '.' or '-';
            }

            private static readonly (string Text, TokenKind Kind)[] Operators =
            [
                ("&&", TokenKind.And),
                ("||", TokenKind.Or),
                ("!=", TokenKind.NotEqual),
                ("==", TokenKind.Equal),
                ("!", TokenKind.Not),
                ("(", TokenKind.LeftParenthesis),
                (")", TokenKind.RightParenthesis),
                (",", TokenKind.Comma)
            ];
        }

        private sealed record Token(TokenKind Kind, string Value);

        private enum TokenKind
        {
            Identifier,
            String,
            And,
            Or,
            Not,
            Equal,
            NotEqual,
            LeftParenthesis,
            RightParenthesis,
            Comma,
            End
        }
    }

    private sealed record ConditionEvaluation(bool Value, bool UsesAlways);
}

internal sealed class WorkflowReachabilityResult
{
    private readonly IReadOnlyDictionary<string, WorkflowJobState> _states;

    internal WorkflowReachabilityResult(IReadOnlyDictionary<string, WorkflowJobState> states)
    {
        _states = states;
    }

    internal bool AllSucceeded(IEnumerable<string> jobIds)
    {
        return jobIds.All(jobId =>
            _states.TryGetValue(jobId, out var state) && state.Succeeded);
    }

    internal string Describe(IEnumerable<string> jobIds)
    {
        return string.Join(
            "; ",
            jobIds.Select(jobId =>
                _states.TryGetValue(jobId, out var state)
                    ? $"{jobId}={(state.Succeeded ? "success" : $"skipped ({state.Reason})")}"
                    : $"{jobId}=missing"));
    }

    internal string? FirstUnsuccessful(IEnumerable<string> jobIds)
    {
        return jobIds.FirstOrDefault(jobId =>
            !_states.TryGetValue(jobId, out var state) || !state.Succeeded);
    }
}

internal sealed record WorkflowJobState(bool Succeeded, string? Reason)
{
    internal static WorkflowJobState Success() => new(true, null);

    internal static WorkflowJobState Skipped(string reason) => new(false, reason);
}
