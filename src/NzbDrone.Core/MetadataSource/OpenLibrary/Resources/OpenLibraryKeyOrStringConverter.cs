using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NzbDrone.Core.MetadataSource.OpenLibrary.Resources
{
    // OL writes a reference to another record in one of two ways depending on
    // the endpoint and the age of the record:
    //   "/type/redirect"
    //   { "key": "/type/redirect" }
    //
    // `type` is the field this exists for, and both forms turn up for it. Bound
    // to a plain string property, the object form fails the whole document's
    // deserialization -- which is why the resources did not model `type` at all
    // until redirect and delete documents made it necessary.
    //
    // Same posture as OpenLibraryDescriptionConverter: anything unrecognised
    // becomes null and the rest of the document still parses. A field we only
    // consult to spot the unusual case must never be the reason an ordinary
    // record fails to load.
    public class OpenLibraryKeyOrStringConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(string);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
            {
                return null;
            }

            if (reader.TokenType == JsonToken.String)
            {
                return (string)reader.Value;
            }

            // Consume whatever it is, so the reader is positioned correctly for
            // the next property whether or not we can use this one.
            var token = JToken.Load(reader);

            if (token is JObject obj && obj["key"]?.Type == JTokenType.String)
            {
                return obj["key"].ToString();
            }

            return null;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            // We never serialize OL DTOs back out; no-op is safe.
            writer.WriteValue(value as string);
        }
    }

    // What the proxy needs from a top-level OL document in order to tell a live
    // record from a placeholder for one that has gone.
    public interface IOpenLibraryDocument
    {
        string Type { get; }
        string Location { get; }
    }

    // The two document types that stand in for a record that is no longer
    // there. Both come back with a 200, so the status code says nothing and the
    // body has to be inspected.
    public static class OpenLibraryDocumentType
    {
        public const string Redirect = "/type/redirect";
        public const string Delete = "/type/delete";

        public static bool IsRedirect(string type) => Redirect.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase);

        public static bool IsDelete(string type) => Delete.Equals(type?.Trim(), StringComparison.OrdinalIgnoreCase);
    }
}
