using System;
using System.Collections;
using System.Collections.Generic;
using Google.Protobuf.WellKnownTypes;
using MongoDB.Bson;

namespace MUD.Core.Modules
{
    /// <summary>
    /// Conversions between the wire form of a generic map (protobuf Struct, owner decision D52) and the forms core uses:
    /// a plain dictionary for event payloads and a BSON document for stored data. Numbers are floating point on the wire
    /// and stay floating point here; authors convert (D52).
    /// </summary>
    public static class StructConversion
    {
        public static Dictionary<string, object> ToDictionary(Struct source)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            if (source == null) { return result; }
            foreach (KeyValuePair<string, Value> field in source.Fields) { result[field.Key] = ToObject(field.Value); }
            return result;
        }

        public static object ToObject(Value value)
        {
            if (value == null) { return null; }
            switch (value.KindCase)
            {
                case Value.KindOneofCase.NumberValue: return value.NumberValue;
                case Value.KindOneofCase.StringValue: return value.StringValue;
                case Value.KindOneofCase.BoolValue: return value.BoolValue;
                case Value.KindOneofCase.StructValue: return ToDictionary(value.StructValue);
                case Value.KindOneofCase.ListValue:
                    var list = new List<object>(value.ListValue.Values.Count);
                    foreach (Value item in value.ListValue.Values) { list.Add(ToObject(item)); }
                    return list;
                default: return null;
            }
        }

        /// <summary>Builds a Struct from a dictionary of strings, numbers, booleans, nested maps, lists and BSON values.</summary>
        public static Struct FromDictionary(IEnumerable<KeyValuePair<string, object>> source)
        {
            var result = new Struct();
            if (source == null) { return result; }
            foreach (KeyValuePair<string, object> pair in source) { result.Fields[pair.Key] = ToValue(pair.Value); }
            return result;
        }

        public static Struct FromBson(BsonDocument document)
        {
            var result = new Struct();
            if (document == null) { return result; }
            foreach (BsonElement element in document) { result.Fields[element.Name] = BsonToValue(element.Value); }
            return result;
        }

        public static Value ToValue(object item)
        {
            if (item == null) { return Value.ForNull(); }
            if (item is string text) { return Value.ForString(text); }
            if (item is bool flag) { return Value.ForBool(flag); }
            if (item is BsonValue bson) { return BsonToValue(bson); }
            if (item is Value already) { return already; }
            if (item is Struct nested) { return Value.ForStruct(nested); }
            if (item is IDictionary<string, object> map) { return Value.ForStruct(FromDictionary(map)); }
            if (item is IReadOnlyDictionary<string, object> readOnlyMap) { return Value.ForStruct(FromDictionary(readOnlyMap)); }
            if (item is char || item is System.Enum) { return Value.ForString(item.ToString()); }
            if (item is IConvertible && !(item is DateTime))
            {
                try { return Value.ForNumber(Convert.ToDouble(item, System.Globalization.CultureInfo.InvariantCulture)); }
                catch (Exception) { return Value.ForString(item.ToString()); }
            }
            if (item is IEnumerable sequence)
            {
                var values = new List<Value>();
                foreach (object entry in sequence) { values.Add(ToValue(entry)); }
                return Value.ForList(values.ToArray());
            }
            return Value.ForString(item.ToString());
        }

        private static Value BsonToValue(BsonValue value)
        {
            switch (value.BsonType)
            {
                case BsonType.Null:
                case BsonType.Undefined: return Value.ForNull();
                case BsonType.String: return Value.ForString(value.AsString);
                case BsonType.Boolean: return Value.ForBool(value.AsBoolean);
                case BsonType.Int32: return Value.ForNumber(value.AsInt32);
                case BsonType.Int64: return Value.ForNumber(value.AsInt64);
                case BsonType.Double: return Value.ForNumber(value.AsDouble);
                case BsonType.Decimal128: return Value.ForNumber((double)value.AsDecimal);
                case BsonType.Document: return Value.ForStruct(FromBson(value.AsBsonDocument));
                case BsonType.Array:
                    var values = new List<Value>();
                    foreach (BsonValue entry in value.AsBsonArray) { values.Add(BsonToValue(entry)); }
                    return Value.ForList(values.ToArray());
                default: return Value.ForString(value.ToString());
            }
        }

        /// <summary>Stored form of a Struct: every number is a double, as it arrived.</summary>
        public static BsonDocument ToBson(Struct source)
        {
            var result = new BsonDocument();
            if (source == null) { return result; }
            foreach (KeyValuePair<string, Value> field in source.Fields) { result[field.Key] = ValueToBson(field.Value); }
            return result;
        }

        /// <summary>Stored form of a payload object (a dictionary produced by ToDictionary). Null when the object is not a map.</summary>
        public static BsonDocument ToBson(object map)
        {
            var dictionary = map as IEnumerable<KeyValuePair<string, object>>;
            if (dictionary == null) { return null; }
            var result = new BsonDocument();
            foreach (KeyValuePair<string, object> pair in dictionary) { result[pair.Key] = ObjectToBson(pair.Value); }
            return result;
        }

        private static BsonValue ObjectToBson(object item)
        {
            if (item == null) { return BsonNull.Value; }
            if (item is string text) { return new BsonString(text); }
            if (item is bool flag) { return new BsonBoolean(flag); }
            if (item is double number) { return new BsonDouble(number); }
            if (item is IEnumerable<KeyValuePair<string, object>> map) { return ToBson(map); }
            if (item is IEnumerable sequence)
            {
                var array = new BsonArray();
                foreach (object entry in sequence) { array.Add(ObjectToBson(entry)); }
                return array;
            }
            return new BsonString(item.ToString());
        }

        private static BsonValue ValueToBson(Value value)
        {
            switch (value.KindCase)
            {
                case Value.KindOneofCase.NumberValue: return new BsonDouble(value.NumberValue);
                case Value.KindOneofCase.StringValue: return new BsonString(value.StringValue);
                case Value.KindOneofCase.BoolValue: return new BsonBoolean(value.BoolValue);
                case Value.KindOneofCase.StructValue: return ToBson(value.StructValue);
                case Value.KindOneofCase.ListValue:
                    var array = new BsonArray();
                    foreach (Value entry in value.ListValue.Values) { array.Add(ValueToBson(entry)); }
                    return array;
                default: return BsonNull.Value;
            }
        }
    }
}
