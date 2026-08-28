#include "OpenGameAgentSubsystem.h"

#include "Async/Async.h"
#include "Dom/JsonObject.h"
#include "HttpModule.h"
#include "Interfaces/IHttpResponse.h"
#include "OpenGameAgentSseDecoder.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"

namespace
{
    bool ParseObject(const FString& Json, TSharedPtr<FJsonObject>& OutObject)
    {
        const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
        return FJsonSerializer::Deserialize(Reader, OutObject) && OutObject.IsValid();
    }

    FString SerializeObject(const TSharedRef<FJsonObject>& Object)
    {
        FString Result;
        const TSharedRef<TJsonWriter<>> Writer = TJsonWriterFactory<>::Create(&Result);
        FJsonSerializer::Serialize(Object, Writer);
        return Result;
    }

    FString NewRequestId()
    {
        return FGuid::NewGuid().ToString(EGuidFormats::DigitsWithHyphensLower);
    }

    class FOpenGameAgentStreamState final : public TSharedFromThis<FOpenGameAgentStreamState, ESPMode::ThreadSafe>
    {
    public:
        FOpenGameAgentStreamState(const int32 MaximumBytes, TFunction<void(FOpenGameAgentDecodedEvent&&)> InEmit)
            : Decoder(MaximumBytes), Emit(MoveTemp(InEmit))
        {
        }

        bool Push(void* Data, int64& InOutLength)
        {
            FScopeLock Lock(&Gate);
            if (bFailed)
            {
                InOutLength = 0;
                return false;
            }
            TArray<FOpenGameAgentDecodedEvent> Events;
            FString Error;
            if (!Decoder.Push(static_cast<const uint8*>(Data), InOutLength, Events, Error))
            {
                Failure = Error;
                bFailed = true;
                InOutLength = 0;
                return false;
            }
            for (FOpenGameAgentDecodedEvent& Event : Events) Emit(MoveTemp(Event));
            return true;
        }

        bool Finish(FString& OutFailure)
        {
            FScopeLock Lock(&Gate);
            if (bFailed)
            {
                OutFailure = Failure;
                return false;
            }
            TArray<FOpenGameAgentDecodedEvent> Events;
            if (!Decoder.Finish(Events, OutFailure)) return false;
            for (FOpenGameAgentDecodedEvent& Event : Events) Emit(MoveTemp(Event));
            return true;
        }

    private:
        FCriticalSection Gate;
        FOpenGameAgentSseDecoder Decoder;
        TFunction<void(FOpenGameAgentDecodedEvent&&)> Emit;
        FString Failure;
        bool bFailed = false;
    };
}

bool UOpenGameAgentSubsystem::Configure(const FString& InServerUrl, const FString& InAuthenticationObjectJson)
{
    if (!ActiveRequests.IsEmpty()) return false;
    FString Normalized;
    if (!ValidateServerUrl(InServerUrl, Normalized)) return false;
    if (!InAuthenticationObjectJson.IsEmpty())
    {
        TSharedPtr<FJsonObject> Authentication;
        if (!ParseObject(InAuthenticationObjectJson, Authentication)) return false;
    }
    ServerUrl = MoveTemp(Normalized);
    AuthenticationObjectJson = InAuthenticationObjectJson;
    return true;
}

FString UOpenGameAgentSubsystem::StartRun(const FString& InputObjectJson, const FString& RunId)
{
    TSharedPtr<FJsonObject> Input;
    if (!ParseObject(InputObjectJson, Input)) return FString();
    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    Body->SetObjectField(TEXT("input"), Input);
    if (!RunId.IsEmpty())
    {
        if (RunId.Len() > 512) return FString();
        Body->SetStringField(TEXT("runId"), RunId);
    }
    return StartStream(TEXT("v1/runs/stream"), SerializeObject(Body));
}

FString UOpenGameAgentSubsystem::StreamActions(const FString& SessionObjectJson, const int32 Maximum)
{
    if (Maximum < 1 || Maximum > 32) return FString();
    TSharedPtr<FJsonObject> Session;
    if (!ParseObject(SessionObjectJson, Session)) return FString();
    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    Body->SetObjectField(TEXT("session"), Session);
    Body->SetNumberField(TEXT("maximum"), Maximum);
    return StartStream(TEXT("v1/actions/stream"), SerializeObject(Body));
}

FString UOpenGameAgentSubsystem::PostJson(const FString& Path, const FString& BodyObjectJson)
{
    return StartJsonRequest(Path, BodyObjectJson);
}

FString UOpenGameAgentSubsystem::Steer(
    const FString& SessionObjectJson,
    const FString& ExpectedRunCoordinateJson,
    const FString& InputObjectJson)
{
    return StartControl(TEXT("steer"), SessionObjectJson, ExpectedRunCoordinateJson, &InputObjectJson);
}

FString UOpenGameAgentSubsystem::FollowUp(
    const FString& SessionObjectJson,
    const FString& ExpectedRunCoordinateJson,
    const FString& InputObjectJson)
{
    return StartControl(TEXT("follow-up"), SessionObjectJson, ExpectedRunCoordinateJson, &InputObjectJson);
}

