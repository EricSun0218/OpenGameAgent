#include "OpenGameAgentSubsystem.h"

#include "Async/Async.h"
#include "Dom/JsonObject.h"
#include "HttpModule.h"
#include "Interfaces/IHttpRequest.h"
#include "Interfaces/IHttpResponse.h"
#include "Misc/Guid.h"
#include "Misc/ScopeLock.h"
#include "OpenGameAgentSseParser.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"

struct FOpenGameAgentStreamState
{
    FOpenGameAgentStreamState(
        const int32 MaximumEventCharacters,
        const int32 MaximumResponseBytes)
        : Parser(MaximumEventCharacters, MaximumResponseBytes)
    {
    }

    FHttpRequestPtr Request;
    FOpenGameAgentSseParser Parser;
    FCriticalSection StreamLock;
    TArray<uint8> PendingBytes;
    FString TerminalResultJson;
    int64 TotalReceivedBytes = 0;
    bool bStreamFailed = false;
    bool bDrainScheduled = false;

    bool EnqueueBytes(const void* Data, const int64 Length, const int32 MaximumResponseBytes)
    {
        FScopeLock Guard(&StreamLock);
        if (bStreamFailed)
        {
            return false;
        }
        if (Data == nullptr || Length < 0 || Length > MaximumResponseBytes
            || TotalReceivedBytes + Length > MaximumResponseBytes)
        {
            bStreamFailed = true;
            if (bDrainScheduled)
            {
                return false;
            }
            bDrainScheduled = true;
            return true;
        }
        PendingBytes.Append(static_cast<const uint8*>(Data), static_cast<int32>(Length));
        TotalReceivedBytes += Length;
        if (bDrainScheduled)
        {
            return false;
        }
        bDrainScheduled = true;
        return true;
    }

    bool TakePendingBytes(TArray<uint8>& Bytes)
    {
        FScopeLock Guard(&StreamLock);
        if (bStreamFailed)
        {
            return false;
        }
        Bytes = MoveTemp(PendingBytes);
        PendingBytes.Reset();
        bDrainScheduled = false;
        return true;
    }
};

struct FOpenGameAgentRunState final : FOpenGameAgentStreamState
{
    FOpenGameAgentRunState(
        const FString& InInputId,
        const int32 MaximumEventCharacters,
        const int32 MaximumResponseBytes)
        : FOpenGameAgentStreamState(MaximumEventCharacters, MaximumResponseBytes)
        , InputId(InInputId)
    {
    }

    FString InputId;
};

struct FOpenGameAgentActionStreamState final : FOpenGameAgentStreamState
{
    FOpenGameAgentActionStreamState(
        const FString& InStreamId,
        const FString& InSessionId,
        const FString& InActorId,
        const int32 MaximumEventCharacters,
        const int32 MaximumResponseBytes)
        : FOpenGameAgentStreamState(MaximumEventCharacters, MaximumResponseBytes)
        , StreamId(InStreamId)
        , SessionId(InSessionId)
        , ActorId(InActorId)
    {
    }

    FString StreamId;
    FString SessionId;
    FString ActorId;
};

struct FOpenGameAgentBoundedResponseState final
{
    explicit FOpenGameAgentBoundedResponseState(const int32 InMaximumBytes)
        : MaximumBytes(InMaximumBytes)
    {
    }

    bool Append(const void* Data, const int64 Length)
    {
        FScopeLock Guard(&Lock);
        if (bOverflow
            || Data == nullptr
            || Length < 0
            || Length > MaximumBytes
            || Bytes.Num() > MaximumBytes - Length)
        {
            bOverflow = true;
            return false;
        }

        Bytes.Append(static_cast<const uint8*>(Data), static_cast<int32>(Length));
        return true;
    }

    bool Take(FString& Text)
    {
        FScopeLock Guard(&Lock);
        if (bOverflow)
        {
            return false;
        }

        if (Bytes.IsEmpty())
        {
            Text.Reset();
            return true;
        }

        const FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Bytes.GetData()), Bytes.Num());
        Text = FString(Converted.Length(), Converted.Get());
        const FTCHARToUTF8 RoundTrip(*Text);
        if (RoundTrip.Length() != Bytes.Num()
            || FMemory::Memcmp(RoundTrip.Get(), Bytes.GetData(), Bytes.Num()) != 0)
        {
            Text.Reset();
            return false;
        }
        Bytes.Reset();
        return true;
    }

private:
    int32 MaximumBytes;
    FCriticalSection Lock;
    TArray<uint8> Bytes;
    bool bOverflow = false;
};

