namespace MUD.Core.Interfaces
{
    public interface IExaminable
    {
        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public string Examine();
    }
}