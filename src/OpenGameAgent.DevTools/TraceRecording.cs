using System;
using System.Buffers;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using OpenGameAgent.Extensions;

namespace OpenGameAgent.DevTools;

public enum GameAgentTraceFileMode
{
    CreateNew,
    Overwrite,
    Append,
}

public sealed class GameAgentTraceFileOptions
{
    public GameAgentTraceFileMode Mode { get; set; } = GameAgentTraceFileMode.CreateNew;

    public long MaximumFileBytes { get; set; } = 512L * 1024 * 1024;

    public int MaximumLineBytes { get; set; } = 10 * 1024 * 1024;

    public bool FlushEachEntry { get; set; } = true;

    internal GameAgentTraceFileOptions CopyAndValidate()
    {
        var copy = (GameAgentTraceFileOptions)MemberwiseClone();
        if (!Enum.IsDefined(typeof(GameAgentTraceFileMode), copy.Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode));
        }

        if (copy.MaximumFileBytes < 1 || copy.MaximumFileBytes > 16L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }

        if (copy.MaximumLineBytes < 256 || copy.MaximumLineBytes > 100 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumLineBytes));
        }

        if (copy.MaximumLineBytes > copy.MaximumFileBytes)
        {
            throw new ArgumentException("The maximum trace line cannot exceed the maximum trace file size.");
        }

        return copy;
    }
}

public sealed class GameAgentTraceReadOptions
{
    public long MaximumFileBytes { get; set; } = 512L * 1024 * 1024;

    public int MaximumLineBytes { get; set; } = 10 * 1024 * 1024;

    public int MaximumEntries { get; set; } = 1_000_000;

    public bool AllowTruncatedFinalLine { get; set; } = true;

    internal GameAgentTraceReadOptions CopyAndValidate()
    {
        var copy = (GameAgentTraceReadOptions)MemberwiseClone();
        if (copy.MaximumFileBytes < 1 || copy.MaximumFileBytes > 16L * 1024 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumFileBytes));
        }

        if (copy.MaximumLineBytes < 256 || copy.MaximumLineBytes > 100 * 1024 * 1024)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumLineBytes));
        }

        if (copy.MaximumEntries < 1 || copy.MaximumEntries > 10_000_000)
        {
            throw new ArgumentOutOfRangeException(nameof(MaximumEntries));
        }

        return copy;
    }
}

public enum GameAgentTraceStorageError
{
    InvalidPath,
    AlreadyExists,
    LimitExceeded,
    CorruptRecording,
    Storage,
}

public sealed class GameAgentTraceStorageException : Exception
{
    public GameAgentTraceStorageException(
        GameAgentTraceStorageError code,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
    }

    public GameAgentTraceStorageError Code { get; }
}

public sealed class GameAgentTraceRecording
{
    public GameAgentTraceRecording(IEnumerable<GameAgentTraceEntry> entries)
        : this(Prepare(entries), ignoredTruncatedFinalLine: false, bytesRead: 0)
    {
    }

    internal GameAgentTraceRecording(
        IReadOnlyList<GameAgentTraceEntry> entries,
        bool ignoredTruncatedFinalLine,
        long bytesRead)
    {
        Entries = entries;
        IgnoredTruncatedFinalLine = ignoredTruncatedFinalLine;
        BytesRead = bytesRead;
    }

    public IReadOnlyList<GameAgentTraceEntry> Entries { get; }

    public bool IgnoredTruncatedFinalLine { get; }

    public long BytesRead { get; }

    private static IReadOnlyList<GameAgentTraceEntry> Prepare(IEnumerable<GameAgentTraceEntry> entries)
    {
        var copy = (entries ?? throw new ArgumentNullException(nameof(entries))).ToArray();
        if (copy.Length > 1_000_000 || copy.Any(entry => entry is null))
        {
            throw new ArgumentException("A recording supports at most 1,000,000 non-null entries.", nameof(entries));
        }

        return new ReadOnlyCollection<GameAgentTraceEntry>(copy);
    }
}