namespace
{
bool ParseJsonValue(const FString& Json, TSharedPtr<FJsonValue>& Value)
{
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    return FJsonSerializer::Deserialize(Reader, Value) && Value.IsValid();
}

bool ParseJsonObject(const FString& Json, TSharedPtr<FJsonObject>& Value)
{
    const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
    return FJsonSerializer::Deserialize(Reader, Value) && Value.IsValid();
}

bool IsBoundedIdentifier(const FString& Value, const int32 MaximumCharacters = 1024)
{
    if (Value.IsEmpty() || Value.Len() > MaximumCharacters)
    {
        return false;
    }
    for (const TCHAR Character : Value)
    {
        if (FChar::IsControl(Character))
        {
            return false;
        }
    }
    return true;
}

FString NormalizeBaseUrl(const FString& Value, const bool bAllowInsecureRemoteHttp, FString& Error)
{
    FString Url = Value.TrimStartAndEnd();
    if (Url.IsEmpty() || Url.Len() > 4096)
    {
        Error = TEXT("The server base URL is empty or exceeds its size limit.");
        return FString();
    }
    for (const TCHAR Character : Url)
    {
        if (FChar::IsControl(Character))
        {
            Error = TEXT("The server base URL contains an invalid control character.");
            return FString();
        }
    }
    while (Url.EndsWith(TEXT("/")))
    {
        Url.LeftChopInline(1, EAllowShrinking::No);
    }

    const bool bHttps = Url.StartsWith(TEXT("https://"), ESearchCase::IgnoreCase);
    const bool bHttp = Url.StartsWith(TEXT("http://"), ESearchCase::IgnoreCase);
    if (!bHttps && !bHttp)
    {
        Error = TEXT("The server base URL must use HTTP or HTTPS.");
        return FString();
    }
    if (Url.Contains(TEXT("?")) || Url.Contains(TEXT("#")))
    {
        Error = TEXT("The server base URL cannot contain a query or fragment.");
        return FString();
    }

    const int32 AuthorityStart = Url.Find(TEXT("://")) + 3;
    int32 AuthorityEnd = Url.Find(TEXT("/"), ESearchCase::CaseSensitive, ESearchDir::FromStart, AuthorityStart);
    if (AuthorityEnd == INDEX_NONE)
    {
        AuthorityEnd = Url.Len();
    }
    const FString Authority = Url.Mid(AuthorityStart, AuthorityEnd - AuthorityStart);
    if (Authority.IsEmpty() || Authority.Contains(TEXT("@")))
    {
        Error = TEXT("The server base URL contains an invalid authority.");
        return FString();
    }

    const bool bLoopback = Authority.Equals(TEXT("localhost"), ESearchCase::IgnoreCase)
        || Authority.StartsWith(TEXT("localhost:"), ESearchCase::IgnoreCase)
        || Authority.Equals(TEXT("127.0.0.1"))
        || Authority.StartsWith(TEXT("127.0.0.1:"))
        || Authority.Equals(TEXT("[::1]"))
        || Authority.StartsWith(TEXT("[::1]:"));
    if (bHttp && !bLoopback && !bAllowInsecureRemoteHttp)
    {
        Error = TEXT("Remote agent servers require HTTPS unless insecure HTTP is explicitly enabled.");
        return FString();
    }
    return Url;
}

FString SerializeControl(
    const FString& SessionId,
    const FString& ActorId,
    const FString* PayloadJson,
    FString& Error)
{
    TSharedPtr<FJsonValue> Payload;
    if (PayloadJson != nullptr && !ParseJsonValue(*PayloadJson, Payload))
    {
        Error = TEXT("The steering payload must be valid JSON.");
        return FString();
    }

    const TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
    Document->SetStringField(TEXT("sessionId"), SessionId);
    Document->SetStringField(TEXT("actorId"), ActorId);
    if (PayloadJson != nullptr)
    {
        Document->SetField(TEXT("payload"), Payload);
    }

    FString Result;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Result);
    if (!FJsonSerializer::Serialize(Document, Writer))
    {
        Error = TEXT("The control request could not be serialized.");
        return FString();
    }
    return Result;
}

bool ParseAccepted(const FString& Json, bool& bAccepted)
{
    TSharedPtr<FJsonObject> Document;
    return ParseJsonObject(Json, Document) && Document->TryGetBoolField(TEXT("accepted"), bAccepted);
}

bool ValidateEventStreamResponse(const FHttpResponsePtr& Response, FString& Error)
{
    if (!Response.IsValid())
    {
        return false;
    }
    const int32 Status = Response->GetResponseCode();
    if (Status < 200 || Status > 299)
    {
        Error = FString::Printf(TEXT("The agent server returned HTTP %d."), Status);
        return false;
    }
    const FString ContentType = Response->GetHeader(TEXT("Content-Type"));
    if (!ContentType.StartsWith(TEXT("text/event-stream"), ESearchCase::IgnoreCase))
    {
        Error = TEXT("The agent server returned an unexpected content type.");
        return false;
    }
    return true;
}
}

bool UOpenGameAgentSubsystem::ConfigureRemote(
    const FString& ServerBaseUrl,
    const FString& ApiKey,
    const bool bAllowInsecureRemoteHttp,
    FString& Error)
{
    if (!IsInGameThread())
    {
        Error = TEXT("ConfigureRemote must run on the Unreal game thread.");
        return false;
    }
    if (!ActiveRuns.IsEmpty() || !ActiveRequests.IsEmpty())
    {
        Error = TEXT("The server cannot be replaced while requests are active.");
        return false;
    }
    bool bInvalidCredential = ApiKey.Len() > 65536;
    for (const TCHAR Character : ApiKey)
    {
        if (FChar::IsControl(Character))
        {
            bInvalidCredential = true;
            break;
        }
    }
    if (bInvalidCredential)
    {
        Error = TEXT("The server credential is invalid or exceeds its size limit.");
        return false;
    }

    FString Normalized = NormalizeBaseUrl(ServerBaseUrl, bAllowInsecureRemoteHttp, Error);
    if (Normalized.IsEmpty())
    {
        return false;
    }

    BaseUrl = MoveTemp(Normalized);
    AuthorizationValue = ApiKey.IsEmpty() ? FString() : TEXT("Bearer ") + ApiKey;
    bConfigured = true;
    bShuttingDown = false;
    Error.Reset();
    return true;
}

