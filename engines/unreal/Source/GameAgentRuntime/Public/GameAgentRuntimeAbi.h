#pragma once

#include <stdint.h>

#if defined(_WIN32)
#define GAR_CALL __cdecl
#else
#define GAR_CALL
#endif

#ifdef __cplusplus
extern "C"
{
#endif

enum
{
    GAR_ABI_VERSION_1 = 1
};

typedef enum GAR_Result
{
    GAR_RESULT_OK = 0,
    GAR_RESULT_INVALID_ARGUMENT = 1,
    GAR_RESULT_UNSUPPORTED_ABI = 2,
    GAR_RESULT_NOT_READY = 3,
    GAR_RESULT_QUEUE_FULL = 4,
    GAR_RESULT_INTERNAL_ERROR = 5
} GAR_Result;

typedef struct GAR_ByteSpan
{
    const uint8_t* Data;
    uint64_t Size;
} GAR_ByteSpan;

typedef struct GAR_RuntimeConfigV1
{
    uint32_t StructSize;
    uint32_t Flags;
    GAR_ByteSpan StoragePathUtf8;
    GAR_ByteSpan OptionsJsonUtf8;
} GAR_RuntimeConfigV1;

typedef void(GAR_CALL* GAR_EventCallbackV1)(
    void* UserData,
    uint64_t CorrelationId,
    GAR_ByteSpan RuntimeEventJsonUtf8);

typedef void(GAR_CALL* GAR_LogCallbackV1)(
    void* UserData,
    int32_t Level,
    GAR_ByteSpan MessageUtf8);

typedef struct GAR_CallbacksV1
{
    uint32_t StructSize;
    uint32_t Reserved;
    void* UserData;
    GAR_EventCallbackV1 OnEvent;
    GAR_LogCallbackV1 OnLog;
} GAR_CallbacksV1;

typedef void* GAR_RuntimeHandle;

typedef struct GAR_RuntimeApiV1
{
    uint32_t AbiVersion;
    uint32_t StructSize;

    int32_t(GAR_CALL* Create)(
        const GAR_RuntimeConfigV1* Config,
        const GAR_CallbacksV1* Callbacks,
        GAR_RuntimeHandle* OutRuntime);

    void(GAR_CALL* Destroy)(GAR_RuntimeHandle Runtime);

    int32_t(GAR_CALL* SubmitObservation)(
        GAR_RuntimeHandle Runtime,
        uint64_t CorrelationId,
        GAR_ByteSpan ObservationJsonUtf8);

    int32_t(GAR_CALL* SubmitActionReceipt)(
        GAR_RuntimeHandle Runtime,
        uint64_t CorrelationId,
        GAR_ByteSpan ActionReceiptJsonUtf8);

    int32_t(GAR_CALL* SendControl)(
        GAR_RuntimeHandle Runtime,
        uint64_t CorrelationId,
        GAR_ByteSpan ControlJsonUtf8);

    int32_t(GAR_CALL* Poll)(
        GAR_RuntimeHandle Runtime,
        uint32_t MaxEvents);
} GAR_RuntimeApiV1;

typedef int32_t(GAR_CALL* GAR_GetRuntimeApiV1Fn)(
    uint32_t RequestedAbiVersion,
    GAR_RuntimeApiV1* OutApi);

#define GAR_GET_RUNTIME_API_V1_SYMBOL "game_agent_runtime_get_api_v1"

#ifdef __cplusplus
}
#endif
