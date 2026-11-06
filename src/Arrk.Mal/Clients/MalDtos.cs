using System.Text.Json;
using System.Text.Json.Serialization;

namespace Arrk.Mal.Clients;

public sealed class MalTokenResponse
{
    [JsonPropertyName("token_type")]
    public string TokenType { get; set; } = "Bearer";

    [JsonPropertyName("expires_in")]
    public int ExpiresIn { get; set; }

    [JsonPropertyName("access_token")]
    public string AccessToken { get; set; } = "";

    [JsonPropertyName("refresh_token")]
    public string RefreshToken { get; set; } = "";
}

public sealed class MalUser
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("picture_url")]
    public string PictureUrl { get; set; } = "";
}

public sealed class MalPicture
{
    [JsonPropertyName("medium")]
    public string Medium { get; set; } = "";

    [JsonPropertyName("large")]
    public string Large { get; set; } = "";
}

public sealed class MalAlternativeTitles
{
    [JsonPropertyName("synonyms")]
    public List<string> Synonyms { get; set; } = [];

    [JsonPropertyName("en")]
    public string English { get; set; } = "";

    [JsonPropertyName("ja")]
    public string Japanese { get; set; } = "";
}

public sealed class MalGenre
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class MalStudio
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";
}

public sealed class MalListStatus
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("score")]
    public int Score { get; set; }

    [JsonPropertyName("num_episodes_watched")]
    public int WatchedEpisodes { get; set; }

    [JsonPropertyName("is_rewatching")]
    public bool IsRewatching { get; set; }

    [JsonPropertyName("updated_at")]
    public DateTime? UpdatedAt { get; set; }

    [JsonPropertyName("priority")]
    public int Priority { get; set; }

    [JsonPropertyName("num_times_rewatched")]
    public int TimesRewatched { get; set; }

    [JsonPropertyName("rewatch_value")]
    public int RewatchValue { get; set; }

    [JsonPropertyName("tags")]
    [JsonConverter(typeof(StringOrArrayConverter))]
    public List<string> Tags { get; set; } = [];

    [JsonPropertyName("comments")]
    public string Comments { get; set; } = "";
}

public sealed class MalAnime
{
    [JsonPropertyName("id")]
    public int Id { get; set; }

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("main_picture")]
    public MalPicture? MainPicture { get; set; }

    [JsonPropertyName("alternative_titles")]
    public MalAlternativeTitles? AlternativeTitles { get; set; }

    [JsonPropertyName("num_episodes")]
    public int NumEpisodes { get; set; }

    [JsonPropertyName("media_type")]
    public string MediaType { get; set; } = "";

    [JsonPropertyName("status")]
    public string Status { get; set; } = "";

    [JsonPropertyName("start_date")]
    public string StartDate { get; set; } = "";

    [JsonPropertyName("end_date")]
    public string EndDate { get; set; } = "";

    [JsonPropertyName("synopsis")]
    public string Synopsis { get; set; } = "";

    [JsonPropertyName("mean")]
    public double? Mean { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("genres")]
    public List<MalGenre> Genres { get; set; } = [];

    [JsonPropertyName("studios")]
    public List<MalStudio> Studios { get; set; } = [];

    [JsonPropertyName("nsfw")]
    public string? Nsfw { get; set; }

    [JsonPropertyName("my_list_status")]
    public MalListStatus? MyListStatus { get; set; }

    [JsonPropertyName("related_anime")]
    public List<MalRelatedAnime> RelatedAnime { get; set; } = [];
}

public sealed class MalRelatedAnime
{
    [JsonPropertyName("node")]
    public MalAnime Node { get; set; } = new();

    [JsonPropertyName("relation_type")]
    public string RelationType { get; set; } = "";

    [JsonPropertyName("relation_type_formatted")]
    public string RelationTypeFormatted { get; set; } = "";
}

public sealed class MalListData
{
    [JsonPropertyName("node")]
    public MalAnime Node { get; set; } = new();

    [JsonPropertyName("list_status")]
    public MalListStatus ListStatus { get; set; } = new();
}

public sealed class MalListPage
{
    [JsonPropertyName("data")]
    public List<MalListData> Data { get; set; } = [];

    [JsonPropertyName("paging")]
    public MalPaging? Paging { get; set; }
}

public sealed class MalPaging
{
    [JsonPropertyName("next")]
    public string? Next { get; set; }
}

public sealed class MalSearchPage
{
    [JsonPropertyName("data")]
    public List<MalSearchData> Data { get; set; } = [];

    [JsonPropertyName("paging")]
    public MalPaging? Paging { get; set; }
}

public sealed class MalSearchData
{
    [JsonPropertyName("node")]
    public MalAnime Node { get; set; } = new();
}

/// <summary>
/// MAL's list_status.tags is documented as a string but the API returns an array in practice
/// (and older responses may return a string). Accept both so one shape never breaks a list pull.
/// </summary>
public sealed class StringOrArrayConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
            return [];

        if (reader.TokenType == JsonTokenType.String)
            return string.IsNullOrWhiteSpace(reader.GetString()) ? [] : [reader.GetString()!];

        if (reader.TokenType != JsonTokenType.StartArray)
            throw new JsonException($"Unexpected token {reader.TokenType} for a string list.");

        List<string> values = [];
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType == JsonTokenType.String)
                values.Add(reader.GetString() ?? "");
        }
        return values;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (string item in value)
            writer.WriteStringValue(item);
        writer.WriteEndArray();
    }
}