bool UOpenGameAgentSubsystem::RunJson(const FString& InputJson, FString& InputId, FString& Error)
{
    if (!IsInGameThread())
    {
        Error = TEXT("RunJson must run on the Unreal game thread.");
        return false;
    }
    if (!bConfigured || bShuttingDown)
    {
        Error = TEXT("Configure the OpenGameAgent subsystem before starting a run.");
        return false;
    }
    if (InputJson.Len() < 2 || InputJson.Len() > MaximumRequestCharacters)
    {
        Error = TEXT("The run request is empty or exceeds its character limit.");
        return false;
    }
    if (ActiveRuns.Num() >= MaximumActiveRuns)
    {
        Error = TEXT("The Unreal adapter reached its active-run limit.");
        return false;
    }

    TSharedPtr<FJsonObject> Document;
    if (!ParseJsonObject(InputJson, Document)
        || !Document->TryGetStringField(TEXT("inputId"), InputId)
        || !IsBoundedIdentifier(InputId))
    {
        Error = TEXT("Canonical input JSON requires a bounded string inputId.");
        return false;
    }
    if (ActiveRuns.Contains(InputId))
    {
        Error = TEXT("The input ID is already active in this Unreal instance.");
        return false;
    }

    const TSharedRef<FOpenGameAgentRunState, ESPMode::ThreadSafe> State =
        MakeShared<FOpenGameAgentRunState, ESPMode::ThreadSafe>(
            InputId,
            MaximumEventCharacters,
            MaximumResponseBytes);
    State->Request = FHttpModule::Get().CreateRequest();
    State->Request->SetURL(BaseUrl + TEXT("/v1/run/stream"));
    State->Request->SetVerb(TEXT("POST"));
    State->Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
    State->Request->SetHeader(TEXT("Accept"), TEXT("text/event-stream"));
    if (!AuthorizationValue.IsEmpty())
    {
        State->Request->SetHeader(TEXT("Authorization"), AuthorizationValue);
    }
    State->Request->SetContentAsString(InputJson);

    const TWeakObjectPtr<UOpenGameAgentSubsystem> WeakThis(this);
    const TWeakPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe> WeakState = State;
    if (!State->Request->SetResponseBodyReceiveStreamDelegateV2(
        FHttpRequestStreamDelegateV2::CreateLambda(
        [WeakThis, WeakState, CapturedInputId = InputId](void* Data, int64& Length)
        {
            const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe> State = WeakState.Pin();
            if (!State.IsValid()
                || !State->EnqueueBytes(Data, Length, UOpenGameAgentSubsystem::MaximumResponseBytes))
            {
                return;
            }
            const auto Dispatch = [WeakThis, CapturedInputId]()
            {
                if (WeakThis.IsValid())
                {
                    WeakThis->HandleProgress(CapturedInputId);
                }
            };
            if (IsInGameThread())
            {
                Dispatch();
            }
            else
            {
                AsyncTask(ENamedThreads::GameThread, Dispatch);
            }
        })))
    {
        Error = TEXT("The Unreal HTTP backend cannot stream the response body.");
        return false;
    }
    State->Request->OnProcessRequestComplete().BindLambda(
        [WeakThis, CapturedInputId = InputId](FHttpRequestPtr, FHttpResponsePtr, const bool bSucceeded)
        {
            const auto Dispatch = [WeakThis, CapturedInputId, bSucceeded]()
            {
                if (WeakThis.IsValid())
                {
                    WeakThis->HandleComplete(CapturedInputId, bSucceeded);
                }
            };
            if (IsInGameThread())
            {
                Dispatch();
            }
            else
            {
                AsyncTask(ENamedThreads::GameThread, Dispatch);
            }
        });

    ActiveRuns.Add(InputId, State);
    if (!State->Request->ProcessRequest())
    {
        ActiveRuns.Remove(InputId);
        Error = TEXT("Unreal could not dispatch the agent server request.");
        return false;
    }

    Error.Reset();
    return true;
}

bool UOpenGameAgentSubsystem::SteerActor(
    const FString& SessionId,
    const FString& ActorId,
    const FString& PayloadJson,
    FString& Error)
{
    return SendControl(TEXT("steer"), TEXT("/v1/control/steer"), SessionId, ActorId, &PayloadJson, Error);
}

bool UOpenGameAgentSubsystem::AbortActor(
    const FString& SessionId,
    const FString& ActorId,
    FString& Error)
{
    return SendControl(TEXT("abort"), TEXT("/v1/control/abort"), SessionId, ActorId, nullptr, Error);
}

bool UOpenGameAgentSubsystem::CancelRun(const FString& InputId)
{
    if (!IsInGameThread())
    {
        return false;
    }
    const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>* State = ActiveRuns.Find(InputId);
    if (State == nullptr)
    {
        return false;
    }
    const FHttpRequestPtr Request = (*State)->Request;
    ActiveRuns.Remove(InputId);
    Request->CancelRequest();
    OnRunFailed.Broadcast(InputId, TEXT("canceled"));
    return true;
}

bool UOpenGameAgentSubsystem::ReadServerCapabilities(FString& Error)
{
    return SendQueryRequest(
        TEXT("capabilities"),
        TEXT("/v1/capabilities"),
        FString(),
        FString(),
        FString(),
        Error);
}

bool UOpenGameAgentSubsystem::ReadUsage(
    const FString& SessionId,
    const FString& ActorId,
    FString& Error)
{
    const FString Body = SerializeControl(SessionId, ActorId, nullptr, Error);
    if (Body.IsEmpty())
    {
        return false;
    }
    return SendQueryRequest(TEXT("usage"), TEXT("/v1/usage"), SessionId, ActorId, Body, Error);
}

