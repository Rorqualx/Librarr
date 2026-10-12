using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Resources
{
    // `covers` and `photos` are arrays of image ids:
    //   "covers": [11481354, 12725606]
    //
    // Bound to a plain List<int>, a single null in the array --
    //   "covers": [null, 11481354]
    // -- is a JsonSerializationException ("Error converting value {null} to
    // type 'System.Int32'"), and it is thrown for the document, not the
    // element. On a works list that is one bad slot in one of a thousand
    // entries failing the author's whole refresh.
    //
    // None of the captured payloads carries one, so this is not a fix for an
    // observed failure; it is the same posture as the other converters here.
    // A cover id is decoration. It must never be the reason a record fails to
    // load, so anything in the array that is not an id is skipped and the rest
    // of the array, and of the document, is kept.
    //
    // The property type stays List<int> so the mappers and the cover picker,
    // which already skip the -1 OL uses for "no cover", need no changes.
    public class OpenLibraryIdListConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(List<int>);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            // Consume whatever it is, so the reader is positioned correctly for
            // the next property whether or not we can use this one.
            var token = JToken.Load(reader);

            if (token is not JArray array)
            {
                // A bare id where a list was expected is still an id.
                return TryReadId(token, out var single) ? new List<int> { single } : null;
            }

            var ids = new List<int>(array.Count);

            foreach (var element in array)
            {
                if (TryReadId(element, out var id))
                {
                    ids.Add(id);
                }
            }

            return ids;
        }

        private static bool TryReadId(JToken token, out int id)
        {
            id = 0;

            if (token.Type == JTokenType.Integer)
            {
                var value = token.Value<long>();

                if (value < int.MinValue || value > int.MaxValue)
                {
                    return false;
                }

                id = (int)value;
                return true;
            }

            return token.Type == JTokenType.String &&
                   int.TryParse(token.Value<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out id);
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            // We never serialize OL DTOs back out, but stay well-formed if
            // something does.
            if (value is not List<int> ids)
            {
                writer.WriteNull();
                return;
            }

            writer.WriteStartArray();

            foreach (var id in ids)
            {
                writer.WriteValue(id);
            }

            writer.WriteEndArray();
        }
    }
}
