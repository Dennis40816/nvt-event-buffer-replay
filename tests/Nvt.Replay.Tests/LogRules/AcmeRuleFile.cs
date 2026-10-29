// Copyright (c) 2026 Dennis Liu. All rights reserved.
using Nvt.Replay.Core;
using Nvt.Replay.Sources.LogRules;

namespace Nvt.Replay.Tests.LogRules;

/// <summary>Synthetic rule file of docs/log-rule-files.md. The Acme Motors format is invented.</summary>
internal static class AcmeRuleFile
{
    /// <summary>Hash of <see cref="CompactHashInput"/>, computed with Python hashlib and not with the code under test.</summary>
    public const string ContentSha256 = "b9a57a7b33c2f3e77b84bf8c13bcfaddf2b287f8a4fb88075d31d793b7475567";

    /// <summary>Compact canonical bytes without contentSha256, produced by Python json.dumps with compact separators, not by the code under test.</summary>
    public const string CompactHashInput = """{"schemaVersion":1,"fileKind":"logRules","profileId":"acme.example.v1","displayName":"Acme Motors example (synthetic)","scope":{"carmaker":"Acme Motors (synthetic)","note":"invented format","platform":"example"},"lineEncoding":"utf-8","timestamp":{"regex":"^\\[\\s*(?<ts>\\d+\\.\\d+)\\]","format":"secondsSinceBoot"},"eventTypes":[{"id":"touch.esd","displayName":"ESD detected","category":"deviceState","severity":"Warning"},{"id":"touch.frameLost","displayName":"Frame lost","category":"behaviorFinding","severity":"Warning"},{"id":"touch.reset","displayName":"Touch reset","category":"deviceState","severity":"Warning"}],"rules":[{"id":"esd-detected","eventType":"touch.esd","contains":["esd detected","tp_drv:"],"regex":"esd detected cnt=(?<cnt>\\d+)","captures":[{"name":"cnt","type":"integer"}],"message":"ESD detected, count {cnt}"},{"id":"frame-lost","eventType":"touch.frameLost","contains":["frame lost","tp_drv:"],"regex":"frame lost seq=(?<seq>\\d+)","captures":[{"name":"seq","type":"integer"}],"message":"Frame lost, sequence {seq}"},{"id":"reset-reason","eventType":"touch.reset","contains":["reset reason=","tp_drv:"],"regex":"reset reason=(?<code>0x[0-9a-f]+)","captures":[{"name":"code","type":"enum","map":{"0x01":"power-on","0x04":"watchdog"}}],"message":"Reset, reason {code}"}],"tests":[{"name":"esd-basic","line":"[   12.345678] tp_drv: esd detected cnt=3, recovering","expect":[{"rule":"esd-detected","fields":{"cnt":"3"}}]},{"name":"unrelated-line","line":"[   15.000900] input: unrelated line","expect":[]}]}""";

    /// <summary>Saved canonical text, produced by Python json.dumps with indent 2, not by the code under test.</summary>
    public static string CanonicalText { get; } = Golden.ReplaceLineEndings("\n") + "\n";

    private const string Golden = """
{
  "schemaVersion": 1,
  "fileKind": "logRules",
  "profileId": "acme.example.v1",
  "displayName": "Acme Motors example (synthetic)",
  "scope": {
    "carmaker": "Acme Motors (synthetic)",
    "note": "invented format",
    "platform": "example"
  },
  "lineEncoding": "utf-8",
  "timestamp": {
    "regex": "^\\[\\s*(?<ts>\\d+\\.\\d+)\\]",
    "format": "secondsSinceBoot"
  },
  "eventTypes": [
    {
      "id": "touch.esd",
      "displayName": "ESD detected",
      "category": "deviceState",
      "severity": "Warning"
    },
    {
      "id": "touch.frameLost",
      "displayName": "Frame lost",
      "category": "behaviorFinding",
      "severity": "Warning"
    },
    {
      "id": "touch.reset",
      "displayName": "Touch reset",
      "category": "deviceState",
      "severity": "Warning"
    }
  ],
  "rules": [
    {
      "id": "esd-detected",
      "eventType": "touch.esd",
      "contains": [
        "esd detected",
        "tp_drv:"
      ],
      "regex": "esd detected cnt=(?<cnt>\\d+)",
      "captures": [
        {
          "name": "cnt",
          "type": "integer"
        }
      ],
      "message": "ESD detected, count {cnt}"
    },
    {
      "id": "frame-lost",
      "eventType": "touch.frameLost",
      "contains": [
        "frame lost",
        "tp_drv:"
      ],
      "regex": "frame lost seq=(?<seq>\\d+)",
      "captures": [
        {
          "name": "seq",
          "type": "integer"
        }
      ],
      "message": "Frame lost, sequence {seq}"
    },
    {
      "id": "reset-reason",
      "eventType": "touch.reset",
      "contains": [
        "reset reason=",
        "tp_drv:"
      ],
      "regex": "reset reason=(?<code>0x[0-9a-f]+)",
      "captures": [
        {
          "name": "code",
          "type": "enum",
          "map": {
            "0x01": "power-on",
            "0x04": "watchdog"
          }
        }
      ],
      "message": "Reset, reason {code}"
    }
  ],
  "tests": [
    {
      "name": "esd-basic",
      "line": "[   12.345678] tp_drv: esd detected cnt=3, recovering",
      "expect": [
        {
          "rule": "esd-detected",
          "fields": {
            "cnt": "3"
          }
        }
      ]
    },
    {
      "name": "unrelated-line",
      "line": "[   15.000900] input: unrelated line",
      "expect": []
    }
  ],
  "contentSha256": "b9a57a7b33c2f3e77b84bf8c13bcfaddf2b287f8a4fb88075d31d793b7475567"
}
""";