bool UOpenGameAgentSubsystem::ReadTranscript(
    const FString& SessionId,
    const FString& ActorId,
    const int32 PageSize,
    const FString& Cursor,
    FString& Error)
{
    if (PageSize < 1 || PageSize > 256)
    {
        Error = TEXT("The transcript page size must be between 1 and 256.");
        return false;
    }
    if (Cursor.Len() > 256)
    {
        Error = TEXT("The transcript cursor exceeds its size limit.");
        return false;
    }
    for (const TCHAR Character : Cursor)
    {
        if (FChar::IsControl(Character))
        {
            Error = TEXT("The transcript cursor contains an invalid control character.");
            return false;
        }
    }

    const TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
    Document->SetStringField(TEXT("sessionId"), SessionId);
    Document->SetStringField(TEXT("actorId"), ActorId);
    Document->SetNumberField(TEXT("pageSize"), PageSize);
    if (Cursor.IsEmpty())
    {
        Document->SetField(TEXT("cursor"), MakeShared<FJsonValueNull>());
    }
    else
    {
        Document->SetStringField(TEXT("cursor"), Cursor);
    }
    FString Body;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Body);
    if (!FJsonSerializer::Serialize(Document, Writer))
    {
        Error = TEXT("The transcript query could not be serialized.");
        return false;
    }
    return SendQueryRequest(
        TEXT("transcript"),
        TEXT("/v1/transcript"),
        SessionId,
        ActorId,
        Body,
        Error);
}

bool UOpenGameAgentSubsystem::ReadImageAttachment(
    const FString& SessionId,
    const FString& ActorId,
    const FString& AttachmentId,
    FString& Error)
{
    if (!IsBoundedIdentifier(AttachmentId, 256))
    {
        Error = TEXT("A bounded attachment ID is required.");
        return false;
    }

    const TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
    Document->SetStringField(TEXT("sessionId"), SessionId);
    Document->SetStringField(TEXT("actorId"), ActorId);
    Document->SetStringField(TEXT("attachmentId"), AttachmentId);
    FString Body;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Body);
    if (!FJsonSerializer::Serialize(Document, Writer))
    {
        Error = TEXT("The attachment query could not be serialized.");
        return false;
    }
    return SendQueryRequest(
        TEXT("attachment"),
        TEXT("/v1/attachments/read"),
        SessionId,
        ActorId,
        Body,
        Error);
}

bool UOpenGameAgentSubsystem::ClaimActions(
    const FString& SessionId,
    const FString& ActorId,
    const int32 Limit,
    FString& Error)
{
    if (Limit < 1 || Limit > 256)
    {
        Error = TEXT("The action claim limit must be between 1 and 256.");
        return false;
    }

    const TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
    Document->SetStringField(TEXT("sessionId"), SessionId);
    Document->SetStringField(TEXT("actorId"), ActorId);
    Document->SetNumberField(TEXT("limit"), Limit);
    FString Body;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Body);
    if (!FJsonSerializer::Serialize(Document, Writer))
    {
        Error = TEXT("The action claim could not be serialized.");
        return false;
    }
    return SendActionRequest(TEXT("claim"), TEXT("/v1/actions/claim"), SessionId, ActorId, Body, Error);
}

bool UOpenGameAgentSubsystem::SubmitActionReceiptJson(
    const FString& ReceiptRequestJson,
    FString& Error)
{
    if (ReceiptRequestJson.Len() < 2 || ReceiptRequestJson.Len() > MaximumRequestCharacters)
    {
        Error = TEXT("The action receipt request is empty or exceeds its size limit.");
        return false;
    }
    TSharedPtr<FJsonObject> Document;
    FString SessionId;
    FString ActorId;
    FString OperationId;
    if (!ParseJsonObject(ReceiptRequestJson, Document)
        || !Document->TryGetStringField(TEXT("sessionId"), SessionId)
        || !Document->TryGetStringField(TEXT("actorId"), ActorId)
        || !Document->TryGetStringField(TEXT("operationId"), OperationId)
        || !IsBoundedIdentifier(OperationId, 16384))
    {
        Error = TEXT("The canonical receipt request requires bounded session, actor, and operation IDs.");
        return false;
    }
    return SendActionRequest(
        TEXT("receipt"),
        TEXT("/v1/actions/receipt"),
        SessionId,
        ActorId,
        ReceiptRequestJson,
        Error);
}

bool UOpenGameAgentSubsystem::ReconcileAction(
    const FString& SessionId,
    const FString& ActorId,
    const FString& OperationId,
    FString& Error)
{
    if (!IsBoundedIdentifier(OperationId, 16384))
    {
        Error = TEXT("A bounded operation ID is required.");
        return false;
    }

    const TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
    Document->SetStringField(TEXT("sessionId"), SessionId);
    Document->SetStringField(TEXT("actorId"), ActorId);
    Document->SetStringField(TEXT("operationId"), OperationId);
    FString Body;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Body);
    if (!FJsonSerializer::Serialize(Document, Writer))
    {
        Error = TEXT("The action reconciliation request could not be serialized.");
        return false;
    }
    return SendActionRequest(
        TEXT("reconcile"),
        TEXT("/v1/actions/reconcile"),
        SessionId,
        ActorId,
        Body,
        Error);
}

