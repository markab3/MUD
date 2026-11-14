namespace MUD.Core.Components
{
    public enum ComponentKind
    {
        // Not a component name core can place: a single segment that no core type registered.
        Unknown,

        // Typed in C#, schema fixed by core, stored as a typed field on the entity.
        Core,

        // "<module>.<name>": opaque BSON to core, stored under components.<module>.<name>.
        Module
    }
}
