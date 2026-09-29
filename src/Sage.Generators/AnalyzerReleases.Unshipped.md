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
SAGE0020 | Sage.Declarations | Error | A registration written in Start, OnWorldCreated, CreateRules or a system (register in Init)
SAGE0021 | Sage.Declarations | Error | A [Record] type or [SavedResource] name that is empty, or a saved resource Version below 1
SAGE0022 | Sage.Declarations | Error | An [Upgrade] method nothing runs, or a saved resource's malformed or duplicate upgrader
SAGE0023 | Sage.Saves | Error | Strict saves (SageStrictSaves): a public component field that is neither [Property] nor [Transient]
SAGE0024 | Sage.Architecture | Error | A MonoGame type in a simulation-only assembly (SageSimulationOnly)
SAGE0025 | Sage.Architecture | Error | A base assembly (SageBaseAssembly) that references a Sage.Kits.* assembly or uses its types
SAGE0040 | Sage.Declarations | Error | A [Property] range on a field that is not a number or a vector, or Min above Max
SAGE0041 | Sage.Declarations | Error | A [RecordRef] on a field that is not a RecordId
SAGE0042 | Sage.Declarations | Error | An [AssetKind] on a field that is not an AssetPath
