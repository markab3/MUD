using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.IdGenerators;

namespace MUD.Data
{
    public class MongoDBClassMapManager
    {
        private IServiceProvider _serviceProvider;

        public MongoDBClassMapManager(IServiceProvider serviceProvider)
        {
            _serviceProvider = serviceProvider;
        }

        public BsonClassMap RegisterClassMap(Type typeToRegister)
        {

            var classMap = BsonClassMap.LookupClassMap(typeToRegister); // This also registers the type and automaps.
            classMap.SetCreator(() => { return _serviceProvider.GetService(typeToRegister); });
            return classMap;
        }

        public BsonClassMap RegisterRootClassMap(Type rootType)
        {
            if (BsonClassMap.IsClassMapRegistered(rootType))
            {
                return BsonClassMap.LookupClassMap(rootType); // This registers, but also freezes the thing.
            }

            var rootClassMap = new BsonClassMap(rootType);
            rootClassMap.AutoMap();
            rootClassMap.SetIsRootClass(true);
            rootClassMap.MapIdProperty("Id").SetIdGenerator(StringObjectIdGenerator.Instance);
            rootClassMap.SetCreator(() => { return _serviceProvider.GetService(rootType); });
            BsonClassMap.RegisterClassMap(rootClassMap);
            return rootClassMap;
        }

        public void Unregister<T>()
        {
            Unregister(typeof(T));
        }
        public void Unregister(Type typeToUnregister)
        {
            GetClassMaps()?.Remove(typeToUnregister);
        }

        public void ClearClassMaps()
        {
            GetClassMaps()?.Clear();
            RegisterRootClassMap(typeof(Entity));
        }

        public void RegisterEntityClasses(IEnumerable<Assembly> assemblies)
        {
            foreach (Assembly currentAssembly in assemblies)
            {
                foreach (var currentType in currentAssembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Entity))))
                {
                    RegisterClassMap(currentType);
                }
            }
        }

        // Dirty dirty guy on the internet made this.
        // They were using it for mocking unit test objects, which is a better excuse.
        private Dictionary<Type, BsonClassMap> GetClassMaps()
        {
            var allClassMaps = BsonClassMap.GetRegisteredClassMaps();
            if (allClassMaps == null || allClassMaps.Count() == 0) { return null; }

            var cm = BsonClassMap.GetRegisteredClassMaps().First();
            var fi = typeof(BsonClassMap).GetField("__classMaps", BindingFlags.Static | BindingFlags.NonPublic);
            var classMaps = (Dictionary<Type, BsonClassMap>)fi.GetValue(cm);
            return classMaps;
        }
    }
}