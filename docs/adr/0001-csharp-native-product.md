# ADR 0001: C#-native production application

Status: accepted.

The production decoder, analysis engine, CLI, rendering, and Avalonia desktop
application use C# on .NET 10. Python is not a runtime dependency. The existing
Python repository remains a reference oracle, simulator, and golden generator.

This avoids subprocess deployment and two production implementations while
preserving the validated behavior accumulated in Python. C# parity is checked
semantically against frozen reference JSON and source provenance.
