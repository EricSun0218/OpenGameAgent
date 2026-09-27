#include "OpenGameAgentRuntimeClient.h"

#include "OpenGameAgentModule.h"
#include "OpenGameAgentTransportLifecycle.h"

#include "Async/Async.h"
#include "Dom/JsonObject.h"
#include "Dom/JsonValue.h"
#include "HAL/CriticalSection.h"
#include "HAL/PlatformTime.h"
#include "Misc/IQueuedWork.h"
#include "Misc/QueuedThreadPool.h"
#include "Misc/Base64.h"
#if WITH_DEV_AUTOMATION_TESTS
#include "Misc/AutomationTest.h"
#endif
#include "Misc/ScopeExit.h"
#include "Misc/ScopeLock.h"
#include "Policies/CondensedJsonPrintPolicy.h"
#include "Serialization/JsonReader.h"
#include "Serialization/JsonSerializer.h"
#include "Serialization/JsonWriter.h"
#if OGA_WITH_OPENSSL
#include "Ssl.h"
#endif

THIRD_PARTY_INCLUDES_START
#if OGA_WITH_LIBCURL
#include <curl/curl.h>
#endif

#if OGA_WITH_OPENSSL
#include <openssl/evp.h>
#include <openssl/ssl.h>
#endif
THIRD_PARTY_INCLUDES_END

#ifdef GetEnvironmentVariable
#undef GetEnvironmentVariable
#endif

namespace
{
FCriticalSection GTransportLifecycleMutex;
FEvent* GTransportIdleEvent = nullptr;
int64 GOutstandingTransportWork = 0;
bool bTransportStarted = false;
TAtomic<bool> bTransportStopping{false};
TSharedPtr<FOpenGameAgentTransportState, ESPMode::ThreadSafe> GTransportState;
}

bool StartOpenGameAgentTransport()
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	if (bTransportStarted)
	{
		return true;
	}
#if OGA_WITH_LIBCURL
	if (curl_global_init(CURL_GLOBAL_DEFAULT) != CURLE_OK)
	{
		return false;
	}
#endif
	GTransportIdleEvent = FPlatformProcess::GetSynchEventFromPool(false);
	bTransportStopping.Store(false);
	bTransportStarted = true;
	return true;
}

bool IsOpenGameAgentTransportReady()
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	return bTransportStarted && !bTransportStopping.Load();
}

bool IsOpenGameAgentTransportStopping()
{
	return bTransportStopping.Load();
}

bool TryBeginOpenGameAgentTransportWork()
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	if (!bTransportStarted || bTransportStopping.Load())
	{
		return false;
	}
	GOutstandingTransportWork += 1;
	return true;
}

void EndOpenGameAgentTransportWork()
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	check(GOutstandingTransportWork > 0);
	GOutstandingTransportWork -= 1;
	GTransportIdleEvent->Trigger();
}

void NotifyOpenGameAgentTransportProgress()
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	if (GTransportIdleEvent != nullptr)
	{
		GTransportIdleEvent->Trigger();
	}
}

class FOpenGameAgentQueuedWork final : public IQueuedWork
{
public:
	FOpenGameAgentQueuedWork(TFunction<void()> InWork, TFunction<void()> InAbandon)
		: Work(MoveTemp(InWork))
		, AbandonWork(MoveTemp(InAbandon))
	{
	}

	virtual void DoThreadedWork() override
	{
		Work();
		delete this;
	}

	virtual void Abandon() override
	{
		AbandonWork();
		delete this;
	}

	virtual const TCHAR* GetDebugName() const override
	{
		return TEXT("OpenGameAgentRuntimeRequest");
	}

private:
	TFunction<void()> Work;
	TFunction<void()> AbandonWork;
};

class FOpenGameAgentTransportState final
	: public TSharedFromThis<FOpenGameAgentTransportState, ESPMode::ThreadSafe>
{
public:
	FOpenGameAgentTransportState(const int32 MaximumConcurrentRequests, const int32 MaximumQueuedRequests)
		: MaximumInFlightRequests(MaximumConcurrentRequests > 0 && MaximumQueuedRequests >= 0
			? static_cast<int64>(MaximumConcurrentRequests) + MaximumQueuedRequests
			: 0)
		, ConfiguredConcurrentRequests(MaximumConcurrentRequests)
		, ConfiguredQueuedRequests(MaximumQueuedRequests)
	{
		if (MaximumConcurrentRequests <= 0 || MaximumQueuedRequests < 0 ||
			MaximumConcurrentRequests > MAX_int32 - MaximumQueuedRequests)
		{
			return;
		}
		ThreadPool = FQueuedThreadPool::Allocate();
		if (ThreadPool != nullptr &&
			!ThreadPool->Create(static_cast<uint32>(MaximumConcurrentRequests), 0, TPri_Normal, TEXT("OpenGameAgentRuntime")))
		{
			delete ThreadPool;
			ThreadPool = nullptr;
		}
		bReady.Store(ThreadPool != nullptr);
	}

	~FOpenGameAgentTransportState()
	{
		Shutdown();
	}

	bool IsReady() const
	{
		return bReady.Load();
	}

	bool Matches(const int32 MaximumConcurrentRequests, const int32 MaximumQueuedRequests) const
	{
		return ConfiguredConcurrentRequests == MaximumConcurrentRequests &&
			ConfiguredQueuedRequests == MaximumQueuedRequests;
	}

	bool TryReserve()
	{
		if (!bReady.Load() || !IsOpenGameAgentTransportReady())
		{
			return false;
		}
		int64 Current = InFlightRequests.Load();
		while (Current < MaximumInFlightRequests)
		{
			if (InFlightRequests.CompareExchange(Current, Current + 1))
			{
				if (TryBeginOpenGameAgentTransportWork())
				{
					return true;
				}
				--InFlightRequests;
				return false;
			}
		}
		return false;
	}

	void EnqueueReserved(TFunction<void()> Work, TFunction<void()> AbandonWork)
	{
		check(bReady.Load() && ThreadPool != nullptr);
		ThreadPool->AddQueuedWork(new FOpenGameAgentQueuedWork(MoveTemp(Work), MoveTemp(AbandonWork)));
	}

	void EndRequest()
	{
		const int64 Remaining = --InFlightRequests;
		check(Remaining >= 0);
		EndOpenGameAgentTransportWork();
	}

	void Shutdown()
	{
		if (!bReady.Exchange(false))
		{
			return;
		}
		check(InFlightRequests.Load() == 0);
		ThreadPool->Destroy();
		delete ThreadPool;
		ThreadPool = nullptr;
	}

private:
	FQueuedThreadPool* ThreadPool = nullptr;
	const int64 MaximumInFlightRequests;
	const int32 ConfiguredConcurrentRequests;
	const int32 ConfiguredQueuedRequests;
	TAtomic<int64> InFlightRequests{0};
	TAtomic<bool> bReady{false};
};

bool ConfigureOpenGameAgentTransport(const FOpenGameAgentTransportLimits& Limits)
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	if (!Limits.IsValid() || !bTransportStarted || bTransportStopping.Load())
	{
		return false;
	}
	if (GTransportState.IsValid())
	{
		return GTransportState->Matches(Limits.MaxConcurrentRequests, Limits.MaxQueuedRequests);
	}
	const TSharedRef<FOpenGameAgentTransportState, ESPMode::ThreadSafe> Candidate =
		MakeShared<FOpenGameAgentTransportState, ESPMode::ThreadSafe>(
			Limits.MaxConcurrentRequests,
			Limits.MaxQueuedRequests);
	if (!Candidate->IsReady())
	{
		return false;
	}
	GTransportState = Candidate;
	return true;
}

TSharedPtr<FOpenGameAgentTransportState, ESPMode::ThreadSafe> AcquireOpenGameAgentTransportState()
{
	FScopeLock Lock(&GTransportLifecycleMutex);
	return bTransportStarted && !bTransportStopping.Load() && GTransportState.IsValid()
		? GTransportState
		: nullptr;
}

void StopOpenGameAgentTransport()
{
	check(IsInGameThread());
	FEvent* ProgressEvent = nullptr;
	TSharedPtr<FOpenGameAgentTransportState, ESPMode::ThreadSafe> TransportToShutdown;
	{
		FScopeLock Lock(&GTransportLifecycleMutex);
		if (!bTransportStarted)
		{
			return;
		}
		bTransportStopping.Store(true);
		ProgressEvent = GTransportIdleEvent;
		TransportToShutdown = GTransportState;
	}
	while (true)
	{
		FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
		{
			FScopeLock Lock(&GTransportLifecycleMutex);
			if (GOutstandingTransportWork == 0)
			{
				break;
			}
		}
		ProgressEvent->Wait();
	}
	if (TransportToShutdown.IsValid())
	{
		TransportToShutdown->Shutdown();
	}
	{
		FScopeLock Lock(&GTransportLifecycleMutex);
		GTransportState.Reset();
#if OGA_WITH_LIBCURL
		curl_global_cleanup();
#endif
		bTransportStarted = false;
		GTransportIdleEvent = nullptr;
	}
	FPlatformProcess::ReturnSynchEventToPool(ProgressEvent);
}

namespace
{
constexpr int64 MaxProtocolInteger = 9007199254740991LL;
constexpr int64 AuthorizationPrefixBytes = 22;

bool ParseDecimalInt64(const FString& Value, int64& OutValue)
{
	if (Value.IsEmpty())
	{
		return false;
	}
	int64 Parsed = 0;
	for (const TCHAR Character : Value)
	{
		if (!FChar::IsDigit(Character) || Parsed > (MAX_int64 - 9) / 10)
		{
			return false;
		}
		Parsed = Parsed * 10 + (Character - TEXT('0'));
	}
	OutValue = Parsed;
	return true;
}

bool TryCalculateRemainingTimeoutMilliseconds(
	const int64 TotalTimeoutMilliseconds,
	const double ElapsedMilliseconds,
	int64& OutRemainingMilliseconds)
{
	if (TotalTimeoutMilliseconds <= 0 || !FMath::IsFinite(ElapsedMilliseconds) || ElapsedMilliseconds < 0.0 ||
		ElapsedMilliseconds >= static_cast<double>(TotalTimeoutMilliseconds))
	{
		return false;
	}
	const int64 ElapsedBudgetMilliseconds = static_cast<int64>(FMath::CeilToDouble(ElapsedMilliseconds));
	if (ElapsedBudgetMilliseconds >= TotalTimeoutMilliseconds)
	{
		return false;
	}
	OutRemainingMilliseconds = TotalTimeoutMilliseconds - ElapsedBudgetMilliseconds;
	return OutRemainingMilliseconds > 0;
}

struct FRawHttpResult
{
	int32 StatusCode = 0;
	TArray<uint8> Body;
	TArray<TPair<FString, FString>> Headers;
	FString EffectiveUrl;
	int64 RedirectCount = 0;
};

enum class ERawHttpFailureKind : uint8
{
	Rejected,
	Cancelled,
	Ambiguous,
	Redirected,
	ResponseTooLarge,
};

struct FRawHttpFailure
{
	ERawHttpFailureKind Kind = ERawHttpFailureKind::Rejected;
	FString Message;
};

FString StableTransportFailureCode(const ERawHttpFailureKind Kind)
{
	switch (Kind)
	{
	case ERawHttpFailureKind::Cancelled:
		return TEXT("cancelled");
	case ERawHttpFailureKind::Ambiguous:
	case ERawHttpFailureKind::ResponseTooLarge:
		return TEXT("ambiguous_transport");
	default:
		return TEXT("transport_rejected");
	}
}

FString StableAttachmentFailureCode(const ERawHttpFailureKind Kind)
{
	if (Kind == ERawHttpFailureKind::Redirected)
	{
		return TEXT("redirected");
	}
	if (Kind == ERawHttpFailureKind::ResponseTooLarge)
	{
		return TEXT("response_too_large");
	}
	return TEXT("transport_failed");
}

using FRawHttpResultCallback = TFunction<void(bool, FRawHttpResult, FRawHttpFailure)>;

class FOpenGameAgentCurlRequest final : public IOpenGameAgentRequest
{
public:
	FOpenGameAgentCurlRequest() = default;

	explicit FOpenGameAgentCurlRequest(FRawHttpResultCallback InCallback)
		: Callback(MoveTemp(InCallback))
	{
	}

	virtual void Cancel() override
	{
		FRawHttpResultCallback CallbackToDestroy;
		{
			FScopeLock Lock(&CallbackMutex);
			if (DeliveryState.Load() != Pending)
			{
				return;
			}
			DeliveryState.Store(Cancelled);
			CallbackToDestroy = MoveTemp(Callback);
		}
	}

#if WITH_DEV_AUTOMATION_TESTS
	bool IsDeliveryCancelledForTest() const
	{
		return IsCancelled();
	}
#endif

	bool IsCancelled() const
	{
		return DeliveryState.Load() == Cancelled;
	}

	void Deliver(const bool bSucceeded, FRawHttpResult Result, FRawHttpFailure Failure)
	{
		FRawHttpResultCallback CallbackToRun;
		{
			FScopeLock Lock(&CallbackMutex);
			if (DeliveryState.Load() != Pending)
			{
				return;
			}
			DeliveryState.Store(Delivering);
			CallbackToRun = MoveTemp(Callback);
		}
		if (CallbackToRun)
		{
			CallbackToRun(bSucceeded, MoveTemp(Result), MoveTemp(Failure));
		}
	}

private:
	static constexpr int32 Pending = 0;
	static constexpr int32 Cancelled = 1;
	static constexpr int32 Delivering = 2;
	mutable FCriticalSection CallbackMutex;
	TAtomic<int32> DeliveryState{Pending};
	FRawHttpResultCallback Callback;
};

#if WITH_DEV_AUTOMATION_TESTS
class FReentrantCancelProbe final
{
public:
	~FReentrantCancelProbe()
	{
		if (OnDestroy)
		{
			OnDestroy();
		}
	}

	TFunction<void()> OnDestroy;
};
#endif

#if OGA_WITH_LIBCURL
class FCurlResponseCollector final
{
public:
	FCurlResponseCollector(
		const int64 InMaximumSuccessBytes,
		const int64 InMaximumErrorBytes,
		const int64 InMaximumHeaderBytes,
		const int32 InMaximumHeaders,
		const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>& InRequest)
		: MaximumSuccessBytes(InMaximumSuccessBytes)
		, MaximumErrorBytes(InMaximumErrorBytes)
		, MaximumHeaderBytes(InMaximumHeaderBytes)
		, MaximumHeaders(InMaximumHeaders)
		, Request(InRequest)
	{
	}

