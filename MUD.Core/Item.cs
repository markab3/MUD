using MUD.Core.Entities;
using MUD.Core.Interfaces;

namespace MUD.Core
{
    public class Item : ItemEntity, IUpdatable, IExaminable
    {
        public string ShortDescription { get => throw new System.NotImplementedException(); set => throw new System.NotImplementedException(); }
        public string LongDescription { get => throw new System.NotImplementedException(); set => throw new System.NotImplementedException(); }

        public string Examine()
        {
            throw new System.NotImplementedException();
        }

        public void Update()
        {
            throw new System.NotImplementedException();
        }
    }
}