/// <summary>
/// Writes one self-contained JSON object per trace entry. The sink never records provider
/// credentials; payload and tool-argument capture remain controlled by GameAgentTracingOptions.
/// </summary>
public sealed class JsonLinesGameAgentTraceSink : IGameAgentTraceSink, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource<object?> _disposeCompletion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly GameAgentTraceFileOptions _options;
    private readonly FileStream _stream;
    private long _length;
    private int _lifecycle;
    private int _faulted;
    private Exception? _disposeFailure;

    public JsonLinesGameAgentTraceSink(string path, GameAgentTraceFileOptions? options = null)
    {
        Path = NormalizePath(path);
        _options = (options ?? new GameAgentTraceFileOptions()).CopyAndValidate();
        FileStream? openedStream = null;
        try
        {
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            openedStream = Open(Path, _options.Mode);
            _length = openedStream.Length;
            if (_length > _options.MaximumFileBytes)
            {
                throw new GameAgentTraceStorageException(
                    GameAgentTraceStorageError.LimitExceeded,
                    "The existing trace file exceeds the configured size limit.");
            }

            if (_options.Mode == GameAgentTraceFileMode.Append && _length > 0)
            {
                openedStream.Position = _length - 1;
                if (openedStream.ReadByte() != (byte)'\n')
                {
                    throw new GameAgentTraceStorageException(
                        GameAgentTraceStorageError.CorruptRecording,
                        "Append mode requires an existing trace that ends with a complete line.");
                }
            }

            openedStream.Position = _length;
            _stream = openedStream;
            openedStream = null;
        }
        catch (GameAgentTraceStorageException)
        {
            openedStream?.Dispose();
            throw;
        }
        catch (IOException exception) when (File.Exists(Path) && _options.Mode == GameAgentTraceFileMode.CreateNew)
        {
            openedStream?.Dispose();
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.AlreadyExists,
                "The trace file already exists.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            openedStream?.Dispose();
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace file could not be opened.",
                exception);
        }
    }

    public string Path { get; }

    public async ValueTask WriteAsync(GameAgentTraceEntry entry, CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            throw new ArgumentNullException(nameof(entry));
        }

        ThrowIfUnavailable();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(TraceLineDocument.Encode(entry), JsonOptions);
        if (bytes.Length > _options.MaximumLineBytes)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.LimitExceeded,
                "The serialized trace entry exceeds the configured line limit.");
        }

        var required = checked(bytes.Length + 1L);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_length + required > _options.MaximumFileBytes)
            {
                throw new GameAgentTraceStorageException(
                    GameAgentTraceStorageError.LimitExceeded,
                    "The trace file reached its configured size limit.");
            }

            var writeStarted = false;
            try
            {
                writeStarted = true;
                await _stream.WriteAsync(bytes, 0, bytes.Length, cancellationToken).ConfigureAwait(false);
                await _stream.WriteAsync(new byte[] { (byte)'\n' }, 0, 1, cancellationToken).ConfigureAwait(false);
                if (_options.FlushEachEntry)
                {
                    await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                _length += required;
            }
            catch
            {
                if (writeStarted)
                {
                    Volatile.Write(ref _faulted, 1);
                }

                throw;
            }
        }
        catch (GameAgentTraceStorageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace entry could not be written.",
                exception);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _lifecycle, 1, 0) != 0)
        {
            _disposeCompletion.Task.GetAwaiter().GetResult();
            ThrowDisposeFailure();
            return;
        }

        Exception? failure = null;
        _gate.Wait();
        try
        {
            try
            {
                _stream.Flush();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }

            try
            {
                _stream.Dispose();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure ??= exception;
            }
        }
        finally
        {
            Volatile.Write(ref _lifecycle, 2);
            _gate.Release();
            CompleteDisposal(failure);
        }

        if (failure is not null)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace file could not be flushed while closing.",
                failure);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.CompareExchange(ref _lifecycle, 1, 0) != 0)
        {
            await _disposeCompletion.Task.ConfigureAwait(false);
            ThrowDisposeFailure();
            return;
        }

        Exception? failure = null;
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            try
            {
                await _stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure = exception;
            }

            try
            {
                _stream.Dispose();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failure ??= exception;
            }
        }
        finally
        {
            Volatile.Write(ref _lifecycle, 2);
            _gate.Release();
            CompleteDisposal(failure);
        }

        if (failure is not null)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace file could not be flushed while closing.",
                failure);
        }
    }

    private static FileStream Open(string path, GameAgentTraceFileMode mode)
    {
        var fileMode = mode switch
        {
            GameAgentTraceFileMode.CreateNew => FileMode.CreateNew,
            GameAgentTraceFileMode.Overwrite => FileMode.Create,
            GameAgentTraceFileMode.Append => FileMode.OpenOrCreate,
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return new FileStream(path, fileMode, FileAccess.ReadWrite, FileShare.Read, 16_384, useAsync: true);
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOf('\0') >= 0)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.InvalidPath,
                "A non-empty trace path is required.");
        }

        try
        {
            return System.IO.Path.GetFullPath(path);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.InvalidPath,
                "The trace path is invalid.",
                exception);
        }
    }

    private static JsonSerializerOptions CreateJsonOptions() => new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private void ThrowIfUnavailable()
    {
        if (Volatile.Read(ref _lifecycle) != 0)
        {
            throw new ObjectDisposedException(nameof(JsonLinesGameAgentTraceSink));
        }

        if (Volatile.Read(ref _faulted) != 0)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace sink cannot continue after an interrupted or failed write.");
        }
    }

    private void CompleteDisposal(Exception? failure)
    {
        _disposeFailure = failure;
        _disposeCompletion.TrySetResult(null);
    }

    private void ThrowDisposeFailure()
    {
        if (_disposeFailure is { } failure)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace file could not be flushed while closing.",
                failure);
        }
    }
}