	size_t ReceiveHeader(const ANSICHAR* Data, const size_t Length)
	{
		if (Request->IsCancelled())
		{
			return CURL_WRITEFUNC_ERROR;
		}
		if (Length == 0)
		{
			return 0;
		}
		if (Length > static_cast<size_t>(MaximumHeaderBytes - HeaderBytes))
		{
			bLimitExceeded = true;
			return CURL_WRITEFUNC_ERROR;
		}
		HeaderBytes += static_cast<int64>(Length);

		FString Line;
		if (!DecodeHeaderLine(Data, Length, Line))
		{
			bInvalidResponse = true;
			return CURL_WRITEFUNC_ERROR;
		}
		if (Line.StartsWith(TEXT("HTTP/"), ESearchCase::CaseSensitive))
		{
			int32 ParsedStatus = 0;
			if (!ParseStatusLine(Line, ParsedStatus) || bInHeaderBlock || bFinalStatusReceived)
			{
				bInvalidResponse = true;
				return CURL_WRITEFUNC_ERROR;
			}
			bInHeaderBlock = true;
			bCollectingFinalHeaders = ParsedStatus >= 200;
			if (bCollectingFinalHeaders)
			{
				bFinalStatusReceived = true;
				StatusCode = ParsedStatus;
				MaximumBodyBytes = ParsedStatus < 300 ? MaximumSuccessBytes : MaximumErrorBytes;
			}
			return Length;
		}
		if (Line.IsEmpty())
		{
			if (!bInHeaderBlock)
			{
				bInvalidResponse = true;
				return CURL_WRITEFUNC_ERROR;
			}
			if (bCollectingFinalHeaders)
			{
				bFinalHeadersComplete = true;
			}
			bInHeaderBlock = false;
			bCollectingFinalHeaders = false;
			return Length;
		}
		if (!bInHeaderBlock || bFinalHeadersComplete)
		{
			bInvalidResponse = true;
			return CURL_WRITEFUNC_ERROR;
		}

		int32 Colon = INDEX_NONE;
		if (!Line.FindChar(TEXT(':'), Colon) || Colon <= 0)
		{
			bInvalidResponse = true;
			return CURL_WRITEFUNC_ERROR;
		}
		if (HeaderCount >= MaximumHeaders)
		{
			bLimitExceeded = true;
			return CURL_WRITEFUNC_ERROR;
		}
		FString Name = Line.Left(Colon);
		FString Value = Line.Mid(Colon + 1).TrimStartAndEnd();
		if (!IsValidHeaderName(Name) || !IsValidHeaderValue(Value))
		{
			bInvalidResponse = true;
			return CURL_WRITEFUNC_ERROR;
		}
		HeaderCount += 1;
		if (!bCollectingFinalHeaders)
		{
			return Length;
		}
		if (Name.Equals(TEXT("Content-Length"), ESearchCase::IgnoreCase))
		{
			int64 DeclaredBytes = 0;
			if (bContentLengthReceived || !ParseDecimalInt64(Value, DeclaredBytes))
			{
				bInvalidResponse = true;
				return CURL_WRITEFUNC_ERROR;
			}
			bContentLengthReceived = true;
			if (DeclaredBytes > MaximumBodyBytes)
			{
				bLimitExceeded = true;
				return CURL_WRITEFUNC_ERROR;
			}
		}
		Headers.Emplace(MoveTemp(Name), MoveTemp(Value));
		return Length;
	}

	size_t ReceiveBody(const uint8* Data, const size_t Length)
	{
		if (Request->IsCancelled())
		{
			return CURL_WRITEFUNC_ERROR;
		}
		if (Length == 0)
		{
			return 0;
		}
		if (!bFinalStatusReceived || !bFinalHeadersComplete || bInvalidResponse)
		{
			bInvalidResponse = true;
			return CURL_WRITEFUNC_ERROR;
		}
		if (bLimitExceeded || Length > static_cast<size_t>(MaximumBodyBytes - Body.Num()))
		{
			bLimitExceeded = true;
			return CURL_WRITEFUNC_ERROR;
		}
		const int32 Offset = Body.AddUninitialized(static_cast<int32>(Length));
		FMemory::Memcpy(Body.GetData() + Offset, Data, Length);
		return Length;
	}

	bool IsComplete() const
	{
		return bFinalStatusReceived && bFinalHeadersComplete && !bInvalidResponse && !bLimitExceeded;
	}

	bool LimitExceeded() const
	{
		return bLimitExceeded;
	}

	FRawHttpResult TakeResult()
	{
		FRawHttpResult Result;
		Result.StatusCode = StatusCode;
		Result.Body = MoveTemp(Body);
		Result.Headers = MoveTemp(Headers);
		return Result;
	}

private:
	static bool DecodeHeaderLine(const ANSICHAR* Data, const size_t Length, FString& OutLine)
	{
		if (Length > static_cast<size_t>(MAX_int32))
		{
			return false;
		}
		size_t ContentLength = Length;
		while (ContentLength > 0 && (Data[ContentLength - 1] == '\r' || Data[ContentLength - 1] == '\n'))
		{
			ContentLength -= 1;
		}
		for (size_t Index = 0; Index < ContentLength; ++Index)
		{
			const uint8 Character = static_cast<uint8>(Data[Index]);
			if (Character > 0x7f || (Character < 0x20 && Character != '\t'))
			{
				return false;
			}
		}
		TArray<ANSICHAR> Copy;
		Copy.Append(Data, static_cast<int32>(ContentLength));
		Copy.Add('\0');
		OutLine = ANSI_TO_TCHAR(Copy.GetData());
		return true;
	}

	static bool ParseStatusLine(const FString& Line, int32& OutStatus)
	{
		int32 Space = INDEX_NONE;
		if (!Line.FindChar(TEXT(' '), Space) || Space <= 5 || Line.Len() < Space + 4)
		{
			return false;
		}
		const TCHAR A = Line[Space + 1];
		const TCHAR B = Line[Space + 2];
		const TCHAR C = Line[Space + 3];
		if (!FChar::IsDigit(A) || !FChar::IsDigit(B) || !FChar::IsDigit(C) ||
			(Line.Len() > Space + 4 && !FChar::IsWhitespace(Line[Space + 4])))
		{
			return false;
		}
		OutStatus = (A - TEXT('0')) * 100 + (B - TEXT('0')) * 10 + (C - TEXT('0'));
		return OutStatus >= 100 && OutStatus <= 599;
	}

	static bool IsValidHeaderName(const FString& Name)
	{
		for (const TCHAR Character : Name)
		{
			const bool bAlphaNumeric =
				(Character >= TEXT('A') && Character <= TEXT('Z')) ||
				(Character >= TEXT('a') && Character <= TEXT('z')) ||
				(Character >= TEXT('0') && Character <= TEXT('9'));
			const bool bAllowedPunctuation = Character == TEXT('!') || Character == TEXT('#') ||
				Character == TEXT('$') || Character == TEXT('%') || Character == TEXT('&') ||
				Character == TEXT('\'') || Character == TEXT('*') || Character == TEXT('+') ||
				Character == TEXT('-') || Character == TEXT('.') || Character == TEXT('^') ||
				Character == TEXT('_') || Character == TEXT('`') || Character == TEXT('|') ||
				Character == TEXT('~');
			if (!bAlphaNumeric && !bAllowedPunctuation)
			{
				return false;
			}
		}
		return !Name.IsEmpty();
	}

	static bool IsValidHeaderValue(const FString& Value)
	{
		for (const TCHAR Character : Value)
		{
			if (Character > 0x7f || (Character < 0x20 && Character != TEXT('\t')) || Character == 0x7f)
			{
				return false;
			}
		}
		return true;
	}

