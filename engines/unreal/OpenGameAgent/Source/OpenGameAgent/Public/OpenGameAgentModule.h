#pragma once

#include "CoreMinimal.h"
#include "Modules/ModuleManager.h"

struct OPENGAMEAGENT_API FOpenGameAgentTransportLimits
{
	int32 MaxConcurrentRequests = 0;
	int32 MaxQueuedRequests = -1;

	bool IsValid() const;
};

class OPENGAMEAGENT_API FOpenGameAgentModule final : public IModuleInterface
{
public:
	static bool ConfigureTransport(const FOpenGameAgentTransportLimits& Limits);

	virtual void StartupModule() override;
	virtual void ShutdownModule() override;
	virtual bool SupportsDynamicReloading() override;

private:
	bool bTransportStarted = false;
};
