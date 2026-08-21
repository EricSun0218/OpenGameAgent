#include "Misc/AutomationTest.h"
#include "OpenGameAgentSseParser.h"

#if WITH_DEV_AUTOMATION_TESTS

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FOpenGameAgentSseChunkTest,
    "OpenGameAgent.Unreal.SSE.ChunkedUtf8AndTerminal",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentSseChunkTest::RunTest(const FString& Parameters)
{
    (void)Parameters;
    const FString Source =
        TEXT(": keepalive\r\n")
        TEXT("event: agent\r\n")
        TEXT("data: {\"text\":\"你\"}\r\n\r\n")
        TEXT("event: result\n")
        TEXT("data: {\"status\":\"Completed\",\n")
        TEXT("data: \"route\":\"Agent\",\"sessionRevision\":1}\n\n");
    const FTCHARToUTF8 Utf8(*Source);
    const TArrayView<const uint8> Bytes(reinterpret_cast<const uint8*>(Utf8.Get()), Utf8.Length());

    FOpenGameAgentSseParser Parser(4096, 16384);
    TArray<FString> Names;
    TArray<FString> Values;
    FString Error;
    for (int32 Offset = 0; Offset < Bytes.Num(); Offset += 3)
    {
        const int32 Count = FMath::Min(3, Bytes.Num() - Offset);
        if (!Parser.Feed(
            TArrayView<const uint8>(Bytes.GetData() + Offset, Count),
            [&Names, &Values](const FString& Name, const FString& Value)
            {
                Names.Add(Name);
                Values.Add(Value);
            },
            Error))
        {
            AddError(Error);
            return false;
        }
    }

    TestEqual(TEXT("Only non-terminal events are visible before stream verification"), Names.Num(), 1);
    TestTrue(TEXT("Finish succeeds"), Parser.Finish(
        [&Names, &Values](const FString& Name, const FString& Value)
        {
            Names.Add(Name);
            Values.Add(Value);
        },
        Error));
    TestEqual(TEXT("Two events"), Names.Num(), 2);
    TestEqual(TEXT("Agent event"), Names[0], FString(TEXT("agent")));
    TestEqual(TEXT("UTF-8 payload"), Values[0], FString(TEXT("{\"text\":\"你\"}")));
    TestEqual(TEXT("Terminal event"), Names[1], FString(TEXT("result")));
    TestTrue(TEXT("Multiline data joins with newline"), Values[1].Contains(TEXT("\n")));
    return true;
}

IMPLEMENT_SIMPLE_AUTOMATION_TEST(
    FOpenGameAgentSseFailureTest,
    "OpenGameAgent.Unreal.SSE.FailClosed",
    EAutomationTestFlags::EditorContext | EAutomationTestFlags::EngineFilter)

bool FOpenGameAgentSseFailureTest::RunTest(const FString& Parameters)
{
    (void)Parameters;
    FString Error;
    FOpenGameAgentSseParser MissingTerminal(1024, 4096);
    const FTCHARToUTF8 AgentOnly(TEXT("event: agent\ndata: {}\n\n"));
    TestTrue(TEXT("Agent data parses"), MissingTerminal.Feed(
        TArrayView<const uint8>(reinterpret_cast<const uint8*>(AgentOnly.Get()), AgentOnly.Length()),
        [](const FString&, const FString&) {},
        Error));
    TestFalse(TEXT("Missing terminal fails"), MissingTerminal.Finish(
        [](const FString&, const FString&) {},
        Error));

    FOpenGameAgentSseParser OpenEnded(1024, 4096);
    Error.Reset();
    TArray<FString> OpenEndedEvents;
    TestTrue(TEXT("Open-ended data parses"), OpenEnded.Feed(
        TArrayView<const uint8>(reinterpret_cast<const uint8*>(AgentOnly.Get()), AgentOnly.Length()),
        [&OpenEndedEvents](const FString& Name, const FString&) { OpenEndedEvents.Add(Name); },
        Error));
    TestTrue(TEXT("Open-ended stream does not require a result"), OpenEnded.FinishOpenStream(
        [&OpenEndedEvents](const FString& Name, const FString&) { OpenEndedEvents.Add(Name); },
        Error));
    TestEqual(TEXT("Open-ended event count"), OpenEndedEvents.Num(), 1);
    TestEqual(TEXT("Open-ended event name"), OpenEndedEvents[0], FString(TEXT("agent")));

    FOpenGameAgentSseParser AfterTerminal(1024, 4096);
    const FTCHARToUTF8 InvalidOrder(
        TEXT("event: result\ndata: {}\n\nevent: agent\ndata: {}\n\n"));
    Error.Reset();
    TestFalse(TEXT("Data after terminal fails"), AfterTerminal.Feed(
        TArrayView<const uint8>(reinterpret_cast<const uint8*>(InvalidOrder.Get()), InvalidOrder.Length()),
        [](const FString&, const FString&) {},
        Error));

    FOpenGameAgentSseParser Oversized(4, 4096);
    const FTCHARToUTF8 Large(TEXT("event: agent\ndata: {\"large\":true}\n\n"));
    Error.Reset();
    TestFalse(TEXT("Oversized event fails"), Oversized.Feed(
        TArrayView<const uint8>(reinterpret_cast<const uint8*>(Large.Get()), Large.Length()),
        [](const FString&, const FString&) {},
        Error));

    FOpenGameAgentSseParser TooManyEvents(1024, 4096, 1);
    const FTCHARToUTF8 EventOverflow(
        TEXT("event: agent\ndata: {}\n\nevent: result\ndata: {}\n\n"));
    Error.Reset();
    TestFalse(TEXT("Event-count limit fails"), TooManyEvents.Feed(
        TArrayView<const uint8>(reinterpret_cast<const uint8*>(EventOverflow.Get()), EventOverflow.Length()),
        [](const FString&, const FString&) {},
        Error));
    return true;
}

#endif
