using System.Text.Json.Serialization;

public class ExternalTaskInfo
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }

    [JsonPropertyName("topicName")]
    public string? TopicName { get; set; }

    public string? ProcessInstanceId { get; set; }

    public Dictionary<string, ExternalTaskData>? Variables { get; set; }
}
