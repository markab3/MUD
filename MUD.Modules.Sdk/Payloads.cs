using System;
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf.WellKnownTypes;

namespace MUD.Modules.Sdk
{
    /// <summary>
    /// Builds and reads the generic payload maps of the wire contract (protobuf Struct, owner decision D52). Numbers travel as
    /// floating point; text, true and false, null, lists and nested maps are supported.
    /// </summary>
    public static class Payloads
    {
        /// <summary>One map from name and value pairs, for example Payloads.Map(("text", "Hello"), ("count", 3)).</summary>
        public static Struct Map(params (string Key, object Value)[] fields)
        {
            var result = new Struct();
            if (fields == null) { return result; }
            foreach ((string key, object value) in fields) { result.Fields[key] = ToValue(value); }
            return result;
        }

        /// <summary>One map from a dictionary. A null dictionary gives an empty map.</summary>
        public static Struct FromDictionary(IEnumerable<KeyValuePair<string, object>> fields)
        {
            var result = new Struct();
            if (fields == null) { return result; }
            foreach (KeyValuePair<string, object> field in fields) { result.Fields[field.Key] = ToValue(field.Value); }
            return result;
        }

        public static Value ToValue(object value)
        {
            switch (value)
            {
                case null: return Value.ForNull();
                case Value existing: return existing;
                case Struct map: return Value.ForStruct(map);
                case string text: return Value.ForString(text);
                case bool flag: return Value.ForBool(flag);
                case double number: return Value.ForNumber(number);
                case float number: return Value.ForNumber(number);
                case int number: return Value.ForNumber(number);
                case long number: return Value.ForNumber(number);
                case uint number: return Value.ForNumber(number);
                case ulong number: return Value.ForNumber(number);
                case short number: return Value.ForNumber(number);
                case decimal number: return Value.ForNumber((double)number);
                case IEnumerable<KeyValuePair<string, object>> dictionary: return Value.ForStruct(FromDictionary(dictionary));
                case IEnumerable list:
                    {
                        var values = new List<Value>();
                        foreach (object item in list) { values.Add(ToValue(item)); }
                        return Value.ForList(values.ToArray());
                    }
                default:
                    throw new ArgumentException("A payload cannot hold a value of type " + value.GetType().Name + ".");
            }
        }

        /// <summary>The text under the key, or null when the key is missing or is not text.</summary>
        public static string GetText(Struct map, string key)
        {
            Value value;
            if (map != null && map.Fields.TryGetValue(key, out value) && value.KindCase == Value.KindOneofCase.StringValue) { return value.StringValue; }
            return null;
        }

        /// <summary>The number under the key, or the fallback when the key is missing or is not a number.</summary>
        public static double GetNumber(Struct map, string key, double fallback = 0)
        {
            Value value;
            if (map != null && map.Fields.TryGetValue(key, out value) && value.KindCase == Value.KindOneofCase.NumberValue) { return value.NumberValue; }
            return fallback;
        }

        /// <summary>The nested map under the key, or null.</summary>
        public static Struct GetMap(Struct map, string key)
        {
            Value value;
            if (map != null && map.Fields.TryGetValue(key, out value) && value.KindCase == Value.KindOneofCase.StructValue) { return value.StructValue; }
            return null;
        }
    }
}
