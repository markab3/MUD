using MUD.Core.GameObjects;

namespace MUD.Core.Events
{
    // An immutable copy of one party as a Before handler sees it (owner decision D6). It carries CORE data only: no module
    // components, not even the receiving module's own (D57). A handler that needs more reads the entity with GetEntity
    // (D58) once there are out-of-process modules; an in-process handler is handed the entry's Config with the delivery.
    public readonly struct EntitySnapshot
    {
        public string Role { get; }

        public string Id { get; }

        // "room", "item" or "living".
        public string Kind { get; }

        public string ShortDescription { get; }

        // The room a living stands in; null for rooms and items.
        public string LocationId { get; }

        public EntitySnapshot(string role, string id, string kind, string shortDescription, string locationId)
        {
            Role = role;
            Id = id;
            Kind = kind;
            ShortDescription = shortDescription;
            LocationId = locationId;
        }

        public static EntitySnapshot From(EventParty party)
        {
            GameObject entity = party.Entity;
            string kind = entity is Room ? "room" : entity is Living ? "living" : "item";
            string locationId = entity is Living living ? living.CurrentLocationId : null;
            return new EntitySnapshot(party.Role, entity.Id, kind, entity.ShortDescription, locationId);
        }
    }
}
