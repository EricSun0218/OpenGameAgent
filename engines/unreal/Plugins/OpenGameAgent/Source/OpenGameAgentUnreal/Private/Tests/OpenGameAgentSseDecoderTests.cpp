#if WITH_DEV_AUTOMATION_TESTS

#include "Misc/AutomationTest.h"
#include "OpenGameAgentSseDecoder.h"

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FOpenGameAgentSplitSseTest,
    "OpenGameAgent.Client.Sse.SplitFrames",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentSplitSseTest::RunTest(const FString& Parameters)
{
    FOpenGameAgentSseDecoder Decoder(1024);
    const FTCHARToUTF8 Bytes(TEXT("id: e1\r\nevent: run.started\r\ndata: {\"type\":\"run.started\"}\r\n\r\n"));
    TArray<FOpenGameAgentDecodedEvent> Events;
    FString Error;
    TestTrue(TEXT("first chunk"), Decoder.Push(reinterpret_cast<const uint8*>(Bytes.Get()), 7, Events, Error));
    TestEqual(TEXT("no partial event"), Events.Num(), 0);
    TestTrue(TEXT("second chunk"), Decoder.Push(reinterpret_cast<const uint8*>(Bytes.Get()) + 7, Bytes.Length() - 7, Events, Error));
    TestEqual(TEXT("one event"), Events.Num(), 1);
    if (Events.Num() == 1)
    {
        TestEqual(TEXT("event id"), Events[0].Id, FString(TEXT("e1")));
        TestEqual(TEXT("event name"), Events[0].Name, FString(TEXT("run.started")));
    }
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FOpenGameAgentBoundedSseTest,
    "OpenGameAgent.Client.Sse.Bounds",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentBoundedSseTest::RunTest(const FString& Parameters)
{
    FOpenGameAgentSseDecoder Decoder(1024);
    TArray<uint8> Bytes;
    Bytes.Init('x', 1025);
    TArray<FOpenGameAgentDecodedEvent> Events;
    FString Error;
    TestFalse(TEXT("oversized partial event is rejected"), Decoder.Push(Bytes.GetData(), Bytes.Num(), Events, Error));
    TestEqual(TEXT("bounded category"), Error, FString(TEXT("event-too-large")));
    return true;
}

#endif
