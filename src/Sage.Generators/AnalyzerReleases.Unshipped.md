; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
SAGE0001 | Sage.Declarations | Error | A declaration whose plugin cannot be inferred
SAGE0002 | Sage.Declarations | Error | A declared type the registration cannot construct
SAGE0003 | Sage.Declarations | Error | Two declarations of one id in an assembly
SAGE0004 | Sage.Declarations | Error | An IComponent or ITag struct without [Component] / [Tag]
SAGE0005 | Sage.Declarations | Error | A malformed component or tag declaration
SAGE0006 | Sage.Declarations | Error | Two components or tags with one id in an assembly
SAGE0007 | Sage.Declarations | Error | An [Upgrade] method with the wrong signature or version
SAGE0010 | Sage.Declarations | Error | A prefab part whose plugin cannot be inferred
SAGE0011 | Sage.Declarations | Error | A declared part or system that cannot be one (abstract, private, wrong interface)
SAGE0012 | Sage.Declarations | Error | Two declarations of one part or system id in an assembly
SAGE0013 | Sage.Declarations | Error | A system ordered against a system in another phase
