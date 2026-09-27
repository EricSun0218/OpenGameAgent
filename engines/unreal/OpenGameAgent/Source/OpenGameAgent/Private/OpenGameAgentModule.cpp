#include "OpenGameAgentModule.h"

#include "OpenGameAgentTransportLifecycle.h"
#include "Modules/ModuleManager.h"

bool FOpenGameAgentTransportLimits::IsValid() const
{
	return MaxConcurrentRequests > 0 && MaxQueuedRequests >= 0 &&
		MaxConcurrentRequests <= MAX_int32 - MaxQueuedRequests;
}

bool FOpenGameAgentModule::ConfigureTransport(const FOpenGameAgentTransportLimits& Limits)
{
	return Limits.IsValid() && ConfigureOpenGameAgentTransport(Limits);
}

void FOpenGameAgentModule::StartupModule()
{
	bTransportStarted = StartOpenGameAgentTransport();
}

void FOpenGameAgentModule::ShutdownModule()
{
	if (bTransportStarted)
	{
		StopOpenGameAgentTransport();
		bTransportStarted = false;
	}
}

bool FOpenGameAgentModule::SupportsDynamicReloading()
{
	return false;
}

IMPLEMENT_MODULE(FOpenGameAgentModule, OpenGameAgent)
