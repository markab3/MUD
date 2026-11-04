namespace MUD.Core.Events
{
    // The names of the events core raises for actions (owner decision D4: generic string names). entity.move covers
    // exit and enter in one event (D4); item.move covers get, drop and the like, with the reason in the payload.
    public static class EventNames
    {
        public const string EntityMove = "entity.move";
        public const string ItemMove = "item.move";
        public const string CommandEntered = "command.entered";
    }
}
