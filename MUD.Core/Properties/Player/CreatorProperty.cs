namespace MUD.Core.Properties
{
    public class CreatorProperty : ExtendedProperty
    {
        public string CurrentDirectory { get; set; }

        public override void InitializeProperty()
        {
            // Have this impart creator commands when this applies.
            return;
        }

        public override void RemoveProperty()
        {
            // Remove those creator commands when this is removed
            return;
        }
    }
}