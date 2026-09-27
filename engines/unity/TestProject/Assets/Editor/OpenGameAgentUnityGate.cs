using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OpenGameAgent.Unity;
using OpenGameAgent.Unity.Protocol;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;
using ProtocolResolution = OpenGameAgent.Unity.Protocol.Resolution;

public static class OpenGameAgentUnityGate
{
    private static readonly Stack<IEnumerator> TransportGate = new Stack<IEnumerator>();
    private static UnityWebRequestAsyncOperation currentOperation;

    public static void Run()
    {
        try
        {
            var request = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" };
            string json = RuntimeProtocolJson.Serialize(request);
            RuntimeActionNextRequest roundTrip = RuntimeProtocolJson.Deserialize<RuntimeActionNextRequest>(json);
            if (roundTrip.SessionId != "session" || roundTrip.ActorId != "actor")
            {
                throw new InvalidOperationException("Runtime DTO round trip failed.");
            }

            VerifyCanonicalEventShapes();
            VerifyApprovalPresentation();
            VerifyAuthoritativeActionFences();

            var client = new UnityRuntimeClient("http://127.255.0.1:1/runtime", string.Empty, 1024, 1024, 1024, 1);
            try
            {
                _ = new UnityRuntimeClient("http://example.com", "secret", 1, 1, 1, 1);
                throw new InvalidOperationException("Remote plaintext Runtime URL was accepted.");
            }
            catch (ArgumentException)
            {
            }

            try
            {
                _ = new UnityRuntimeClient("http://127.0.0.1:1", "abc def", 1, 1, 1, 1);
                throw new InvalidOperationException("Invalid Runtime bearer token was accepted.");
            }
            catch (ArgumentException)
            {
            }

            var tinyRequestClient = new UnityRuntimeClient(
                "http://127.0.0.1:1",
                string.Empty,
                1,
                1,
                1,
                1);
            MethodInfo serializeRequestBody = typeof(UnityRuntimeClient).GetMethod(
                "SerializeRequestBody",
                BindingFlags.Instance | BindingFlags.NonPublic);
            try
            {
                _ = serializeRequestBody.Invoke(tinyRequestClient, new object[]
                {
                    new RuntimeRunRequest
                    {
                        SessionId = "session",
                        ActorId = "actor",
                        InputId = "input",
                        Payload = null,
                    },
                });
                throw new InvalidOperationException("Oversized Runtime request body was not rejected before transport.");
            }
            catch (TargetInvocationException exception) when (exception.InnerException is InvalidDataException)
            {
            }

            MethodInfo createPost = typeof(UnityRuntimeClient).GetMethod("CreatePost", BindingFlags.Instance | BindingFlags.NonPublic);
            using (var transportRequest = (UnityEngine.Networking.UnityWebRequest)createPost.Invoke(
                client,
                new object[] { "/runtime/v1/initialize", Array.Empty<byte>(), 1, 1 }))
            {
                if (transportRequest.redirectLimit != 0)
                {
                    throw new InvalidOperationException("Runtime redirects were enabled.");
                }
            }

            MethodInfo responseLimit = typeof(UnityRuntimeClient).GetMethod(
                "ResponseLimitForStatus",
                BindingFlags.Static | BindingFlags.NonPublic);
            if ((int)responseLimit.Invoke(null, new object[] { 200L, 8, 2 }) != 8
                || (int)responseLimit.Invoke(null, new object[] { 400L, 8, 2 }) != 2
                || (int)responseLimit.Invoke(null, new object[] { 0L, 8, 2 }) != 0)
            {
                throw new InvalidOperationException("Runtime response budgets are not status-aware and fail-closed.");
            }

            TransportGate.Clear();
            TransportGate.Push(RunTransportGates(createPost));
            EditorApplication.update += AdvanceTransportGate;
            AdvanceTransportGate();
        }
        catch (Exception exception)
        {
            Finish(1, exception);
        }
    }

    private static void VerifyCanonicalEventShapes()
    {
        const string updatedJson = "{\"type\":\"event\",\"revision\":\"4\",\"event\":{\"protocolVersion\":\"1\",\"eventId\":\"event-update\",\"sequence\":\"4\",\"revision\":\"4\",\"occurredAt\":\"2026-08-29T00:00:00.000Z\",\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},\"audience\":{\"visibility\":\"internal\"},\"data\":{\"type\":\"item.updated\",\"itemId\":\"tool-item\",\"audience\":{\"visibility\":\"internal\"},\"updateOrdinal\":\"2\",\"content\":[{\"type\":\"text\",\"text\":\"working\"},{\"type\":\"media-summary\",\"mediaType\":\"image/png\",\"bytes\":8,\"sha256\":\"4c4b6a3be1314ab86138bef4314dde022e600960d8689a2c8f8631802d20dab6\"}]}}}";
        RuntimeSseFrame updated = RuntimeProtocolJson.Deserialize<RuntimeSseFrame>(updatedJson);
        if (updated.Event == null
            || updated.Event.Data.Type != DataType.ItemUpdated
            || updated.Event.Data.UpdateOrdinal != "2"
            || updated.Event.Data.Content == null
            || updated.Event.Data.Content.Length != 2
            || updated.Event.Data.Content[1].Type != PurpleType.MediaSummary
            || updated.Event.Data.Content[1].Bytes != 8)
        {
            throw new InvalidOperationException("Canonical Tool update event was not decoded.");
        }

        const string messageJson = "{\"type\":\"event\",\"revision\":\"5\",\"event\":{\"protocolVersion\":\"1\",\"eventId\":\"event-message\",\"sequence\":\"5\",\"revision\":\"5\",\"occurredAt\":\"2026-08-29T00:00:00.000Z\",\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},\"audience\":{\"visibility\":\"owner\"},\"data\":{\"type\":\"item.completed\",\"item\":{\"itemType\":\"message\",\"itemId\":\"assistant-item\",\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"visible\",\"sourceIndex\":3}],\"audience\":{\"visibility\":\"owner\"}}}}}";
        RuntimeSseFrame message = RuntimeProtocolJson.Deserialize<RuntimeSseFrame>(messageJson);
        if (message.Event == null
            || message.Event.Data.Item == null
            || message.Event.Data.Item.Content == null
            || message.Event.Data.Item.Content.Length != 1
            || message.Event.Data.Item.Content[0].SourceIndex != 3)
        {
            throw new InvalidOperationException("Canonical assistant source index was not decoded.");
        }

        const string deferredJson = "{\"type\":\"event\",\"revision\":\"6\",\"event\":{\"protocolVersion\":\"1\",\"eventId\":\"event-turn\",\"sequence\":\"6\",\"revision\":\"6\",\"occurredAt\":\"2026-08-29T00:00:00.000Z\",\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},\"audience\":{\"visibility\":\"owner\"},\"data\":{\"type\":\"turn.completed\",\"finishReason\":\"deferred\",\"toolResultItemIds\":[]}}}";
        RuntimeSseFrame deferred = RuntimeProtocolJson.Deserialize<RuntimeSseFrame>(deferredJson);
        if (deferred.Event == null || deferred.Event.Data.FinishReason != FinishReason.Deferred)
        {
            throw new InvalidOperationException("Canonical deferred turn reason was not decoded.");
        }
    }

    private static void VerifyApprovalPresentation()
    {
        const string json = "{\"approvals\":[{\"request\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},\"approvalId\":\"apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"requestFingerprint\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\",\"policyId\":\"high-risk\",\"operationId\":\"act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\"intentFingerprint\":\"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\"actionName\":\"world.write\",\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"3\",\"revision\":\"9\"},\"presentation\":{\"kind\":\"game-choice\",\"optionId\":\"opaque-option-7\",\"label\":\"赠送礼物\"},\"requestedAt\":\"2026-08-28T00:00:00.000Z\",\"expiresAt\":\"2026-08-28T00:00:01.000Z\"},\"status\":\"pending\",\"revision\":\"1\",\"updatedAt\":\"2026-08-28T00:00:00.000Z\"}]}";
        RuntimeApprovalListResponse response = RuntimeProtocolJson.Deserialize<RuntimeApprovalListResponse>(json);
        if (!HasExactApprovalPresentation(response.Approvals[0].Request.Presentation))
        {
            throw new InvalidOperationException("Approval presentation JSON was not decoded exactly.");
        }
    }

    private static bool HasExactApprovalPresentation(object value)
    {
        JObject presentation = value as JObject;
        return presentation != null
            && (string)presentation["optionId"] == "opaque-option-7"
            && (string)presentation["label"] == "赠送礼物";
    }