bool UOpenGameAgentSubsystem::StartActionStream(
    const FString& SessionId,
    const FString& ActorId,
    const int32 Limit,
    FString& StreamId,
    FString& Error)
{
    if (!IsInGameThread())
    {
        Error = TEXT("Action streams must start on the Unreal game thread.");
        return false;
    }
    if (!bConfigured || bShuttingDown)
    {
        Error = TEXT("Configure the OpenGameAgent subsystem before starting an action stream.");
        return false;
    }
    if (!IsBoundedIdentifier(SessionId) || !IsBoundedIdentifier(ActorId))
    {
        Error = TEXT("A bounded session and actor ID are required.");
        return false;
    }
    if (Limit < 1 || Limit > 256)
    {
        Error = TEXT("The action stream limit must be between 1 and 256.");
        return false;
    }
    if (ActiveActionStreams.Num() >= MaximumActiveActionStreams)
    {
        Error = TEXT("The Unreal adapter reached its active action-stream limit.");
        return false;
    }

    const TSharedRef<FJsonObject> Document = MakeShared<FJsonObject>();
    Document->SetStringField(TEXT("sessionId"), SessionId);
    Document->SetStringField(TEXT("actorId"), ActorId);
    Document->SetNumberField(TEXT("limit"), Limit);
    FString Body;
    const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
        TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&Body);
    if (!FJsonSerializer::Serialize(Document, Writer))
    {
        Error = TEXT("The action stream request could not be serialized.");
        return false;
    }

    StreamId = FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    const TSharedRef<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe> State =
        MakeShared<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>(
            StreamId,
            SessionId,
            ActorId,
            MaximumEventCharacters,
            MaximumResponseBytes);
    State->Request = FHttpModule::Get().CreateRequest();
    State->Request->SetURL(BaseUrl + TEXT("/v1/actions/stream"));
    State->Request->SetVerb(TEXT("POST"));
    State->Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
    State->Request->SetHeader(TEXT("Accept"), TEXT("text/event-stream"));
    if (!AuthorizationValue.IsEmpty())
    {
        State->Request->SetHeader(TEXT("Authorization"), AuthorizationValue);
    }
    State->Request->SetContentAsString(Body);

    const TWeakObjectPtr<UOpenGameAgentSubsystem> WeakThis(this);
    const TWeakPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe> WeakState = State;
    if (!State->Request->SetResponseBodyReceiveStreamDelegateV2(
        FHttpRequestStreamDelegateV2::CreateLambda(
        [WeakThis, WeakState, CapturedStreamId = StreamId](void* Data, int64& Length)
        {
            const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe> Stream = WeakState.Pin();
            if (!Stream.IsValid()
                || !Stream->EnqueueBytes(Data, Length, UOpenGameAgentSubsystem::MaximumResponseBytes))
            {
                return;
            }
            const auto Dispatch = [WeakThis, CapturedStreamId]()
            {
                if (WeakThis.IsValid())
                {
                    WeakThis->HandleActionStreamProgress(CapturedStreamId);
                }
            };
            if (IsInGameThread())
            {
                Dispatch();
            }
            else
            {
                AsyncTask(ENamedThreads::GameThread, Dispatch);
            }
        })))
    {
        Error = TEXT("The Unreal HTTP backend cannot stream action deliveries.");
        return false;
    }
    State->Request->OnProcessRequestComplete().BindLambda(
        [WeakThis, CapturedStreamId = StreamId](FHttpRequestPtr, FHttpResponsePtr, const bool bSucceeded)
        {
            const auto Dispatch = [WeakThis, CapturedStreamId, bSucceeded]()
            {
                if (WeakThis.IsValid())
                {
                    WeakThis->HandleActionStreamComplete(CapturedStreamId, bSucceeded);
                }
            };
            if (IsInGameThread())
            {
                Dispatch();
            }
            else
            {
                AsyncTask(ENamedThreads::GameThread, Dispatch);
            }
        });

    ActiveActionStreams.Add(StreamId, State);
    if (!State->Request->ProcessRequest())
    {
        ActiveActionStreams.Remove(StreamId);
        StreamId.Reset();
        Error = TEXT("Unreal could not dispatch the action stream request.");
        return false;
    }

    Error.Reset();
    return true;
}

bool UOpenGameAgentSubsystem::StopActionStream(const FString& StreamId)
{
    if (!IsInGameThread())
    {
        return false;
    }
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>* Found =
        ActiveActionStreams.Find(StreamId);
    if (Found == nullptr)
    {
        return false;
    }
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe> State = *Found;
    ActiveActionStreams.Remove(StreamId);
    State->Request->CancelRequest();
    OnActionStreamClosed.Broadcast(StreamId, State->SessionId, State->ActorId, FString());
    return true;
}

int32 UOpenGameAgentSubsystem::GetActiveRunCount() const
{
    return ActiveRuns.Num();
}

int32 UOpenGameAgentSubsystem::GetActiveActionStreamCount() const
{
    return ActiveActionStreams.Num();
}

void UOpenGameAgentSubsystem::Deinitialize()
{
    bShuttingDown = true;
    TArray<FHttpRequestPtr> Requests;
    Requests.Reserve(ActiveRuns.Num() + ActiveActionStreams.Num() + ActiveRequests.Num());
    for (const TPair<FString, TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>>& Pair : ActiveRuns)
    {
        Requests.Add(Pair.Value->Request);
    }
    for (const TPair<FString, TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>>& Pair : ActiveActionStreams)
    {
        Requests.Add(Pair.Value->Request);
    }
    for (const FHttpRequestPtr& Request : ActiveRequests)
    {
        Requests.Add(Request);
    }
    ActiveRuns.Reset();
    ActiveActionStreams.Reset();
    ActiveRequests.Reset();
    for (const FHttpRequestPtr& Request : Requests)
    {
        Request->CancelRequest();
    }
    AuthorizationValue.Reset();
    bConfigured = false;
    Super::Deinitialize();
}

