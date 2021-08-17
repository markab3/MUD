using MUD.Core.GameObjects;
using MUD.Core.Properties;
using MUD.Core.Repositories.Interfaces;

namespace MUD.PlanarRealms.Planes.Neutral.Rooms
{
    public class TowerOfAellioc_1 : Room
    {
        public TowerOfAellioc_1(IRoomRepository roomRepository) : base(roomRepository)
        {
            ExtendedProperties.Add(new SearchProperty((searcher, searchTarget) =>
            {
                if (searchTarget.ToLower() == "table")
                {
                    searcher.ReceiveMessage("You search the table and find a really cool sword.");
                }
                else
                {
                    searcher.ReceiveMessage(string.Format("You search the {0} but find nothing of any value.", searchTarget));
                }
            }));
        }
    }
}