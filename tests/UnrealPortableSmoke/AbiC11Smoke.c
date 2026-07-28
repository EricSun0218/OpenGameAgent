#include "GameAgentRuntimeAbi.h"

#include <stddef.h>

_Static_assert(offsetof(GAR_RuntimeApiV1, AbiVersion) == 0U, "ABI version must be first");
_Static_assert(
    offsetof(GAR_RuntimeApiV1, StructSize) == sizeof(uint32_t),
    "struct size must be second");

int game_agent_abi_c11_smoke(void)
{
    GAR_RuntimeConfigV1 config = {0};
    config.StructSize = (uint32_t)sizeof(config);

    return config.StructSize == sizeof(GAR_RuntimeConfigV1) &&
                   GAR_GET_RUNTIME_API_V1_SYMBOL[0] == 'g'
               ? 0
               : 1;
}