void UOpenGameAgentSubsystem::HandleProgress(const FString& InputId)
{
    const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>* Found = ActiveRuns.Find(InputId);
    if (Found == nullptr)
    {
        return;
    }
    const FHttpResponsePtr Response = (*Found)->Request->GetResponse();
    if (!Response.IsValid() || Response->GetResponseCode() == 0)
    {
        return;
    }
    FString Error;
    if (!ValidateEventStreamResponse(Response, Error))
    {
        FailRun(InputId, Error);
        return;
    }
    if (!FeedAvailableResponseBytes(InputId, *Found, Error))
    {
        FailRun(InputId, Error);
    }
}

void UOpenGameAgentSubsystem::HandleComplete(const FString& InputId, const bool bConnectedSuccessfully)
{
    const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>* Found = ActiveRuns.Find(InputId);
    if (Found == nullptr)
    {
        return;
    }
    const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe> State = *Found;
    if (!bConnectedSuccessfully || !State->Request->GetResponse().IsValid())
    {
        FailRun(InputId, TEXT("The agent server request did not complete successfully."));
        return;
    }

    FString Error;
    if (!ValidateEventStreamResponse(State->Request->GetResponse(), Error))
    {
        FailRun(InputId, Error);
        return;
    }

    if (!FeedAvailableResponseBytes(InputId, State, Error))
    {
        FailRun(InputId, Error);
        return;
    }
    if (!State->Parser.Finish(
        [this, &InputId, &State](const FString& EventName, const FString& Json)
        {
            if (EventName == TEXT("result"))
            {
                State->TerminalResultJson = Json;
            }
            else
            {
                OnRunEvent.Broadcast(InputId, EventName, Json);
            }
        },
        Error))
    {
        FailRun(InputId, Error);
        return;
    }
    ActiveRuns.Remove(InputId);
    OnRunCompleted.Broadcast(InputId, State->TerminalResultJson);
}

bool UOpenGameAgentSubsystem::FeedAvailableResponseBytes(
    const FString& InputId,
    const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>& State,
    FString& Error)
{
    TArray<uint8> NewBytes;
    if (!State->TakePendingBytes(NewBytes))
    {
        Error = TEXT("The server stream exceeded its response byte limit.");
        return false;
    }
    if (NewBytes.IsEmpty())
    {
        return true;
    }
    return State->Parser.Feed(
        MakeArrayView(NewBytes),
        [this, &InputId, &State](const FString& EventName, const FString& Json)
        {
            if (EventName == TEXT("result"))
            {
                State->TerminalResultJson = Json;
            }
            else
            {
                OnRunEvent.Broadcast(InputId, EventName, Json);
            }
        },
        Error);
}

void UOpenGameAgentSubsystem::FailRun(const FString& InputId, const FString& Error)
{
    const TSharedPtr<FOpenGameAgentRunState, ESPMode::ThreadSafe>* State = ActiveRuns.Find(InputId);
    if (State == nullptr)
    {
        return;
    }
    const FHttpRequestPtr Request = (*State)->Request;
    ActiveRuns.Remove(InputId);
    Request->CancelRequest();
    OnRunFailed.Broadcast(InputId, Error.Left(4096));
}

void UOpenGameAgentSubsystem::HandleActionStreamProgress(const FString& StreamId)
{
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>* Found =
        ActiveActionStreams.Find(StreamId);
    if (Found == nullptr)
    {
        return;
    }
    const FHttpResponsePtr Response = (*Found)->Request->GetResponse();
    if (!Response.IsValid() || Response->GetResponseCode() == 0)
    {
        return;
    }
    FString Error;
    if (!ValidateEventStreamResponse(Response, Error)
        || !FeedAvailableActionStreamBytes(StreamId, *Found, Error))
    {
        FailActionStream(StreamId, Error);
    }
}

void UOpenGameAgentSubsystem::HandleActionStreamComplete(
    const FString& StreamId,
    const bool bConnectedSuccessfully)
{
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>* Found =
        ActiveActionStreams.Find(StreamId);
    if (Found == nullptr)
    {
        return;
    }
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe> State = *Found;
    if (!bConnectedSuccessfully || !State->Request->GetResponse().IsValid())
    {
        FailActionStream(StreamId, TEXT("The action stream did not complete successfully."));
        return;
    }

    FString Error;
    if (!ValidateEventStreamResponse(State->Request->GetResponse(), Error)
        || !FeedAvailableActionStreamBytes(StreamId, State, Error)
        || !State->Parser.FinishOpenStream(
            [this, &StreamId, &State](const FString& EventName, const FString& Json)
            {
                OnActionStreamEvent.Broadcast(
                    StreamId,
                    State->SessionId,
                    State->ActorId,
                    EventName,
                    Json);
            },
            Error))
    {
        FailActionStream(StreamId, Error);
        return;
    }

    ActiveActionStreams.Remove(StreamId);
    OnActionStreamClosed.Broadcast(StreamId, State->SessionId, State->ActorId, FString());
}

bool UOpenGameAgentSubsystem::FeedAvailableActionStreamBytes(
    const FString& StreamId,
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>& State,
    FString& Error)
{
    TArray<uint8> NewBytes;
    if (!State->TakePendingBytes(NewBytes))
    {
        Error = TEXT("The action stream exceeded its response byte limit.");
        return false;
    }
    if (NewBytes.IsEmpty())
    {
        return true;
    }
    return State->Parser.Feed(
        MakeArrayView(NewBytes),
        [this, &StreamId, &State](const FString& EventName, const FString& Json)
        {
            OnActionStreamEvent.Broadcast(
                StreamId,
                State->SessionId,
                State->ActorId,
                EventName,
                Json);
        },
        Error);
}

