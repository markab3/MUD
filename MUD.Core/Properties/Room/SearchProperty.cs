using System;
using MUD.Core.GameObjects;

namespace MUD.Core.Properties
{
    public class SearchProperty : ExtendedProperty
    {
        private Action<Player, string> _searchFunction;

        public SearchProperty(Action<Player, string> searchFunction)
        {
            _searchFunction = searchFunction;
        }

        public void DoSearch(Player searcher, string searchTarget)
        {
            _searchFunction(searcher, searchTarget);
        }

        public override void InitializeProperty()
        {
            throw new NotImplementedException();
        }

        public override void RemoveProperty()
        {
            throw new NotImplementedException();
        }
    }
}