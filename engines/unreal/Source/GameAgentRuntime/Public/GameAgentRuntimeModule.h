#pragma once

#include "GameAgentHostBoundary.h"
#include "GameAgentMainThreadDispatcher.h"
#include "Modules/ModuleInterface.h"
#include "Modules/ModuleManager.h"
#include "Templates/SharedPointer.h"

class GAMEAGENTRUNTIME_API FGameAgentRuntimeModule final : public IModuleInterface
{
public:
    static FGameAgentRuntimeModule& Get();
    static bool IsAvailable();

    virtual void StartupModule() override;
    virtual void ShutdownModule() override;

    TSharedRef<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe> GetDispatcher() const;
    TSharedRef<FGameAgentHostRouter, ESPMode::ThreadSafe> GetHostRouter() const;

private:
    bool Tick(float DeltaTime);

    TSharedPtr<FGameAgentMainThreadDispatcher, ESPMode::ThreadSafe> Dispatcher_;
    TSharedPtr<FGameAgentHostRouter, ESPMode::ThreadSafe> HostRouter_;
    FDelegateHandle TickerHandle_;
};