public static class GameAgentTraceRecordingReader
{
    private static readonly HashSet<string> TraceProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "sequence",
        "kind",
        "sessionId",
        "actorId",
        "inputId",
        "timelineId",
        "tick",
        "calendarJson",
        "operationalTimestamp",
        "details",
    };
    private static readonly string[] RequiredTraceProperties =
    {
        "schemaVersion",
        "sequence",
        "kind",
        "sessionId",
        "actorId",
        "inputId",
        "timelineId",
        "tick",
        "operationalTimestamp",
        "details",
    };
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
    };

    public static async Task<GameAgentTraceRecording> ReadAsync(
        string path,
        GameAgentTraceReadOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var validated = (options ?? new GameAgentTraceReadOptions()).CopyAndValidate();
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new GameAgentTraceStorageException(GameAgentTraceStorageError.InvalidPath, "A trace path is required.");
        }

        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 16_384, useAsync: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.Storage,
                "The trace file could not be opened.",
                exception);
        }

        using (stream)
        {
            if (stream.Length > validated.MaximumFileBytes)
            {
                throw new GameAgentTraceStorageException(
                    GameAgentTraceStorageError.LimitExceeded,
                    "The trace file exceeds the configured size limit.");
            }

            var entries = new List<GameAgentTraceEntry>();
            var line = new ArrayBufferWriter<byte>(Math.Min(validated.MaximumLineBytes, 16_384));
            var buffer = ArrayPool<byte>.Shared.Rent(16_384);
            var lineNumber = 0;
            var ignoredTail = false;
            long bytesRead = 0;
            try
            {
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    bytesRead = checked(bytesRead + read);
                    for (var index = 0; index < read; index++)
                    {
                        var value = buffer[index];
                        if (value == (byte)'\n')
                        {
                            lineNumber++;
                            DecodeCompleteLine(line.WrittenSpan, lineNumber, entries, validated);
                            line = new ArrayBufferWriter<byte>(Math.Min(validated.MaximumLineBytes, 16_384));
                            continue;
                        }

                        if (line.WrittenCount >= validated.MaximumLineBytes)
                        {
                            throw new GameAgentTraceStorageException(
                                GameAgentTraceStorageError.LimitExceeded,
                                $"Trace line {lineNumber + 1} exceeds the configured line limit.");
                        }

                        line.GetSpan(1)[0] = value;
                        line.Advance(1);
                    }
                }

                if (line.WrittenCount > 0)
                {
                    lineNumber++;
                    try
                    {
                        DecodeCompleteLine(line.WrittenSpan, lineNumber, entries, validated);
                    }
                    catch (GameAgentTraceStorageException exception)
                        when (validated.AllowTruncatedFinalLine
                            && exception.Code == GameAgentTraceStorageError.CorruptRecording)
                    {
                        ignoredTail = true;
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            return new GameAgentTraceRecording(
                new ReadOnlyCollection<GameAgentTraceEntry>(entries),
                ignoredTail,
                bytesRead);
        }
    }

    private static void DecodeCompleteLine(
        ReadOnlySpan<byte> line,
        int lineNumber,
        List<GameAgentTraceEntry> entries,
        GameAgentTraceReadOptions options)
    {
        if (line.Length == 0 || line.SequenceEqual(new byte[] { (byte)'\r' }))
        {
            return;
        }

        if (line.Length > 0 && line[^1] == (byte)'\r')
        {
            line = line[..^1];
        }

        if (entries.Count >= options.MaximumEntries)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.LimitExceeded,
                "The trace contains more entries than the configured limit.");
        }

        try
        {
            _ = StrictUtf8.GetString(line.ToArray());
            using (var parsed = JsonDocument.Parse(line.ToArray(), new JsonDocumentOptions { MaxDepth = 128 }))
            {
                ValidateEnvelope(parsed.RootElement);
            }

            var document = JsonSerializer.Deserialize<TraceLineDocument>(line, JsonOptions)
                ?? throw new JsonException("The trace line is empty.");
            entries.Add(document.Decode());
        }
        catch (Exception exception) when (exception is JsonException or DecoderFallbackException or ArgumentException)
        {
            throw new GameAgentTraceStorageException(
                GameAgentTraceStorageError.CorruptRecording,
                $"Trace line {lineNumber} is invalid.",
                exception);
        }
    }

    private static void ValidateEnvelope(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("A trace line must be a JSON object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new JsonException($"Duplicate trace property '{property.Name}' is not allowed.");
            }

            if (!TraceProperties.Contains(property.Name))
            {
                throw new JsonException($"Unknown trace property '{property.Name}'.");
            }
        }

        foreach (var required in RequiredTraceProperties)
        {
            if (!names.Contains(required))
            {
                throw new JsonException($"Required trace property '{required}' is missing.");
            }
        }
    }
}

