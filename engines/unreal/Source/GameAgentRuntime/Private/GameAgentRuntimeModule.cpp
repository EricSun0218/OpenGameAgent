#include "GameAgentRuntimeModule.h"

#include "Containers/Ticker.h"
#include "Misc/AssertionMacros.h"

IMPLEMENT_MODULE(FGameAgentRuntimeModule, GameAgentRuntime)

FGameAgentRuntimeModule& FGameAgentRuntimeModule::Get()
{
    return FModuleManager::LoadModuleChecked<FGameAgentRuntimeModule>(TEXT("GameAgentRuntime"));
}

bool FGameAgentRuntimeModule::IsAvailable()
{
    return FModuleManager::Get().IsModuleLoaded(TEXT("GameAgentRuntime"));
}

void FGameAgentRuntimeModule::StartupModule()
{
    Dispatcher_ =
        MakeShared<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>();
    HostRouter_ =
        MakeShared<FGameAgentHostRouter, ESPMode::ThreadSafe>(Dispatcher_.ToSharedRef());
    TickerHandle_ = FTSTicker::GetCoreTicker().AddTicker(
        FTickerDelegate::CreateRaw(this, &FGameAgentRuntimeModule::Tick),
        0.0f);
}

void FGameAgentRuntimeModule::ShutdownModule()
{
    if (TickerHandle_.IsValid())
    {
        FTSTicker::GetCoreTicker().RemoveTicker(TickerHandle_);
        TickerHandle_.Reset();
    }
    if (HostRouter_.IsValid())
    {
        HostRouter_->Stop();
        HostRouter_->UnbindHost();
    }
    if (Dispatcher_.IsValid())
    {
        Dispatcher_->Stop();
        Dispatcher_.Reset();
    }
    HostRouter_.Reset();
}

TSharedRef<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe>
FGameAgentRuntimeModule::GetDispatcher() const
{
    check(Dispatcher_.IsValid());
    return Dispatcher_.ToSharedRef();
}

TSharedRef<FGameAgentHostRouter, ESPMode::ThreadSafe>
FGameAgentRuntimeModule::GetHostRouter() const
{
    check(HostRouter_.IsValid());
    return HostRouter_.ToSharedRef();
}

bool FGameAgentRuntimeModule::Tick(float)
{
    if (Dispatcher_.IsValid())
    {
        Dispatcher_->Drain();
    }
    return true;
}