	const int64 MaximumSuccessBytes;
	const int64 MaximumErrorBytes;
	const int64 MaximumHeaderBytes;
	const int32 MaximumHeaders;
	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> Request;
	TArray<uint8> Body;
	TArray<TPair<FString, FString>> Headers;
	int64 HeaderBytes = 0;
	int64 MaximumBodyBytes = 0;
	int32 HeaderCount = 0;
	int32 StatusCode = 0;
	bool bCollectingFinalHeaders = false;
	bool bInHeaderBlock = false;
	bool bFinalStatusReceived = false;
	bool bFinalHeadersComplete = false;
	bool bContentLengthReceived = false;
	bool bInvalidResponse = false;
	bool bLimitExceeded = false;
};

size_t CurlHeaderCallback(char* Data, const size_t Size, const size_t Count, void* UserData)
{
	if (Size != 0 && Count > MAX_uint64 / Size)
	{
		return CURL_WRITEFUNC_ERROR;
	}
	return static_cast<FCurlResponseCollector*>(UserData)->ReceiveHeader(Data, Size * Count);
}

size_t CurlBodyCallback(char* Data, const size_t Size, const size_t Count, void* UserData)
{
	if (Size != 0 && Count > MAX_uint64 / Size)
	{
		return CURL_WRITEFUNC_ERROR;
	}
	return static_cast<FCurlResponseCollector*>(UserData)->ReceiveBody(
		reinterpret_cast<const uint8*>(Data),
		Size * Count);
}

int CurlProgressCallback(void* UserData, curl_off_t, curl_off_t, curl_off_t, curl_off_t)
{
	return static_cast<FOpenGameAgentCurlRequest*>(UserData)->IsCancelled() || IsOpenGameAgentTransportStopping()
		? 1
		: 0;
}

#if OGA_WITH_OPENSSL
int VerifySslCertificate(const int PreverifyOk, X509_STORE_CTX* Context)
{
	if (PreverifyOk != 1)
	{
		return PreverifyOk;
	}
	SSL* SslHandle = static_cast<SSL*>(
		X509_STORE_CTX_get_ex_data(Context, SSL_get_ex_data_X509_STORE_CTX_idx()));
	SSL_CTX* SslContext = SslHandle == nullptr ? nullptr : SSL_get_SSL_CTX(SslHandle);
	const FString* Domain = SslContext == nullptr
		? nullptr
		: static_cast<const FString*>(SSL_CTX_get_app_data(SslContext));
	return Domain != nullptr &&
		FSslModule::Get().GetCertificateManager().VerifySslCertificates(Context, *Domain)
		? 1
		: 0;
}

CURLcode CurlSslContextCallback(CURL*, void* SslContext, void* UserData)
{
	SSL_CTX* Context = static_cast<SSL_CTX*>(SslContext);
	FSslModule::Get().GetCertificateManager().AddCertificatesToSslContext(Context);
	SSL_CTX_set_verify(Context, SSL_CTX_get_verify_mode(Context), VerifySslCertificate);
	SSL_CTX_set_app_data(Context, UserData);
	return CURLE_OK;
}
#endif

template <typename TValue>
bool SetCurlOption(CURL* Handle, const CURLoption Option, TValue Value)
{
	return curl_easy_setopt(Handle, Option, Value) == CURLE_OK;
}

bool CurlUrlPartIsAbsent(CURLU* Url, const CURLUPart Part, const CURLUcode AbsentCode)
{
	ANSICHAR* Value = nullptr;
	const CURLUcode Result = curl_url_get(Url, Part, &Value, 0);
	if (Value != nullptr)
	{
		curl_free(Value);
	}
	return Result == AbsentCode;
}

bool GetCurlUrlPart(CURLU* Url, const CURLUPart Part, FString& OutValue)
{
	ANSICHAR* Value = nullptr;
	if (curl_url_get(Url, Part, &Value, 0) != CURLUE_OK || Value == nullptr)
	{
		return false;
	}
	OutValue = UTF8_TO_TCHAR(Value);
	curl_free(Value);
	return true;
}

bool ParseCurlBaseUrl(const FString& Candidate, FString& OutScheme, FString& OutHost, FString& OutPort)
{
	CURLU* Url = curl_url();
	if (Url == nullptr)
	{
		return false;
	}
	ON_SCOPE_EXIT
	{
		curl_url_cleanup(Url);
	};
	const FTCHARToUTF8 Utf8(*Candidate);
	if (curl_url_set(Url, CURLUPART_URL, Utf8.Get(), CURLU_DISALLOW_USER | CURLU_PATH_AS_IS) != CURLUE_OK ||
		!GetCurlUrlPart(Url, CURLUPART_SCHEME, OutScheme) || !GetCurlUrlPart(Url, CURLUPART_HOST, OutHost) ||
		!CurlUrlPartIsAbsent(Url, CURLUPART_USER, CURLUE_NO_USER) ||
		!CurlUrlPartIsAbsent(Url, CURLUPART_PASSWORD, CURLUE_NO_PASSWORD) ||
		!CurlUrlPartIsAbsent(Url, CURLUPART_OPTIONS, CURLUE_NO_OPTIONS) ||
		!CurlUrlPartIsAbsent(Url, CURLUPART_QUERY, CURLUE_NO_QUERY) ||
		!CurlUrlPartIsAbsent(Url, CURLUPART_FRAGMENT, CURLUE_NO_FRAGMENT) ||
		!CurlUrlPartIsAbsent(Url, CURLUPART_ZONEID, CURLUE_NO_ZONEID))
	{
		return false;
	}
	ANSICHAR* Port = nullptr;
	const CURLUcode PortResult = curl_url_get(Url, CURLUPART_PORT, &Port, 0);
	if (PortResult == CURLUE_OK && Port != nullptr)
	{
		OutPort = UTF8_TO_TCHAR(Port);
		curl_free(Port);
		return true;
	}
	if (Port != nullptr)
	{
		curl_free(Port);
	}
	return PortResult == CURLUE_NO_PORT;
}
#endif

bool IsCanonicalPort(const FStringView Port)
{
	if (Port.IsEmpty() || (Port.Len() > 1 && Port[0] == TEXT('0')))
	{
		return false;
	}
	uint32 Value = 0;
	for (const TCHAR Character : Port)
	{
		if (!FChar::IsDigit(Character))
		{
			return false;
		}
		Value = Value * 10U + static_cast<uint32>(Character - TEXT('0'));
		if (Value > MAX_uint16)
		{
			return false;
		}
	}
	return Value > 0;
}

bool IsLoopbackIpv4(const FStringView Host)
{
	TArray<FString> Parts;
	FString(Host).ParseIntoArray(Parts, TEXT("."), false);
	if (Parts.Num() != 4)
	{
		return false;
	}
	for (int32 Index = 0; Index < Parts.Num(); ++Index)
	{
		const FString& Part = Parts[Index];
		if (Part.IsEmpty() || (Part.Len() > 1 && Part[0] == TEXT('0')))
		{
			return false;
		}
		uint32 Value = 0;
		for (const TCHAR Character : Part)
		{
			if (!FChar::IsDigit(Character))
			{
				return false;
			}
			Value = Value * 10U + static_cast<uint32>(Character - TEXT('0'));
		}
		if (Value > 255U || (Index == 0 && Value != 127U))
		{
			return false;
		}
	}
	return true;
}

bool IsAllowedBaseUrl(const FString& BaseUrl)
{
	const FString Candidate = BaseUrl.TrimStartAndEnd();
	if (Candidate.IsEmpty() || Candidate != BaseUrl || Candidate.EndsWith(TEXT("/")) ||
		Candidate.Contains(TEXT("?")) || Candidate.Contains(TEXT("#")) || Candidate.Contains(TEXT("\\")) ||
		Candidate.Contains(TEXT("%")))
	{
		return false;
	}
	for (const TCHAR Character : Candidate)
	{
		if (Character <= 0x20 || Character >= 0x7f)
		{
			return false;
		}
	}

	FString ParsedScheme;
	FString ParsedHost;
	FString ParsedPort;
#if OGA_WITH_LIBCURL
	if (!ParseCurlBaseUrl(Candidate, ParsedScheme, ParsedHost, ParsedPort))
	{
		return false;
	}
#else
	return false;
#endif

	int32 SchemeEnd = INDEX_NONE;
	if (!Candidate.FindChar(TEXT(':'), SchemeEnd) || SchemeEnd <= 0 ||
		Candidate.Mid(SchemeEnd, 3) != TEXT("://"))
	{
		return false;
	}
	const FString OriginalScheme = Candidate.Left(SchemeEnd);
	const FString Scheme = OriginalScheme.ToLower();
	if (OriginalScheme != Scheme)
	{
		return false;
	}
	const int32 AuthorityStart = SchemeEnd + 3;
	int32 AuthorityEnd = Candidate.Find(TEXT("/"), ESearchCase::CaseSensitive, ESearchDir::FromStart, AuthorityStart);
	if (AuthorityEnd == INDEX_NONE)
	{
		AuthorityEnd = Candidate.Len();
	}
	const FString Authority = Candidate.Mid(AuthorityStart, AuthorityEnd - AuthorityStart);
	const FString Path = AuthorityEnd < Candidate.Len() ? Candidate.Mid(AuthorityEnd) : FString();
	if (Authority.IsEmpty() || Authority.Contains(TEXT("@")) || Authority.Contains(TEXT("%")))
	{
		return false;
	}
	if (Path == TEXT("/.") || Path == TEXT("/..") || Path.Contains(TEXT("/./")) ||
		Path.Contains(TEXT("/../")))
	{
		return false;
	}

	FString Host;
	FString Port;
	if (Authority.StartsWith(TEXT("[")))
	{
		int32 Closing = INDEX_NONE;
		if (!Authority.FindChar(TEXT(']'), Closing) || Closing <= 1)
		{
			return false;
		}
		Host = Authority.Mid(1, Closing - 1);
		const FString Suffix = Authority.Mid(Closing + 1);
		if (!Suffix.IsEmpty())
		{
			if (!Suffix.StartsWith(TEXT(":")))
			{
				return false;
			}
			Port = Suffix.Mid(1);
		}
	}
	else
	{
		int32 Colon = INDEX_NONE;
		if (Authority.FindChar(TEXT(':'), Colon))
		{
			if (Authority.Find(TEXT(":"), ESearchCase::CaseSensitive, ESearchDir::FromStart, Colon + 1) != INDEX_NONE)
			{
				return false;
			}
			Host = Authority.Left(Colon);
			Port = Authority.Mid(Colon + 1);
		}
		else
		{
			Host = Authority;
		}
	}
	if (Host.IsEmpty() || Host.EndsWith(TEXT(".")) || Host != Host.ToLower() ||
		(!Port.IsEmpty() && !IsCanonicalPort(Port)) ||
		ParsedScheme != Scheme || ParsedPort != Port)
	{
		return false;
	}
	if (Scheme == TEXT("https"))
	{
		return Port != TEXT("443");
	}
	return Scheme == TEXT("http") &&
		Port != TEXT("80") &&
		(Host == TEXT("::1") || IsLoopbackIpv4(Host));
}

bool IsValidAccessToken(const FString& Token)
{
	if (Token.IsEmpty())
	{
		return true;
	}
	bool bSawTokenCharacter = false;
	bool bSawPadding = false;
	for (const TCHAR Character : Token)
	{
		if (Character == TEXT('='))
		{
			if (!bSawTokenCharacter)
			{
				return false;
			}
			bSawPadding = true;
			continue;
		}
		const bool bAsciiAlphaNumeric =
			(Character >= TEXT('A') && Character <= TEXT('Z')) ||
			(Character >= TEXT('a') && Character <= TEXT('z')) ||
			(Character >= TEXT('0') && Character <= TEXT('9'));
		if (bSawPadding || !bAsciiAlphaNumeric)
		{
			if (Character != TEXT('-') && Character != TEXT('.') && Character != TEXT('_') &&
				Character != TEXT('~') && Character != TEXT('+') && Character != TEXT('/'))
			{
				return false;
			}
			if (bSawPadding)
			{
				return false;
			}
		}
		bSawTokenCharacter = true;
	}
	return bSawTokenCharacter;
}

bool IsImageAttachmentId(const FString& Value)
{
	if (Value.Len() != 68 || !Value.StartsWith(TEXT("img_"), ESearchCase::CaseSensitive))
	{
		return false;
	}
	for (int32 Index = 4; Index < Value.Len(); ++Index)
	{
		const TCHAR Character = Value[Index];
		if (!((Character >= TEXT('0') && Character <= TEXT('9')) ||
			(Character >= TEXT('a') && Character <= TEXT('f'))))
		{
			return false;
		}
	}
	return true;
}

TOptional<FString> GetSingleHeader(
	const TArray<TPair<FString, FString>>& Headers,
	const FStringView Name,
	bool& bDuplicate)
{
	bDuplicate = false;
	TOptional<FString> Result;
	for (const TPair<FString, FString>& Header : Headers)
	{
		if (!Header.Key.Equals(FString(Name), ESearchCase::IgnoreCase))
		{
			continue;
		}
		if (Result.IsSet())
		{
			bDuplicate = true;
			return {};
		}
		Result = Header.Value.TrimStartAndEnd();
	}
	return Result;
}

bool HasPrivateNoStore(const FString& Value)
{
	TArray<FString> Directives;
	Value.ParseIntoArray(Directives, TEXT(","), false);
	if (Directives.Num() != 2)
	{
		return false;
	}
	for (FString& Directive : Directives)
	{
		Directive = Directive.TrimStartAndEnd().ToLower();
	}
	Directives.Sort();
	return Directives[0] == TEXT("no-store") && Directives[1] == TEXT("private");
}

bool CalculateSha256(const TArray<uint8>& Bytes, TArray<uint8>& OutDigest)
{
#if OGA_WITH_OPENSSL
	OutDigest.SetNumUninitialized(32);
	uint32 DigestLength = 0;
	if (EVP_Digest(
			Bytes.GetData(),
			static_cast<SIZE_T>(Bytes.Num()),
			OutDigest.GetData(),
			&DigestLength,
			EVP_sha256(),
			nullptr) != 1 ||
		DigestLength != 32)
	{
		OutDigest.Reset();
		return false;
	}
	return true;
#else
	OutDigest.Reset();
	return false;
#endif
}

bool ValidateAttachmentResponse(
	const FOpenGameAgentImageReference& Reference,
	const FRawHttpResult& Result,
	FString& OutCode,
	FString& OutMessage)
{
	bool bDuplicate = false;
	auto Header = [&Result, &bDuplicate](const FStringView Name)
	{
		bool bThisDuplicate = false;
		TOptional<FString> Value = GetSingleHeader(Result.Headers, Name, bThisDuplicate);
		bDuplicate = bDuplicate || bThisDuplicate;
		return Value;
	};
	const TOptional<FString> ContentLength = Header(TEXT("Content-Length"));
	const TOptional<FString> ContentType = Header(TEXT("Content-Type"));
	const TOptional<FString> ETag = Header(TEXT("ETag"));
	const TOptional<FString> CacheControl = Header(TEXT("Cache-Control"));
	const TOptional<FString> NoSniff = Header(TEXT("X-Content-Type-Options"));
	const TOptional<FString> ContentDigest = Header(TEXT("Content-Digest"));
	if (bDuplicate || !ContentLength.IsSet() || ContentLength.GetValue() != LexToString(Reference.Bytes) ||
		Result.Body.Num() != Reference.Bytes || !ContentType.IsSet() ||
		!ContentType.GetValue().Equals(TEXT("image/png"), ESearchCase::IgnoreCase) || !ETag.IsSet() ||
		ETag.GetValue() != TEXT("\"") + Reference.AttachmentId + TEXT("\"") || !CacheControl.IsSet() ||
		!HasPrivateNoStore(CacheControl.GetValue()) || !NoSniff.IsSet() ||
		!NoSniff.GetValue().Equals(TEXT("nosniff"), ESearchCase::IgnoreCase) || !ContentDigest.IsSet())
	{
		OutCode = TEXT("invalid_response");
		OutMessage = TEXT("The attachment response metadata is invalid.");
		return false;
	}

	TArray<uint8> Digest;
	if (!CalculateSha256(Result.Body, Digest))
	{
		OutCode = TEXT("hash_unavailable");
		OutMessage = TEXT("SHA-256 is unavailable on this Unreal target.");
		return false;
	}
	const FString ExpectedAttachmentId = TEXT("img_") + BytesToHex(Digest.GetData(), Digest.Num()).ToLower();
	const FString ExpectedContentDigest = TEXT("sha-256=:") + FBase64::Encode(Digest) + TEXT(":");
	if (Reference.AttachmentId != ExpectedAttachmentId || ContentDigest.GetValue() != ExpectedContentDigest)
	{
		OutCode = TEXT("content_mismatch");
		OutMessage = TEXT("The attachment bytes do not match their reference.");
		return false;
	}
	return true;
}

bool SerializeJsonObject(const TSharedRef<FJsonObject>& Object, FString& OutJson)
{
	const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
		TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&OutJson);
	return FJsonSerializer::Serialize(Object, Writer);
}

bool SerializeJsonValue(const TSharedPtr<FJsonValue>& Value, FString& OutJson)
{
	if (!Value.IsValid())
	{
		return false;
	}
	const TSharedRef<TJsonWriter<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>> Writer =
		TJsonWriterFactory<TCHAR, TCondensedJsonPrintPolicy<TCHAR>>::Create(&OutJson);
	return FJsonSerializer::Serialize(Value.ToSharedRef(), FString(), Writer);
}

TSharedPtr<FJsonValue> ParseJsonValue(const FString& Json)
{
	TSharedPtr<FJsonValue> Value;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	return FJsonSerializer::Deserialize(Reader, Value) ? Value : nullptr;
}

bool ParseActionObject(
	const TSharedPtr<FJsonObject>& Action,
	const bool bRequireClaim,
	FOpenGameAgentAction& OutAction)
{
	if (!Action.IsValid() ||
		!Action->TryGetStringField(TEXT("sessionId"), OutAction.SessionId) ||
		!Action->TryGetStringField(TEXT("actorId"), OutAction.ActorId) ||
		!Action->TryGetStringField(TEXT("operationId"), OutAction.OperationId) ||
		!Action->TryGetStringField(TEXT("actionName"), OutAction.ActionName) ||
		!Action->TryGetStringField(TEXT("attempt"), OutAction.Attempt) ||
		!Action->TryGetStringField(TEXT("status"), OutAction.Status))
	{
		return false;
	}

	const TSharedPtr<FJsonValue> ClaimValue = Action->TryGetField(TEXT("claim"));
	if (!ClaimValue.IsValid() || ClaimValue->Type == EJson::Null)
	{
		if (bRequireClaim)
		{
			return false;
		}
		OutAction.ClaimToken.Reset();
		OutAction.ClaimExpiresAt.Reset();
	}
	else
	{
		if (ClaimValue->Type != EJson::Object)
		{
			return false;
		}
		const TSharedPtr<FJsonObject> Claim = ClaimValue->AsObject();
		FString ClaimAttempt;
		if (!Claim.IsValid() || !Claim->TryGetStringField(TEXT("claimToken"), OutAction.ClaimToken) ||
			!Claim->TryGetStringField(TEXT("attempt"), ClaimAttempt) ||
			!Claim->TryGetStringField(TEXT("expiresAt"), OutAction.ClaimExpiresAt) ||
			ClaimAttempt != OutAction.Attempt)
		{
			return false;
		}
	}

	if (!SerializeJsonValue(Action->TryGetField(TEXT("payload")), OutAction.PayloadJson) ||
		!SerializeJsonValue(Action->TryGetField(TEXT("expectedAuthority")), OutAction.ExpectedAuthorityJson) ||
		!SerializeJsonObject(Action.ToSharedRef(), OutAction.RecordJson))
	{
		return false;
	}
	return true;
}

bool ParseNextActionResponse(
	const FString& Json,
	TOptional<FOpenGameAgentActionDelivery>& OutDelivery)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return false;
	}

	const TSharedPtr<FJsonValue> DeliveryValue = Root->TryGetField(TEXT("delivery"));
	if (!DeliveryValue.IsValid())
	{
		return false;
	}
	if (DeliveryValue->Type == EJson::Null)
	{
		OutDelivery.Reset();
		return true;
	}
	if (DeliveryValue->Type != EJson::Object)
	{
		return false;
	}

	const TSharedPtr<FJsonObject> DeliveryObject = DeliveryValue->AsObject();
	const TSharedPtr<FJsonObject>* ActionObject = nullptr;
	FString Kind;
	FOpenGameAgentActionDelivery Parsed;
	if (!DeliveryObject.IsValid() || !DeliveryObject->TryGetStringField(TEXT("kind"), Kind) ||
		!DeliveryObject->TryGetObjectField(TEXT("action"), ActionObject) || ActionObject == nullptr ||
		!ParseActionObject(*ActionObject, true, Parsed.Action))
	{
		return false;
	}
	if (Kind == TEXT("dispatch"))
	{
		Parsed.Kind = EOpenGameAgentActionDeliveryKind::Dispatch;
	}
	else if (Kind == TEXT("reconcile"))
	{
		Parsed.Kind = EOpenGameAgentActionDeliveryKind::Reconcile;
	}
	else
	{
		return false;
	}
	if ((Parsed.Kind == EOpenGameAgentActionDeliveryKind::Dispatch && Parsed.Action.Status != TEXT("claimed")) ||
		(Parsed.Kind == EOpenGameAgentActionDeliveryKind::Reconcile &&
			Parsed.Action.Status != TEXT("claimed") && Parsed.Action.Status != TEXT("uncertain")))
	{
		return false;
	}
	OutDelivery = MoveTemp(Parsed);
	return true;
}

bool ParseGetActionResponse(const FString& Json, TOptional<FOpenGameAgentAction>& OutAction)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return false;
	}
	const TSharedPtr<FJsonValue> ActionValue = Root->TryGetField(TEXT("action"));
	if (!ActionValue.IsValid())
	{
		return false;
	}
	if (ActionValue->Type == EJson::Null)
	{
		OutAction.Reset();
		return true;
	}
	if (ActionValue->Type != EJson::Object)
	{
		return false;
	}
	FOpenGameAgentAction Parsed;
	if (!ParseActionObject(ActionValue->AsObject(), false, Parsed))
	{
		return false;
	}
	OutAction = MoveTemp(Parsed);
	return true;
}

bool AddOptionalJsonField(
	const TSharedRef<FJsonObject>& Object,
	const TCHAR* Field,
	const TOptional<FString>& Json)
{
	if (!Json.IsSet())
	{
		return true;
	}
	const TSharedPtr<FJsonValue> Value = ParseJsonValue(Json.GetValue());
	if (!Value.IsValid())
	{
		return false;
	}
	Object->SetField(Field, Value);
	return true;
}

