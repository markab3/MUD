using System;
using MUD.Core.GameObjects;

namespace MUD.Core.Properties
{
    public class SearchProperty : ExtendedProperty
    {
        private Action<Living, string> _searchFunction;

        public SearchProperty(Action<Living, string> searchFunction)
        {
            _searchFunction = searchFunction;
        }

        public void DoSearch(Living searcher, string searchTarget)
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