    private static void VerifyAuthoritativeActionFences()
    {
        const string operationId = "act_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var action = new RuntimeActionRecord
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = operationId,
            Claim = new Claim
            {
                ClaimToken = "claim-token",
                Attempt = "7",
                ExpiresAt = "2099-01-01T00:00:00.000Z",
            },
        };
        bool active = true;
        var execution = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 11L, new Func<long, bool>(value => active && value == 11L) });
        var deniedExecution = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 11L, new Func<long, bool>(value => active && value == 11L) });
        int deniedTransactions = 0;
        if (deniedExecution.TryCommit(
            _ => false,
            _ =>
            {
                deniedTransactions += 1;
                return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
            })
            || deniedTransactions != 0)
        {
            throw new InvalidOperationException("Unity ran a game transaction after expected authority was rejected.");
        }
        if (!execution.TryCommit(
                _ => true,
                _ => new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded })
            || execution.TryCommit(
                _ => true,
                _ => new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded }))
        {
            throw new InvalidOperationException("Unity world-write permit did not close after one synchronous transaction.");
        }

        bool authorityPermitOpen = true;
        var authorityClosedExecution = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[]
            {
                action,
                11L,
                new Func<long, bool>(_ => authorityPermitOpen),
            });
        int authorityClosedTransactions = 0;
        if (authorityClosedExecution.TryCommit(
                _ =>
                {
                    authorityPermitOpen = false;
                    return true;
                },
                _ =>
                {
                    authorityClosedTransactions += 1;
                    return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
                })
            || authorityClosedTransactions != 0)
        {
            throw new InvalidOperationException(
                "Unity did not recheck its local permit after expected-authority validation.");
        }

        var reentrant = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 11L, new Func<long, bool>(value => value == 11L) });
        int worldWrites = 0;
        bool nestedCommit = true;
        bool outerCommit = reentrant.TryCommit(_ => true, _ =>
        {
            worldWrites += 1;
            nestedCommit = reentrant.TryCommit(__ => true, __ =>
            {
                worldWrites += 1;
                return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
            });
            return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
        });
        if (!outerCommit || nestedCommit || worldWrites != 1)
        {
            throw new InvalidOperationException("Unity left its world-write permit open during a synchronous game transaction.");
        }


        var authorityReentrant = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 11L, new Func<long, bool>(_ => true) });
        bool nestedAuthorityCommit = true;
        int authorityReentrantWrites = 0;
        bool authorityOuterCommit = authorityReentrant.TryCommit(
            _ =>
            {
                nestedAuthorityCommit = authorityReentrant.TryCommit(
                    __ => true,
                    __ =>
                    {
                        authorityReentrantWrites += 1;
                        return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
                    });
                return true;
            },
            _ =>
            {
                authorityReentrantWrites += 1;
                return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
            });
        if (!authorityOuterCommit || nestedAuthorityCommit || authorityReentrantWrites != 1)
        {
            throw new InvalidOperationException(
                "Unity reopened its reserved permit during expected-authority validation.");
        }

        int executionPredicateCalls = 0;
        int backgroundWrites = 0;
        var backgroundExecution = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[]
            {
                action,
                11L,
                new Func<long, bool>(_ =>
                {
                    Interlocked.Increment(ref executionPredicateCalls);
                    return true;
                }),
            });
        bool backgroundCommit = Task.Run(() => backgroundExecution.TryCommit(_ => true, _ =>
        {
            Interlocked.Increment(ref backgroundWrites);
            return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
        })).GetAwaiter().GetResult();
        if (backgroundCommit || executionPredicateCalls != 0 || backgroundWrites != 0)
        {
            throw new InvalidOperationException("Unity allowed a background world write or touched its main-thread clock predicate.");
        }

        int backgroundCompletionPredicateCalls = 0;
        var backgroundCompletionContext = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[]
            {
                action,
                11L,
                new Func<long, bool>(_ =>
                {
                    Interlocked.Increment(ref backgroundCompletionPredicateCalls);
                    return true;
                }),
            });
        bool backgroundCompletionWithoutWrite = Task.Run(() =>
            backgroundCompletionContext.TryCompleteWithoutWrite(
                new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Rejected })).GetAwaiter().GetResult();
        if (backgroundCompletionWithoutWrite
            || backgroundCompletionPredicateCalls != 0
            || backgroundCompletionContext.IsCompleted)
        {
            throw new InvalidOperationException(
                "Unity allowed a background completion to mutate its main-thread action state.");
        }

        var late = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 12L, new Func<long, bool>(value => active && value == 12L) });
        active = false;
        if (late.TryCommit(
            _ => true,
            _ => new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded }))
        {
            throw new InvalidOperationException("Unity accepted a world write after its local epoch fence closed.");
        }

        var captured = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 13L, new Func<long, bool>(value => value == 13L) });
        var capturedInspection = (AuthoritativeActionInspectionContext)typeof(AuthoritativeActionInspectionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 13L, new Func<long, bool>(value => value == 13L) });
        RuntimeActionRecord stableAction = captured.Action;
        if (stableAction.GetType() != typeof(RuntimeActionRecord) || !ReferenceEquals(stableAction, action))
        {
            throw new InvalidOperationException(
                "Unity did not expose the stable generated action record directly through its adapter API.");
        }
        action.SessionId = "mutated-session";
        action.ActorId = "mutated-actor";
        action.OperationId = "act_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        action.Claim.ClaimToken = "mutated-token";
        action.Claim.Attempt = "99";
        object capturedFence = typeof(AuthoritativeActionExecutionContext).GetProperty(
            "Fence",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(captured);
        if ((string)capturedFence.GetType().GetProperty("ClaimToken", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(capturedFence) != "claim-token"
            || (string)capturedFence.GetType().GetProperty("Attempt", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(capturedFence) != "7")
        {
            throw new InvalidOperationException("Unity reread a mutable generated action after capturing its authoritative fence.");
        }
        stableAction.SessionId = "adapter-mutated-session";
        stableAction.ActorId = "adapter-mutated-actor";
        stableAction.OperationId = "act_cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc";
        stableAction.Claim.ClaimToken = "adapter-mutated-token";
        stableAction.Claim.Attempt = "100";

        var adapterReceipt = new RuntimeActionReceiptRequest
        {
            SessionId = "forged-session",
            ActorId = "forged-actor",
            OperationId = "act_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ClaimToken = "forged-token",
            Attempt = "99",
            Outcome = ReceiptOutcome.Rejected,
        };
        if (!captured.TryCompleteWithoutWrite(adapterReceipt))
        {
            throw new InvalidOperationException("Unity rejected the bound receipt fixture.");
        }
        var receipt = (RuntimeActionReceiptRequest)typeof(AuthoritativeActionExecutionContext)
            .GetProperty("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(captured);
        if (ReferenceEquals(receipt, adapterReceipt)
            || adapterReceipt.SessionId != "forged-session"
            || adapterReceipt.ClaimToken != "forged-token"
            || receipt.SessionId != "session"
            || receipt.ActorId != "actor"
            || receipt.OperationId != operationId
            || receipt.ClaimToken != "claim-token"
            || receipt.Attempt != "7")
        {
            throw new InvalidOperationException("Unity did not create a fresh receipt bound to its captured Runtime claim fence.");
        }

        var adapterReconciliation = new RuntimeActionReconcileRequest
        {
            SessionId = "forged-session",
            ActorId = "forged-actor",
            OperationId = "act_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            ClaimToken = "forged-token",
            Attempt = "99",
            Resolution = ProtocolResolution.Unknown,
        };
        if (!capturedInspection.TryComplete(adapterReconciliation))
        {
            throw new InvalidOperationException("Unity rejected the bound reconciliation fixture.");
        }
        var reconciliation = (RuntimeActionReconcileRequest)typeof(AuthoritativeActionInspectionContext)
            .GetProperty("Reconciliation", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(capturedInspection);
        if (ReferenceEquals(reconciliation, adapterReconciliation)
            || adapterReconciliation.SessionId != "forged-session"
            || adapterReconciliation.ClaimToken != "forged-token"
            || reconciliation.SessionId != "session"
            || reconciliation.ActorId != "actor"
            || reconciliation.OperationId != operationId
            || reconciliation.ClaimToken != "claim-token"
            || reconciliation.Attempt != "7")
        {
            throw new InvalidOperationException("Unity did not bind the exact Runtime claim fence to reconciliation.");
        }

        var mutableReceiptResult = new JObject
        {
            ["root"] = "before",
            ["nested"] = new JObject { ["value"] = "before" },
        };
        var mutableReceipt = new RuntimeActionReceiptRequest
        {
            Outcome = ReceiptOutcome.Rejected,
            Result = mutableReceiptResult,
            ModelResult = new JObject { ["model"] = "before" },
        };
        var evidenceExecution = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 16L, new Func<long, bool>(_ => true) });
        if (!evidenceExecution.TryCompleteWithoutWrite(mutableReceipt))
        {
            throw new InvalidOperationException("Unity rejected the mutable receipt snapshot fixture.");
        }
        mutableReceipt.Outcome = ReceiptOutcome.Succeeded;
        mutableReceiptResult["root"] = "after";
        ((JObject)mutableReceiptResult["nested"])["value"] = "after";
        ((JObject)mutableReceipt.ModelResult)["model"] = "after";
        var receiptSnapshot = (RuntimeActionReceiptRequest)typeof(AuthoritativeActionExecutionContext)
            .GetProperty("Receipt", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(evidenceExecution);
        var receiptSnapshotResult = (JObject)receiptSnapshot.Result;
        var receiptSnapshotModel = (JObject)receiptSnapshot.ModelResult;
        if (receiptSnapshot.Outcome != ReceiptOutcome.Rejected
            || (string)receiptSnapshotResult["root"] != "before"
            || (string)receiptSnapshotResult["nested"]["value"] != "before"
            || (string)receiptSnapshotModel["model"] != "before")
        {
            throw new InvalidOperationException(
                "Unity retained caller-owned mutable receipt evidence after completion.");
        }

        var mutableReconciliationResult = new JObject
        {
            ["root"] = "before",
            ["nested"] = new JObject { ["value"] = "before" },
        };
        var mutableReconciliation = new RuntimeActionReconcileRequest
        {
            Resolution = ProtocolResolution.NotApplied,
            Result = mutableReconciliationResult,
            ModelResult = new JObject { ["model"] = "before" },
        };
        var evidenceInspection = (AuthoritativeActionInspectionContext)typeof(AuthoritativeActionInspectionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 17L, new Func<long, bool>(_ => true) });
        if (!evidenceInspection.TryComplete(mutableReconciliation))
        {
            throw new InvalidOperationException("Unity rejected the mutable reconciliation snapshot fixture.");
        }
        mutableReconciliation.Resolution = ProtocolResolution.Applied;
        mutableReconciliationResult["root"] = "after";
        ((JObject)mutableReconciliationResult["nested"])["value"] = "after";
        ((JObject)mutableReconciliation.ModelResult)["model"] = "after";
        var reconciliationSnapshot = (RuntimeActionReconcileRequest)typeof(AuthoritativeActionInspectionContext)
            .GetProperty("Reconciliation", BindingFlags.Instance | BindingFlags.NonPublic)
            .GetValue(evidenceInspection);
        var reconciliationSnapshotResult = (JObject)reconciliationSnapshot.Result;
        var reconciliationSnapshotModel = (JObject)reconciliationSnapshot.ModelResult;
        if (reconciliationSnapshot.Resolution != ProtocolResolution.NotApplied
            || (string)reconciliationSnapshotResult["root"] != "before"
            || (string)reconciliationSnapshotResult["nested"]["value"] != "before"
            || (string)reconciliationSnapshotModel["model"] != "before")
        {
            throw new InvalidOperationException(
                "Unity retained caller-owned mutable reconciliation evidence after completion.");
        }

        int inspectionPredicateCalls = 0;
        var inspection = (AuthoritativeActionInspectionContext)typeof(AuthoritativeActionInspectionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[]
            {
                action,
                14L,
                new Func<long, bool>(_ =>
                {
                    Interlocked.Increment(ref inspectionPredicateCalls);
                    return true;
                }),
            });
        bool backgroundCompletion = Task.Run(() => inspection.TryComplete(
            new RuntimeActionReconcileRequest { Resolution = ProtocolResolution.Unknown })).GetAwaiter().GetResult();
        if (backgroundCompletion || inspectionPredicateCalls != 0 || inspection.IsCompleted)
        {
            throw new InvalidOperationException("Unity allowed background inspection completion or touched its main-thread clock predicate.");
        }

        bool inspectionCurrent = true;
        var lateInspection = (AuthoritativeActionInspectionContext)typeof(AuthoritativeActionInspectionContext)
            .GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                null)
            .Invoke(new object[] { action, 15L, new Func<long, bool>(_ => inspectionCurrent) });
        inspectionCurrent = false;
        if (lateInspection.TryComplete(new RuntimeActionReconcileRequest { Resolution = ProtocolResolution.Unknown }))
        {
            throw new InvalidOperationException("Unity accepted a late inspection result after its epoch or deadline closed.");
        }

        var tinyRequestClient = new UnityRuntimeClient("http://127.0.0.1:1", string.Empty, 1, 1, 1, 1);
        RuntimeClientResult<RuntimeActionNextResponse> localFailure = default;
        IEnumerator localRequest = tinyRequestClient.NextAction(
            new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" },
            value => localFailure = value);
        while (localRequest.MoveNext())
        {
            throw new InvalidOperationException("An oversized local request unexpectedly reached transport.");
        }
        PropertyInfo failureKind = typeof(RuntimeClientFailure).GetProperty("Kind");
        if (failureKind == null
            || localFailure.Succeeded
            || !string.Equals(failureKind.GetValue(localFailure.Failure)?.ToString(), "PreDispatch", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unity did not classify a local request failure before transport dispatch.");
        }

        MethodInfo ambiguous = typeof(AuthoritativeActionPump).GetMethod(
            "IsAmbiguousFailure",
            BindingFlags.Static | BindingFlags.NonPublic);
        if ((bool)ambiguous.Invoke(null, new object[] { localFailure.Failure }))
        {
            throw new InvalidOperationException("Unity treated a local pre-dispatch failure as an ambiguous action delivery.");
        }
    }

    private static IEnumerator RunTransportGates(MethodInfo createPost)
    {
        IEnumerator stage = VerifyAtomicActionTransport();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyActionPumpStateMachine();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyActionRetryPolicy();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyEvidenceRetrySnapshots();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyDeferredLifecycle();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyAuthorityTransport();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyWorkAuthorityTransport();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyApprovalTransport();
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyStatusAwareTransport(createPost);
        while (stage.MoveNext()) yield return stage.Current;
        stage = VerifyRedirectRejection();
        while (stage.MoveNext()) yield return stage.Current;
    }

    private static IEnumerator VerifyAtomicActionTransport()
    {
        const string operationId = "act_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var nextRequest = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" };
        var recoveryRequest = new RuntimeActionRecoveryRequest { SessionId = "session", ActorId = "actor" };
        var receiptRequest = new RuntimeActionReceiptRequest
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = operationId,
            ClaimToken = "claim-token",
            Attempt = "7",
            Outcome = ReceiptOutcome.Succeeded,
        };
        var reconciliationRequest = new RuntimeActionReconcileRequest
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = operationId,
            ClaimToken = "claim-token",
            Attempt = "7",
            Resolution = ProtocolResolution.Applied,
        };
        int maxRequestBytes = Math.Max(
            Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(nextRequest)),
            Math.Max(
                Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(receiptRequest)),
                Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(reconciliationRequest))));
        const string empty = "{\"delivery\":null}";
        const string conflict = "{\"error\":{\"code\":\"action_conflict\",\"message\":\"conflict\"}}";
        string invalidRecovery = RuntimeProtocolJson.Serialize(new RuntimeActionNextResponse
        {
            Delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Dispatch,
                Action = ActionForPump(operationId),
            },
        });
        string invalidPendingRecovery = invalidRecovery
            .Replace("\"kind\":\"dispatch\"", "\"kind\":\"reconcile\"")
            .Replace("\"status\":\"claimed\"", "\"status\":\"pending\"");
        int maxResponseBytes = Math.Max(
            Math.Max(Encoding.UTF8.GetByteCount(invalidRecovery), Encoding.UTF8.GetByteCount(invalidPendingRecovery)),
            Math.Max(Encoding.UTF8.GetByteCount(empty), Encoding.UTF8.GetByteCount(conflict)));

        using (var server = JsonServer("200 OK", empty))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeActionNextResponse> result = default;
            IEnumerator operation = client.NextAction(nextRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (!result.Succeeded || result.Value.Delivery != null)
            {
                throw new InvalidOperationException("Unity atomic next-action response was not decoded.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/actions/next");
        }

        using (var server = JsonServer("200 OK", empty))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeActionRecoveryResponse> result = default;
            IEnumerator operation = client.RecoverAction(recoveryRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (!result.Succeeded || result.Value.Delivery != null)
            {
                throw new InvalidOperationException("Unity recovery-only action response was not decoded.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/actions/recovery");
        }

        using (var server = JsonServer("200 OK", invalidRecovery))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeActionRecoveryResponse> result = default;
            IEnumerator operation = client.RecoverAction(recoveryRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (result.Succeeded || result.Failure.Kind != RuntimeClientFailureKind.Protocol)
            {
                throw new InvalidOperationException("Unity accepted a dispatch from the recovery-only action endpoint.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/actions/recovery");
        }

        using (var server = JsonServer("200 OK", invalidPendingRecovery))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeActionRecoveryResponse> result = default;
            IEnumerator operation = client.RecoverAction(recoveryRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (result.Succeeded || result.Failure.Kind != RuntimeClientFailureKind.Protocol)
            {
                throw new InvalidOperationException("Unity accepted a pending Action from recovery-only authority.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/actions/recovery");
        }

        using (var server = JsonServer("409 Conflict", conflict))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeActionRecordResponse> result = default;
            IEnumerator operation = client.RecordActionReceipt(receiptRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeActionReceiptRequest sent = RuntimeProtocolJson.Deserialize<RuntimeActionReceiptRequest>(server.RequestBody);
            if (result.Succeeded
                || result.Failure.Status != 409
                || sent.OperationId != operationId
                || sent.ClaimToken != "claim-token"
                || sent.Attempt != "7")
            {
                throw new InvalidOperationException("Unity action receipt lost its exact attempt fence.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/actions/receipt");
        }

        using (var server = JsonServer("409 Conflict", conflict))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeActionRecordResponse> result = default;
            IEnumerator operation = client.ReconcileAction(reconciliationRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeActionReconcileRequest sent = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(server.RequestBody);
            if (result.Succeeded
                || result.Failure.Status != 409
                || sent.OperationId != operationId
                || sent.ClaimToken != "claim-token"
                || sent.Attempt != "7")
            {
                throw new InvalidOperationException("Unity action reconciliation lost its exact attempt fence.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/actions/reconcile");
        }
    }

    private static IEnumerator VerifyApprovalTransport()
    {
        const string requestFingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string approvalId = "apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string requestJson = "{\"approvalId\":\"" + approvalId
            + "\",\"policyId\":\"high-risk\",\"sessionId\":\"session\",\"actorId\":\"actor\","
            + "\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},"
            + "\"operationId\":\"operation\",\"actionName\":\"dialogue.offer\","
            + "\"intentFingerprint\":\"bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb\","
            + "\"expectedAuthority\":{\"timelineId\":\"world\",\"generation\":\"2\",\"revision\":\"7\"},"
            + "\"requestFingerprint\":\"" + requestFingerprint
            + "\",\"requestedAt\":\"2026-09-01T00:00:00.000Z\",\"expiresAt\":\"2026-09-01T00:01:00.000Z\","
            + "\"presentation\":{\"kind\":\"game-choice\",\"optionId\":\"opaque-option-7\",\"label\":\"赠送礼物\"}}";
        string listBody = "{\"approvals\":[{\"request\":" + requestJson
            + ",\"status\":\"pending\",\"revision\":\"3\",\"updatedAt\":\"2026-09-01T00:00:00.000Z\"}]}";
        string decisionBody = "{\"approval\":{\"request\":" + requestJson
            + ",\"status\":\"approved\",\"revision\":\"4\",\"updatedAt\":\"2026-09-01T00:00:01.000Z\"}}";
        var listRequest = new RuntimeApprovalListRequest
        {
            SessionId = "session",
            ActorId = "actor",
            Limit = 7,
        };
        var decisionRequest = new RuntimeApprovalDecisionRequest
        {
            SessionId = "session",
            ActorId = "actor",
            ApprovalId = approvalId,
            ExpectedRevision = "3",
            RequestFingerprint = requestFingerprint,
            Decision = RuntimeApprovalDecisionRequestDecision.Approve,
        };
        int maxRequestBytes = Math.Max(
            Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(listRequest)),
            Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(decisionRequest)));
        int maxResponseBytes = Math.Max(
            Encoding.UTF8.GetByteCount(listBody),
            Encoding.UTF8.GetByteCount(decisionBody));

        using (var server = JsonServer("200 OK", listBody))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeApprovalListResponse> result = default;
            IEnumerator operation = client.ListApprovals(listRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeApprovalListRequest sent = RuntimeProtocolJson.Deserialize<RuntimeApprovalListRequest>(server.RequestBody);
            if (!result.Succeeded
                || sent.SessionId != "session"
                || sent.ActorId != "actor"
                || sent.Limit != 7
                || !HasExactApprovalPresentation(result.Value.Approvals[0].Request.Presentation))
            {
                throw new InvalidOperationException("Unity approval listing did not preserve the exact host presentation.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/approvals");
        }

        using (var server = JsonServer("200 OK", decisionBody))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeApprovalDecisionResponse> result = default;
            IEnumerator operation = client.DecideApproval(decisionRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeApprovalDecisionRequest sent = RuntimeProtocolJson.Deserialize<RuntimeApprovalDecisionRequest>(server.RequestBody);
            if (!result.Succeeded
                || sent.ApprovalId != approvalId
                || sent.ExpectedRevision != "3"
                || sent.RequestFingerprint != requestFingerprint
                || sent.Decision != RuntimeApprovalDecisionRequestDecision.Approve
                || result.Value.Approval.Status != RuntimeApprovalStatus.Approved
                || !HasExactApprovalPresentation(result.Value.Approval.Request.Presentation))
            {
                throw new InvalidOperationException("Unity approval decision lost its identity fence or host presentation.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/approvals/decision");
        }
    }

    private static IEnumerator VerifyActionPumpStateMachine()
    {
        const string operationId = "act_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string conflict = "{\"error\":{\"code\":\"action_conflict\",\"message\":\"conflict\"}}";
        var delivery = new RuntimeActionDelivery
        {
            Kind = RuntimeActionDeliveryKind.Dispatch,
            Action = ActionForPump(operationId),
        };
        var receiptProbe = new ActionPumpProbeAdapter();
        RuntimeActionRecord claimedAfterConflict = ActionForPump(
            operationId,
            RuntimeActionRecordStatus.Claimed,
            "claim-token-2",
            "8");
        string claimedRead = RuntimeProtocolJson.Serialize(new RuntimeActionGetResponse
        {
            Action = claimedAfterConflict,
        });
        string reconciled = RuntimeProtocolJson.Serialize(new RuntimeActionRecordResponse
        {
            Action = claimedAfterConflict,
        });

        using (var server = new SingleResponseServer(
            new[] { "409 Conflict", "200 OK", "200 OK" },
            new[]
            {
                "Content-Type: application/json\r\n",
                "Content-Type: application/json\r\n",
                "Content-Type: application/json\r\n",
            },
            new[]
            {
                Encoding.UTF8.GetBytes(conflict),
                Encoding.UTF8.GetBytes(claimedRead),
                Encoding.UTF8.GetBytes(reconciled),
            }))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            receiptProbe.BeforeExecute = () =>
            {
                SetPrivateField(pump, "sessionId", "mutated-session");
                SetPrivateField(pump, "actorId", "mutated-actor");
            };
            var client = ActionClient(
                server.Port,
                delivery.Action,
                claimedAfterConflict,
                conflict,
                claimedRead,
                reconciled);
            IEnumerator operation = InvokePumpStage("Execute", pump, receiptProbe, client, delivery);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();

            RuntimeActionReceiptRequest sentReceipt = RuntimeProtocolJson.Deserialize<RuntimeActionReceiptRequest>(
                server.RequestBodies[0]);
            RuntimeActionGetRequest sentRead = RuntimeProtocolJson.Deserialize<RuntimeActionGetRequest>(
                server.RequestBodies[1]);
            RuntimeActionReconcileRequest sentReconciliation = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                server.RequestBodies[2]);
            if (receiptProbe.ExecuteCount != 1
                || receiptProbe.InspectCount != 1
                || !receiptProbe.SawExecuting
                || !receiptProbe.SawInspecting
                || sentReceipt.SessionId != "session"
                || sentReceipt.ActorId != "actor"
                || sentReceipt.OperationId != operationId
                || sentRead.SessionId != "session"
                || sentRead.ActorId != "actor"
                || sentRead.OperationId != operationId
                || sentReconciliation.ClaimToken != "claim-token-2"
                || sentReconciliation.Attempt != "8"
                || server.RequestLines[0] != "POST /runtime/v1/actions/receipt HTTP/1.1"
                || server.RequestLines[1] != "POST /runtime/v1/actions/get HTTP/1.1"
                || server.RequestLines[2] != "POST /runtime/v1/actions/reconcile HTTP/1.1"
                || pump.State != AuthoritativeActionPumpState.Reconciling)
            {
                throw new InvalidOperationException("Unity dispatch did not inspect and reconcile the new claim returned after 409.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }

        delivery.Kind = RuntimeActionDeliveryKind.Reconcile;
        delivery.Action = ActionForPump(operationId);
        var inspectionProbe = new ActionPumpProbeAdapter();
        RuntimeActionRecord pendingAfterConflict = ActionForPump(
            operationId,
            RuntimeActionRecordStatus.Pending,
            "claim-token-2",
            "8");
        string pendingRead = RuntimeProtocolJson.Serialize(new RuntimeActionGetResponse
        {
            Action = pendingAfterConflict,
        });
        using (var server = new SingleResponseServer(
            new[] { "409 Conflict", "200 OK" },
            new[] { "Content-Type: application/json\r\n", "Content-Type: application/json\r\n" },
            new[] { Encoding.UTF8.GetBytes(conflict), Encoding.UTF8.GetBytes(pendingRead) }))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            inspectionProbe.BeforeInspect = () =>
            {
                SetPrivateField(pump, "sessionId", "mutated-session");
                SetPrivateField(pump, "actorId", "mutated-actor");
            };
            var client = ActionClient(server.Port, delivery.Action, conflict, pendingRead);
            IEnumerator operation = InvokePumpStage("Inspect", pump, inspectionProbe, client, delivery);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();

            RuntimeActionReconcileRequest sentReconciliation = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                server.RequestBodies[0]);
            RuntimeActionGetRequest sentRead = RuntimeProtocolJson.Deserialize<RuntimeActionGetRequest>(
                server.RequestBodies[1]);
            if (inspectionProbe.ExecuteCount != 0
                || inspectionProbe.InspectCount != 1
                || !inspectionProbe.SawInspecting
                || sentReconciliation.SessionId != "session"
                || sentReconciliation.ActorId != "actor"
                || sentReconciliation.OperationId != operationId
                || sentRead.SessionId != "session"
                || sentRead.ActorId != "actor"
                || sentRead.OperationId != operationId
                || server.RequestLines[0] != "POST /runtime/v1/actions/reconcile HTTP/1.1"
                || server.RequestLines[1] != "POST /runtime/v1/actions/get HTTP/1.1"
                || pump.State != AuthoritativeActionPumpState.Reconciling)
            {
                throw new InvalidOperationException("Unity inspection did not accept a pending action as resolved after 409.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }

        MethodInfo shouldInspect = typeof(AuthoritativeActionPump).GetMethod(
            "ShouldInspectAfterConflict",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (!(bool)shouldInspect.Invoke(null, new object[] { RuntimeActionRecordStatus.Claimed })
            || !(bool)shouldInspect.Invoke(null, new object[] { RuntimeActionRecordStatus.Uncertain })
            || (bool)shouldInspect.Invoke(null, new object[] { RuntimeActionRecordStatus.Pending })
            || (bool)shouldInspect.Invoke(null, new object[] { RuntimeActionRecordStatus.Succeeded })
            || (bool)shouldInspect.Invoke(null, new object[] { RuntimeActionRecordStatus.Failed }))
        {
            throw new InvalidOperationException("Unity conflict resolution did not distinguish unresolved from settled action states.");
        }

        MethodInfo hasExactIdentity = typeof(AuthoritativeActionPump).GetMethod(
            "HasExactIdentity",
            BindingFlags.Static | BindingFlags.NonPublic);
        if (hasExactIdentity == null)
        {
            throw new InvalidOperationException("Unity conflict resolution has no exact Runtime action identity fence.");
        }
        object originalFence = typeof(AuthoritativeActionExecutionContext).GetProperty(
            "Fence",
            BindingFlags.Instance | BindingFlags.NonPublic).GetValue(
                typeof(AuthoritativeActionExecutionContext)
                    .GetConstructor(
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null,
                        new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                        null)
                    .Invoke(new object[]
                    {
                        ActionForPump(operationId),
                        1L,
                        new Func<long, bool>(_ => true),
                    }));
        RuntimeActionRecord mismatchedSession = ActionForPump(operationId);
        mismatchedSession.SessionId = "other-session";
        RuntimeActionRecord mismatchedActor = ActionForPump(operationId);
        mismatchedActor.ActorId = "other-actor";
        RuntimeActionRecord mismatchedOperation = ActionForPump(
            "act_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb");
        if ((bool)hasExactIdentity.Invoke(null, new[] { originalFence, mismatchedSession })
            || (bool)hasExactIdentity.Invoke(null, new[] { originalFence, mismatchedActor })
            || (bool)hasExactIdentity.Invoke(null, new[] { originalFence, mismatchedOperation })
            || !(bool)hasExactIdentity.Invoke(null, new object[] { originalFence, ActionForPump(operationId) }))
        {
            throw new InvalidOperationException("Unity accepted a mismatched Runtime action after a conflict read.");
        }

        IEnumerator uncertainty = VerifyExecutionUncertainty(
            "expired claim",
            ActionForPump(
                operationId,
                claimExpiresAt: "1970-01-01T00:00:00.000Z"),
            new ActionPumpProbeAdapter(),
            null,
            0);
        while (uncertainty.MoveNext()) yield return uncertainty.Current;

        uncertainty = VerifyExecutionUncertainty(
            "transaction exception",
            ActionForPump(operationId),
            new ActionPumpProbeAdapter { FailTransaction = true },
            null,
            1);
        while (uncertainty.MoveNext()) yield return uncertainty.Current;

        uncertainty = VerifyExecutionUncertainty(
            "adapter coroutine exception",
            ActionForPump(operationId),
            new ActionPumpProbeAdapter { FailCoroutine = true },
            null,
            1);
        while (uncertainty.MoveNext()) yield return uncertainty.Current;

        var deadlineProbe = new ActionPumpProbeAdapter { HoldExecution = true };
        uncertainty = VerifyExecutionUncertainty(
            "execution deadline",
            ActionForPump(operationId),
            deadlineProbe,
            pump => deadlineProbe.BeforeExecute = () => SetPrivateField(
                pump,
                "adapterDeadline",
                Time.realtimeSinceStartupAsDouble - 1d),
            1);
        while (uncertainty.MoveNext()) yield return uncertainty.Current;

        IEnumerator identityConflict = VerifyConflictIdentityFence(operationId);
        while (identityConflict.MoveNext()) yield return identityConflict.Current;

        VerifyStopClosesExecution(operationId);
        VerifySelectorMismatchStops(operationId);
        VerifyAuthorityCallbackClosesPermit(operationId);

        IEnumerator unknown = VerifyUnknownSuccessHalts(operationId);
        while (unknown.MoveNext()) yield return unknown.Current;

        IEnumerator progress = VerifyConflictProgress(operationId);
        while (progress.MoveNext()) yield return progress.Current;
    }

    private static IEnumerator VerifyConflictProgress(string operationId)
    {
        const string conflict = "{\"error\":{\"code\":\"action_conflict\",\"message\":\"conflict\"}}";
        RuntimeActionRecord initial = ActionForPump(operationId);
        RuntimeActionRecord secondFence = ActionForPump(
            operationId,
            RuntimeActionRecordStatus.Claimed,
            "claim-token-2",
            "8");
        RuntimeActionRecord thirdFence = ActionForPump(
            operationId,
            RuntimeActionRecordStatus.Claimed,
            "claim-token-3",
            "9");
        string secondRead = RuntimeProtocolJson.Serialize(new RuntimeActionGetResponse { Action = secondFence });
        string thirdRead = RuntimeProtocolJson.Serialize(new RuntimeActionGetResponse { Action = thirdFence });
        string completed = RuntimeProtocolJson.Serialize(new RuntimeActionRecordResponse { Action = thirdFence });
        string jsonHeader = "Content-Type: application/json\r\n";

        using (var server = new SingleResponseServer(
            new[] { "409 Conflict", "200 OK", "409 Conflict", "200 OK" },
            new[] { jsonHeader, jsonHeader, jsonHeader, jsonHeader },
            new[]
            {
                Encoding.UTF8.GetBytes(conflict),
                Encoding.UTF8.GetBytes(secondRead),
                Encoding.UTF8.GetBytes(conflict),
                Encoding.UTF8.GetBytes(secondRead),
            }))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            var adapter = new ActionPumpProbeAdapter();
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Dispatch,
                Action = initial,
            };
            IEnumerator operation = InvokePumpStage(
                "Execute",
                pump,
                adapter,
                ActionClient(server.Port, initial, secondFence, conflict, secondRead),
                delivery);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (adapter.ExecuteCount != 1
                || adapter.InspectCount != 1
                || pump.State != AuthoritativeActionPumpState.Halted
                || server.RequestCount != 4
                || server.RequestLines[0] != "POST /runtime/v1/actions/receipt HTTP/1.1"
                || server.RequestLines[1] != "POST /runtime/v1/actions/get HTTP/1.1"
                || server.RequestLines[2] != "POST /runtime/v1/actions/reconcile HTTP/1.1"
                || server.RequestLines[3] != "POST /runtime/v1/actions/get HTTP/1.1")
            {
                throw new InvalidOperationException(
                    "Unity re-inspected an unchanged unresolved fence after an authoritative read.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }

        using (var server = new SingleResponseServer(
            new[] { "409 Conflict", "200 OK", "409 Conflict", "200 OK", "200 OK" },
            new[] { jsonHeader, jsonHeader, jsonHeader, jsonHeader, jsonHeader },
            new[]
            {
                Encoding.UTF8.GetBytes(conflict),
                Encoding.UTF8.GetBytes(secondRead),
                Encoding.UTF8.GetBytes(conflict),
                Encoding.UTF8.GetBytes(thirdRead),
                Encoding.UTF8.GetBytes(completed),
            }))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            var adapter = new ActionPumpProbeAdapter();
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Dispatch,
                Action = initial,
            };
            IEnumerator operation = InvokePumpStage(
                "Execute",
                pump,
                adapter,
                ActionClient(server.Port, initial, secondFence, conflict, secondRead, thirdRead, completed),
                delivery);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeActionReconcileRequest first = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                server.RequestBodies[2]);
            RuntimeActionReconcileRequest second = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                server.RequestBodies[4]);
            if (adapter.ExecuteCount != 1
                || adapter.InspectCount != 2
                || pump.State != AuthoritativeActionPumpState.Reconciling
                || server.RequestCount != 5
                || first.ClaimToken != "claim-token-2"
                || first.Attempt != "8"
                || second.ClaimToken != "claim-token-3"
                || second.Attempt != "9")
            {
                throw new InvalidOperationException(
                    "Unity did not inspect a changed authoritative claim fence exactly once.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }
    }

    private static IEnumerator VerifyUnknownSuccessHalts(string operationId)
    {
        RuntimeActionRecord uncertain = ActionForPump(
            operationId,
            RuntimeActionRecordStatus.Uncertain);
        string success = RuntimeProtocolJson.Serialize(new RuntimeActionRecordResponse { Action = uncertain });

        using (var server = JsonServer("200 OK", success))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            var adapter = new ActionPumpProbeAdapter { ReceiptOutcome = ReceiptOutcome.Unknown };
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Dispatch,
                Action = ActionForPump(operationId),
            };
            IEnumerator operation = InvokePumpStage(
                "Execute",
                pump,
                adapter,
                ActionClient(server.Port, delivery.Action, success),
                delivery);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeActionReceiptRequest request = RuntimeProtocolJson.Deserialize<RuntimeActionReceiptRequest>(
                server.RequestBody);
            if (request.Outcome != ReceiptOutcome.Unknown
                || pump.State != AuthoritativeActionPumpState.Halted
                || server.RequestCount != 1)
            {
                throw new InvalidOperationException(
                    "Unity repolled after a successful receipt left the action unresolved.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }

        using (var server = JsonServer("200 OK", success))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            var adapter = new ActionPumpProbeAdapter
            {
                InspectionResolution = ProtocolResolution.Unknown,
            };
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Reconcile,
                Action = ActionForPump(operationId),
            };
            IEnumerator operation = InvokePumpStage(
                "Inspect",
                pump,
                adapter,
                ActionClient(server.Port, delivery.Action, success),
                delivery);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeActionReconcileRequest request = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                server.RequestBody);
            if (request.Resolution != ProtocolResolution.Unknown
                || pump.State != AuthoritativeActionPumpState.Halted
                || server.RequestCount != 1)
            {
                throw new InvalidOperationException(
                    "Unity repolled after a successful reconciliation left the action unresolved.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }
    }

    private static void VerifySelectorMismatchStops(string operationId)
    {
        foreach (RuntimeActionDeliveryKind kind in new[]
        {
            RuntimeActionDeliveryKind.Dispatch,
            RuntimeActionDeliveryKind.Reconcile,
        })
        {
            RuntimeActionRecord action = ActionForPump(operationId);
            if (kind == RuntimeActionDeliveryKind.Dispatch) action.SessionId = "other-session";
            else action.ActorId = "other-actor";
            var delivery = new RuntimeActionDelivery { Kind = kind, Action = action };
            var adapter = new ActionPumpProbeAdapter();
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            IEnumerator operation = InvokePumpStage(
                kind == RuntimeActionDeliveryKind.Dispatch ? "Execute" : "Inspect",
                pump,
                adapter,
                ActionClient(1, action, "{}"),
                delivery);
            bool yielded = operation.MoveNext();
            AuthoritativeActionPumpState state = pump.State;
            typeof(AuthoritativeActionPump).GetMethod(
                "OnDisable",
                BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pump, null);
            if (yielded
                || state != AuthoritativeActionPumpState.Halted
                || adapter.ExecuteCount != 0
                || adapter.InspectCount != 0)
            {
                throw new InvalidOperationException(
                    "Unity invoked an adapter for a delivery outside its startup-bound selector.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }
    }

    private static void VerifyAuthorityCallbackClosesPermit(string operationId)
    {
        RuntimeActionRecord action = ActionForPump(operationId);
        var delivery = new RuntimeActionDelivery
        {
            Kind = RuntimeActionDeliveryKind.Dispatch,
            Action = action,
        };

        AuthoritativeActionPump stoppedPump = CreateActionPump(Array.Empty<float>());
        var stoppedAdapter = new ActionPumpProbeAdapter();
        stoppedAdapter.BeforeAuthorityReturn = () => typeof(AuthoritativeActionPump).GetMethod(
            "OnDisable",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(stoppedPump, null);
        IEnumerator stopped = InvokePumpStage(
            "Execute",
            stoppedPump,
            stoppedAdapter,
            ActionClient(1, action, "{}"),
            delivery);
        while (stopped.MoveNext())
        {
        }
        if (stoppedAdapter.TransactionAttempts != 0
            || stoppedPump.State != AuthoritativeActionPumpState.Stopped)
        {
            throw new InvalidOperationException(
                "Unity began a game transaction after authority validation synchronously stopped the pump.");
        }
        UnityEngine.Object.DestroyImmediate(stoppedPump.gameObject);

        AuthoritativeActionPump expiredPump = CreateActionPump(Array.Empty<float>());
        var expiredAdapter = new ActionPumpProbeAdapter();
        expiredAdapter.BeforeAuthorityReturn = () => SetPrivateField(
            expiredPump,
            "adapterDeadline",
            Time.realtimeSinceStartupAsDouble - 1d);
        IEnumerator expired = InvokePumpStage(
            "Execute",
            expiredPump,
            expiredAdapter,
            ActionClient(1, action, "{}"),
            delivery);
        expired.MoveNext();
        typeof(AuthoritativeActionPump).GetMethod(
            "OnDisable",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(expiredPump, null);
        if (expiredAdapter.TransactionAttempts != 0)
        {
            throw new InvalidOperationException(
                "Unity began a game transaction after authority validation consumed its deadline.");
        }
        UnityEngine.Object.DestroyImmediate(expiredPump.gameObject);
    }

    private static void VerifyStopClosesExecution(string operationId)
    {
        RuntimeActionRecord action = ActionForPump(operationId);
        var delivery = new RuntimeActionDelivery
        {
            Kind = RuntimeActionDeliveryKind.Dispatch,
            Action = action,
        };
        var adapter = new ActionPumpProbeAdapter { HoldExecution = true };
        AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
        IEnumerator operation = InvokePumpStage(
            "Execute",
            pump,
            adapter,
            ActionClient(1, action, "{}"),
            delivery);
        if (!operation.MoveNext() || adapter.LastExecution == null)
        {
            throw new InvalidOperationException("Unity did not start the stoppable execution fixture.");
        }
        typeof(AuthoritativeActionPump).GetMethod(
            "OnDisable",
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(pump, null);
        while (operation.MoveNext())
        {
        }
        int lateWrites = 0;
        if (pump.State != AuthoritativeActionPumpState.Stopped
            || adapter.InspectCount != 0
            || !adapter.LastExecution.IsCancellationRequested
            || adapter.LastExecution.TryCommit(
                _ => true,
                _ =>
                {
                    lateWrites += 1;
                    return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
                })
            || lateWrites != 0)
        {
            throw new InvalidOperationException("Unity stop did not close the active execution permit and coroutine.");
        }
        UnityEngine.Object.DestroyImmediate(pump.gameObject);
    }

    private static IEnumerator VerifyExecutionUncertainty(
        string scenario,
        RuntimeActionRecord action,
        ActionPumpProbeAdapter adapter,
        Action<AuthoritativeActionPump> configure,
        int expectedExecuteCount)
    {
        string reconciled = RuntimeProtocolJson.Serialize(new RuntimeActionRecordResponse { Action = action });
        using (var server = JsonServer("200 OK", reconciled))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            configure?.Invoke(pump);
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Dispatch,
                Action = action,
            };
            UnityRuntimeClient client = ActionClient(server.Port, action, reconciled);
            IEnumerator operation = InvokePumpStage("Execute", pump, adapter, client, delivery);
            while (operation.MoveNext()) yield return operation.Current;

            if (adapter.ExecuteCount != expectedExecuteCount
                || adapter.InspectCount != 1
                || !adapter.SawInspecting
                || pump.State != AuthoritativeActionPumpState.Reconciling)
            {
                throw new InvalidOperationException(
                    "Unity did not inspect and reconcile after " + scenario + ".");
            }
            if (adapter.LastExecution != null)
            {
                int lateWrites = 0;
                bool lateCommit = adapter.LastExecution.TryCommit(
                    _ => true,
                    _ =>
                    {
                        lateWrites += 1;
                        return new RuntimeActionReceiptRequest { Outcome = ReceiptOutcome.Succeeded };
                    });
                if (lateCommit || lateWrites != 0 || !adapter.LastExecution.IsCancellationRequested)
                {
                    throw new InvalidOperationException(
                        "Unity left a late execution permit open after " + scenario + ".");
                }
            }
            server.Wait();
            if (server.RequestCount != 1
                || server.RequestLine != "POST /runtime/v1/actions/reconcile HTTP/1.1")
            {
                throw new InvalidOperationException(
                    "Unity sent the wrong authoritative request after " + scenario + ".");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }
    }

    private static IEnumerator VerifyConflictIdentityFence(string operationId)
    {
        RuntimeActionRecord original = ActionForPump(operationId);
        RuntimeActionRecord mismatched = ActionForPump(operationId);
        mismatched.ActorId = "other-actor";
        string responseBody = RuntimeProtocolJson.Serialize(new RuntimeActionGetResponse { Action = mismatched });
        using (var server = JsonServer("200 OK", responseBody))
        {
            AuthoritativeActionPump pump = CreateActionPump(Array.Empty<float>());
            var adapter = new ActionPumpProbeAdapter();
            var context = (AuthoritativeActionExecutionContext)typeof(AuthoritativeActionExecutionContext)
                .GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic,
                    null,
                    new[] { typeof(RuntimeActionRecord), typeof(long), typeof(Func<long, bool>) },
                    null)
                .Invoke(new object[] { original, 1L, new Func<long, bool>(_ => true) });
            object fence = typeof(AuthoritativeActionExecutionContext).GetProperty(
                "Fence",
                BindingFlags.Instance | BindingFlags.NonPublic).GetValue(context);
            MethodInfo resolveConflict = typeof(AuthoritativeActionPump).GetMethod(
                "ResolveConflict",
                BindingFlags.Instance | BindingFlags.NonPublic);
            UnityRuntimeClient client = ActionClient(server.Port, original, responseBody);
            IEnumerator operation = (IEnumerator)resolveConflict.Invoke(
                pump,
                new[] { adapter, client, fence });
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (adapter.InspectCount != 0
                || server.RequestCount != 1
                || server.RequestLine != "POST /runtime/v1/actions/get HTTP/1.1"
                || pump.State != AuthoritativeActionPumpState.Halted)
            {
                throw new InvalidOperationException(
                    "Unity inspected or reconciled a mismatched authoritative conflict record.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }
    }

    private static IEnumerator VerifyActionRetryPolicy()
    {
        const string operationId = "act_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        var receipt = new RuntimeActionReceiptRequest
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = operationId,
            ClaimToken = "claim-token",
            Attempt = "7",
            Outcome = ReceiptOutcome.Succeeded,
        };
        int requestBytes = Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(receipt));
        const string unavailable = "{\"error\":{\"code\":\"unavailable\",\"message\":\"unavailable\"}}";
        int responseBytes = Encoding.UTF8.GetByteCount(unavailable);
        MethodInfo deliverRecord = typeof(AuthoritativeActionPump).GetMethod(
            "DeliverRecord",
            BindingFlags.Instance | BindingFlags.NonPublic);
        MethodInfo ambiguous = typeof(AuthoritativeActionPump).GetMethod(
            "IsAmbiguousFailure",
            BindingFlags.Static | BindingFlags.NonPublic);

        using (var server = new SingleResponseServer("503 Service Unavailable", null, Encoding.UTF8.GetBytes(unavailable), 3))
        {
            AuthoritativeActionPump pump = CreateActionPump(new[] { 0f, 0f });
            var client = new UnityRuntimeClient(
                "http://127.0.0.1:" + server.Port,
                "test-token",
                requestBytes,
                responseBytes,
                responseBytes,
                1);
            RuntimeClientResult<RuntimeActionRecordResponse> result = default;
            var operation = (IEnumerator)deliverRecord.Invoke(
                pump,
                new object[]
                {
                    new Func<Action<RuntimeClientResult<RuntimeActionRecordResponse>>, IEnumerator>(
                        completed => client.RecordActionReceipt(receipt, completed)),
                    new Action<RuntimeClientResult<RuntimeActionRecordResponse>>(value => result = value),
                });
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (server.RequestCount != 3
                || result.Succeeded
                || result.Failure.Kind != RuntimeClientFailureKind.Http
                || result.Failure.Status != 503
                || !(bool)ambiguous.Invoke(null, new object[] { result.Failure }))
            {
                throw new InvalidOperationException("Unity did not apply the exact host retry count to an ambiguous 5xx delivery.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }

        using (var server = new SingleResponseServer(null, null, Array.Empty<byte>()))
        {
            var next = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" };
            int nextBytes = Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(next));
            var client = new UnityRuntimeClient(
                "http://127.0.0.1:" + server.Port,
                string.Empty,
                nextBytes,
                1,
                1,
                1);
            RuntimeClientResult<RuntimeActionNextResponse> result = default;
            IEnumerator operation = client.NextAction(next, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (result.Succeeded
                || result.Failure.Kind != RuntimeClientFailureKind.Transport
                || !(bool)ambiguous.Invoke(null, new object[] { result.Failure }))
            {
                throw new InvalidOperationException("Unity did not classify a dispatched request without a response as ambiguous transport.");
            }
        }

        using (var server = JsonServer("200 OK", "{"))
        {
            var next = new RuntimeActionNextRequest { SessionId = "session", ActorId = "actor" };
            int nextBytes = Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(next));
            var client = new UnityRuntimeClient(
                "http://127.0.0.1:" + server.Port,
                string.Empty,
                nextBytes,
                1,
                1,
                1);
            RuntimeClientResult<RuntimeActionNextResponse> result = default;
            IEnumerator operation = client.NextAction(next, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (result.Succeeded
                || result.Failure.Kind != RuntimeClientFailureKind.Protocol
                || (bool)ambiguous.Invoke(null, new object[] { result.Failure }))
            {
                throw new InvalidOperationException("Unity treated a malformed successful response as ambiguous transport.");
            }
        }
    }

    private static IEnumerator VerifyEvidenceRetrySnapshots()
    {
        const string operationId = "act_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        RuntimeActionRecord action = ActionForPump(operationId);
        string completed = RuntimeProtocolJson.Serialize(new RuntimeActionRecordResponse
        {
            Action = ActionForPump(operationId, RuntimeActionRecordStatus.Failed),
        });
        const string unavailable = "{\"error\":{\"code\":\"unavailable\",\"message\":\"unavailable\"}}";
        string jsonHeader = "Content-Type: application/json\r\n";

        var receiptResult = new JObject
        {
            ["root"] = "before",
            ["nested"] = new JObject { ["value"] = "before" },
        };
        var receipt = new RuntimeActionReceiptRequest
        {
            SessionId = action.SessionId,
            ActorId = action.ActorId,
            OperationId = action.OperationId,
            ClaimToken = action.Claim.ClaimToken,
            Attempt = action.Claim.Attempt,
            Outcome = ReceiptOutcome.Rejected,
            Result = receiptResult,
            ModelResult = new JObject { ["model"] = "before" },
        };
        using (var server = new SingleResponseServer(
            new[] { "503 Service Unavailable", "200 OK" },
            new[] { jsonHeader, jsonHeader },
            new[] { Encoding.UTF8.GetBytes(unavailable), Encoding.UTF8.GetBytes(completed) }))
        {
            AuthoritativeActionPump pump = CreateActionPump(new[] { 0f });
            var adapter = new MutableEvidenceAdapter { Receipt = receipt };
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Dispatch,
                Action = action,
            };
            IEnumerator operation = InvokePumpStage(
                "Execute",
                pump,
                adapter,
                ActionClient(
                    server.Port,
                    new[] { action },
                    new[] { unavailable, completed },
                    Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(receipt))),
                delivery);
            bool receiptMutated = false;
            while (operation.MoveNext())
            {
                yield return operation.Current;
                if (receiptMutated || server.RequestCount != 1) continue;
                receipt.Outcome = ReceiptOutcome.Succeeded;
                receiptResult["root"] = "after";
                ((JObject)receiptResult["nested"])["value"] = "after";
                ((JObject)receipt.ModelResult)["model"] = "after";
                receiptMutated = true;
            }
            if (server.RequestCount != 2)
            {
                throw new InvalidOperationException("Unity did not perform the planned receipt retry.");
            }
            server.Wait();
            RuntimeActionReceiptRequest sent = RuntimeProtocolJson.Deserialize<RuntimeActionReceiptRequest>(
                server.RequestBodies[1]);
            if (server.RequestCount != 2
                || server.RequestBodies[0] != server.RequestBodies[1]
                || sent.Outcome != ReceiptOutcome.Rejected
                || (string)((JObject)sent.Result)["root"] != "before"
                || (string)((JObject)sent.Result)["nested"]["value"] != "before"
                || (string)((JObject)sent.ModelResult)["model"] != "before")
            {
                throw new InvalidOperationException(
                    "Unity reserialized caller-mutated receipt evidence during retry.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }

        var reconciliationResult = new JObject
        {
            ["root"] = "before",
            ["nested"] = new JObject { ["value"] = "before" },
        };
        var reconciliation = new RuntimeActionReconcileRequest
        {
            SessionId = action.SessionId,
            ActorId = action.ActorId,
            OperationId = action.OperationId,
            ClaimToken = action.Claim.ClaimToken,
            Attempt = action.Claim.Attempt,
            Resolution = ProtocolResolution.NotApplied,
            Result = reconciliationResult,
            ModelResult = new JObject { ["model"] = "before" },
        };
        using (var server = new SingleResponseServer(
            new[] { "503 Service Unavailable", "200 OK" },
            new[] { jsonHeader, jsonHeader },
            new[] { Encoding.UTF8.GetBytes(unavailable), Encoding.UTF8.GetBytes(completed) }))
        {
            AuthoritativeActionPump pump = CreateActionPump(new[] { 0f });
            var adapter = new MutableEvidenceAdapter { Reconciliation = reconciliation };
            var delivery = new RuntimeActionDelivery
            {
                Kind = RuntimeActionDeliveryKind.Reconcile,
                Action = action,
            };
            IEnumerator operation = InvokePumpStage(
                "Inspect",
                pump,
                adapter,
                ActionClient(
                    server.Port,
                    new[] { action },
                    new[] { unavailable, completed },
                    Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(reconciliation))),
                delivery);
            bool reconciliationMutated = false;
            while (operation.MoveNext())
            {
                yield return operation.Current;
                if (reconciliationMutated || server.RequestCount != 1) continue;
                reconciliation.Resolution = ProtocolResolution.Applied;
                reconciliationResult["root"] = "after";
                ((JObject)reconciliationResult["nested"])["value"] = "after";
                ((JObject)reconciliation.ModelResult)["model"] = "after";
                reconciliationMutated = true;
            }
            if (server.RequestCount != 2)
            {
                throw new InvalidOperationException("Unity did not perform the planned reconciliation retry.");
            }
            server.Wait();
            RuntimeActionReconcileRequest sent = RuntimeProtocolJson.Deserialize<RuntimeActionReconcileRequest>(
                server.RequestBodies[1]);
            if (server.RequestCount != 2
                || server.RequestBodies[0] != server.RequestBodies[1]
                || sent.Resolution != ProtocolResolution.NotApplied
                || (string)((JObject)sent.Result)["root"] != "before"
                || (string)((JObject)sent.Result)["nested"]["value"] != "before"
                || (string)((JObject)sent.ModelResult)["model"] != "before")
            {
                throw new InvalidOperationException(
                    "Unity reserialized caller-mutated reconciliation evidence during retry.");
            }
            UnityEngine.Object.DestroyImmediate(pump.gameObject);
        }
    }

    private static RuntimeActionRecord ActionForPump(
        string operationId,
        RuntimeActionRecordStatus status = RuntimeActionRecordStatus.Claimed,
        string claimToken = "claim-token",
        string claimAttempt = "7",
        string claimExpiresAt = "2099-01-01T00:00:00.000Z")
    {
        return new RuntimeActionRecord
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = operationId,
            ActionName = "game.action",
            OperationKey = "operation-key",
            Attempt = claimAttempt,
            ConflictKeys = Array.Empty<string>(),
            Coordinate = new RuntimeActionRecordCoordinate
            {
                SessionId = "session",
                ActorId = "actor",
                RunId = "run",
                Turn = "1",
            },
            CreatedAt = "2026-08-30T00:00:00.000Z",
            UpdatedAt = "2026-08-30T00:00:00.000Z",
            ExpectedAuthority = new RuntimeActionRecordExpectedAuthority
            {
                TimelineId = "timeline",
                Generation = "1",
                Revision = "2",
            },
            IntentFingerprint = new string('a', 64),
            PayloadFingerprint = new string('b', 64),
            Payload = null,
            ReplayPolicy = ReplayPolicy.Never,
            Status = status,
            Claim = new Claim
            {
                Attempt = claimAttempt,
                ClaimToken = claimToken,
                ClaimantId = "unity",
                ClaimedAt = "2026-08-30T00:00:00.000Z",
                ExpiresAt = claimExpiresAt,
            },
        };
    }

    private static AuthoritativeActionPump CreateActionPump(float[] retryDelays)
    {
        var gameObject = new GameObject("OpenGameAgent action pump gate");
        gameObject.SetActive(false);
        var pump = gameObject.AddComponent<AuthoritativeActionPump>();
        SetPrivateField(pump, "sessionId", "session");
        SetPrivateField(pump, "actorId", "actor");
        SetPrivateField(pump, "boundSessionId", "session");
        SetPrivateField(pump, "boundActorId", "actor");
        SetPrivateField(pump, "executionDurationSeconds", 60f);
        SetPrivateField(pump, "deliveryRetryDelaysSeconds", retryDelays);
        SetPrivateField(pump, "epoch", 1L);
        SetPrivateField(pump, "mainThreadId", Thread.CurrentThread.ManagedThreadId);
        pump.enabled = false;
        gameObject.SetActive(true);
        return pump;
    }

    private static UnityRuntimeClient ActionClient(
        int port,
        RuntimeActionRecord action,
        params string[] responseBodies)
    {
        return ActionClient(port, new[] { action }, responseBodies);
    }

    private static UnityRuntimeClient ActionClient(
        int port,
        RuntimeActionRecord action,
        RuntimeActionRecord laterAction,
        params string[] responseBodies)
    {
        return ActionClient(port, new[] { action, laterAction }, responseBodies);
    }

    private static UnityRuntimeClient ActionClient(
        int port,
        RuntimeActionRecord[] actions,
        string[] responseBodies,
        int minimumRequestBytes = 1)
    {
        int maxRequestBytes = minimumRequestBytes;
        foreach (RuntimeActionRecord action in actions)
        {
            var next = new RuntimeActionNextRequest { SessionId = action.SessionId, ActorId = action.ActorId };
            var receipt = new RuntimeActionReceiptRequest
            {
                SessionId = action.SessionId,
                ActorId = action.ActorId,
                OperationId = action.OperationId,
                ClaimToken = action.Claim.ClaimToken,
                Attempt = action.Claim.Attempt,
                Outcome = ReceiptOutcome.Succeeded,
            };
            var reconciliation = new RuntimeActionReconcileRequest
            {
                SessionId = action.SessionId,
                ActorId = action.ActorId,
                OperationId = action.OperationId,
                ClaimToken = action.Claim.ClaimToken,
                Attempt = action.Claim.Attempt,
                Resolution = ProtocolResolution.Applied,
            };
            var read = new RuntimeActionGetRequest
            {
                SessionId = action.SessionId,
                ActorId = action.ActorId,
                OperationId = action.OperationId,
            };
            maxRequestBytes = Math.Max(
                maxRequestBytes,
                Math.Max(
                    Math.Max(
                        Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(next)),
                        Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(receipt))),
                    Math.Max(
                        Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(reconciliation)),
                        Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(read)))));
        }
        int maxResponseBytes = 1;
        foreach (string body in responseBodies)
        {
            maxResponseBytes = Math.Max(maxResponseBytes, Encoding.UTF8.GetByteCount(body));
        }
        return new UnityRuntimeClient(
            "http://127.0.0.1:" + port,
            "test-token",
            maxRequestBytes,
            maxResponseBytes,
            maxResponseBytes,
            1);
    }

    private static IEnumerator InvokePumpStage(
        string method,
        AuthoritativeActionPump pump,
        IAuthoritativeActionAdapter adapter,
        UnityRuntimeClient client,
        RuntimeActionDelivery delivery)
    {
        return (IEnumerator)typeof(AuthoritativeActionPump).GetMethod(
            method,
            BindingFlags.Instance | BindingFlags.NonPublic).Invoke(
            pump,
            new object[] { adapter, client, delivery });
    }

    private static void SetPrivateField(object target, string field, object value)
    {
        typeof(AuthoritativeActionPump).GetField(
            field,
            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);
    }

    private sealed class ActionPumpProbeAdapter : IAuthoritativeActionAdapter
    {
        public Action BeforeExecute { get; set; }

        public Action BeforeInspect { get; set; }

        public int ExecuteCount { get; private set; }

        public int InspectCount { get; private set; }

        public int TransactionAttempts { get; private set; }

        public bool SawExecuting { get; private set; }

        public bool SawInspecting { get; private set; }

        public bool FailTransaction { get; set; }

        public bool FailCoroutine { get; set; }

        public bool HoldExecution { get; set; }

        public Action BeforeAuthorityReturn { get; set; }

        public bool AuthorityAllowed { get; set; } = true;

        public ReceiptOutcome ReceiptOutcome { get; set; } = ReceiptOutcome.Succeeded;

        public ProtocolResolution InspectionResolution { get; set; } = ProtocolResolution.Applied;

        public AuthoritativeActionExecutionContext LastExecution { get; private set; }

        public IEnumerator Execute(AuthoritativeActionExecutionContext context)
        {
            LastExecution = context;
            ExecuteCount += 1;
            BeforeExecute?.Invoke();
            if (FailCoroutine) return FailingCoroutine();
            if (HoldExecution) return HoldingCoroutine();
            SawExecuting = context.TryCommit(
                _ =>
                {
                    BeforeAuthorityReturn?.Invoke();
                    return AuthorityAllowed;
                },
                _ =>
                {
                    TransactionAttempts += 1;
                    if (FailTransaction)
                    {
                        throw new InvalidOperationException("simulated game transaction failure");
                    }
                    return new RuntimeActionReceiptRequest
                    {
                        Outcome = ReceiptOutcome,
                    };
                });
            return null;
        }

        public IEnumerator Inspect(AuthoritativeActionInspectionContext context)
        {
            InspectCount += 1;
            BeforeInspect?.Invoke();
            SawInspecting = context.TryComplete(new RuntimeActionReconcileRequest
            {
                Resolution = InspectionResolution,
            });
            return null;
        }

        private static IEnumerator FailingCoroutine()
        {
            yield return null;
            throw new InvalidOperationException("simulated adapter coroutine failure");
        }

        private static IEnumerator HoldingCoroutine()
        {
            while (true) yield return null;
        }
    }

    private sealed class MutableEvidenceAdapter : IAuthoritativeActionAdapter
    {
        public RuntimeActionReceiptRequest Receipt { get; set; }

        public RuntimeActionReconcileRequest Reconciliation { get; set; }

        public IEnumerator Execute(AuthoritativeActionExecutionContext context)
        {
            context.TryCompleteWithoutWrite(Receipt);
            return null;
        }

        public IEnumerator Inspect(AuthoritativeActionInspectionContext context)
        {
            context.TryComplete(Reconciliation);
            return null;
        }
    }

    private static IEnumerator VerifyDeferredLifecycle()
    {
        var readRequest = new RuntimeDeferredReadRequest { SessionId = "session", ActorId = "actor" };
        var command = new RuntimeDeferredCommandRequest
        {
            SessionId = "session",
            ActorId = "actor",
            OperationId = "operation",
        };
        int maxRequestBytes = Math.Max(
            Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(readRequest)),
            Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(command)));
        const string readBody = "{\"deferred\":{\"operationId\":\"operation\",\"inputId\":\"input\",\"coordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"}}}";
        const string resumeBody = "{\"runId\":\"run\",\"initialCoordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"1\"},\"eventCursor\":\"17\"}";
        const string abandonBody = "{\"operationId\":\"operation\",\"providerCancellation\":\"failed\"}";
        const string emptyBody = "{\"deferred\":null}";
        const string conflictBody = "{\"error\":{\"code\":\"deferred_busy\",\"message\":\"busy\"}}";
        int maxResponseBytes = Math.Max(
            Math.Max(Encoding.UTF8.GetByteCount(readBody), Encoding.UTF8.GetByteCount(resumeBody)),
            Math.Max(
                Math.Max(Encoding.UTF8.GetByteCount(abandonBody), Encoding.UTF8.GetByteCount(emptyBody)),
                Encoding.UTF8.GetByteCount(conflictBody)));

        using (var server = JsonServer("200 OK", readBody))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeDeferredReadResponse> result = default;
            IEnumerator operation = client.ReadDeferred(readRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeDeferredReadRequest sent = RuntimeProtocolJson.Deserialize<RuntimeDeferredReadRequest>(server.RequestBody);
            if (!result.Succeeded
                || result.Value.Deferred == null
                || result.Value.Deferred.OperationId != "operation"
                || sent.SessionId != "session"
                || sent.ActorId != "actor")
            {
                throw new InvalidOperationException("Unity deferred read did not preserve its request or safe response.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/deferred/read");
        }

        using (var server = JsonServer("202 Accepted", resumeBody, 2))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeRunAccepted> result = default;
            IEnumerator operation = client.ResumeDeferred(command, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            RuntimeClientResult<RuntimeRunAccepted> repeated = default;
            operation = client.ResumeDeferred(command, value => repeated = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            RuntimeDeferredCommandRequest first = RuntimeProtocolJson.Deserialize<RuntimeDeferredCommandRequest>(server.RequestBodies[0]);
            RuntimeDeferredCommandRequest second = RuntimeProtocolJson.Deserialize<RuntimeDeferredCommandRequest>(server.RequestBodies[1]);
            if (!result.Succeeded
                || !repeated.Succeeded
                || result.Value.RunId != "run"
                || repeated.Value.RunId != "run"
				|| result.Value.EventCursor != "17"
				|| repeated.Value.EventCursor != "17"
                || first.OperationId != "operation"
                || second.OperationId != "operation"
                || server.RequestCount != 2)
            {
                throw new InvalidOperationException("Unity explicit deferred resumes did not produce two independent requests.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/deferred/resume");
        }

        using (var server = JsonServer("200 OK", abandonBody))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeDeferredAbandonResponse> result = default;
            IEnumerator operation = client.AbandonDeferred(command, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (!result.Succeeded || result.Value.ProviderCancellation != ProviderCancellation.Failed)
            {
                throw new InvalidOperationException("Unity deferred abandon response was not decoded.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/deferred/abandon");
        }

        using (var server = JsonServer("200 OK", emptyBody))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeDeferredReadResponse> result = default;
            IEnumerator operation = client.ReadDeferred(readRequest, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (!result.Succeeded || result.Value.Deferred != null)
            {
                throw new InvalidOperationException("Unity empty deferred projection was not decoded.");
            }
        }

        using (var server = JsonServer("409 Conflict", conflictBody))
        {
            var client = RuntimeClientForTest(server.Port, maxRequestBytes, maxResponseBytes);
            RuntimeClientResult<RuntimeRunAccepted> result = default;
            IEnumerator operation = client.ResumeDeferred(command, value => result = value);
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (result.Succeeded
                || result.Failure.Status != 409
                || result.Failure.Message.IndexOf("deferred_busy", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException("Unity deferred conflict failure was not preserved.");
            }
            VerifyJsonPostTransport(server, "/runtime/v1/deferred/resume");
        }
    }

    private static IEnumerator VerifyAuthorityTransport()
    {
        var run = new RuntimeRunRequest
        {
            SessionId = "session", ActorId = "actor", InputId = "input", Payload = new JObject(),
            AdmissionAuthority = new RuntimeRunAdmissionAuthority { Scope = "planning", Generation = "1" },
        };
        var close = new RuntimeAuthorityCloseRequest
        {
            SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1", OperationId = "close-1",
        };
        var read = new RuntimeAuthorityReadRequest
        {
            SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1",
        };
        const string accepted = "{\"runId\":\"run\",\"initialCoordinate\":{\"sessionId\":\"session\",\"actorId\":\"actor\",\"runId\":\"run\",\"turn\":\"0\"},\"eventCursor\":\"0\"}";
        const string closure = "{\"sessionId\":\"session\",\"actorId\":\"actor\",\"scope\":\"planning\",\"generation\":\"1\",\"operationId\":\"close-1\",\"closedAt\":\"2026-09-01T00:00:00.000Z\"}";
        string readResponse = "{\"closure\":" + closure + "}";
        int requestBytes = Math.Max(Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(run)),
            Math.Max(Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(close)), Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(read))));
        int responseBytes = Math.Max(Encoding.UTF8.GetByteCount(accepted), Encoding.UTF8.GetByteCount(readResponse));
        using (var server = JsonServer("202 Accepted", accepted))
        {
            var client = RuntimeClientForTest(server.Port, requestBytes, responseBytes);
            RuntimeClientResult<RuntimeRunAccepted> result = default;
            IEnumerator operation = client.SubmitRun(run, value => result = value);
            run.AdmissionAuthority.Generation = "2";
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (!result.Succeeded || RuntimeProtocolJson.Deserialize<RuntimeRunRequest>(server.RequestBody).AdmissionAuthority.Generation != "1")
                throw new InvalidOperationException("Unity run authority changed before coroutine dispatch.");
            VerifyJsonPostTransport(server, "/runtime/v1/runs");
        }
        using (var server = JsonServer("200 OK", closure))
        {
            var client = RuntimeClientForTest(server.Port, requestBytes, responseBytes);
            RuntimeClientResult<RuntimeAuthorityClosure> result = default;
            IEnumerator operation = client.CloseAuthority(close, value => result = value);
            close.Generation = "2";
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            if (!result.Succeeded || result.Value.Generation != "1"
                || RuntimeProtocolJson.Deserialize<RuntimeAuthorityCloseRequest>(server.RequestBody).Generation != "1")
                throw new InvalidOperationException("Unity close authority did not preserve its operation snapshot.");
            VerifyJsonPostTransport(server, "/runtime/v1/authority/close");
        }
        foreach (bool mismatch in new[] { false, true })
        {
            using (var server = JsonServer("200 OK", mismatch ? readResponse.Replace("\"generation\":\"1\"", "\"generation\":\"2\"") : readResponse))
            {
                var client = RuntimeClientForTest(server.Port, requestBytes, responseBytes);
                RuntimeClientResult<RuntimeAuthorityReadResponse> result = default;
                IEnumerator operation = client.ReadAuthorityClosure(read, value => result = value);
                while (operation.MoveNext()) yield return operation.Current;
                server.Wait();
                if (mismatch ? result.Succeeded || result.Failure.Kind != RuntimeClientFailureKind.Protocol
                    : !result.Succeeded || result.Value.Closure.Generation != "1")
                    throw new InvalidOperationException("Unity authority read did not enforce the requested key.");
                VerifyJsonPostTransport(server, "/runtime/v1/authority/read");
            }
        }
    }

    private static IEnumerator VerifyWorkAuthorityTransport()
    {
        const string requestJson = "{\"sessionId\":\"session\",\"actorId\":\"actor\",\"scope\":\"planning\",\"generation\":\"1\",\"operationId\":\"close-work\",\"workRetirement\":{\"retirementId\":\"work\",\"recordedAt\":\"2026-09-05T00:00:00.000Z\",\"partition\":{\"ownerId\":\"owner\",\"timelineId\":\"world\",\"generation\":\"7\",\"sessionId\":\"session\",\"actorId\":\"actor\"}}}";
        JObject closure;
        using (var reader = new Newtonsoft.Json.JsonTextReader(new StringReader(requestJson))
            { DateParseHandling = Newtonsoft.Json.DateParseHandling.None })
            closure = JObject.Load(reader);
        closure["closedAt"] = "2026-09-05T00:00:00.000Z";
        closure["workRetirementResult"] = JObject.Parse("{\"phase\":\"finalized\",\"cancelledScheduleCount\":\"9007199254740993\",\"cancelledOccurrenceCount\":\"2\",\"cancelledMessageCount\":\"3\"}");
        var read = new RuntimeAuthorityReadRequest { SessionId = "session", ActorId = "actor", Scope = "planning", Generation = "1" };
        using (var server = JsonServer("200 OK", closure.ToString()))
        {
            var request = RuntimeProtocolJson.Deserialize<RuntimeAuthorityCloseRequest>(requestJson);
            var client = RuntimeClientForTest(server.Port, Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(request)), Encoding.UTF8.GetByteCount(closure.ToString()));
            RuntimeClientResult<RuntimeAuthorityClosure> result = default;
            IEnumerator operation = client.CloseAuthority(request, value => result = value);
            request.WorkRetirement.Partition.OwnerId = "mutated";
            request.WorkRetirement.RetirementId = "mutated";
            while (operation.MoveNext()) yield return operation.Current;
            server.Wait();
            JObject sent = JObject.Parse(server.RequestBody);
            if (!result.Succeeded || result.Value.WorkRetirementResult.CancelledScheduleCount != "9007199254740993"
                || (string)sent["workRetirement"]["partition"]["ownerId"] != "owner" || (string)sent["workRetirement"]["retirementId"] != "work")
                throw new InvalidOperationException("Unity Work close lost its snapshot or decimal count. "
                    + (result.Succeeded ? "Decoded: " + RuntimeProtocolJson.Serialize(result.Value)
                        : "Failure: " + result.Failure.Kind + " " + result.Failure.Status + " " + result.Failure.Message)
                    + " Request: " + server.RequestBody + " Response: " + closure);
        }
        var cases = new List<JObject>();
        foreach (string field in new[] { "workRetirement", "workRetirementResult" })
        {
            var missing = (JObject)closure.DeepClone(); missing.Remove(field); cases.Add(missing);
            var explicitNull = (JObject)closure.DeepClone(); explicitNull[field] = JValue.CreateNull(); cases.Add(explicitNull);
        }
        foreach (string field in new[] { "cancelledScheduleCount", "cancelledOccurrenceCount", "cancelledMessageCount" })
        {
            foreach (string value in new[] { "1", "\"01\"", "\"-1\"", "\"1.0\"", "null" })
            {
                var malformed = (JObject)closure.DeepClone(); malformed["workRetirementResult"][field] = JToken.Parse(value); cases.Add(malformed);
            }
        }
        foreach (var replacement in new[] { new[] { "actorId", "other" }, new[] { "ownerId", "bad\nowner" }, new[] { "generation", "01" } })
        {
            var malformed = (JObject)closure.DeepClone(); malformed["workRetirement"]["partition"][replacement[0]] = replacement[1]; cases.Add(malformed);
        }
        var badPhase = (JObject)closure.DeepClone(); badPhase["workRetirementResult"]["phase"] = "completed"; cases.Add(badPhase);
        var badTime = (JObject)closure.DeepClone(); badTime["workRetirement"]["recordedAt"] = "2026-02-30T00:00:00.000Z"; cases.Add(badTime);
        foreach (JObject malformed in cases)
        {
            foreach (bool isRead in new[] { false, true })
            {
                string body = isRead ? new JObject { ["closure"] = malformed.DeepClone() }.ToString() : malformed.ToString();
                using (var server = JsonServer("200 OK", body))
                {
                    var request = RuntimeProtocolJson.Deserialize<RuntimeAuthorityCloseRequest>(requestJson);
                    var client = RuntimeClientForTest(server.Port, Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(request)), Encoding.UTF8.GetByteCount(body));
                    bool rejected = false;
                    IEnumerator operation = isRead
                        ? client.ReadAuthorityClosure(read, value => rejected = !value.Succeeded && value.Failure.Kind == RuntimeClientFailureKind.Protocol)
                        : client.CloseAuthority(request, value => rejected = !value.Succeeded && value.Failure.Kind == RuntimeClientFailureKind.Protocol);
                    while (operation.MoveNext()) yield return operation.Current;
                    server.Wait();
                    if (!rejected) throw new InvalidOperationException("Unity accepted an invalid Work closure: " + body);
                }
            }
        }
        foreach (Phase phase in new[] { Phase.Fenced, Phase.Finalized })
        {
            JObject snapshot = (JObject)closure.DeepClone();
            if (phase == Phase.Fenced) snapshot["workRetirementResult"] = JObject.Parse("{\"phase\":\"fenced\",\"cancelledScheduleCount\":\"0\",\"cancelledOccurrenceCount\":\"0\",\"cancelledMessageCount\":\"0\"}");
            string body = new JObject { ["closure"] = snapshot }.ToString();
            using (var server = JsonServer("200 OK", body))
            {
                var client = RuntimeClientForTest(server.Port, Encoding.UTF8.GetByteCount(RuntimeProtocolJson.Serialize(read)), Encoding.UTF8.GetByteCount(body));
                RuntimeClientResult<RuntimeAuthorityReadResponse> result = default;
                IEnumerator operation = client.ReadAuthorityClosure(read, value => result = value);
                while (operation.MoveNext()) yield return operation.Current;
                server.Wait();
                if (!result.Succeeded || result.Value.Closure.WorkRetirementResult.Phase != phase)
                    throw new InvalidOperationException("Unity Work read lost its durable phase.");
            }
        }
    }

    private static UnityRuntimeClient RuntimeClientForTest(int port, int maxRequestBytes, int maxResponseBytes)
    {
        return new UnityRuntimeClient(
            "http://127.0.0.1:" + port,
            "test-token",
            maxRequestBytes,
            maxResponseBytes,
            maxResponseBytes,
            1);
    }

    private static SingleResponseServer JsonServer(string status, string body, int responseCount = 1)
    {
        return new SingleResponseServer(
            status,
            "Content-Type: application/json\r\n",
            Encoding.UTF8.GetBytes(body),
            responseCount);
    }

    private static void VerifyJsonPostTransport(SingleResponseServer server, string path)
    {
        for (int index = 0; index < server.RequestCount; index++)
        {
            if (server.RequestLines[index] != "POST " + path + " HTTP/1.1"
                || server.RequestHeadersList[index].IndexOf(
                    "Authorization: Bearer test-token",
                    StringComparison.OrdinalIgnoreCase) < 0
                || server.RequestHeadersList[index].IndexOf(
                    "Content-Type: application/json",
                    StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException("Unity Runtime request method, path, JSON media type, or bearer token changed.");
            }
        }
    }

    private static void AdvanceTransportGate()
    {
        if (currentOperation != null && !currentOperation.isDone) return;
        currentOperation = null;
        try
        {
            while (TransportGate.Count > 0)
            {
                IEnumerator current = TransportGate.Peek();
                if (!current.MoveNext())
                {
                    TransportGate.Pop();
                    continue;
                }
                if (current.Current is IEnumerator nested)
                {
                    TransportGate.Push(nested);
                    continue;
                }
                if (current.Current is UnityWebRequestAsyncOperation operation)
                {
                    currentOperation = operation;
                    return;
                }
                if (current.Current == null || current.Current is WaitForSecondsRealtime)
                {
                    return;
                }
                throw new InvalidOperationException(
                    "Unity transport gate yielded an unsupported operation: " + current.Current.GetType().FullName);
            }
            Finish(0, null);
        }
        catch (Exception exception)
        {
            Finish(1, exception);
        }
    }

    private static void Finish(int exitCode, Exception exception)
    {
        EditorApplication.update -= AdvanceTransportGate;
        currentOperation = null;
        TransportGate.Clear();
        if (exception != null) UnityEngine.Debug.LogException(exception);
        EditorApplication.Exit(exitCode);
    }

    private static IEnumerator VerifyStatusAwareTransport(MethodInfo createPost)
    {
        byte[] successBody = Encoding.ASCII.GetBytes("data");
        int errorLimit = successBody.Length / 2;
        using (var server = new SingleResponseServer("200 OK", null, successBody))
        {
            var client = new UnityRuntimeClient(
                "http://127.0.0.1:" + server.Port,
                string.Empty,
                1,
                errorLimit,
                successBody.Length,
                1);
            using (var request = (UnityWebRequest)createPost.Invoke(
                client,
                new object[] { "/success", Array.Empty<byte>(), successBody.Length, errorLimit }))
            {
                yield return request.SendWebRequest();
                server.Wait();
                byte[] received = (byte[])request.downloadHandler.GetType()
                    .GetProperty("Bytes")
                    .GetValue(request.downloadHandler);
                bool invalidStatus = (bool)request.downloadHandler.GetType()
                    .GetProperty("InvalidResponseStatus")
                    .GetValue(request.downloadHandler);
                if (request.responseCode != 200
                    || request.result != UnityWebRequest.Result.Success
                    || invalidStatus
                    || received.Length != successBody.Length)
                {
                    throw new InvalidOperationException("Unity 6 did not expose the success status before the bounded body callback.");
                }
            }
        }

        using (var server = new SingleResponseServer("400 Bad Request", null, successBody))
        {
            var client = new UnityRuntimeClient(
                "http://127.0.0.1:" + server.Port,
                string.Empty,
                1,
                errorLimit,
                successBody.Length,
                1);
            using (var request = (UnityWebRequest)createPost.Invoke(
                client,
                new object[] { "/failure", Array.Empty<byte>(), successBody.Length, errorLimit }))
            {
                yield return request.SendWebRequest();
                server.Wait();
                bool limitExceeded = (bool)request.downloadHandler.GetType()
                    .GetProperty("LimitExceeded")
                    .GetValue(request.downloadHandler);
                if (!limitExceeded)
                {
                    throw new InvalidOperationException("Runtime error response escaped its smaller JSON byte budget.");
                }
            }
        }
    }

    private static IEnumerator VerifyRedirectRejection()
    {
        using (var target = new SingleResponseServer("200 OK", null, Array.Empty<byte>()))
        using (var source = new SingleResponseServer(
            "307 Temporary Redirect",
            "Location: http://127.0.0.1:" + target.Port + "/target\r\n",
            Array.Empty<byte>()))
        {
            var client = new UnityRuntimeClient(
                "http://127.0.0.1:" + source.Port,
                "test-token",
                1,
                1,
                1,
                1);
            RuntimeClientResult<RuntimeInitializeResponse> result = default;
            IEnumerator request = client.Initialize(value => result = value);
            while (request.MoveNext()) yield return request.Current;
            source.Wait();
            if (result.Succeeded
                || result.Failure.Status != 307
                || result.Failure.Message.IndexOf("redirect", StringComparison.OrdinalIgnoreCase) < 0
                || target.Received)
            {
                throw new InvalidOperationException("Runtime redirect was followed or not reported as a failure.");
            }
        }
    }

    private sealed class SingleResponseServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly byte[][] responseBodies;
        private readonly byte[][] responseHeaders;
        private readonly List<string> requestBodies = new List<string>();
        private readonly List<string> requestHeadersList = new List<string>();
        private readonly List<string> requestLines = new List<string>();
        private readonly Task worker;
        private volatile bool received;

        public SingleResponseServer(string status, string headers, byte[] body, int responseCount = 1)
            : this(
                Repeated(status, responseCount),
                Repeated(headers, responseCount),
                Repeated(body, responseCount))
        {
        }

        public SingleResponseServer(string[] statuses, string[] headers, byte[][] bodies)
        {
            if (statuses == null
                || headers == null
                || bodies == null
                || statuses.Length == 0
                || statuses.Length != headers.Length
                || statuses.Length != bodies.Length)
            {
                throw new ArgumentException("A Runtime test server requires aligned response plans.");
            }
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            responseBodies = bodies;
            responseHeaders = new byte[statuses.Length][];
            for (int index = 0; index < statuses.Length; index++)
            {
                responseHeaders[index] = statuses[index] == null
                    ? null
                    : Encoding.ASCII.GetBytes(
                        "HTTP/1.1 " + statuses[index] + "\r\n"
                        + (headers[index] ?? string.Empty)
                        + "Content-Length: " + bodies[index].Length + "\r\n"
                        + "Connection: close\r\n\r\n");
            }
            worker = Task.Run(Serve);
        }

        public int Port { get; }

        public bool Received => received;

        public string RequestBody => requestBodies[requestBodies.Count - 1];

        public IReadOnlyList<string> RequestBodies => requestBodies;

        public string RequestHeaders => requestHeadersList[requestHeadersList.Count - 1];

        public IReadOnlyList<string> RequestHeadersList => requestHeadersList;

        public int RequestCount => requestLines.Count;

        public string RequestLine => requestLines[requestLines.Count - 1];

        public IReadOnlyList<string> RequestLines => requestLines;

        public void Wait()
        {
            worker.GetAwaiter().GetResult();
        }

        public void Dispose()
        {
            listener.Stop();
            try
            {
                worker.GetAwaiter().GetResult();
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }

        private void Serve()
        {
            for (int index = 0; index < responseHeaders.Length; index++)
            {
                using (TcpClient client = listener.AcceptTcpClient())
                using (NetworkStream stream = client.GetStream())
                {
                    received = true;
                    ReadRequest(stream);
                    byte[] response = responseHeaders[index];
                    byte[] body = responseBodies[index];
                    if (response != null) stream.Write(response, 0, response.Length);
                    if (response != null && body.Length > 0) stream.Write(body, 0, body.Length);
                }
            }
        }

        private static string[] Repeated(string value, int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            var values = new string[count];
            for (int index = 0; index < values.Length; index++) values[index] = value;
            return values;
        }

        private static byte[][] Repeated(byte[] value, int count)
        {
            if (count <= 0) throw new ArgumentOutOfRangeException(nameof(count));
            var values = new byte[count][];
            for (int index = 0; index < values.Length; index++) values[index] = value;
            return values;
        }

        private void ReadRequest(Stream stream)
        {
            int matched = 0;
            byte[] terminator = { 13, 10, 13, 10 };
            using (var headers = new MemoryStream())
            {
                while (matched < terminator.Length)
                {
                    int value = stream.ReadByte();
                    if (value < 0) throw new EndOfStreamException("Runtime test request ended before its headers.");
                    headers.WriteByte((byte)value);
                    matched = value == terminator[matched] ? matched + 1 : value == terminator[0] ? 1 : 0;
                }
                requestHeadersList.Add(Encoding.ASCII.GetString(headers.ToArray()));
            }
            string[] lines = requestHeadersList[requestHeadersList.Count - 1].Split(
                new[] { "\r\n" },
                StringSplitOptions.None);
            requestLines.Add(lines[0]);
            int contentLength = 0;
            foreach (string line in lines)
            {
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)
                    && !int.TryParse(line.Substring("Content-Length:".Length).Trim(), out contentLength))
                {
                    throw new InvalidDataException("Runtime test request Content-Length was invalid.");
                }
            }
            byte[] body = new byte[contentLength];
            int offset = 0;
            while (offset < body.Length)
            {
                int read = stream.Read(body, offset, body.Length - offset);
                if (read <= 0) throw new EndOfStreamException("Runtime test request body ended early.");
                offset += read;
            }
            requestBodies.Add(Encoding.UTF8.GetString(body));
        }
    }
}
