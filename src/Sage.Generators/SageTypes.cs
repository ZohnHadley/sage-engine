namespace Sage.Generators;

// The engine types the generators and analyzers look for and write, by fully qualified metadata name
// (issue #24). One list, so an engine type that moves between assemblies or namespaces is changed here
// and nowhere else; SageTypesTests resolves every one of them against the base assemblies.
//
// Sage.Core holds the declaration vocabulary (attributes, the metadata table, records, cvars);
// Sage.Simulation holds what needs a World (systems, modules, prefab parts, saves, registrations).
public static class SageTypes
{
    public const string Core = "Sage.Core";
    public const string Simulation = "Sage.Simulation";

    // ---- Sage.Core ---------------------------------------------------------------------------------
    public const string ComponentAttribute = Core + ".ComponentAttribute";
    public const string TagAttribute = Core + ".TagAttribute";
    public const string UpgradeAttribute = Core + ".UpgradeAttribute";
    public const string IGeneratedComponents = Core + ".IGeneratedComponents";
    public const string GeneratedComponentsAttribute = Core + ".GeneratedComponentsAttribute";
    public const string ComponentDeclaration = Core + ".ComponentDeclaration";
    public const string RecordAttribute = Core + ".RecordAttribute";
    public const string SavedResourceAttribute = Core + ".SavedResourceAttribute";
    public const string TransientAttribute = Core + ".TransientAttribute";
    public const string PrefabPartAttribute = Core + ".PrefabPartAttribute";
    public const string PluginAttribute = Core + ".PluginAttribute";
    public const string PropertyAttribute = Core + ".PropertyAttribute";
    public const string RecordRefAttribute = Core + ".RecordRefAttribute";
    public const string AssetKindAttribute = Core + ".AssetKindAttribute";
    public const string IGeneratedMetadata = Core + ".IGeneratedMetadata";
    public const string GeneratedMetadataAttribute = Core + ".GeneratedMetadataAttribute";
    public const string TypeMetadata = Core + ".TypeMetadata";
    public const string FieldMetadata = Core + ".FieldMetadata";
    public const string DeclarationKind = Core + ".DeclarationKind";
    public const string ValueKind = Core + ".ValueKind";
    public const string RecordId = Core + ".RecordId";
    public const string AssetPath = Core + ".AssetPath";
    public const string RecordRef = Core + ".RecordRef<T>";        // as ToDisplayString writes it
    public const string RecordRefMetadata = Core + ".RecordRef`1";   // as metadata names it
    public const string CVarRegistry = Core + ".CVarRegistry";
    public const string RecordStore = Core + ".RecordStore";
    // Open vocabularies (issue #28), by metadata name: the entry attribute's base and the registry.
    public const string VocabularyAttribute = Core + ".VocabularyAttribute";
    public const string VocabularyEntryAttribute = Core + ".VocabularyEntryAttribute`1";
    public const string Vocabulary = Core + ".Vocabulary`1";

    // ---- Sage.Simulation ---------------------------------------------------------------------------
    public const string SystemAttribute = Simulation + ".SystemAttribute";
    public const string IGeneratedSystems = Simulation + ".IGeneratedSystems";
    public const string GeneratedRegistrationsAttribute = Simulation + ".GeneratedRegistrationsAttribute";
    public const string IGeneratedRegistrations = Simulation + ".IGeneratedRegistrations";
    public const string RegistrationBuilder = Simulation + ".RegistrationBuilder";
    public const string IPrefabPart = Simulation + ".IPrefabPart";
    public const string ISystem = Simulation + ".ISystem";
    public const string IModule = Simulation + ".IModule";
    public const string IGameModule = Simulation + ".IGameModule";
    public const string ModuleManager = Simulation + ".ModuleManager";
    public const string ActionRegistry = Simulation + ".ActionRegistry";
    public const string EntityInputs = Simulation + ".EntityInputs";
    public const string PrefabRegistry = Simulation + ".PrefabRegistry";
    public const string SaveSystem = Simulation + ".SaveSystem";

    // The ECS vocabulary, Sage's own since issue #25 (Friflo is the storage underneath).
    public const string Entity = Simulation + ".Entity";
    public const string IComponent = Simulation + ".IComponent";
    public const string ITag = Simulation + ".ITag";

    // ---- what games must not name (SAGE0050) ---------------------------------------------------------
    public const string FrifloNamespace = "Friflo";

    // The text after the namespace: what a message calls a type.
    public static string ShortName(string fullName)
    {
        int dot = fullName.LastIndexOf('.');
        return dot < 0 ? fullName : fullName.Substring(dot + 1);
    }
}