internal sealed class TraceLineDocument
{
    public int SchemaVersion { get; set; }
    public long Sequence { get; set; }
    public string? Kind { get; set; }
    public string? SessionId { get; set; }
    public string? ActorId { get; set; }
    public string? InputId { get; set; }
    public string? TimelineId { get; set; }
    public long Tick { get; set; }
    public string? CalendarJson { get; set; }
    public DateTimeOffset OperationalTimestamp { get; set; }
    public JsonElement Details { get; set; }

    public static TraceLineDocument Encode(GameAgentTraceEntry entry)
    {
        using var details = JsonDocument.Parse(entry.DetailsJson, new JsonDocumentOptions { MaxDepth = 128 });
        return new TraceLineDocument
        {
            SchemaVersion = 1,
            Sequence = entry.Sequence,
            Kind = entry.Kind,
            SessionId = entry.SessionId,
            ActorId = entry.ActorId,
            InputId = entry.InputId,
            TimelineId = entry.Moment.TimelineId,
            Tick = entry.Moment.Tick,
            CalendarJson = entry.Moment.CalendarJson,
            OperationalTimestamp = entry.OperationalTimestamp,
            Details = details.RootElement.Clone(),
        };
    }

    public GameAgentTraceEntry Decode()
    {
        if (SchemaVersion != 1)
        {
            throw new JsonException("The trace schema version is not supported.");
        }

        if (Details.ValueKind is JsonValueKind.Undefined)
        {
            throw new JsonException("Trace details are required.");
        }

        return new GameAgentTraceEntry(
            Sequence,
            Kind ?? throw new JsonException("Trace kind is required."),
            SessionId ?? throw new JsonException("Session ID is required."),
            ActorId ?? throw new JsonException("Actor ID is required."),
            InputId ?? throw new JsonException("Input ID is required."),
            new GameMoment(
                TimelineId ?? throw new JsonException("Timeline ID is required."),
                Tick,
                CalendarJson),
            OperationalTimestamp,
            Details.GetRawText());
    }
}
