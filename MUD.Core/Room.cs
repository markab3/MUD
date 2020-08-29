using MUD.Core.Interfaces;

namespace MUD.Core
{
    public class Room : IExaminable
    {
        public string Examine()
        {
            return "This appears to be an empty room. There are four blank, white walls and a featureless ceiling. The floor is covered with rough, commercial-grade carpet.";
        }
    }

}