void UOpenGameAgentSubsystem::FailActionStream(const FString& StreamId, const FString& Error)
{
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe>* Found =
        ActiveActionStreams.Find(StreamId);
    if (Found == nullptr)
    {
        return;
    }
    const TSharedPtr<FOpenGameAgentActionStreamState, ESPMode::ThreadSafe> State = *Found;
    ActiveActionStreams.Remove(StreamId);
    State->Request->CancelRequest();
    OnActionStreamClosed.Broadcast(
        StreamId,
        State->SessionId,
        State->ActorId,
        Error.Left(4096));
}

bool UOpenGameAgentSubsystem::SendControl(
    const FString& Operation,
    const FString& Path,
    const FString& SessionId,
    const FString& ActorId,
    const FString* PayloadJson,
    FString& Error)
{
    if (!IsInGameThread())
    {
        Error = TEXT("Control requests must run on the Unreal game thread.");
        return false;
    }
    if (!bConfigured || bShuttingDown)
    {
        Error = TEXT("Configure the OpenGameAgent subsystem before sending control requests.");
        return false;
    }
    if (ActiveRequests.Num() >= MaximumActiveRequests)
    {
        Error = TEXT("The Unreal adapter reached its control-request limit.");
        return false;
    }
    if (!IsBoundedIdentifier(SessionId) || !IsBoundedIdentifier(ActorId))
    {
        Error = TEXT("A bounded session and actor ID are required.");
        return false;
    }

    const FString Body = SerializeControl(SessionId, ActorId, PayloadJson, Error);
    if (Body.IsEmpty() || Body.Len() > MaximumRequestCharacters)
    {
        if (Error.IsEmpty())
        {
            Error = TEXT("The control request exceeded its size limit.");
        }
        return false;
    }

    const FHttpRequestRef Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(BaseUrl + Path);
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
    if (!AuthorizationValue.IsEmpty())
    {
        Request->SetHeader(TEXT("Authorization"), AuthorizationValue);
    }
    Request->SetContentAsString(Body);

    const TWeakObjectPtr<UOpenGameAgentSubsystem> WeakThis(this);
    const TSharedRef<FOpenGameAgentBoundedResponseState, ESPMode::ThreadSafe> ResponseState =
        MakeShared<FOpenGameAgentBoundedResponseState, ESPMode::ThreadSafe>(MaximumResponseBytes);
    if (!Request->SetResponseBodyReceiveStreamDelegateV2(
        FHttpRequestStreamDelegateV2::CreateLambda(
        [ResponseState](void* Data, int64& Length)
        {
            if (!ResponseState->Append(Data, Length))
            {
                Length = 0;
            }
        })))
    {
        Error = TEXT("The Unreal HTTP backend cannot stream the control response.");
        return false;
    }
    Request->OnProcessRequestComplete().BindLambda(
        [WeakThis, ResponseState, Operation, SessionId, ActorId](FHttpRequestPtr Completed, FHttpResponsePtr Response, const bool bSucceeded)
        {
            const auto Dispatch = [WeakThis, ResponseState, Operation, SessionId, ActorId, Completed, Response, bSucceeded]()
            {
                if (!WeakThis.IsValid())
                {
                    return;
                }
                WeakThis->ActiveRequests.Remove(Completed);
                bool bAccepted = false;
                FString ResponseJson;
                FString Failure;
                if (!ResponseState->Take(ResponseJson))
                {
                    Failure = TEXT("The control response exceeded its size limit.");
                }
                else if (!bSucceeded || !Response.IsValid())
                {
                    Failure = TEXT("The control request did not complete successfully.");
                }
                else if (Response->GetResponseCode() == 404)
                {
                    bAccepted = false;
                }
                else if (Response->GetResponseCode() < 200 || Response->GetResponseCode() > 299)
                {
                    Failure = FString::Printf(TEXT("The agent server returned HTTP %d."), Response->GetResponseCode());
                }
                else if (!ParseAccepted(ResponseJson, bAccepted))
                {
                    Failure = TEXT("The control response is invalid or exceeds its size limit.");
                }
                WeakThis->OnControlCompleted.Broadcast(Operation, SessionId, ActorId, bAccepted, Failure);
            };
            if (IsInGameThread())
            {
                Dispatch();
            }
            else
            {
                AsyncTask(ENamedThreads::GameThread, Dispatch);
            }
        });

    ActiveRequests.Add(Request);
    if (!Request->ProcessRequest())
    {
        ActiveRequests.Remove(Request);
        Error = TEXT("Unreal could not dispatch the control request.");
        return false;
    }
    Error.Reset();
    return true;
}

bool UOpenGameAgentSubsystem::SendActionRequest(
    const FString& Operation,
    const FString& Path,
    const FString& SessionId,
    const FString& ActorId,
    const FString& RequestJson,
    FString& Error)
{
    return SendJsonResponseRequest(
        Operation,
        Path,
        SessionId,
        ActorId,
        RequestJson,
        false,
        Error);
}

bool UOpenGameAgentSubsystem::SendQueryRequest(
    const FString& Operation,
    const FString& Path,
    const FString& SessionId,
    const FString& ActorId,
    const FString& RequestJson,
    FString& Error)
{
    return SendJsonResponseRequest(
        Operation,
        Path,
        SessionId,
        ActorId,
        RequestJson,
        true,
        Error);
}