bool BuildReceiptJson(
	const FOpenGameAgentAction& Action,
	const FOpenGameAgentActionResult& Result,
	FString& OutJson)
{
	if (Result.Outcome != TEXT("succeeded") && Result.Outcome != TEXT("rejected") && Result.Outcome != TEXT("unknown"))
	{
		return false;
	}

	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), Action.SessionId);
	Root->SetStringField(TEXT("actorId"), Action.ActorId);
	Root->SetStringField(TEXT("operationId"), Action.OperationId);
	Root->SetStringField(TEXT("claimToken"), Action.ClaimToken);
	Root->SetStringField(TEXT("attempt"), Action.Attempt);
	Root->SetStringField(TEXT("outcome"), Result.Outcome);
	if (!AddOptionalJsonField(Root, TEXT("result"), Result.ResultJson) ||
		!AddOptionalJsonField(Root, TEXT("modelResult"), Result.ModelResultJson) ||
		!AddOptionalJsonField(Root, TEXT("authorityAfter"), Result.AuthorityAfterJson))
	{
		return false;
	}
	return SerializeJsonObject(Root, OutJson);
}

bool BuildReconciliationJson(
	const FOpenGameAgentAction& Action,
	const FOpenGameAgentActionReconciliation& Reconciliation,
	FString& OutJson)
{
	if (Reconciliation.Resolution != TEXT("applied") &&
		Reconciliation.Resolution != TEXT("not-applied") &&
		Reconciliation.Resolution != TEXT("unknown"))
	{
		return false;
	}

	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), Action.SessionId);
	Root->SetStringField(TEXT("actorId"), Action.ActorId);
	Root->SetStringField(TEXT("operationId"), Action.OperationId);
	Root->SetStringField(TEXT("claimToken"), Action.ClaimToken);
	Root->SetStringField(TEXT("attempt"), Action.Attempt);
	Root->SetStringField(TEXT("resolution"), Reconciliation.Resolution);
	if (!AddOptionalJsonField(Root, TEXT("result"), Reconciliation.ResultJson) ||
		!AddOptionalJsonField(Root, TEXT("modelResult"), Reconciliation.ModelResultJson) ||
		!AddOptionalJsonField(Root, TEXT("authorityAfter"), Reconciliation.AuthorityAfterJson))
	{
		return false;
	}
	return SerializeJsonObject(Root, OutJson);
}

bool BuildActionGetJson(
	const FString& SessionId,
	const FString& ActorId,
	const FString& OperationId,
	FString& OutJson)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), SessionId);
	Root->SetStringField(TEXT("actorId"), ActorId);
	Root->SetStringField(TEXT("operationId"), OperationId);
	return SerializeJsonObject(Root, OutJson);
}

bool BuildAttachmentJson(
	const FString& SessionId,
	const FString& ActorId,
	const FOpenGameAgentImageReference& Reference,
	FString& OutJson)
{
	const TSharedRef<FJsonObject> ReferenceJson = MakeShared<FJsonObject>();
	ReferenceJson->SetStringField(TEXT("type"), TEXT("image-reference"));
	ReferenceJson->SetStringField(TEXT("attachmentId"), Reference.AttachmentId);
	ReferenceJson->SetStringField(TEXT("mediaType"), Reference.MediaType);
	ReferenceJson->SetNumberField(TEXT("bytes"), static_cast<double>(Reference.Bytes));
	ReferenceJson->SetNumberField(TEXT("width"), static_cast<double>(Reference.Width));
	ReferenceJson->SetNumberField(TEXT("height"), static_cast<double>(Reference.Height));
	if (Reference.Name.IsSet())
	{
		ReferenceJson->SetStringField(TEXT("name"), Reference.Name.GetValue());
	}

	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), SessionId);
	Root->SetStringField(TEXT("actorId"), ActorId);
	Root->SetObjectField(TEXT("reference"), ReferenceJson);
	return SerializeJsonObject(Root, OutJson);
}

bool BuildDeferredReadJson(
	const FString& SessionId,
	const FString& ActorId,
	FString& OutJson)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), SessionId);
	Root->SetStringField(TEXT("actorId"), ActorId);
	return SerializeJsonObject(Root, OutJson);
}

bool BuildDeferredCommandJson(
	const FString& SessionId,
	const FString& ActorId,
	const FString& OperationId,
	FString& OutJson)
{
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), SessionId);
	Root->SetStringField(TEXT("actorId"), ActorId);
	Root->SetStringField(TEXT("operationId"), OperationId);
	return SerializeJsonObject(Root, OutJson);
}

bool ParseCoordinate(
	const TSharedPtr<FJsonObject>& Coordinate,
	FOpenGameAgentCoordinate& OutCoordinate)
{
	return Coordinate.IsValid() &&
		Coordinate->TryGetStringField(TEXT("sessionId"), OutCoordinate.SessionId) &&
		Coordinate->TryGetStringField(TEXT("actorId"), OutCoordinate.ActorId) &&
		Coordinate->TryGetStringField(TEXT("runId"), OutCoordinate.RunId) &&
		Coordinate->TryGetStringField(TEXT("turn"), OutCoordinate.Turn);
}

bool ParseDeferredOperation(
	const TSharedPtr<FJsonObject>& Object,
	FOpenGameAgentDeferredOperation& OutOperation)
{
	const TSharedPtr<FJsonObject>* Coordinate = nullptr;
	return Object.IsValid() &&
		Object->TryGetStringField(TEXT("operationId"), OutOperation.OperationId) &&
		Object->TryGetStringField(TEXT("inputId"), OutOperation.InputId) &&
		Object->TryGetObjectField(TEXT("coordinate"), Coordinate) && Coordinate != nullptr &&
		ParseCoordinate(*Coordinate, OutOperation.Coordinate);
}

bool ParseDeferredReadResponse(
	const FString& Json,
	TOptional<FOpenGameAgentDeferredOperation>& OutDeferred)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid())
	{
		return false;
	}
	const TSharedPtr<FJsonValue> Deferred = Root->TryGetField(TEXT("deferred"));
	if (!Deferred.IsValid())
	{
		return false;
	}
	if (Deferred->Type == EJson::Null)
	{
		OutDeferred.Reset();
		return true;
	}
	if (Deferred->Type != EJson::Object)
	{
		return false;
	}
	FOpenGameAgentDeferredOperation Operation;
	if (!ParseDeferredOperation(Deferred->AsObject(), Operation))
	{
		return false;
	}
	OutDeferred = MoveTemp(Operation);
	return true;
}

bool ParseRunAcceptedResponse(
	const FString& Json,
	FOpenGameAgentRunAccepted& OutAccepted)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	const TSharedPtr<FJsonObject>* Coordinate = nullptr;
	return FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid() &&
		Root->TryGetStringField(TEXT("runId"), OutAccepted.RunId) &&
		Root->TryGetObjectField(TEXT("initialCoordinate"), Coordinate) && Coordinate != nullptr &&
		ParseCoordinate(*Coordinate, OutAccepted.InitialCoordinate);
}

bool ParseDeferredAbandonResponse(
	const FString& Json,
	FOpenGameAgentDeferredAbandonResult& OutResult)
{
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Json);
	FString ProviderCancellation;
	if (!FJsonSerializer::Deserialize(Reader, Root) || !Root.IsValid() ||
		!Root->TryGetStringField(TEXT("operationId"), OutResult.OperationId) ||
		!Root->TryGetStringField(TEXT("providerCancellation"), ProviderCancellation))
	{
		return false;
	}
	if (ProviderCancellation == TEXT("completed"))
	{
		OutResult.ProviderCancellation = EOpenGameAgentProviderCancellation::Completed;
		return true;
	}
	if (ProviderCancellation == TEXT("failed"))
	{
		OutResult.ProviderCancellation = EOpenGameAgentProviderCancellation::Failed;
		return true;
	}
	if (ProviderCancellation == TEXT("unavailable"))
	{
		OutResult.ProviderCancellation = EOpenGameAgentProviderCancellation::Unavailable;
		return true;
	}
	return false;
}

FOpenGameAgentClientError ParseRuntimeClientError(
	const int32 StatusCode,
	FString Body)
{
	FOpenGameAgentClientError Error;
	Error.StatusCode = StatusCode;
	TSharedPtr<FJsonObject> Root;
	const TSharedRef<TJsonReader<>> Reader = TJsonReaderFactory<>::Create(Body);
	const TSharedPtr<FJsonObject>* RuntimeError = nullptr;
	FString Code;
	FString Message;
	if (FJsonSerializer::Deserialize(Reader, Root) && Root.IsValid() &&
		Root->TryGetObjectField(TEXT("error"), RuntimeError) && RuntimeError != nullptr &&
		(*RuntimeError)->TryGetStringField(TEXT("code"), Code) &&
		(*RuntimeError)->TryGetStringField(TEXT("message"), Message))
	{
		Error.Code = MoveTemp(Code);
		Error.Message = MoveTemp(Message);
		return Error;
	}
	Error.Message = Body.IsEmpty() ? TEXT("The Runtime rejected the request.") : MoveTemp(Body);
	return Error;
}

FString Utf8Body(const TArray<uint8>& Bytes)
{
	if (Bytes.IsEmpty())
	{
		return FString();
	}
	const FUTF8ToTCHAR Converted(reinterpret_cast<const ANSICHAR*>(Bytes.GetData()), Bytes.Num());
	return FString(Converted.Length(), Converted.Get());
}

#if OGA_WITH_LIBCURL
bool PerformCurlPost(
	const FOpenGameAgentClientLimits& Limits,
	const FString& ExpectedUrl,
	const FString& AccessToken,
	const TArray<uint8>& Upload,
	const int64 MaximumSuccessBytes,
	const int64 MaximumErrorBytes,
	const uint64 RequestStartCycles,
	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>& Request,
	FRawHttpResult& OutResult,
	FRawHttpFailure& OutFailure)
{
	if (Request->IsCancelled() || IsOpenGameAgentTransportStopping())
	{
		OutFailure = {ERawHttpFailureKind::Cancelled, TEXT("The Runtime request was cancelled.")};
		return false;
	}
	const double ElapsedMilliseconds =
		FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - RequestStartCycles);
	int64 RemainingTimeoutMilliseconds = 0;
	if (!TryCalculateRemainingTimeoutMilliseconds(
			Limits.RequestTimeoutMilliseconds,
			ElapsedMilliseconds,
			RemainingTimeoutMilliseconds))
	{
		OutFailure = {ERawHttpFailureKind::Rejected, TEXT("The Runtime request exceeded its configured total timeout.")};
		return false;
	}
	FString RequestScheme;
	FString TlsDomain;
	FString RequestPort;
	if (!ParseCurlBaseUrl(ExpectedUrl, RequestScheme, TlsDomain, RequestPort))
	{
		OutFailure = {ERawHttpFailureKind::Rejected, TEXT("The Runtime URL is invalid.")};
		return false;
	}
	CURL* Handle = curl_easy_init();
	if (Handle == nullptr)
	{
		OutFailure = {ERawHttpFailureKind::Rejected, TEXT("The Runtime HTTP transport could not initialize.")};
		return false;
	}
	curl_slist* RequestHeaders = nullptr;
	ON_SCOPE_EXIT
	{
		curl_slist_free_all(RequestHeaders);
		curl_easy_cleanup(Handle);
	};

	auto AppendHeader = [&RequestHeaders](const FString& Header)
	{
		curl_slist* Appended = curl_slist_append(RequestHeaders, TCHAR_TO_UTF8(*Header));
		if (Appended == nullptr)
		{
			return false;
		}
		RequestHeaders = Appended;
		return true;
	};
	if (!AppendHeader(TEXT("Content-Type: application/json")) || !AppendHeader(TEXT("Accept: application/json, image/png")) ||
		!AppendHeader(TEXT("Expect:")) ||
		(!AccessToken.IsEmpty() && !AppendHeader(TEXT("Authorization: Bearer ") + AccessToken)))
	{
		OutFailure = {ERawHttpFailureKind::Rejected, TEXT("The Runtime HTTP request headers could not be prepared.")};
		return false;
	}

	FCurlResponseCollector Collector(
		MaximumSuccessBytes,
		MaximumErrorBytes,
		Limits.MaxResponseHeaderBytes,
		Limits.MaxResponseHeaders,
		Request);
	const FTCHARToUTF8 UrlUtf8(*ExpectedUrl);
	const bool bConfigured =
		SetCurlOption(Handle, CURLOPT_URL, UrlUtf8.Get()) &&
		SetCurlOption(Handle, CURLOPT_PATH_AS_IS, 1L) &&
		SetCurlOption(Handle, CURLOPT_PROTOCOLS_STR, "http,https") &&
		SetCurlOption(Handle, CURLOPT_REDIR_PROTOCOLS_STR, "http,https") &&
		SetCurlOption(Handle, CURLOPT_FOLLOWLOCATION, 0L) && SetCurlOption(Handle, CURLOPT_MAXREDIRS, 0L) &&
		SetCurlOption(Handle, CURLOPT_UNRESTRICTED_AUTH, 0L) &&
		SetCurlOption(Handle, CURLOPT_NETRC, static_cast<long>(CURL_NETRC_IGNORED)) &&
		SetCurlOption(Handle, CURLOPT_PROXY, "") && SetCurlOption(Handle, CURLOPT_NOPROXY, "*") &&
		SetCurlOption(Handle, CURLOPT_POST, 1L) && SetCurlOption(Handle, CURLOPT_POSTFIELDS, Upload.GetData()) &&
		SetCurlOption(Handle, CURLOPT_POSTFIELDSIZE_LARGE, static_cast<curl_off_t>(Upload.Num())) &&
		SetCurlOption(Handle, CURLOPT_HTTPHEADER, RequestHeaders) && SetCurlOption(Handle, CURLOPT_NOSIGNAL, 1L) &&
		SetCurlOption(Handle, CURLOPT_TIMEOUT_MS, static_cast<long>(RemainingTimeoutMilliseconds)) &&
		SetCurlOption(Handle, CURLOPT_CONNECTTIMEOUT_MS, static_cast<long>(RemainingTimeoutMilliseconds)) &&
		SetCurlOption(Handle, CURLOPT_SUPPRESS_CONNECT_HEADERS, 1L) && SetCurlOption(Handle, CURLOPT_HTTP09_ALLOWED, 0L) &&
		SetCurlOption(Handle, CURLOPT_SSL_VERIFYPEER, 1L) && SetCurlOption(Handle, CURLOPT_SSL_VERIFYHOST, 2L) &&
		SetCurlOption(Handle, CURLOPT_HEADERDATA, &Collector) &&
		SetCurlOption(Handle, CURLOPT_HEADERFUNCTION, &CurlHeaderCallback) &&
		SetCurlOption(Handle, CURLOPT_WRITEDATA, &Collector) &&
		SetCurlOption(Handle, CURLOPT_WRITEFUNCTION, &CurlBodyCallback) &&
		SetCurlOption(Handle, CURLOPT_XFERINFODATA, &Request.Get()) &&
		SetCurlOption(Handle, CURLOPT_XFERINFOFUNCTION, &CurlProgressCallback) &&
		SetCurlOption(Handle, CURLOPT_NOPROGRESS, 0L)
#if OGA_WITH_OPENSSL
		&& SetCurlOption(Handle, CURLOPT_CAINFO, static_cast<const ANSICHAR*>(nullptr)) &&
		SetCurlOption(Handle, CURLOPT_SSL_CTX_FUNCTION, &CurlSslContextCallback) &&
		SetCurlOption(Handle, CURLOPT_SSL_CTX_DATA, &TlsDomain)
