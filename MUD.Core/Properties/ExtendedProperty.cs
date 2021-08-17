using MUD.Core.GameObjects;
using MUD.Data;

namespace MUD.Core.Properties
{
    // Maybe make this generic and force the implementing property classes to declare what sort of object it should be on?
    public abstract class ExtendedProperty : Entity
    {
        public GameObject objectContext;

        public abstract void InitializeProperty();

        public abstract void RemoveProperty();
    }
}