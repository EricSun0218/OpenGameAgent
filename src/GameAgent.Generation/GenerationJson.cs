using System.Buffers;
using System.Text;
using System.Text.Json;

namespace GameAgent.Generation;

public static class GenerationJson
{
    public static string SerializeJob(GenerationJob job)
    {
        GenerationValidation.ValidateJob(job);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("operationId", job.OperationId);
            writer.WriteString("requestDigest", job.RequestDigest);
            writer.WriteString("modality", job.Modality);
            writer.WriteString("provider", job.Provider);
            WriteOptionalString(writer, "providerJobId", job.ProviderJobId);
            writer.WriteString("acceptance", job.Acceptance);
            writer.WriteString("status", job.Status);
            if (job.Progress.HasValue)
            {
                writer.WriteNumber("progress", job.Progress.Value);
            }

            writer.WriteString("createdAt", job.CreatedAt);
            writer.WriteString("updatedAt", job.UpdatedAt);
            if (job.Output.HasValue)
            {
                writer.WritePropertyName("output");
                job.Output.Value.WriteTo(writer);
            }

            writer.WriteStartArray("artifacts");
            foreach (var artifact in job.Artifacts)
            {
                writer.WriteStartObject();
                writer.WriteString("artifactId", artifact.ArtifactId);
                writer.WriteString("uri", artifact.Uri);
                writer.WriteString("mediaType", artifact.MediaType);
                writer.WriteString("sha256", artifact.Sha256);
                writer.WriteNumber("sizeBytes", artifact.SizeBytes);
                WriteOptionalString(writer, "fileName", artifact.FileName);
                if (artifact.SourceExpiresAt.HasValue)
                {
                    writer.WriteString("sourceExpiresAt", artifact.SourceExpiresAt.Value);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            WriteOptionalString(writer, "errorCode", job.ErrorCode);
            WriteOptionalString(writer, "errorMessage", job.ErrorMessage);
            writer.WriteBoolean("retryable", job.Retryable);
            WriteOptionalString(writer, "costUsd", job.CostUsd);
            WriteOptionalString(writer, "authorityId", job.AuthorityId);
            writer.WriteNumber("revision", job.Revision);
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan.ToArray());
    }

    private static void WriteOptionalString(
        Utf8JsonWriter writer,
        string name,
        string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }
}
