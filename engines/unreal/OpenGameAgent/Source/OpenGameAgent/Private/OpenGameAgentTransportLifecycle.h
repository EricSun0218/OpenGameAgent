#pragma once

struct FOpenGameAgentTransportLimits;

bool StartOpenGameAgentTransport();
bool ConfigureOpenGameAgentTransport(const FOpenGameAgentTransportLimits& Limits);
void StopOpenGameAgentTransport();
bool IsOpenGameAgentTransportReady();
bool IsOpenGameAgentTransportStopping();
bool TryBeginOpenGameAgentTransportWork();
void EndOpenGameAgentTransportWork();
