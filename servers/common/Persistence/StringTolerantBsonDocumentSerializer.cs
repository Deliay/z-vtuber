using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace Z.Vtuber.Common.Persistence;

public class StringTolerantBsonDocumentSerializer : SerializerBase<BsonDocument>
{
    public override BsonDocument Deserialize(BsonDeserializationContext context, BsonDeserializationArgs args)
    {
        var bsonType = context.Reader.GetCurrentBsonType();
        switch (bsonType)
        {
            case BsonType.Document:
                return BsonDocumentSerializer.Instance.Deserialize(context, args);
            case BsonType.String:
                var str = context.Reader.ReadString();
                try
                {
                    return BsonDocument.Parse(str);
                }
                catch (FormatException)
                {
                    return new BsonDocument { { "raw", str } };
                }
            case BsonType.Null:
                context.Reader.ReadNull();
                return new BsonDocument();
            default:
                throw new BsonSerializationException($"Cannot deserialize BsonType {bsonType} to BsonDocument.");
        }
    }

    public override void Serialize(BsonSerializationContext context, BsonSerializationArgs args, BsonDocument value)
    {
        BsonDocumentSerializer.Instance.Serialize(context, args, value);
    }
}