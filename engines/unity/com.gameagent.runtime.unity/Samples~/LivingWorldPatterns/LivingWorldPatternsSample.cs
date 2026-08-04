using System;
using System.Collections.Generic;
using System.Linq;
using GameAgent.Protocol;
using GameAgent.Simulation;
using GameAgent.Workflow;
using UnityEngine;

namespace GameAgent.Unity.Samples
{
    public sealed class LivingWorldPatternsSample : MonoBehaviour
    {
        private void Start()
        {
            var policy = new LivingWorldPolicy(new LivingWorldPolicyOptions
            {
                MaxActorsPerCycle = 8,
                MaxForegroundActors = 4,
                MaxNearbyActors = 2,
                MaxBackgroundActors = 2,
                DormantAfterGameTicks = 120,
                StarvationAfterGameTicks = 60
            });
            var plan = policy.Plan(
                new LivingWorldCycle { WorldId = "sample-world", GameTick = 120 },
                SampleSignals());
            var build = new WorkflowCompiler().Compile(CreateBuildWorkflow());
            if (plan.Runnable.Count < 4
                || plan.Decisions.Single(item => item.ActorId == "offscreen-merchant").Decision
                != LivingWorldDecisionKinds.Aggregate
                || build.Stages.Count != 4)
            {
                throw new InvalidOperationException("Living-world sample validation failed.");
            }
            Debug.Log("UNITY_LIVING_WORLD_SAMPLE_PASS");
        }

        public static IReadOnlyList<LivingWorldActorSignal> SampleSignals()
        {
            return new[]
            {
                new LivingWorldActorSignal { ActorId = "dialogue-npc", HasDirectPlayerInput = true, PendingMessages = 1, Salience = 1, LastEvaluatedGameTick = 119 },
                new LivingWorldActorSignal { ActorId = "monthly-governor", PendingTriggers = 1, Salience = 0.5, LastEvaluatedGameTick = 30 },
                new LivingWorldActorSignal { ActorId = "nearby-worker", DistanceToNearestPlayer = 20.5, PendingTriggers = 2, Salience = 0.6, LastEvaluatedGameTick = 110 },
                new LivingWorldActorSignal { ActorId = "builder", HasDirectPlayerInput = true, PendingTriggers = 4, Salience = 0.9, LastEvaluatedGameTick = 119, EstimatedSteps = 4 },
                new LivingWorldActorSignal { ActorId = "offscreen-merchant", PendingTriggers = 3, LastEvaluatedGameTick = 0 }
            };
        }

        public static WorkflowDefinition CreateBuildWorkflow()
        {
            var schema = ProtocolJson.ParseElement(
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}");
            var stages = new[]
            {
                Stage("inspect", "game.inspect_build_site", schema),
                Stage("foundation", "game.place_foundation", schema, "inspect"),
                Stage("structure", "game.place_structure", schema, "foundation"),
                Stage("verify", "game.verify_structure", schema, "structure")
            };
            return new WorkflowDefinition("sample-build", "1", schema, schema, "verify", stages,
                new WorkflowLimits(maxStages: 8, maxParallelism: 2));
        }

        private static WorkflowStageDefinition Stage(
            string id,
            string kind,
            System.Text.Json.JsonElement schema,
            params string[] dependsOn)
        {
            return WorkflowStageDefinition.CreateStep(
                id,
                new WorkflowStepReference(kind),
                schema,
                schema,
                dependsOn);
        }
    }
}