#endif
		;
	if (!bConfigured)
	{
		OutFailure = {ERawHttpFailureKind::Rejected, TEXT("The Runtime HTTP transport could not apply its security policy.")};
		return false;
	}

	const CURLcode Transfer = curl_easy_perform(Handle);
	long ResponseCode = 0;
	long RedirectCount = 0;
	ANSICHAR* EffectiveUrl = nullptr;
	const bool bInfoAvailable = curl_easy_getinfo(Handle, CURLINFO_RESPONSE_CODE, &ResponseCode) == CURLE_OK &&
		curl_easy_getinfo(Handle, CURLINFO_REDIRECT_COUNT, &RedirectCount) == CURLE_OK &&
		curl_easy_getinfo(Handle, CURLINFO_EFFECTIVE_URL, &EffectiveUrl) == CURLE_OK && EffectiveUrl != nullptr;
	const bool bComplete = Collector.IsComplete();
	const bool bLimitExceeded = Collector.LimitExceeded();
	OutResult = Collector.TakeResult();
	OutResult.RedirectCount = RedirectCount;
	OutResult.EffectiveUrl = EffectiveUrl == nullptr ? FString() : UTF8_TO_TCHAR(EffectiveUrl);

	if (Request->IsCancelled() || IsOpenGameAgentTransportStopping())
	{
		OutFailure = {ERawHttpFailureKind::Cancelled, TEXT("The Runtime request was cancelled.")};
		return false;
	}
	if (bLimitExceeded)
	{
		OutFailure = {ERawHttpFailureKind::ResponseTooLarge, TEXT("The Runtime response exceeded its configured response limit.")};
		return false;
	}
	if (Transfer != CURLE_OK)
	{
		OutFailure = {ERawHttpFailureKind::Ambiguous, TEXT("The Runtime HTTP transport failed.")};
		return false;
	}
	if (!bInfoAvailable || !bComplete || ResponseCode != OutResult.StatusCode)
	{
		OutFailure = {ERawHttpFailureKind::Ambiguous, TEXT("The Runtime response status or headers are invalid.")};
		return false;
	}
	if (RedirectCount != 0 || (OutResult.StatusCode >= 300 && OutResult.StatusCode < 400) ||
		!OutResult.EffectiveUrl.Equals(ExpectedUrl, ESearchCase::CaseSensitive))
	{
		OutFailure = {ERawHttpFailureKind::Redirected, TEXT("The Runtime request was redirected.")};
		return false;
	}
	return true;
}
#endif

FOpenGameAgentRequestPtr StartPost(
	const FOpenGameAgentClientOptions& Options,
	const TSharedPtr<FOpenGameAgentTransportState, ESPMode::ThreadSafe>& TransportState,
	const FString& Path,
	const FString& BodyJson,
	const int64 MaxSuccessResponseBytes,
	const int64 MaxErrorResponseBytes,
	FRawHttpResultCallback Callback)
{
	const uint64 RequestStartCycles = FPlatformTime::Cycles64();
	if (!Options.IsValid() || !TransportState.IsValid() || !TransportState->IsReady() ||
		!IsOpenGameAgentTransportReady())
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime client transport is not configured.")});
		return nullptr;
	}
	if (MaxSuccessResponseBytes <= 0 || MaxSuccessResponseBytes > MAX_int32 ||
		MaxErrorResponseBytes <= 0 || MaxErrorResponseBytes > MAX_int32)
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime client requires a valid URL, token provider and positive limits.")});
		return nullptr;
	}

	const FTCHARToUTF8 BodyUtf8(*BodyJson);
	if (BodyUtf8.Length() > Options.Limits.MaxRequestBytes)
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime request exceeded its configured byte limit.")});
		return nullptr;
	}

	TArray<uint8> Upload;
	Upload.Append(reinterpret_cast<const uint8*>(BodyUtf8.Get()), BodyUtf8.Length());
	const FString AccessToken = Options.AccessTokenProvider();
	const FTCHARToUTF8 AccessTokenUtf8(*AccessToken);
	if (!IsValidAccessToken(AccessToken) || AccessTokenUtf8.Length() > Options.Limits.MaxAccessTokenBytes)
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime access token is invalid or exceeds its configured byte limit.")});
		return nullptr;
	}

	const FTCHARToUTF8 BaseUrlUtf8(*Options.BaseUrl);
	const FTCHARToUTF8 PathUtf8(*Path);
	if (Path.Len() > MAX_int32 - Options.BaseUrl.Len() || PathUtf8.Length() > Options.Limits.MaxUrlBytes ||
		BaseUrlUtf8.Length() > Options.Limits.MaxUrlBytes - PathUtf8.Length())
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime URL exceeded its configured byte limit.")});
		return nullptr;
	}
	const FString ExpectedUrl = Options.BaseUrl + Path;
	const FTCHARToUTF8 ExpectedUrlUtf8(*ExpectedUrl);
	if (ExpectedUrlUtf8.Length() > Options.Limits.MaxUrlBytes)
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime URL exceeded its configured byte limit.")});
		return nullptr;
	}

#if OGA_WITH_LIBCURL
	if (!TransportState->TryReserve())
	{
		Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("The Runtime transport concurrency and queue budget is exhausted.")});
		return nullptr;
	}
	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> Request =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>(MoveTemp(Callback));
	TransportState->EnqueueReserved(
		[Limits = Options.Limits,
			ExpectedUrl,
			AccessToken,
			Upload = MoveTemp(Upload),
			MaxSuccessResponseBytes,
			MaxErrorResponseBytes,
			RequestStartCycles,
			Request,
			TransportState]() mutable
		{
			FRawHttpResult Result;
			FRawHttpFailure Failure;
			const bool bSucceeded = PerformCurlPost(
				Limits,
				ExpectedUrl,
				AccessToken,
				Upload,
				MaxSuccessResponseBytes,
				MaxErrorResponseBytes,
				RequestStartCycles,
				Request,
				Result,
				Failure);
			AsyncTask(
				ENamedThreads::GameThread,
				[Request,
					TransportState,
					bSucceeded,
					Result = MoveTemp(Result),
					Failure = MoveTemp(Failure)]() mutable
				{
					TransportState->EndRequest();
					if (IsOpenGameAgentTransportStopping())
					{
						Request->Cancel();
					}
					else
					{
						Request->Deliver(bSucceeded, MoveTemp(Result), MoveTemp(Failure));
					}
				});
			NotifyOpenGameAgentTransportProgress();
		},
		[Request, TransportState]
		{
			Request->Cancel();
			AsyncTask(
				ENamedThreads::GameThread,
				[TransportState, Request]
				{
					Request->Cancel();
					TransportState->EndRequest();
				});
			NotifyOpenGameAgentTransportProgress();
		});
	return Request;
#else
	Callback(false, {}, {ERawHttpFailureKind::Rejected, TEXT("This Unreal target does not provide the required no-follow libcurl transport.")});
	return nullptr;
#endif
}
} // namespace

bool FOpenGameAgentClientLimits::IsValid() const
{
	return MaxRequestBytes > 0 && MaxRequestBytes <= MAX_int32 && MaxResponseBytes > 0 &&
		MaxResponseBytes <= MAX_int32 && MaxAttachmentResponseBytes > 0 &&
		MaxAttachmentResponseBytes <= MAX_int32 && MaxResponseHeaderBytes > 0 &&
		MaxResponseHeaderBytes <= MAX_int32 && MaxResponseHeaders > 0 && MaxAccessTokenBytes > 0 &&
		MaxAccessTokenBytes <= MAX_int32 - AuthorizationPrefixBytes && MaxUrlBytes > 0 && MaxUrlBytes <= MAX_int32 &&
		RequestTimeoutMilliseconds > 0 && RequestTimeoutMilliseconds <= MAX_int32;
}

bool FOpenGameAgentClientOptions::IsValid() const
{
	if (!Limits.IsValid())
	{
		return false;
	}
	const FTCHARToUTF8 BaseUrlUtf8(*BaseUrl);
	return BaseUrlUtf8.Length() <= Limits.MaxUrlBytes && IsAllowedBaseUrl(BaseUrl) && AccessTokenProvider;
}

bool FOpenGameAgentImageReference::IsValid(const int64 MaxAttachmentResponseBytes) const
{
	return IsImageAttachmentId(AttachmentId) && MediaType == TEXT("image/png") && Bytes > 0 &&
		Bytes <= MaxAttachmentResponseBytes && Width > 0 && Width <= MaxProtocolInteger && Height > 0 &&
		Height <= MaxProtocolInteger;
}

FOpenGameAgentRuntimeClient::FOpenGameAgentRuntimeClient(FOpenGameAgentClientOptions InOptions)
	: Options(MoveTemp(InOptions))
{
	while (Options.BaseUrl.RemoveFromEnd(TEXT("/")))
	{
	}
	if (Options.IsValid())
	{
		TransportState = AcquireOpenGameAgentTransportState();
	}
}

