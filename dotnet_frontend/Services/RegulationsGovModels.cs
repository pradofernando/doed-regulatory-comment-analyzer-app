using System.Text.Json.Serialization;

namespace DoedRegulatoryComments.Web.Services;

/// <summary>
/// Lightweight DTOs for the subset of the Regulations.gov v4 response shape we render.
/// Designed to also work with custom backends that return a JSON:API-style payload.
/// </summary>
public class CommentListResponse
{
    [JsonPropertyName("data")]
    public List<CommentResource> Data { get; set; } = new();

    [JsonPropertyName("meta")]
    public CommentMeta? Meta { get; set; }
}

public class CommentMeta
{
    [JsonPropertyName("numberOfPages")]
    public int? NumberOfPages { get; set; }

    [JsonPropertyName("totalElements")]
    public int? TotalElements { get; set; }
}

public class CommentResource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("attributes")]
    public CommentAttributes Attributes { get; set; } = new();
}

public class CommentAttributes
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("comment")]
    public string? Comment { get; set; }

    [JsonPropertyName("firstName")]
    public string? FirstName { get; set; }

    [JsonPropertyName("lastName")]
    public string? LastName { get; set; }

    [JsonPropertyName("organization")]
    public string? Organization { get; set; }

    [JsonPropertyName("postedDate")]
    public DateTimeOffset? PostedDate { get; set; }

    [JsonPropertyName("modifyDate")]
    public DateTimeOffset? ModifyDate { get; set; }

    [JsonPropertyName("agencyId")]
    public string? AgencyId { get; set; }

    [JsonPropertyName("documentType")]
    public string? DocumentType { get; set; }
}

public class CommentDetailResponse
{
    [JsonPropertyName("data")]
    public CommentResource? Data { get; set; }

    [JsonPropertyName("included")]
    public List<IncludedResource> Included { get; set; } = new();
}

public class IncludedResource
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("attributes")]
    public AttachmentAttributes Attributes { get; set; } = new();
}

public class AttachmentAttributes
{
    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("fileFormats")]
    public List<FileFormat>? FileFormats { get; set; }
}

public class FileFormat
{
    [JsonPropertyName("fileUrl")]
    public string? FileUrl { get; set; }

    [JsonPropertyName("format")]
    public string? Format { get; set; }

    [JsonPropertyName("size")]
    public long? Size { get; set; }
}

public class FetchCommentsRequest
{
    public string DocumentId { get; set; } = string.Empty;
    public bool UseDocketFilter { get; set; } = true;
    public int PageSize { get; set; } = 25;
    public int Page { get; set; } = 1;
}

public class FetchCommentsResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public List<CommentResource> Comments { get; set; } = new();
    public int? TotalPages { get; set; }
    public int? TotalElements { get; set; }
    public string? RequestedUrl { get; set; }
}
