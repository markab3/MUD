
using MUD.Core.GameObjects;

namespace MUD.Core.Properties.Interfaces
{
    public interface ISearchable
    {
        bool DoSearch(Living searcher, string searchTarget);
    }
}