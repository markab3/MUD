using System;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.IdGenerators;

namespace MUD.Data
{
    public class Helper
    {
        private IServiceProvider _serviceProvider;

        public Helper(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public void RegisterRootClass(Type rootType, string idProperty)
        {
            var classMap = RegisterClassMap(rootType);
            classMap.SetIsRootClass(true);
            classMap.MapIdProperty(idProperty).SetIdGenerator(StringObjectIdGenerator.Instance);
        }

        public BsonClassMap RegisterClassMap(Type typeToRegister)
        {
            var classMap = BsonClassMap.LookupClassMap(typeToRegister); // This also registers the type and automaps.
            classMap.SetCreator(() => { return _serviceProvider.GetService(typeToRegister); });
            return classMap;
        }
    }
}