bool UOpenGameAgentSubsystem::SendJsonResponseRequest(
    const FString& Operation,
    const FString& Path,
    const FString& SessionId,
    const FString& ActorId,
    const FString& RequestJson,
    const bool bQueryResponse,
    FString& Error)
{
    if (!IsInGameThread())
    {
        Error = TEXT("Server requests must run on the Unreal game thread.");
        return false;
    }
    if (!bConfigured || bShuttingDown)
    {
        Error = TEXT("Configure the OpenGameAgent subsystem before sending server requests.");
        return false;
    }
    if (ActiveRequests.Num() >= MaximumActiveRequests)
    {
        Error = TEXT("The Unreal adapter reached its server-request limit.");
        return false;
    }
    if (!IsBoundedIdentifier(Operation, 256)
        || Path.IsEmpty()
        || Path.Len() > 1024
        || !Path.StartsWith(TEXT("/v1/")))
    {
        Error = TEXT("The server request identity is invalid.");
        return false;
    }

    const bool bHasActorIdentity = !SessionId.IsEmpty() || !ActorId.IsEmpty();
    if (bHasActorIdentity)
    {
        if (!IsBoundedIdentifier(SessionId) || !IsBoundedIdentifier(ActorId))
        {
            Error = TEXT("A bounded session and actor ID are required.");
            return false;
        }
        if (RequestJson.Len() < 2 || RequestJson.Len() > MaximumRequestCharacters)
        {
            Error = TEXT("The server request is empty or exceeds its size limit.");
            return false;
        }
        TSharedPtr<FJsonObject> RequestDocument;
        FString BoundSession;
        FString BoundActor;
        if (!ParseJsonObject(RequestJson, RequestDocument)
            || !RequestDocument->TryGetStringField(TEXT("sessionId"), BoundSession)
            || !RequestDocument->TryGetStringField(TEXT("actorId"), BoundActor)
            || !BoundSession.Equals(SessionId, ESearchCase::CaseSensitive)
            || !BoundActor.Equals(ActorId, ESearchCase::CaseSensitive))
        {
            Error = TEXT("The request identity does not match its authorized session and actor.");
            return false;
        }
    }
    else if (!RequestJson.IsEmpty())
    {
        Error = TEXT("An identity-free request cannot contain a request body.");
        return false;
    }

    const FHttpRequestRef Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(BaseUrl + Path);
    Request->SetVerb(RequestJson.IsEmpty() ? TEXT("GET") : TEXT("POST"));
    Request->SetHeader(TEXT("Accept"), TEXT("application/json"));
    if (!RequestJson.IsEmpty())
    {
        Request->SetHeader(TEXT("Content-Type"), TEXT("application/json; charset=utf-8"));
        Request->SetContentAsString(RequestJson);
    }
    if (!AuthorizationValue.IsEmpty())
    {
        Request->SetHeader(TEXT("Authorization"), AuthorizationValue);
    }

    const TWeakObjectPtr<UOpenGameAgentSubsystem> WeakThis(this);
    const TSharedRef<FOpenGameAgentBoundedResponseState, ESPMode::ThreadSafe> ResponseState =
        MakeShared<FOpenGameAgentBoundedResponseState, ESPMode::ThreadSafe>(MaximumResponseBytes);
    if (!Request->SetResponseBodyReceiveStreamDelegateV2(
        FHttpRequestStreamDelegateV2::CreateLambda(
        [ResponseState](void* Data, int64& Length)
        {
            if (!ResponseState->Append(Data, Length))
            {
                Length = 0;
            }
        })))
    {
        Error = TEXT("The Unreal HTTP backend cannot stream the server response.");
        return false;
    }
    Request->OnProcessRequestComplete().BindLambda(
        [WeakThis, ResponseState, Operation, SessionId, ActorId, bQueryResponse](
            FHttpRequestPtr Completed,
            FHttpResponsePtr Response,
            const bool bSucceeded)
        {
            const auto Dispatch = [WeakThis, ResponseState, Operation, SessionId, ActorId, bQueryResponse, Completed, Response, bSucceeded]()
            {
                if (!WeakThis.IsValid())
                {
                    return;
                }
                WeakThis->ActiveRequests.Remove(Completed);
                FString Json;
                FString Failure;
                if (!ResponseState->Take(Json))
                {
                    Failure = TEXT("The server response is invalid or exceeded its size limit.");
                }
                else if (!bSucceeded || !Response.IsValid())
                {
                    Failure = TEXT("The server request did not complete successfully.");
                }
                else if (Response->GetResponseCode() < 200 || Response->GetResponseCode() > 299)
                {
                    Json.Reset();
                    Failure = FString::Printf(
                        TEXT("The agent server returned HTTP %d."),
                        Response->GetResponseCode());
                }
                else
                {
                    const FString ContentType = Response->GetHeader(TEXT("Content-Type"));
                    TSharedPtr<FJsonValue> Parsed;
                    if (!ContentType.StartsWith(TEXT("application/json"), ESearchCase::IgnoreCase)
                        || !ParseJsonValue(Json, Parsed))
                    {
                        Json.Reset();
                        Failure = TEXT("The server response is not bounded JSON.");
                    }
                }
                if (bQueryResponse)
                {
                    WeakThis->OnQueryResponse.Broadcast(Operation, SessionId, ActorId, Json, Failure);
                }
                else
                {
                    WeakThis->OnActionResponse.Broadcast(Operation, SessionId, ActorId, Json, Failure);
                }
            };
            if (IsInGameThread())
            {
                Dispatch();
            }
            else
            {
                AsyncTask(ENamedThreads::GameThread, Dispatch);
            }
        });

    ActiveRequests.Add(Request);
    if (!Request->ProcessRequest())
    {
        ActiveRequests.Remove(Request);
        Error = TEXT("Unreal could not dispatch the server request.");
        return false;
    }
    Error.Reset();
    return true;
}