FString UOpenGameAgentSubsystem::Abort(const FString& SessionObjectJson, const FString& ExpectedRunCoordinateJson)
{
    return StartControl(TEXT("abort"), SessionObjectJson, ExpectedRunCoordinateJson, nullptr);
}

bool UOpenGameAgentSubsystem::CancelLocalRequest(const FString& RequestId)
{
    FHttpRequestPtr* Request = ActiveRequests.Find(RequestId);
    if (Request == nullptr) return false;
    (*Request)->CancelRequest();
    return true;
}

void UOpenGameAgentSubsystem::Deinitialize()
{
    TArray<FHttpRequestPtr> Requests;
    ActiveRequests.GenerateValueArray(Requests);
    ActiveRequests.Reset();
    for (const FHttpRequestPtr& Request : Requests)
    {
        if (Request.IsValid()) Request->CancelRequest();
    }
    AuthenticationObjectJson.Reset();
    Super::Deinitialize();
}

FString UOpenGameAgentSubsystem::StartStream(const FString& Path, const FString& BodyObjectJson)
{
    if (!ValidatePath(Path)) return FString();
    FString Body;
    if (!CreateAuthenticatedBody(BodyObjectJson, Body) || FTCHARToUTF8(*Body).Length() > MaximumRequestBytes) return FString();

    const FString RequestId = NewRequestId();
    const TWeakObjectPtr<UOpenGameAgentSubsystem> WeakThis(this);
    const TSharedRef<FOpenGameAgentStreamState, ESPMode::ThreadSafe> State = MakeShared<FOpenGameAgentStreamState, ESPMode::ThreadSafe>(
        MaximumEventBytes,
        [WeakThis, RequestId](FOpenGameAgentDecodedEvent&& Decoded)
        {
            AsyncTask(ENamedThreads::GameThread, [WeakThis, RequestId, Event = MoveTemp(Decoded)]()
            {
                if (!WeakThis.IsValid()) return;
                FOpenGameAgentEvent PublicEvent;
                PublicEvent.EventId = Event.Id;
                PublicEvent.EventName = Event.Name;
                PublicEvent.Json = Event.Json;
                WeakThis->OnStreamEvent.Broadcast(RequestId, PublicEvent);
            });
        });

    FHttpRequestRef Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(ServerUrl + Path);
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    Request->SetContentAsString(Body);
    const bool bStreaming = Request->SetResponseBodyReceiveStreamDelegateV2(
        FHttpRequestStreamDelegateV2::CreateLambda([State](void* Data, int64& InOutLength)
        {
            State->Push(Data, InOutLength);
        }));
    if (!bStreaming) return FString();

    const FString ExpectedUrl = ServerUrl + Path;
    Request->OnProcessRequestComplete().BindWeakLambda(this,
        [this, RequestId, State, ExpectedUrl](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
        {
            FString Failure;
            const int32 Status = Response.IsValid() ? Response->GetResponseCode() : 0;
            if (Response.IsValid() && Response->GetEffectiveURL() != ExpectedUrl)
            {
                Failure = TEXT("redirect-not-allowed");
            }
            else if (!bSucceeded || !EHttpResponseCodes::IsOk(Status))
            {
                Failure = Status > 0 ? FString::Printf(TEXT("http-%d"), Status) : TEXT("transport");
            }
            else if (!State->Finish(Failure))
            {
                if (Failure.IsEmpty()) Failure = TEXT("invalid-stream");
            }
            CompleteRequest(RequestId);
            if (!Failure.IsEmpty()) OnRequestFailed.Broadcast(RequestId, Failure);
        });
    ActiveRequests.Add(RequestId, Request);
    if (!Request->ProcessRequest())
    {
        ActiveRequests.Remove(RequestId);
        return FString();
    }
    return RequestId;
}

FString UOpenGameAgentSubsystem::StartJsonRequest(const FString& Path, const FString& BodyObjectJson)
{
    if (!ValidatePath(Path)) return FString();
    FString Body;
    if (!CreateAuthenticatedBody(BodyObjectJson, Body) || FTCHARToUTF8(*Body).Length() > MaximumRequestBytes) return FString();
    const FString RequestId = NewRequestId();
    FHttpRequestRef Request = FHttpModule::Get().CreateRequest();
    Request->SetURL(ServerUrl + Path);
    Request->SetVerb(TEXT("POST"));
    Request->SetHeader(TEXT("Content-Type"), TEXT("application/json"));
    Request->SetContentAsString(Body);
    const FString ExpectedUrl = ServerUrl + Path;
    Request->OnProcessRequestComplete().BindWeakLambda(this,
        [this, RequestId, Path, ExpectedUrl](FHttpRequestPtr, FHttpResponsePtr Response, bool bSucceeded)
        {
            const int32 Status = Response.IsValid() ? Response->GetResponseCode() : 0;
            if (Response.IsValid() && Response->GetEffectiveURL() != ExpectedUrl)
            {
                CompleteRequest(RequestId);
                OnRequestFailed.Broadcast(RequestId, TEXT("redirect-not-allowed"));
                return;
            }
            if (!bSucceeded || !Response.IsValid() || !EHttpResponseCodes::IsOk(Status))
            {
                CompleteRequest(RequestId);
                OnRequestFailed.Broadcast(RequestId, Status > 0 ? FString::Printf(TEXT("http-%d"), Status) : TEXT("transport"));
                return;
            }
            const TArray<uint8>& Content = Response->GetContent();
            if (Content.Num() > MaximumResponseBytes)
            {
                CompleteRequest(RequestId);
                OnRequestFailed.Broadcast(RequestId, TEXT("response-too-large"));
                return;
            }
            const FString Json = Response->GetContentAsString();
            CompleteRequest(RequestId);
            OnJsonResponse.Broadcast(RequestId, Path, Status, Json);
        });
    ActiveRequests.Add(RequestId, Request);
    if (!Request->ProcessRequest())
    {
        ActiveRequests.Remove(RequestId);
        return FString();
    }
    return RequestId;
}

FString UOpenGameAgentSubsystem::StartControl(
    const FString& Operation,
    const FString& SessionObjectJson,
    const FString& ExpectedRunCoordinateJson,
    const FString* InputObjectJson)
{
    TSharedPtr<FJsonObject> Session;
    TSharedPtr<FJsonObject> Expected;
    if (!ParseObject(SessionObjectJson, Session) || !ParseObject(ExpectedRunCoordinateJson, Expected)) return FString();
    const TSharedRef<FJsonObject> Body = MakeShared<FJsonObject>();
    Body->SetObjectField(TEXT("session"), Session);
    Body->SetObjectField(TEXT("expected"), Expected);
    if (InputObjectJson != nullptr)
    {
        TSharedPtr<FJsonObject> Input;
        if (!ParseObject(*InputObjectJson, Input)) return FString();
        Body->SetObjectField(TEXT("input"), Input);
    }
    return StartJsonRequest(TEXT("v1/control/") + Operation, SerializeObject(Body));
}

bool UOpenGameAgentSubsystem::CreateAuthenticatedBody(const FString& BodyObjectJson, FString& OutBody) const
{
    TSharedPtr<FJsonObject> Body;
    if (!ParseObject(BodyObjectJson, Body)) return false;
    if (!AuthenticationObjectJson.IsEmpty())
    {
        TSharedPtr<FJsonObject> Authentication;
        if (!ParseObject(AuthenticationObjectJson, Authentication)) return false;
        Body->SetObjectField(TEXT("authentication"), Authentication);
    }
    OutBody = SerializeObject(Body.ToSharedRef());
    return true;
}

bool UOpenGameAgentSubsystem::ValidateServerUrl(const FString& Value, FString& OutNormalized) const
{
    FString Lower = Value.ToLower();
    const bool bHttps = Lower.StartsWith(TEXT("https://"));
    const int32 SchemeEnd = Value.Find(TEXT("://"));
    if (SchemeEnd == INDEX_NONE) return false;
    const int32 PathStart = Value.Find(TEXT("/"), ESearchCase::CaseSensitive, ESearchDir::FromStart, SchemeEnd + 3);
    const FString Authority = PathStart == INDEX_NONE ? Value.Mid(SchemeEnd + 3) : Value.Mid(SchemeEnd + 3, PathStart - SchemeEnd - 3);
    if (Authority.IsEmpty() || Authority.Contains(TEXT("@")) || Value.Contains(TEXT("?")) || Value.Contains(TEXT("#"))) return false;
    const bool bLoopbackAuthority = Authority.Equals(TEXT("127.0.0.1"), ESearchCase::IgnoreCase)
        || Authority.StartsWith(TEXT("127.0.0.1:"), ESearchCase::IgnoreCase)
        || Authority.Equals(TEXT("localhost"), ESearchCase::IgnoreCase)
        || Authority.StartsWith(TEXT("localhost:"), ESearchCase::IgnoreCase)
        || Authority.Equals(TEXT("[::1]"), ESearchCase::IgnoreCase)
        || Authority.StartsWith(TEXT("[::1]:"), ESearchCase::IgnoreCase);
    const bool bLoopbackHttp = Lower.StartsWith(TEXT("http://")) && bLoopbackAuthority;
    if (!bHttps && !bLoopbackHttp) return false;
    for (const TCHAR Character : Value)
    {
        if (FChar::IsControl(Character) || FChar::IsWhitespace(Character)) return false;
    }
    OutNormalized = Value.EndsWith(TEXT("/")) ? Value : Value + TEXT("/");
    return true;
}

bool UOpenGameAgentSubsystem::ValidatePath(const FString& Path) const
{
    return !Path.IsEmpty() && Path.Len() <= 512 && !Path.Contains(TEXT("..")) && !Path.Contains(TEXT("://"));
}

void UOpenGameAgentSubsystem::CompleteRequest(const FString& RequestId)
{
    ActiveRequests.Remove(RequestId);
}
