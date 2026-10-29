// Copyright (c) 2026 Dennis Liu. All rights reserved.
namespace Nvt.Replay.Sources.LogRules;

/// <summary>
/// Reads the placeholders of a rule <c>message</c>. A template has literal text and <c>{name}</c> placeholders.
/// It has no operators, functions or conditions. <c>{{</c> and <c>}}</c> write a literal brace.
/// </summary>
internal static class LogRuleMessageTemplate
{
    /// <summary>Returns the placeholder names in order of appearance. Throws <see cref="LogRuleValidationException"/> for a brace that is not part of a placeholder or an escape.</summary>
    public static IReadOnlyList<string> PlaceholderNames(string template, string field)
    {
        var names = new List<string>();
        for (var index = 0; index < template.Length; index++)
        {
            var current = template[index];
            if (current == '{' && index + 1 < template.Length && template[index + 1] == '{')
            {
                index++;
            }
            else if (current == '}' && index + 1 < template.Length && template[index + 1] == '}')
            {
                index++;
            }
            else if (current == '{')
            {
                var close = template.IndexOf('}', index + 1);
                if (close < 0)
                    throw new LogRuleValidationException($"{field} has a '{{' without a closing '}}'.");
                names.Add(template[(index + 1)..close]);
                index = close;
            }
            else if (current == '}')
            {
                throw new LogRuleValidationException($"{field} has a '}}' without an opening '{{'.");
            }
        }
        return names;
    }
}
