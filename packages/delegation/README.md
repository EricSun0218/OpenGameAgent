# @opengameagent/delegation

Optional durable game-task delegation over one shared OpenGameAgent runtime. Hosts register fixed delegate actors,
configure every resource bound, and retain authority over context projection and terminal outcome projection.

Delegation does not create another Agent loop. Child actors use the same runtime and Pi lifecycle as ordinary actors.

The terminal outcome projector receives `childFinalOutput`, the exact child run's last non-empty canonical assistant
text. The manager obtains it from the run result rather than replaying the actor's shared event history, validates the
run identity, and bounds it with `maxResultBytes` before calling host code. Durable recovery reconstructs the same value
from canonical events. Missing, oversized or malformed output from a completed child settles as `result_unavailable`;
aborted and failed children settle as `child_aborted` and `child_failed`. None of those paths invokes the projector.
Deferred children remain nonterminal for explicit resume. The projector's returned `DelegationRecord.result` remains
independently bounded by `maxResultBytes`; model-visible reads remain bounded by `maxModelResultBytes`.
