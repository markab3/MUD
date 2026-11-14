using System.Collections.Generic;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;

namespace MUD.PlanarRealms.Planes.Neutral.Rooms
{
    public class TowerOfAellioc_1 : Room
    {
        public TowerOfAellioc_1(IRoomRepository roomRepository) : base(roomRepository)
        {
            Features.Add(new Feature
            {
                ShortDescription = "a heavy oak table",
                Handles = new List<string> { "table" },
                Adjectives = new List<string> { "heavy", "oak" },
                Plurals = new List<string> { "tables" },
                RoomLine = "A heavy oak table stands against the far wall.",
                DetailDescription = "The oak table is scarred by countless knife marks and ringed with the stains of long-forgotten cups."
            });

            Features.Add(new Feature
            {
                ShortDescription = "a narrow window",
                Handles = new List<string> { "window" },
                Adjectives = new List<string> { "narrow" },
                Plurals = new List<string> { "windows" },
                RoomLine = "A narrow window lets in a thin shaft of pale light.",
                DetailDescription = "Through the narrow window you glimpse the shimmering, shifting expanse of the Astral sea far below."
            });

            // TODO: SearchProperty no longer exists (replaced by BasicSearchProperty); disabled until the behavior registry replaces this lambda.
            //ExtendedProperties.Add(new SearchProperty((searcher, searchTarget) =>
            //{
            //    if (searchTarget.ToLower() == "table")
            //    {
            //        searcher.ReceiveMessage("You search the table and find a really cool sword.");
            //    }
            //    else
            //    {
            //        searcher.ReceiveMessage(string.Format("You search the {0} but find nothing of any value.", searchTarget));
            //    }
            //}));
        }
    }
}