    public static LogRuleDocumentV1 Unsealed() => new(
        SchemaVersion: 1,
        FileKind: "logRules",
        ProfileId: "acme.example.v1",
        DisplayName: "Acme Motors example (synthetic)",
        Scope: new LogRuleScopeV1(Carmaker: "Acme Motors (synthetic)", Platform: "example", Note: "invented format"),
        LineEncoding: "utf-8",
        EventTypes:
        [
            new("touch.reset", "Touch reset", LogEventCategory.DeviceState, DiagnosticSeverity.Warning),
            new("touch.frameLost", "Frame lost", LogEventCategory.BehaviorFinding, DiagnosticSeverity.Warning),
            new("touch.esd", "ESD detected", LogEventCategory.DeviceState, DiagnosticSeverity.Warning),
        ],
        Rules:
        [
            new(
                "reset-reason",
                "touch.reset",
                "Reset, reason {code}",
                Keywords: ["tp_drv:", "reset reason="],
                Pattern: @"reset reason=(?<code>0x[0-9a-f]+)",
                Captures:
                [
                    new(
                        "code",
                        LogCaptureType.Enum,
                        new Dictionary<string, string> { ["0x04"] = "watchdog", ["0x01"] = "power-on" }),
                ]),
            new(
                "frame-lost",
                "touch.frameLost",
                "Frame lost, sequence {seq}",
                Keywords: ["tp_drv:", "frame lost"],
                Pattern: @"frame lost seq=(?<seq>\d+)",
                Captures: [new("seq", LogCaptureType.Integer)]),
            new(
                "esd-detected",
                "touch.esd",
                "ESD detected, count {cnt}",
                Keywords: ["tp_drv:", "esd detected"],
                Pattern: @"esd detected cnt=(?<cnt>\d+)",
                Captures: [new("cnt", LogCaptureType.Integer)]),
        ],
        Tests:
        [
            new("unrelated-line", "[   15.000900] input: unrelated line", []),
            new(
                "esd-basic",
                "[   12.345678] tp_drv: esd detected cnt=3, recovering",
                [new("esd-detected", new Dictionary<string, string> { ["cnt"] = "3" })]),
        ],
        ContentSha256: string.Empty,
        Timestamp: new LogRuleTimestampV1(@"^\[\s*(?<ts>\d+\.\d+)\]", LogTimestampFormat.SecondsSinceBoot));

    public static LogRuleDocumentV1 Create() => LogRuleFile.Seal(Unsealed());

    /// <summary>Gets a document with one rule that lists many unique keywords. At 60000 keywords the compact form is near 600 KB and the indented form is above 1 MiB.</summary>
    public static LogRuleDocumentV1 WithManyKeywords(int count) => Unsealed() with
    {
        Rules = [new("many-keywords", "touch.esd", "Seen", Keywords: [.. Enumerable.Range(0, count).Select(index => $"k{index:D6}")])],
        Tests = [],
    };

    public static LogRuleDocumentV1 WithRule(
        LogRuleDocumentV1 document,
        string ruleId,
        Func<LogRuleV1, LogRuleV1> change) =>
        document with { Rules = document.Rules.Select(rule => rule.Id == ruleId ? change(rule) : rule).ToArray() };
}
