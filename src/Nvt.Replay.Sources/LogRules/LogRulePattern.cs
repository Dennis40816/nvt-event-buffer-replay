// Copyright (c) 2026 Dennis Liu. All rights reserved.
using System.Globalization;
using System.Text.RegularExpressions;

namespace Nvt.Replay.Sources.LogRules;

/// <summary>Holds the version 1 limits of docs/log-rule-files.md. Counts and lengths bound the cost of building patterns, so validity never depends on elapsed time.</summary>
internal static class LogRuleLimits
{
    public const int MaxFileBytes = 1024 * 1024;
    public const int MaxRules = 500;
    public const int MaxEventTypes = 100;
    public const int MaxTests = 1000;
    public const int MaxPatternLength = 1024;
    public const int MaxCaptureGroups = 20;
    public const int MaxRepeatCount = 1000;
    public const int MaxGroupDepth = 8;
    public const int MaxAlternations = 64;
    public const int MaxLineLength = 8192;
    public const int MaxTextLength = 1024;
}

/// <summary>Builds every pattern of a rule file with the non-backtracking engine and checks the version 1 limits first.</summary>
internal static class LogRulePattern
{
    private const RegexOptions EngineOptions = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;

    /// <summary>Checks the pattern limits, then lets the engine accept or reject the pattern. Throws <see cref="LogRuleValidationException"/>.</summary>
    public static Regex Compile(string pattern, string field)
    {
        if (string.IsNullOrEmpty(pattern))
            throw new LogRuleValidationException($"{field} cannot be empty.");
        if (pattern.Length > LogRuleLimits.MaxPatternLength)
        {
            throw new LogRuleValidationException(
                $"{field} is longer than {LogRuleLimits.MaxPatternLength} characters.");
        }

        CheckLimits(pattern, field);
        Regex regex;
        try
        {
            regex = new Regex(pattern, EngineOptions);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new LogRuleValidationException(
                $"{field} is not accepted by the non-backtracking engine: {exception.Message}",
                exception);
        }

        if (regex.GetGroupNumbers().Length - 1 > LogRuleLimits.MaxCaptureGroups)
        {
            throw new LogRuleValidationException(
                $"{field} has more than {LogRuleLimits.MaxCaptureGroups} capture groups.");
        }
        return regex;
    }

    private static void CheckLimits(string pattern, string field)
    {
        var depth = 0;
        var alternations = 0;
        for (var index = 0; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '\\':
                    index = SkipEscape(pattern, index);
                    break;
                case '[':
                    index = SkipCharacterClass(pattern, index, field);
                    break;
                case '(':
                    index = OpenGroup(pattern, index, field, ref depth);
                    break;
                case ')':
                    depth--;
                    if (depth < 0)
                        throw new LogRuleValidationException($"{field} closes a group that is not open.");
                    break;
                case '|':
                    alternations++;
                    if (alternations > LogRuleLimits.MaxAlternations)
                    {
                        throw new LogRuleValidationException(
                            $"{field} has more than {LogRuleLimits.MaxAlternations} alternations.");
                    }
                    break;
                case '{':
                    CheckRepeatCount(pattern, index, field);
                    break;
            }
        }
    }

    // Returns the index of the last character of the escape that starts at index. The engine reads \cX as
    // one control character, three characters in all, and X may be a bracket or a backslash. The other
    // escapes the engine accepts have no structural character after their second one.
    private static int SkipEscape(string pattern, int index) =>
        index + 1 < pattern.Length && pattern[index + 1] == 'c' ? index + 2 : index + 1;

    // A class ends at its first unescaped closing bracket, except that a bracket right after the opening
    // one (or after its ^) is a literal. An unescaped opening bracket inside a class is rejected: the
    // engine reads it as the start of a subtraction such as [a-z-[aeiou]] or of a [:name:] form, each with
    // its own rule for where the class ends. Without that syntax this scan ends where the engine does.
    private static int SkipCharacterClass(string pattern, int start, string field)
    {
        var index = start + 1;
        if (index < pattern.Length && pattern[index] == '^') index++;
        if (index < pattern.Length && pattern[index] == ']') index++;
        for (; index < pattern.Length; index++)
        {
            switch (pattern[index])
            {
                case '\\':
                    index = SkipEscape(pattern, index);
                    break;
                case '[':
                    throw new LogRuleValidationException(
                        $"{field} has an opening bracket inside a character class. Write \\[ for a literal bracket.");
                case ']':
                    return index;
            }
        }
        throw new LogRuleValidationException($"{field} has a character class without a closing bracket.");
    }

    // Returns the index of the last character that belongs to the group opener.
    private static int OpenGroup(string pattern, int index, string field, ref int depth)
    {
        if (index + 1 < pattern.Length && pattern[index + 1] == '?')
        {
            if (index + 2 < pattern.Length && pattern[index + 2] == '#')
                throw new LogRuleValidationException($"{field} cannot use inline comments.");
            var optionsEnd = index + 2;
            while (optionsEnd < pattern.Length && pattern[optionsEnd] is 'i' or 'm' or 'n' or 's' or 'I' or 'M' or 'N' or 'S' or 'x' or 'X' or '+' or '-')
            {
                if (pattern[optionsEnd] is 'x' or 'X')
                {
                    throw new LogRuleValidationException(
                        $"{field} cannot use the x option, because comments hide pattern text from the limit check.");
                }
                optionsEnd++;
            }
            if (optionsEnd > index + 2 && optionsEnd < pattern.Length && pattern[optionsEnd] == ')')
                return optionsEnd;
        }

        depth++;
        if (depth > LogRuleLimits.MaxGroupDepth)
            throw new LogRuleValidationException($"{field} nests groups deeper than {LogRuleLimits.MaxGroupDepth}.");
        return index;
    }

    private static void CheckRepeatCount(string pattern, int start, string field)
    {
        var index = start + 1;
        var first = ReadNumber(pattern, ref index);
        if (first < 0) return;
        var last = first;
        if (index < pattern.Length && pattern[index] == ',')
        {
            index++;
            last = Math.Max(first, ReadNumber(pattern, ref index));
        }
        if (index >= pattern.Length || pattern[index] != '}') return;
        if (last > LogRuleLimits.MaxRepeatCount)
        {
            throw new LogRuleValidationException(
                $"{field} repeats more than {LogRuleLimits.MaxRepeatCount} times in a counted quantifier.");
        }
    }

    // Returns -1 when no digit follows. Long digit runs saturate, so they exceed the repeat limit.
    private static int ReadNumber(string pattern, ref int index)
    {
        var value = -1;
        while (index < pattern.Length && pattern[index] is >= '0' and <= '9')
        {
            var digit = pattern[index] - '0';
            value = value < 0 ? digit : Math.Min(value * 10 + digit, int.MaxValue / 20);
            index++;
        }
        return value;
    }

    /// <summary>Gets the names of the named groups of a pattern, in the order the engine reports them.</summary>
    public static IReadOnlySet<string> NamedGroups(Regex regex) =>
        regex.GetGroupNames()
            .Where(name => !int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            .ToHashSet(StringComparer.Ordinal);
}