bool FOpenGameAgentRuntimeClient::IsConfigured() const
{
	return Options.IsValid() && TransportState.IsValid() && TransportState->IsReady() &&
		IsOpenGameAgentTransportReady()
#if !OGA_WITH_LIBCURL
		&& false
#endif
		;
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::NextAction(
	const FString& SessionId,
	const FString& ActorId,
	FOpenGameAgentNextActionCallback Callback) const
{
	return ReadActionDelivery(SessionId, ActorId, false, MoveTemp(Callback));
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::RecoverAction(
	const FString& SessionId,
	const FString& ActorId,
	FOpenGameAgentNextActionCallback Callback) const
{
	return ReadActionDelivery(SessionId, ActorId, true, MoveTemp(Callback));
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::ReadActionDelivery(
	const FString& SessionId,
	const FString& ActorId,
	const bool bRecoveryOnly,
	FOpenGameAgentNextActionCallback Callback) const
{
	check(IsInGameThread());
	const TSharedRef<FJsonObject> Root = MakeShared<FJsonObject>();
	Root->SetStringField(TEXT("sessionId"), SessionId);
	Root->SetStringField(TEXT("actorId"), ActorId);
	FString Json;
	if (!SerializeJsonObject(Root, Json))
	{
		Callback(
			EOpenGameAgentNextActionResult::Failure,
			{},
			{0,
				bRecoveryOnly
					? TEXT("Could not encode the recovery-only action request.")
					: TEXT("Could not encode the next-action request."),
				TEXT("invalid_request")});
		return nullptr;
	}

	return PostJson(
		bRecoveryOnly ? TEXT("/runtime/v1/actions/recovery") : TEXT("/runtime/v1/actions/next"),
		Json,
		[bRecoveryOnly, Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			const int32 StatusCode,
			FString Body,
			FOpenGameAgentClientError Error) mutable
		{
			if (!bSucceeded)
			{
				Callback(EOpenGameAgentNextActionResult::Failure, {}, MoveTemp(Error));
				return;
			}

			TOptional<FOpenGameAgentActionDelivery> Delivery;
			if (!ParseNextActionResponse(Body, Delivery))
			{
				Callback(
					EOpenGameAgentNextActionResult::Failure,
					{},
					{StatusCode,
						bRecoveryOnly
							? TEXT("The recovery-only action response is invalid.")
							: TEXT("The next-action response is invalid."),
						TEXT("invalid_response")});
				return;
			}
			if (!Delivery.IsSet())
			{
				Callback(EOpenGameAgentNextActionResult::Empty, {}, {});
				return;
			}
			if (bRecoveryOnly && Delivery->Kind != EOpenGameAgentActionDeliveryKind::Reconcile)
			{
				Callback(
					EOpenGameAgentNextActionResult::Failure,
					{},
					{StatusCode,
						TEXT("The recovery-only action response attempted a dispatch."),
						TEXT("invalid_response")});
				return;
			}
			Callback(EOpenGameAgentNextActionResult::Delivery, MoveTemp(Delivery.GetValue()), {});
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::GetAction(
	const FString& SessionId,
	const FString& ActorId,
	const FString& OperationId,
	FOpenGameAgentGetActionCallback Callback) const
{
	check(IsInGameThread());
	FString Json;
	if (!BuildActionGetJson(SessionId, ActorId, OperationId, Json))
	{
		Callback(false, {}, {0, TEXT("Could not encode the authoritative action read."), TEXT("invalid_request")});
		return nullptr;
	}
	return PostJson(
		TEXT("/runtime/v1/actions/get"),
		Json,
		[Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			const int32 StatusCode,
			FString Body,
			FOpenGameAgentClientError Error) mutable
		{
			if (!bSucceeded)
			{
				Callback(false, {}, MoveTemp(Error));
				return;
			}
			TOptional<FOpenGameAgentAction> Action;
			if (!ParseGetActionResponse(Body, Action))
			{
				Callback(false, {}, {StatusCode, TEXT("The authoritative action response is invalid."), TEXT("invalid_response")});
				return;
			}
			Callback(true, MoveTemp(Action), {});
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::RecordActionReceipt(
	const FOpenGameAgentAction& Action,
	const FOpenGameAgentActionResult& Result,
	FOpenGameAgentReceiptCallback Callback) const
{
	check(IsInGameThread());
	FString Json;
	if (!BuildReceiptJson(Action, Result, Json))
	{
		Callback(false, {0, TEXT("The authoritative action receipt is invalid."), TEXT("invalid_request")});
		return nullptr;
	}

	return PostJson(
		TEXT("/runtime/v1/actions/receipt"),
		Json,
		[Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			int32,
			FString,
			FOpenGameAgentClientError Error) mutable
		{
			Callback(bSucceeded, bSucceeded ? FOpenGameAgentClientError{} : MoveTemp(Error));
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::ReconcileAction(
	const FOpenGameAgentAction& Action,
	const FOpenGameAgentActionReconciliation& Reconciliation,
	FOpenGameAgentReceiptCallback Callback) const
{
	check(IsInGameThread());
	FString Json;
	if (!BuildReconciliationJson(Action, Reconciliation, Json))
	{
		Callback(false, {0, TEXT("The authoritative action reconciliation is invalid."), TEXT("invalid_request")});
		return nullptr;
	}

	return PostJson(
		TEXT("/runtime/v1/actions/reconcile"),
		Json,
		[Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			int32,
			FString,
			FOpenGameAgentClientError Error) mutable
		{
			Callback(bSucceeded, bSucceeded ? FOpenGameAgentClientError{} : MoveTemp(Error));
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::ReadAttachment(
	const FString& SessionId,
	const FString& ActorId,
	const FOpenGameAgentImageReference& Reference,
	FOpenGameAgentAttachmentCallback Callback) const
{
	check(IsInGameThread());
	if (SessionId.IsEmpty() || ActorId.IsEmpty() || !Reference.IsValid(Options.Limits.MaxAttachmentResponseBytes))
	{
		Callback(false, {}, {0, TEXT("The attachment request is invalid or exceeds its configured byte limit."), TEXT("invalid_request")});
		return nullptr;
	}

	FString Json;
	if (!BuildAttachmentJson(SessionId, ActorId, Reference, Json))
	{
		Callback(false, {}, {0, TEXT("Could not encode the attachment request."), TEXT("invalid_request")});
		return nullptr;
	}

	return StartPost(
		Options,
		TransportState,
		TEXT("/runtime/v1/attachments/read"),
		Json,
		Options.Limits.MaxAttachmentResponseBytes,
		Options.Limits.MaxResponseBytes,
		[MaxJsonResponseBytes = Options.Limits.MaxResponseBytes, Reference, Callback = MoveTemp(Callback)](
			const bool bTransportSucceeded,
			FRawHttpResult Result,
			FRawHttpFailure TransportFailure) mutable
		{
			if (!bTransportSucceeded)
			{
				Callback(
					false,
					{},
					{Result.StatusCode,
						MoveTemp(TransportFailure.Message),
						StableAttachmentFailureCode(TransportFailure.Kind)});
				return;
			}
			if (Result.StatusCode < 200 || Result.StatusCode >= 300)
			{
				if (Result.Body.Num() > MaxJsonResponseBytes)
				{
					Callback(false, {}, {Result.StatusCode, TEXT("The Runtime error response exceeded its configured byte limit."), TEXT("response_too_large")});
					return;
				}
				FString Body = Utf8Body(Result.Body);
				Callback(false, {}, {Result.StatusCode, Body.IsEmpty() ? TEXT("The Runtime rejected the request.") : MoveTemp(Body), TEXT("http_error")});
				return;
			}

			FString ValidationCode;
			FString ValidationMessage;
			if (!ValidateAttachmentResponse(Reference, Result, ValidationCode, ValidationMessage))
			{
				Callback(false, {}, {Result.StatusCode, MoveTemp(ValidationMessage), MoveTemp(ValidationCode)});
				return;
			}
			Callback(true, MoveTemp(Result.Body), {});
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::ReadDeferred(
	const FString& SessionId,
	const FString& ActorId,
	FOpenGameAgentDeferredReadCallback Callback) const
{
	check(IsInGameThread());
	FString Json;
	if (!BuildDeferredReadJson(SessionId, ActorId, Json))
	{
		Callback(false, {}, {0, TEXT("Could not encode the deferred read request.")});
		return nullptr;
	}

	return PostJson(
		TEXT("/runtime/v1/deferred/read"),
		Json,
		[Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			const int32 StatusCode,
			FString Body,
			FOpenGameAgentClientError Error) mutable
		{
			if (!bSucceeded)
			{
				Callback(false, {}, MoveTemp(Error));
				return;
			}
			TOptional<FOpenGameAgentDeferredOperation> Deferred;
			if (!ParseDeferredReadResponse(Body, Deferred))
			{
				Callback(false, {}, {StatusCode, TEXT("The deferred read response is invalid."), TEXT("invalid_response")});
				return;
			}
			Callback(true, MoveTemp(Deferred), {});
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::ResumeDeferred(
	const FString& SessionId,
	const FString& ActorId,
	const FString& OperationId,
	FOpenGameAgentDeferredResumeCallback Callback) const
{
	check(IsInGameThread());
	FString Json;
	if (!BuildDeferredCommandJson(SessionId, ActorId, OperationId, Json))
	{
		Callback(false, {}, {0, TEXT("Could not encode the deferred resume request.")});
		return nullptr;
	}

	return PostJson(
		TEXT("/runtime/v1/deferred/resume"),
		Json,
		[Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			const int32 StatusCode,
			FString Body,
			FOpenGameAgentClientError Error) mutable
		{
			if (!bSucceeded)
			{
				Callback(false, {}, MoveTemp(Error));
				return;
			}
			FOpenGameAgentRunAccepted Accepted;
			if (!ParseRunAcceptedResponse(Body, Accepted))
			{
				Callback(false, {}, {StatusCode, TEXT("The deferred resume response is invalid."), TEXT("invalid_response")});
				return;
			}
			Callback(true, MoveTemp(Accepted), {});
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::AbandonDeferred(
	const FString& SessionId,
	const FString& ActorId,
	const FString& OperationId,
	FOpenGameAgentDeferredAbandonCallback Callback) const
{
	check(IsInGameThread());
	FString Json;
	if (!BuildDeferredCommandJson(SessionId, ActorId, OperationId, Json))
	{
		Callback(false, {}, {0, TEXT("Could not encode the deferred abandon request.")});
		return nullptr;
	}

	return PostJson(
		TEXT("/runtime/v1/deferred/abandon"),
		Json,
		[Callback = MoveTemp(Callback)](
			const bool bSucceeded,
			const int32 StatusCode,
			FString Body,
			FOpenGameAgentClientError Error) mutable
		{
			if (!bSucceeded)
			{
				Callback(false, {}, MoveTemp(Error));
				return;
			}
			FOpenGameAgentDeferredAbandonResult Result;
			if (!ParseDeferredAbandonResponse(Body, Result))
			{
				Callback(false, {}, {StatusCode, TEXT("The deferred abandon response is invalid."), TEXT("invalid_response")});
				return;
			}
			Callback(true, MoveTemp(Result), {});
		});
}

FOpenGameAgentRequestPtr FOpenGameAgentRuntimeClient::PostJson(
	const FString& Path,
	const FString& BodyJson,
	FHttpResultCallback Callback) const
{
	return StartPost(
		Options,
		TransportState,
		Path,
		BodyJson,
		Options.Limits.MaxResponseBytes,
		Options.Limits.MaxResponseBytes,
		[Callback = MoveTemp(Callback)](
			const bool bTransportSucceeded,
			FRawHttpResult Result,
			FRawHttpFailure TransportFailure) mutable
		{
			if (!bTransportSucceeded)
			{
				Callback(
					false,
					Result.StatusCode,
					{},
					{Result.StatusCode,
						MoveTemp(TransportFailure.Message),
						StableTransportFailureCode(TransportFailure.Kind)});
				return;
			}
			FString Body = Utf8Body(Result.Body);
			if (Result.StatusCode < 200 || Result.StatusCode >= 300)
			{
				Callback(false, Result.StatusCode, {}, ParseRuntimeClientError(Result.StatusCode, MoveTemp(Body)));
				return;
			}
			Callback(true, Result.StatusCode, MoveTemp(Body), {});
		});
}

#if WITH_DEV_AUTOMATION_TESTS
IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FOpenGameAgentRuntimeClientValidationTest,
	"OpenGameAgent.RuntimeClient.Validation",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentRuntimeClientValidationTest::RunTest(const FString&)
{
	FOpenGameAgentTransportLimits TransportLimits;
	TransportLimits.MaxConcurrentRequests = 1;
	TransportLimits.MaxQueuedRequests = 0;
	TestTrue(TEXT("the host configures one process transport budget"), FOpenGameAgentModule::ConfigureTransport(TransportLimits));
	TestTrue(TEXT("repeating the process transport budget is idempotent"), FOpenGameAgentModule::ConfigureTransport(TransportLimits));
	FOpenGameAgentTransportLimits ConflictingTransportLimits = TransportLimits;
	ConflictingTransportLimits.MaxQueuedRequests = 1;
	TestFalse(TEXT("changing the process transport budget fails closed"), FOpenGameAgentModule::ConfigureTransport(ConflictingTransportLimits));
	const TSharedPtr<FOpenGameAgentTransportState, ESPMode::ThreadSafe> SharedTransport =
		AcquireOpenGameAgentTransportState();
	TestTrue(TEXT("configured clients share the process transport"), SharedTransport.IsValid());
	if (SharedTransport.IsValid())
	{
		TestTrue(TEXT("the process transport admits its configured active request"), SharedTransport->TryReserve());
		TestFalse(TEXT("the process transport rejects work beyond its queue budget"), SharedTransport->TryReserve());
		SharedTransport->EndRequest();
	}

	TestTrue(TEXT("HTTPS endpoints are allowed"), IsAllowedBaseUrl(TEXT("https://example.test/sidecar")));
	TestFalse(TEXT("hostname loopback HTTP is rejected"), IsAllowedBaseUrl(TEXT("http://localhost:4411")));
	TestTrue(TEXT("IPv4 loopback range is allowed"), IsAllowedBaseUrl(TEXT("http://127.8.9.10:4411")));
	TestTrue(TEXT("IPv6 loopback is allowed"), IsAllowedBaseUrl(TEXT("http://[::1]:4411")));
	TestFalse(TEXT("remote plaintext is rejected"), IsAllowedBaseUrl(TEXT("http://example.test")));
	TestFalse(TEXT("credentials are rejected"), IsAllowedBaseUrl(TEXT("https://user@example.test")));
	TestFalse(TEXT("queries are rejected"), IsAllowedBaseUrl(TEXT("https://example.test?token=x")));
	TestFalse(TEXT("fragments are rejected"), IsAllowedBaseUrl(TEXT("https://example.test#fragment")));
	TestFalse(TEXT("trailing-dot DNS hosts cannot bypass pin matching"), IsAllowedBaseUrl(TEXT("https://example.test.")));
	TestFalse(TEXT("dot path segments are rejected"), IsAllowedBaseUrl(TEXT("https://example.test/a/../sidecar")));
	TestTrue(TEXT("empty access tokens are omitted"), IsValidAccessToken(FString()));
	TestTrue(TEXT("RFC bearer token characters are accepted"), IsValidAccessToken(TEXT("abc.DEF_123-~/+==")));
	TestFalse(TEXT("bearer token whitespace is rejected"), IsValidAccessToken(TEXT("abc def")));
	TestFalse(TEXT("bearer token controls are rejected"), IsValidAccessToken(TEXT("abc\r\ndef")));
	TestFalse(TEXT("bearer token padding is terminal"), IsValidAccessToken(TEXT("abc=def")));

	const FString DispatchJson =
		TEXT("{\"delivery\":{\"kind\":\"dispatch\",\"action\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"operationId\":\"operation\",\"actionName\":\"world.write\",\"attempt\":\"7\",\"status\":\"claimed\",\"claim\":{\"claimToken\":\"claim-token\",\"attempt\":\"7\",\"expiresAt\":\"2026-08-30T12:00:00.000Z\"},\"payload\":{\"tile\":1},\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"8\"}}}}" );
	TOptional<FOpenGameAgentActionDelivery> Dispatch;
	TestTrue(TEXT("a dispatch delivery is decoded"), ParseNextActionResponse(DispatchJson, Dispatch));
	TestTrue(TEXT("the dispatch delivery exists"), Dispatch.IsSet());
	if (Dispatch.IsSet())
	{
		TestEqual(TEXT("dispatch is not reclassified"), Dispatch->Kind, EOpenGameAgentActionDeliveryKind::Dispatch);
		TestEqual(TEXT("the claim token is preserved"), Dispatch->Action.ClaimToken, FString(TEXT("claim-token")));
		TestEqual(TEXT("the claim attempt is preserved"), Dispatch->Action.Attempt, FString(TEXT("7")));
		TestEqual(
			TEXT("the claim expiry is preserved"),
			Dispatch->Action.ClaimExpiresAt,
			FString(TEXT("2026-08-30T12:00:00.000Z")));
	}
	TOptional<FOpenGameAgentActionDelivery> EmptyDelivery;
	TestTrue(TEXT("an empty next response is decoded"), ParseNextActionResponse(TEXT("{\"delivery\":null}"), EmptyDelivery));
	TestFalse(TEXT("an empty next response invents no delivery"), EmptyDelivery.IsSet());
	FString ReconcileJson = DispatchJson.Replace(TEXT("\"dispatch\""), TEXT("\"reconcile\""));
	TOptional<FOpenGameAgentActionDelivery> Reconcile;
	TestTrue(TEXT("a reconcile delivery is decoded"), ParseNextActionResponse(ReconcileJson, Reconcile));
	if (Reconcile.IsSet())
	{
		TestEqual(TEXT("reconcile is not dispatched"), Reconcile->Kind, EOpenGameAgentActionDeliveryKind::Reconcile);
	}
	TOptional<FOpenGameAgentActionDelivery> UncertainReconcile;
	TestTrue(
		TEXT("an uncertain action can be delivered for reconciliation"),
		ParseNextActionResponse(
			ReconcileJson.Replace(TEXT("\"claimed\""), TEXT("\"uncertain\"")),
			UncertainReconcile));
	for (const TCHAR* Status : { TEXT("pending"), TEXT("succeeded"), TEXT("failed"), TEXT("invalid") })
	{
		const FString Replacement = FString::Printf(TEXT("\"%s\""), Status);
		TOptional<FOpenGameAgentActionDelivery> InvalidDispatch;
		TestFalse(
			FString::Printf(TEXT("a %s action cannot be dispatched"), Status),
			ParseNextActionResponse(DispatchJson.Replace(TEXT("\"claimed\""), *Replacement), InvalidDispatch));
		TestFalse(TEXT("an invalid dispatch exposes no delivery"), InvalidDispatch.IsSet());
		TOptional<FOpenGameAgentActionDelivery> InvalidReconcile;
		TestFalse(
			FString::Printf(TEXT("a %s action cannot be delivered for reconciliation"), Status),
			ParseNextActionResponse(ReconcileJson.Replace(TEXT("\"claimed\""), *Replacement), InvalidReconcile));
		TestFalse(TEXT("an invalid reconciliation exposes no delivery"), InvalidReconcile.IsSet());
	}
	TOptional<FOpenGameAgentActionDelivery> UncertainDispatch;
	TestFalse(
		TEXT("an uncertain action cannot be dispatched"),
		ParseNextActionResponse(
			DispatchJson.Replace(TEXT("\"claimed\""), TEXT("\"uncertain\"")),
			UncertainDispatch));

	FOpenGameAgentAction Fence;
	Fence.SessionId = TEXT("session");
	Fence.ActorId = TEXT("actor");
	Fence.OperationId = TEXT("operation");
	Fence.ClaimToken = TEXT("claim-token");
	Fence.Attempt = TEXT("7");
	FOpenGameAgentActionResult Receipt;
	Receipt.Outcome = TEXT("succeeded");
	FString ReceiptJson;
	TestTrue(TEXT("an exact receipt fence serializes"), BuildReceiptJson(Fence, Receipt, ReceiptJson));
	TSharedPtr<FJsonObject> ReceiptObject;
	TestTrue(
		TEXT("the receipt JSON parses"),
		FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(ReceiptJson), ReceiptObject));
	if (ReceiptObject.IsValid())
	{
		TestEqual(TEXT("receipt claim token is exact"), ReceiptObject->GetStringField(TEXT("claimToken")), Fence.ClaimToken);
		TestEqual(TEXT("receipt attempt is exact"), ReceiptObject->GetStringField(TEXT("attempt")), Fence.Attempt);
	}
	FOpenGameAgentActionReconciliation Reconciliation;
	Reconciliation.Resolution = TEXT("applied");
	FString ReconciliationJson;
	TestTrue(
		TEXT("an exact reconciliation fence serializes"),
		BuildReconciliationJson(Fence, Reconciliation, ReconciliationJson));
	TSharedPtr<FJsonObject> ReconciliationObject;
	TestTrue(
		TEXT("the reconciliation JSON parses"),
		FJsonSerializer::Deserialize(TJsonReaderFactory<>::Create(ReconciliationJson), ReconciliationObject));
	if (ReconciliationObject.IsValid())
	{
		TestEqual(
			TEXT("reconciliation claim token is exact"),
			ReconciliationObject->GetStringField(TEXT("claimToken")),
			Fence.ClaimToken);
		TestEqual(
			TEXT("reconciliation attempt is exact"),
			ReconciliationObject->GetStringField(TEXT("attempt")),
			Fence.Attempt);
	}
	int64 RemainingTimeout = 0;
	TestFalse(
		TEXT("a fractional elapsed duration cannot round to libcurl's unbounded zero timeout"),
		TryCalculateRemainingTimeoutMilliseconds(10, 9.5, RemainingTimeout));
	TestTrue(
		TEXT("a positive rounded remainder is admitted"),
		TryCalculateRemainingTimeoutMilliseconds(10, 8.5, RemainingTimeout));
	TestEqual(TEXT("the rounded remainder stays positive"), RemainingTimeout, static_cast<int64>(1));
	bool bCancelledCallbackRan = false;
	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> CancelledRequest =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>(
			[&bCancelledCallbackRan](bool, FRawHttpResult, FRawHttpFailure)
			{
				bCancelledCallbackRan = true;
			});
	CancelledRequest->Cancel();
	CancelledRequest->Deliver(true, {}, {});
	TestFalse(TEXT("cancellation before delivery suppresses the callback"), bCancelledCallbackRan);
	TSharedPtr<FReentrantCancelProbe, ESPMode::ThreadSafe> ReentrantProbe =
		MakeShared<FReentrantCancelProbe, ESPMode::ThreadSafe>();
	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> ReentrantRequest =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>(
			[ReentrantProbe](bool, FRawHttpResult, FRawHttpFailure) {});
	TestEqual(
		TEXT("ambiguous transport classification is independent of display text"),
		StableTransportFailureCode(ERawHttpFailureKind::Ambiguous),
		FString(TEXT("ambiguous_transport")));
	TestEqual(
		TEXT("cancelled transport classification is independent of display text"),
		StableTransportFailureCode(ERawHttpFailureKind::Cancelled),
		FString(TEXT("cancelled")));
	ReentrantProbe->OnDestroy = [WeakRequest = ReentrantRequest.ToWeakPtr()]
	{
		if (const TSharedPtr<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> Request = WeakRequest.Pin())
		{
			Request->Cancel();
		}
	};
	ReentrantProbe.Reset();
	ReentrantRequest->Cancel();
	TestTrue(TEXT("callback destruction may re-enter cancellation without deadlock"), ReentrantRequest->IsDeliveryCancelledForTest());
	FOpenGameAgentClientOptions ImmediateFailureOptions;
	ImmediateFailureOptions.BaseUrl = TEXT("https://example.test");
	ImmediateFailureOptions.AccessTokenProvider = []
	{
		return FString();
	};
	ImmediateFailureOptions.Limits.MaxRequestBytes = 1;
	ImmediateFailureOptions.Limits.MaxResponseBytes = 1;
	ImmediateFailureOptions.Limits.MaxAttachmentResponseBytes = 1;
	ImmediateFailureOptions.Limits.MaxResponseHeaderBytes = 1;
	ImmediateFailureOptions.Limits.MaxResponseHeaders = 1;
	ImmediateFailureOptions.Limits.MaxAccessTokenBytes = 1;
	ImmediateFailureOptions.Limits.MaxUrlBytes = FTCHARToUTF8(*ImmediateFailureOptions.BaseUrl).Length();
	ImmediateFailureOptions.Limits.RequestTimeoutMilliseconds = 1;
	FOpenGameAgentRuntimeClient ImmediateFailureClient(ImmediateFailureOptions);
	bool bImmediateFailureRanOnGameThread = false;
	ImmediateFailureClient.ReadAttachment(
		FString(),
		FString(),
		{},
		[&bImmediateFailureRanOnGameThread](bool, TArray<uint8>, FOpenGameAgentClientError)
		{
			bImmediateFailureRanOnGameThread = IsInGameThread();
		});
	TestTrue(TEXT("immediate public failures keep the GameThread callback contract"), bImmediateFailureRanOnGameThread);

#if OGA_WITH_LIBCURL
	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> ErrorRequest =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>();
	FCurlResponseCollector ErrorCollector(8, 4, 128, 8, ErrorRequest);
	const ANSICHAR ErrorStatus[] = "HTTP/1.1 400 Bad Request\r\n";
	const ANSICHAR OversizedLength[] = "Content-Length: 5\r\n";
	TestEqual(
		TEXT("error status is admitted before its body"),
		ErrorCollector.ReceiveHeader(ErrorStatus, UE_ARRAY_COUNT(ErrorStatus) - 1),
		static_cast<size_t>(UE_ARRAY_COUNT(ErrorStatus) - 1));
	TestEqual(
		TEXT("oversized error length aborts in the header callback"),
		ErrorCollector.ReceiveHeader(OversizedLength, UE_ARRAY_COUNT(OversizedLength) - 1),
		static_cast<size_t>(CURL_WRITEFUNC_ERROR));
	TestTrue(TEXT("error responses use the JSON budget before streaming"), ErrorCollector.LimitExceeded());

	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> RedirectRequest =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>();
	FCurlResponseCollector RedirectCollector(8, 4, 256, 8, RedirectRequest);
	const ANSICHAR EarlyHints[] = "HTTP/1.1 103 Early Hints\r\n";
	const ANSICHAR RedirectStatus[] = "HTTP/1.1 302 Found\r\n";
	const ANSICHAR SuccessStatus[] = "HTTP/1.1 200 OK\r\n";
	const ANSICHAR HeaderEnd[] = "\r\n";
	RedirectCollector.ReceiveHeader(EarlyHints, UE_ARRAY_COUNT(EarlyHints) - 1);
	RedirectCollector.ReceiveHeader(HeaderEnd, UE_ARRAY_COUNT(HeaderEnd) - 1);
	RedirectCollector.ReceiveHeader(RedirectStatus, UE_ARRAY_COUNT(RedirectStatus) - 1);
	RedirectCollector.ReceiveHeader(HeaderEnd, UE_ARRAY_COUNT(HeaderEnd) - 1);
	TestEqual(
		TEXT("a second final status fails closed"),
		RedirectCollector.ReceiveHeader(SuccessStatus, UE_ARRAY_COUNT(SuccessStatus) - 1),
		static_cast<size_t>(CURL_WRITEFUNC_ERROR));
	TestFalse(TEXT("multiple final status responses are never complete"), RedirectCollector.IsComplete());

	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> OrderingRequest =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>();
	FCurlResponseCollector OrderingCollector(8, 4, 128, 8, OrderingRequest);
	const uint8 EarlyBody[] = {1};
	TestEqual(
		TEXT("body before final status fails closed"),
		OrderingCollector.ReceiveBody(EarlyBody, UE_ARRAY_COUNT(EarlyBody)),
		static_cast<size_t>(CURL_WRITEFUNC_ERROR));
	TestFalse(TEXT("body ordering failure is not a resource limit"), OrderingCollector.LimitExceeded());
	TestFalse(TEXT("body ordering failure is never complete"), OrderingCollector.IsComplete());

	const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> HeaderCountRequest =
		MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>();
	FCurlResponseCollector HeaderCountCollector(8, 4, 128, 1, HeaderCountRequest);
	const ANSICHAR SuccessHeaderStatus[] = "HTTP/1.1 200 OK\r\n";
	const ANSICHAR FirstHeader[] = "Content-Type: application/json\r\n";
	const ANSICHAR SecondHeader[] = "Cache-Control: no-store\r\n";
	HeaderCountCollector.ReceiveHeader(SuccessHeaderStatus, UE_ARRAY_COUNT(SuccessHeaderStatus) - 1);
	HeaderCountCollector.ReceiveHeader(FirstHeader, UE_ARRAY_COUNT(FirstHeader) - 1);
	TestEqual(
		TEXT("configured response header count aborts the next header"),
		HeaderCountCollector.ReceiveHeader(SecondHeader, UE_ARRAY_COUNT(SecondHeader) - 1),
		static_cast<size_t>(CURL_WRITEFUNC_ERROR));
	TestTrue(TEXT("header count exhaustion is a response limit"), HeaderCountCollector.LimitExceeded());
#endif

	FOpenGameAgentImageReference Reference;
	Reference.AttachmentId = TEXT("img_4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6");
	Reference.MediaType = TEXT("image/png");
	Reference.Bytes = 8;
	Reference.Width = 1;
	Reference.Height = 1;
	TestTrue(TEXT("valid reference fits its host cap"), Reference.IsValid(8));
	TestFalse(TEXT("reference over host cap is rejected"), Reference.IsValid(7));

	FRawHttpResult Result;
	Result.StatusCode = 200;
	Result.Body = {137, 80, 78, 71, 13, 10, 26, 10};
	Result.Headers = {
		{TEXT("Content-Length"), TEXT("8")},
		{TEXT("Content-Type"), TEXT("image/png")},
		{TEXT("ETag"), TEXT("\"") + Reference.AttachmentId + TEXT("\"")},
		{TEXT("Cache-Control"), TEXT("private, no-store")},
		{TEXT("X-Content-Type-Options"), TEXT("nosniff")},
		{TEXT("Content-Digest"), TEXT("sha-256=:TEtqO+ExSrhhOL70MU3eAi5gCWDYaJosj4YxgC0g2rY=:")},
	};
	FString Code;
	FString Message;
#if OGA_WITH_OPENSSL
	TestTrue(TEXT("valid attachment response verifies"), ValidateAttachmentResponse(Reference, Result, Code, Message));
	Result.Body[7] ^= 1;
	TestFalse(TEXT("tampered attachment body fails"), ValidateAttachmentResponse(Reference, Result, Code, Message));
	TestEqual(TEXT("body tamper code"), Code, FString(TEXT("content_mismatch")));
	Result.Body[7] ^= 1;
	Result.Headers.Add({TEXT("content-digest"), TEXT("sha-256=:TEtqO+ExSrhhOL70MU3eAi5gCWDYaJosj4YxgC0g2rY=:")});
	TestFalse(TEXT("duplicate security header fails"), ValidateAttachmentResponse(Reference, Result, Code, Message));
	TestEqual(TEXT("duplicate header code"), Code, FString(TEXT("invalid_response")));
#else
	TestFalse(TEXT("unsupported target fails closed"), ValidateAttachmentResponse(Reference, Result, Code, Message));
	TestEqual(TEXT("unsupported hash code"), Code, FString(TEXT("hash_unavailable")));
#endif
	return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
	FOpenGameAgentRuntimeClientTransportTest,
	"OpenGameAgent.RuntimeClient.Transport",
	EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentRuntimeClientTransportTest::RunTest(const FString&)
{
#if OGA_WITH_LIBCURL
	const FString BaseUrl = FPlatformMisc::GetEnvironmentVariable(TEXT("OGA_UNREAL_TEST_BASE_URL"));
	const FString TimeoutText = FPlatformMisc::GetEnvironmentVariable(TEXT("OGA_UNREAL_TEST_TIMEOUT_MS"));
	int64 GateTimeoutMilliseconds = 0;
	if (BaseUrl.IsEmpty() || !ParseDecimalInt64(TimeoutText, GateTimeoutMilliseconds) ||
		GateTimeoutMilliseconds <= 0 || GateTimeoutMilliseconds > MAX_int32)
	{
		AddError(TEXT("The isolated transport gate requires its base URL and an explicit positive timeout."));
		return false;
	}

	const FString AccessToken = TEXT("gate-token");
	const FString RequestJson = TEXT("{\"gate\":true}");
	const FTCHARToUTF8 RequestUtf8(*RequestJson);
	TArray<uint8> Upload;
	Upload.Append(reinterpret_cast<const uint8*>(RequestUtf8.Get()), RequestUtf8.Length());
	const TArray<uint8> Fixture = {137, 80, 78, 71, 13, 10, 26, 10};
	const TArray<FString> DeferredRequestJsonFixtures = {
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\"}"),
		TEXT("{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"operationId\":\"operation\"}"),
	};
	const TArray<FString> DeferredResponseJsonFixtures = {
		TEXT("{\"deferred\":{\"operationId\":\"operation\",\"inputId\":\"input\",\"coordinate\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"runId\":\"run\",\"turn\":\"1\"}}}"),
		TEXT("{\"deferred\":null}"),
		TEXT("{\"runId\":\"run\",\"initialCoordinate\":{\"sessionId\":\"gate-session\",\"actorId\":\"gate-actor\",\"runId\":\"run\",\"turn\":\"1\"}}"),
		TEXT("{\"operationId\":\"operation\",\"providerCancellation\":\"unavailable\"}"),
		TEXT("{\"error\":{\"code\":\"deferred_busy\",\"message\":\"busy\"}}"),
		TEXT("{\"error\":{\"code\":\"deferred_not_found\",\"message\":\"missing\"}}"),
		TEXT("{\"error\":{\"code\":\"deferred_busy\"}}"),
		TEXT("not-json"),
	};
	const TArray<FString> Paths = {
		TEXT("/redirect"),
		TEXT("/attachment"),
		TEXT("/error-length"),
		TEXT("/error-chunked"),
		TEXT("/runtime/v1/attachments/read"),
		TEXT("/runtime/v1/deferred/read"),
		TEXT("/runtime/v1/deferred/resume"),
		TEXT("/runtime/v1/deferred/abandon"),
	};
	int64 MaximumUrlBytes = 0;
	for (const FString& Path : Paths)
	{
		const FTCHARToUTF8 UrlUtf8(*(BaseUrl + Path));
		MaximumUrlBytes = FMath::Max(MaximumUrlBytes, static_cast<int64>(UrlUtf8.Length()));
	}

	FOpenGameAgentClientOptions Options;
	Options.BaseUrl = BaseUrl;
	Options.AccessTokenProvider = [AccessToken]
	{
		return AccessToken;
	};
	FOpenGameAgentImageReference PublicReference;
	PublicReference.AttachmentId = TEXT("img_4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6");
	PublicReference.MediaType = TEXT("image/png");
	PublicReference.Bytes = Fixture.Num();
	PublicReference.Width = 1;
	PublicReference.Height = 1;
	FString PublicRequestJson;
	TestTrue(
		TEXT("the public attachment fixture serializes"),
		BuildAttachmentJson(TEXT("gate-session"), TEXT("gate-actor"), PublicReference, PublicRequestJson));
	Options.Limits.MaxRequestBytes = FMath::Max<int64>(Upload.Num(), FTCHARToUTF8(*PublicRequestJson).Length());
	for (const FString& DeferredRequestJson : DeferredRequestJsonFixtures)
	{
		Options.Limits.MaxRequestBytes = FMath::Max<int64>(
			Options.Limits.MaxRequestBytes,
			FTCHARToUTF8(*DeferredRequestJson).Length());
	}
	Options.Limits.MaxResponseBytes = Fixture.Num() - 1;
	for (const FString& DeferredResponseJson : DeferredResponseJsonFixtures)
	{
		Options.Limits.MaxResponseBytes = FMath::Max<int64>(
			Options.Limits.MaxResponseBytes,
			FTCHARToUTF8(*DeferredResponseJson).Length());
	}
	Options.Limits.MaxAttachmentResponseBytes = Fixture.Num();
	Options.Limits.MaxResponseHeaderBytes = CURL_MAX_HTTP_HEADER;
	Options.Limits.MaxResponseHeaders = 7;
	Options.Limits.MaxAccessTokenBytes = FTCHARToUTF8(*AccessToken).Length();
	Options.Limits.MaxUrlBytes = MaximumUrlBytes;
	Options.Limits.RequestTimeoutMilliseconds = GateTimeoutMilliseconds;
	TestTrue(TEXT("the isolated gate config is valid"), Options.IsValid());

	auto Perform = [&Options, &AccessToken, &Upload](
		const FString& Path,
		const int64 MaximumSuccessBytes,
		const int64 MaximumErrorBytes,
		FRawHttpResult& OutResult,
		FRawHttpFailure& OutFailure)
	{
		const TSharedRef<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe> Request =
			MakeShared<FOpenGameAgentCurlRequest, ESPMode::ThreadSafe>();
		return PerformCurlPost(
			Options.Limits,
			Options.BaseUrl + Path,
			AccessToken,
			Upload,
			MaximumSuccessBytes,
			MaximumErrorBytes,
			FPlatformTime::Cycles64(),
			Request,
			OutResult,
			OutFailure);
	};

	FRawHttpResult RedirectResult;
	FRawHttpFailure RedirectFailure;
	TestFalse(
		TEXT("307 is rejected without following"),
		Perform(Paths[0], Fixture.Num(), Fixture.Num() - 1, RedirectResult, RedirectFailure));
	TestEqual(TEXT("redirect status is preserved"), RedirectResult.StatusCode, 307);
	TestTrue(TEXT("redirect failure is explicit"), RedirectFailure.Message.Contains(TEXT("redirected")));
	TestEqual(TEXT("redirect failure keeps its stable kind"), RedirectFailure.Kind, ERawHttpFailureKind::Redirected);

	FRawHttpResult AttachmentResult;
	FRawHttpFailure AttachmentFailure;
	TestTrue(
		TEXT("the attachment success budget admits the fixture"),
		Perform(Paths[1], Fixture.Num(), Fixture.Num() - 1, AttachmentResult, AttachmentFailure));
	TestTrue(TEXT("the attachment fixture is complete"), AttachmentResult.Body == Fixture);

	FRawHttpResult LengthErrorResult;
	FRawHttpFailure LengthFailure;
	TestFalse(
		TEXT("Content-Length rejects an oversized error before its body"),
		Perform(Paths[2], Fixture.Num(), Fixture.Num() - 1, LengthErrorResult, LengthFailure));
	TestTrue(TEXT("Content-Length failure reports its budget"), LengthFailure.Message.Contains(TEXT("limit")));
	TestEqual(TEXT("Content-Length transfer failure keeps its size kind"), LengthFailure.Kind, ERawHttpFailureKind::ResponseTooLarge);
	TestEqual(TEXT("preflight rejection stores no error bytes"), LengthErrorResult.Body.Num(), 0);

	FRawHttpResult ChunkedErrorResult;
	FRawHttpFailure ChunkedFailure;
	TestFalse(
		TEXT("chunked error streaming enforces the JSON budget"),
		Perform(Paths[3], Fixture.Num(), Fixture.Num() - 1, ChunkedErrorResult, ChunkedFailure));
	TestTrue(TEXT("chunked failure reports its budget"), ChunkedFailure.Message.Contains(TEXT("limit")));
	TestEqual(TEXT("chunked transfer failure keeps its size kind"), ChunkedFailure.Kind, ERawHttpFailureKind::ResponseTooLarge);
	TestTrue(
		TEXT("chunked rejection never stores beyond the JSON budget"),
		ChunkedErrorResult.Body.Num() <= Fixture.Num() - 1);

	FOpenGameAgentTransportLimits TransportLimits;
	TransportLimits.MaxConcurrentRequests = 1;
	TransportLimits.MaxQueuedRequests = 0;
	TestTrue(TEXT("the real gate uses the configured process transport"), FOpenGameAgentModule::ConfigureTransport(TransportLimits));
	FOpenGameAgentRuntimeClient Client(Options);
	TestTrue(TEXT("the public client acquires the shared transport"), Client.IsConfigured());
	auto AwaitPublicCompletion = [this, GateTimeoutMilliseconds](
		const FString& Label,
		bool& bCompleted,
		const FOpenGameAgentRequestPtr& Request)
	{
		TestTrue(Label + TEXT(" request is admitted"), Request.IsValid());
		const uint64 StartCycles = FPlatformTime::Cycles64();
		while (!bCompleted &&
			FPlatformTime::ToMilliseconds64(FPlatformTime::Cycles64() - StartCycles) < GateTimeoutMilliseconds)
		{
			FTaskGraphInterface::Get().ProcessThreadUntilIdle(ENamedThreads::GameThread);
			FPlatformProcess::SleepNoStats(0.001F);
		}
		if (!bCompleted && Request.IsValid())
		{
			Request->Cancel();
		}
		TestTrue(Label + TEXT(" completes on the GameThread"), bCompleted);
	};
	bool bPublicCompleted = false;
	bool bPublicSucceeded = false;
	TArray<uint8> PublicBody;
	const FOpenGameAgentRequestPtr PublicRequest = Client.ReadAttachment(
		TEXT("gate-session"),
		TEXT("gate-actor"),
		PublicReference,
		[this, &bPublicCompleted, &bPublicSucceeded, &PublicBody](
			const bool bSucceeded,
			TArray<uint8> Body,
			FOpenGameAgentClientError)
		{
			TestTrue(TEXT("the public completion callback runs on the GameThread"), IsInGameThread());
			bPublicSucceeded = bSucceeded;
			PublicBody = MoveTemp(Body);
			bPublicCompleted = true;
		});
	AwaitPublicCompletion(TEXT("public attachment"), bPublicCompleted, PublicRequest);
	TestTrue(TEXT("the public attachment path succeeds"), bPublicSucceeded);
	TestTrue(TEXT("the public attachment bytes are verified"), PublicBody == Fixture);

	bool bDeferredReadCompleted = false;
	bool bDeferredReadSucceeded = false;
	TOptional<FOpenGameAgentDeferredOperation> PendingDeferred;
	const FOpenGameAgentRequestPtr DeferredReadRequest = Client.ReadDeferred(
		TEXT("gate-session"),
		TEXT("gate-actor"),
		[this, &bDeferredReadCompleted, &bDeferredReadSucceeded, &PendingDeferred](
			const bool bSucceeded,
			TOptional<FOpenGameAgentDeferredOperation> Deferred,
			FOpenGameAgentClientError)
		{
			TestTrue(TEXT("deferred read callback runs on the GameThread"), IsInGameThread());
			bDeferredReadSucceeded = bSucceeded;
			PendingDeferred = MoveTemp(Deferred);
			bDeferredReadCompleted = true;
		});
	AwaitPublicCompletion(TEXT("deferred read"), bDeferredReadCompleted, DeferredReadRequest);
	TestTrue(TEXT("deferred read succeeds"), bDeferredReadSucceeded);
	TestTrue(TEXT("deferred read returns a pending operation"), PendingDeferred.IsSet());
	if (PendingDeferred.IsSet())
	{
		TestEqual(TEXT("deferred operation id"), PendingDeferred->OperationId, FString(TEXT("operation")));
		TestEqual(TEXT("deferred input id"), PendingDeferred->InputId, FString(TEXT("input")));
		TestEqual(TEXT("deferred session coordinate"), PendingDeferred->Coordinate.SessionId, FString(TEXT("gate-session")));
		TestEqual(TEXT("deferred actor coordinate"), PendingDeferred->Coordinate.ActorId, FString(TEXT("gate-actor")));
		TestEqual(TEXT("deferred run coordinate"), PendingDeferred->Coordinate.RunId, FString(TEXT("run")));
		TestEqual(TEXT("deferred turn coordinate"), PendingDeferred->Coordinate.Turn, FString(TEXT("1")));
	}

	bool bEmptyReadCompleted = false;
	bool bEmptyReadSucceeded = false;
	TOptional<FOpenGameAgentDeferredOperation> EmptyDeferred;
	const FOpenGameAgentRequestPtr EmptyReadRequest = Client.ReadDeferred(
		TEXT("gate-session"),
		TEXT("gate-actor"),
		[&bEmptyReadCompleted, &bEmptyReadSucceeded, &EmptyDeferred](
			const bool bSucceeded,
			TOptional<FOpenGameAgentDeferredOperation> Deferred,
			FOpenGameAgentClientError)
		{
			bEmptyReadSucceeded = bSucceeded;
			EmptyDeferred = MoveTemp(Deferred);
			bEmptyReadCompleted = true;
		});
	AwaitPublicCompletion(TEXT("empty deferred read"), bEmptyReadCompleted, EmptyReadRequest);
	TestTrue(TEXT("empty deferred read succeeds"), bEmptyReadSucceeded);
	TestFalse(TEXT("empty deferred read preserves null"), EmptyDeferred.IsSet());

	auto ResumeAndAwait = [this, &Client, &AwaitPublicCompletion](
		const FString& OperationId,
		FOpenGameAgentRunAccepted& OutAccepted,
		FOpenGameAgentClientError& OutError)
	{
		bool bCompleted = false;
		bool bSucceeded = false;
		const FOpenGameAgentRequestPtr Request = Client.ResumeDeferred(
			TEXT("gate-session"),
			TEXT("gate-actor"),
			OperationId,
			[this, &bCompleted, &bSucceeded, &OutAccepted, &OutError](
				const bool bCallSucceeded,
				FOpenGameAgentRunAccepted Accepted,
				FOpenGameAgentClientError Error)
			{
				TestTrue(TEXT("deferred resume callback runs on the GameThread"), IsInGameThread());
				bSucceeded = bCallSucceeded;
				OutAccepted = MoveTemp(Accepted);
				OutError = MoveTemp(Error);
				bCompleted = true;
			});
		AwaitPublicCompletion(TEXT("deferred resume ") + OperationId, bCompleted, Request);
		return bSucceeded;
	};

	FOpenGameAgentRunAccepted FirstAccepted;
	FOpenGameAgentClientError FirstResumeError;
	TestTrue(TEXT("deferred resume succeeds"), ResumeAndAwait(TEXT("operation"), FirstAccepted, FirstResumeError));
	TestEqual(TEXT("deferred resume run id"), FirstAccepted.RunId, FString(TEXT("run")));
	TestEqual(TEXT("deferred resume coordinate run"), FirstAccepted.InitialCoordinate.RunId, FString(TEXT("run")));
	FOpenGameAgentRunAccepted RepeatedAccepted;
	FOpenGameAgentClientError RepeatedResumeError;
	TestTrue(
		TEXT("an explicit repeated deferred resume succeeds independently"),
		ResumeAndAwait(TEXT("operation"), RepeatedAccepted, RepeatedResumeError));

	const TArray<EOpenGameAgentProviderCancellation> ExpectedCancellations = {
		EOpenGameAgentProviderCancellation::Completed,
		EOpenGameAgentProviderCancellation::Failed,
		EOpenGameAgentProviderCancellation::Unavailable,
	};
	for (const EOpenGameAgentProviderCancellation ExpectedCancellation : ExpectedCancellations)
	{
		bool bAbandonCompleted = false;
		bool bAbandonSucceeded = false;
		FOpenGameAgentDeferredAbandonResult AbandonResult;
		const FOpenGameAgentRequestPtr AbandonRequest = Client.AbandonDeferred(
			TEXT("gate-session"),
			TEXT("gate-actor"),
			TEXT("operation"),
			[&bAbandonCompleted, &bAbandonSucceeded, &AbandonResult](
				const bool bSucceeded,
				FOpenGameAgentDeferredAbandonResult Result,
				FOpenGameAgentClientError)
			{
				bAbandonSucceeded = bSucceeded;
				AbandonResult = MoveTemp(Result);
				bAbandonCompleted = true;
			});
		AwaitPublicCompletion(TEXT("deferred abandon"), bAbandonCompleted, AbandonRequest);
		TestTrue(TEXT("deferred abandon succeeds"), bAbandonSucceeded);
		TestEqual(TEXT("deferred abandon operation id"), AbandonResult.OperationId, FString(TEXT("operation")));
		TestTrue(
			TEXT("deferred provider cancellation outcome is preserved"),
			AbandonResult.ProviderCancellation == ExpectedCancellation);
	}

	FOpenGameAgentRunAccepted IgnoredAccepted;
	FOpenGameAgentClientError ConflictError;
	TestFalse(TEXT("deferred conflict fails"), ResumeAndAwait(TEXT("conflict"), IgnoredAccepted, ConflictError));
	TestEqual(TEXT("deferred conflict status"), ConflictError.StatusCode, 409);
	TestEqual(TEXT("deferred conflict code"), ConflictError.Code, FString(TEXT("deferred_busy")));
	TestEqual(TEXT("deferred conflict message"), ConflictError.Message, FString(TEXT("busy")));

	FOpenGameAgentClientError MissingError;
	TestFalse(TEXT("missing deferred operation fails"), ResumeAndAwait(TEXT("missing"), IgnoredAccepted, MissingError));
	TestEqual(TEXT("missing deferred status"), MissingError.StatusCode, 404);
	TestEqual(TEXT("missing deferred code"), MissingError.Code, FString(TEXT("deferred_not_found")));

	FOpenGameAgentClientError MalformedError;
	TestFalse(TEXT("malformed deferred error fails"), ResumeAndAwait(TEXT("malformed"), IgnoredAccepted, MalformedError));
	TestEqual(TEXT("malformed deferred error status"), MalformedError.StatusCode, 500);
	TestEqual(TEXT("malformed deferred fallback body"), MalformedError.Message, FString(TEXT("not-json")));
	TestTrue(TEXT("malformed deferred error invents no code"), MalformedError.Code.IsEmpty());

	FOpenGameAgentClientError PartialError;
	TestFalse(TEXT("partial deferred error fails"), ResumeAndAwait(TEXT("partial"), IgnoredAccepted, PartialError));
	TestEqual(TEXT("partial deferred error status"), PartialError.StatusCode, 500);
	TestEqual(
		TEXT("partial deferred fallback body"),
		PartialError.Message,
		FString(TEXT("{\"error\":{\"code\":\"deferred_busy\"}}")));
	TestTrue(TEXT("partial deferred error invents no code"), PartialError.Code.IsEmpty());

	FOpenGameAgentClientError RedirectedError;
	TestFalse(TEXT("redirected deferred resume fails"), ResumeAndAwait(TEXT("redirect"), IgnoredAccepted, RedirectedError));
	TestEqual(TEXT("redirected deferred status"), RedirectedError.StatusCode, 307);
	TestTrue(TEXT("redirected deferred failure is explicit"), RedirectedError.Message.Contains(TEXT("redirected")));
	return true;
#else
	AddError(TEXT("The real transport gate requires the UE 5.8 Win64 libcurl backend."));
	return false;
#endif
}
#endif
