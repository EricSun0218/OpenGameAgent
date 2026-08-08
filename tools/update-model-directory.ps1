param(
    [string]$OutputPath = "$PSScriptRoot/../src/OpenGameAgent.Models/Data/model-directory.json",
    [string]$CatalogUrl = "https://models.dev/api.json"
)

$ErrorActionPreference = "Stop"
$source = Invoke-RestMethod -Uri $CatalogUrl -TimeoutSec 60

$providerIds = @(
    "amazon-bedrock", "anthropic", "baseten", "cerebras", "cloudflare-ai-gateway",
    "cloudflare-workers-ai", "deepseek", "fireworks-ai", "google", "google-vertex",
    "groq", "huggingface", "kimi-for-coding", "minimax", "minimax-cn", "mistral",
    "moonshotai", "moonshotai-cn", "nvidia", "openai", "openrouter", "togetherai",
    "xai", "zai", "zai-coding-plan", "zhipuai", "zhipuai-coding-plan"
)

function Get-ApiId([string]$providerId, [object]$provider, [string]$modelId) {
    switch ($providerId) {
        "amazon-bedrock" { return "bedrock-converse-stream" }
        "anthropic" { return "anthropic-messages" }
        "fireworks-ai" {
            if ($modelId -match "glm-5p2|kimi-k3") { return "openai-completions" }
            return "anthropic-messages"
        }
        "kimi-for-coding" { return "anthropic-messages" }
        "minimax" { return "anthropic-messages" }
        "minimax-cn" { return "anthropic-messages" }
        "google" { return "google-generative-ai" }
        "google-vertex" { return "google-vertex" }
        "mistral" { return "mistral-conversations" }
        "openai" { return "openai-responses" }
        "xai" {
            if ($modelId -eq "grok-4.5") { return "openai-responses" }
            return "openai-completions"
        }
        default {
            if ($provider.npm -eq "@ai-sdk/openai") { return "openai-responses" }
            return "openai-completions"
        }
    }
}

function Get-ModelEndpoint([string]$providerId, [string]$apiId, [string]$providerEndpoint) {
    if ($providerId -eq "fireworks-ai") {
        if ($apiId -eq "anthropic-messages") {
            return "https://api.fireworks.ai/inference"
        }
        return "https://api.fireworks.ai/inference/v1"
    }
    return $providerEndpoint
}

function Get-ModelHeaders([string]$providerId) {
    $headers = [ordered]@{}
    if ($providerId -eq "nvidia") { $headers["NVCF-POLL-SECONDS"] = "3600" }
    if ($providerId -eq "kimi-for-coding") { $headers["User-Agent"] = "KimiCLI/1.5" }
    return $headers
}

function Test-AnthropicAdaptiveModel([string]$modelId) {
    return $modelId -match "opus[-.]4[-.](6|7|8)|opus[-.]5|sonnet[-.]4[-.]6|sonnet[-.]5|fable[-.]5"
}

function Get-ReasoningProfile([string]$providerId, [string]$apiId, [string]$modelId, [object]$model) {
    $supported = [ordered]@{}
    if (-not $model.reasoning) {
        $supported["off"] = $null
    } else {
        foreach ($level in @("off", "minimal", "low", "medium", "high")) { $supported[$level] = $null }
        $efforts = @($model.reasoning_options | Where-Object { $_.type -eq "effort" } | ForEach-Object { $_.values })
        $recognized = @($efforts | Where-Object { $_ -in @("none", "minimal", "low", "medium", "high", "xhigh", "max") })
        if ($recognized.Count -gt 0) {
            $supported.Clear()
            if ($recognized -contains "none") { $supported["off"] = "none" }
            foreach ($level in @("minimal", "low", "medium", "high", "xhigh", "max")) {
                if ($recognized -contains $level) { $supported[$level] = $level }
            }
        }

        $id = $modelId.ToLowerInvariant()
        if ($apiId -eq "openai-responses" -and $providerId -eq "openai") {
            if ($modelId -in @("gpt-5.1", "gpt-5.2", "gpt-5.3-codex", "gpt-5.4", "gpt-5.4-mini", "gpt-5.4-nano", "gpt-5.5", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna")) {
                $supported["off"] = "none"
            }
            if ($id -match "gpt-5[.](2|3|4|5|6)") { $supported["xhigh"] = "xhigh" }
            if ($id -match "gpt-5[.]6") { $supported["max"] = "max" }
            if ($modelId -eq "gpt-5.5") { $supported.Remove("minimal") }
            if ($modelId.EndsWith("gpt-5.5-pro", [System.StringComparison]::Ordinal)) {
                $supported.Remove("off"); $supported.Remove("minimal"); $supported.Remove("low")
            }
        }
        if ($providerId -eq "xai" -and $apiId -eq "openai-responses" -and $modelId -eq "grok-4.5") {
            $supported.Remove("off"); $supported.Remove("minimal")
        }
        if ($apiId -in @("google-generative-ai", "google-vertex")) {
            if ($id -match "gemini-3([.][0-9]+)?-pro") {
                $supported.Clear(); $supported["low"] = "LOW"; $supported["high"] = "HIGH"
            } elseif ($id -match "gemini-3([.][0-9]+)?-flash" -or $id -in @("gemini-flash-latest", "gemini-flash-lite-latest")) {
                $supported.Remove("off")
            } elseif ($id -match "gemma-?4") {
                $supported.Clear(); $supported["minimal"] = "MINIMAL"; $supported["high"] = "HIGH"
            }
        }
        if ($providerId -in @("moonshotai", "moonshotai-cn") -and $modelId -in @("kimi-k2.7-code", "kimi-k2.7-code-highspeed")) {
            $supported.Remove("off")
        }
        if ($providerId -eq "openrouter" -and $id.StartsWith("inception/mercury-2")) { $supported.Remove("off") }
        if ($providerId -eq "openrouter" -and $modelId -eq "z-ai/glm-5.2") { $supported["xhigh"] = "xhigh" }
        if ($providerId -eq "deepseek" -and $id.Contains("deepseek-v4")) {
            $supported.Clear(); $supported["off"] = $null; $supported["high"] = "high"; $supported["max"] = "max"
        }
        if ($providerId -in @("zai", "zai-coding-plan", "zhipuai", "zhipuai-coding-plan") -and $modelId -eq "glm-5.2") {
            $supported.Clear(); $supported["off"] = $null; $supported["low"] = "high"; $supported["medium"] = "high"; $supported["high"] = "high"; $supported["max"] = "max"
        }
        if ($providerId -eq "baseten" -and $modelId -in @("zai-org/GLM-5.2", "zai-org/GLM-5.2-Fast")) {
            $supported.Clear(); $supported["off"] = "none"; $supported["high"] = "high"; $supported["max"] = "max"
        } elseif ($providerId -eq "baseten") {
            $hasToggle = @($model.reasoning_options | Where-Object { $_.type -eq "toggle" }).Count -gt 0
            if ($hasToggle) {
                $supported["off"] = "off"
                $supported.Remove("minimal"); $supported.Remove("low"); $supported.Remove("medium")
            }
        }
        if ($providerId -eq "fireworks-ai" -and $modelId -match "glm-5p2") {
            $supported.Clear(); $supported["off"] = "none"; $supported["low"] = "high"
            $supported["medium"] = "high"; $supported["high"] = "high"; $supported["max"] = "max"
        }
        if ($providerId -eq "togetherai" -and $model.reasoning) {
            if ($modelId -in @("deepseek-ai/DeepSeek-R1", "MiniMaxAI/MiniMax-M2.7")) {
                $supported.Remove("off"); $supported.Remove("minimal"); $supported.Remove("low"); $supported.Remove("medium")
            } elseif ($modelId -in @("openai/gpt-oss-20b", "openai/gpt-oss-120b")) {
                $supported.Remove("off"); $supported.Remove("minimal")
            } elseif ($modelId -eq "deepseek-ai/DeepSeek-V4-Pro") {
                $supported.Remove("minimal"); $supported.Remove("low"); $supported.Remove("medium")
                $supported["high"] = "high"
            } else {
                $supported.Remove("minimal"); $supported.Remove("low"); $supported.Remove("medium")
            }
        }
        if ($apiId -eq "anthropic-messages" -and (Test-AnthropicAdaptiveModel $modelId)) {
            $supported["max"] = "max"
            if ($id -match "opus[-.]4[-.](7|8)|opus[-.]5|sonnet[-.]5|fable[-.]5") { $supported["xhigh"] = "xhigh" }
            if ($id -match "fable[-.]5") { $supported.Remove("off") }
        }
        if ($providerId -eq "groq" -and $modelId -eq "qwen/qwen3.6-27b") {
            $supported.Remove("minimal"); $supported.Remove("low"); $supported.Remove("medium")
            $supported["high"] = "default"
        }
    }

    if ($supported.Count -eq 0) { $supported["high"] = $null }
    $levels = [System.Collections.Generic.List[string]]::new()
    $values = [ordered]@{}
    foreach ($level in @("off", "minimal", "low", "medium", "high", "xhigh", "max")) {
        if ($supported.Contains($level)) {
            $levels.Add($level)
            if ($null -ne $supported[$level]) { $values[$level] = [string]$supported[$level] }
        }
    }
    return [pscustomobject]@{ Levels = @($levels); Values = $values }
}

function Get-ModelCompatibility([string]$providerId, [string]$apiId, [string]$endpoint, [string]$modelId, [object]$model) {
    $compatibility = [ordered]@{
        supportsTemperature = [bool]($model.temperature ?? $false)
        structuredOutput = [bool]($model.structured_output ?? $false)
    }
    if ($null -ne $model.interleaved) { $compatibility["interleaved"] = $model.interleaved }

    if ($apiId -eq "openai-completions") {
        $isZai = $providerId -in @("zai", "zai-coding-plan", "zhipuai", "zhipuai-coding-plan")
        $isTogether = $providerId -eq "togetherai"
        $isMoonshot = $providerId -in @("moonshotai", "moonshotai-cn")
        $isOpenRouter = $providerId -eq "openrouter"
        $isWorkers = $providerId -eq "cloudflare-workers-ai"
        $isGateway = $providerId -eq "cloudflare-ai-gateway"
        $isNvidia = $providerId -eq "nvidia"
        $isGrok = $providerId -eq "xai"
        $isDeepSeek = $providerId -eq "deepseek"
        $isNonStandard = $isNvidia -or $providerId -in @("baseten", "cerebras", "fireworks-ai", "xai") -or $isTogether -or $isDeepSeek -or $isZai -or $isMoonshot -or $isWorkers -or $isGateway
        $useMaxTokens = $providerId -eq "baseten" -or $isMoonshot -or $isGateway -or $isTogether -or $isNvidia -or $isZai
        $compatibility["supportsStore"] = -not $isNonStandard
        $compatibility["supportsDeveloperRole"] = if ($isOpenRouter) { $modelId.StartsWith("anthropic/") -or $modelId.StartsWith("openai/") } else { -not $isNonStandard }
        $compatibility["supportsReasoningEffort"] = -not ($isGrok -or $isZai -or $isMoonshot -or $isTogether -or $isGateway -or $isNvidia)
        $compatibility["supportsUsageInStreaming"] = $true
        $compatibility["supportsFinishReason"] = $true
        $compatibility["maxTokensField"] = $useMaxTokens ? "max_tokens" : "max_completion_tokens"
        $compatibility["requiresToolResultName"] = $false
        $compatibility["requiresAssistantAfterToolResult"] = $false
        $compatibility["requiresThinkingAsText"] = $false
        $compatibility["requiresReasoningContentOnAssistantMessages"] = $isDeepSeek
        $compatibility["thinkingFormat"] = $isDeepSeek ? "deepseek" : ($isZai ? "zai" : ($isTogether -and $model.reasoning ? "together" : ($isOpenRouter ? "openrouter" : "openai")))
        $compatibility["supportsStrictMode"] = -not ($isMoonshot -or $isTogether -or $isGateway -or $isNvidia)
        $compatibility["supportsOpenAIGrammarTools"] = $false
        $compatibility["sendSessionAffinityHeaders"] = $isWorkers -or $providerId -eq "fireworks-ai"
        $compatibility["sessionAffinityFormat"] = $isOpenRouter ? "openrouter" : "openai"
        $compatibility["supportsLongCacheRetention"] = -not ($isTogether -or $isWorkers -or $isGateway -or $isNvidia -or $providerId -in @("baseten", "fireworks-ai"))
        if ($isOpenRouter -and $modelId -match "^~?anthropic/") { $compatibility["cacheControlFormat"] = "anthropic" }
        if ($providerId -eq "huggingface") { $compatibility["supportsDeveloperRole"] = $false }
        if ($isZai) {
            $compatibility["supportsDeveloperRole"] = $false
            $compatibility["supportsReasoningEffort"] = $modelId -eq "glm-5.2"
            if ($modelId -notin @("glm-4.5", "glm-4.5-air", "glm-4.5-flash", "glm-4.5v")) { $compatibility["zaiToolStream"] = $true }
        }
        if ($isMoonshot) {
            $compatibility["thinkingFormat"] = $modelId -match "kimi-k3" ? "openai" : "deepseek"
            $compatibility["supportsReasoningEffort"] = $modelId -match "kimi-k3"
        }
        if ($isTogether) {
            if ($modelId -in @("openai/gpt-oss-20b", "openai/gpt-oss-120b")) {
                $compatibility["thinkingFormat"] = "openai"; $compatibility["supportsReasoningEffort"] = $true
            } elseif ($modelId -eq "deepseek-ai/DeepSeek-V4-Pro") {
                $compatibility["thinkingFormat"] = "together"; $compatibility["supportsReasoningEffort"] = $true
            }
        }
        if ($providerId -eq "baseten") {
            $options = @($model.reasoning_options)
            $toggle = @($options | Where-Object type -eq "toggle").Count -gt 0 -or $modelId -in @("zai-org/GLM-5.2", "zai-org/GLM-5.2-Fast")
            $effort = @($options | Where-Object type -eq "effort").Count -gt 0 -or $modelId -in @("zai-org/GLM-5.2", "zai-org/GLM-5.2-Fast")
            $compatibility["supportsReasoningEffort"] = $effort
            if ($toggle) {
                $compatibility["thinkingFormat"] = "baseten"
                $compatibility["chatTemplateArgs"] = [ordered]@{ enable_thinking = [ordered]@{ '$var' = "thinking.enabled" } }
            }
        }
        if ($providerId -eq "fireworks-ai" -and $modelId -match "kimi-k3") {
            $compatibility["requiresReasoningContentOnAssistantMessages"] = $true
            $compatibility["thinkingFormat"] = "openai"
            $compatibility["deferredToolsMode"] = "kimi"
        }
    } elseif ($apiId -eq "openai-responses") {
        $compatibility["supportsDeveloperRole"] = $true
        $compatibility["supportsStrictMode"] = $providerId -eq "openai"
        $compatibility["supportsOpenAIGrammarTools"] = $providerId -eq "openai" -and $modelId -match "^gpt-[5-9]"
        $toolSearch = $providerId -eq "openai" -and $modelId -in @("gpt-5.4", "gpt-5.4-mini", "gpt-5.4-pro", "gpt-5.5", "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna")
        $compatibility["supportsAdditionalTools"] = $toolSearch
        $compatibility["supportsToolSearch"] = $toolSearch
        $compatibility["supportsExplicitPromptCacheMode"] = $providerId -eq "openai" -and [decimal]($model.cost.cache_write ?? 0) -gt 0
        $compatibility["supportsLongCacheRetention"] = $providerId -ne "xai"
        $compatibility["sessionAffinityFormat"] = "openai"
    } elseif ($apiId -eq "anthropic-messages") {
        $compatibility["supportsEagerToolInputStreaming"] = $providerId -ne "fireworks-ai"
        $compatibility["supportsLongCacheRetention"] = $providerId -ne "fireworks-ai"
        $compatibility["sendSessionAffinityHeaders"] = $providerId -eq "fireworks-ai"
        $compatibility["supportsCacheControlOnTools"] = $providerId -ne "fireworks-ai"
        $compatibility["forceAdaptiveThinking"] = $providerId -eq "kimi-for-coding" -or (Test-AnthropicAdaptiveModel $modelId)
        $compatibility["allowEmptySignature"] = $providerId -eq "kimi-for-coding" -and $modelId -in @("k3", "kimi-for-coding")
        $compatibility["supportsStrictTools"] = $providerId -eq "anthropic"
        $toolReferences = $false
        if ($providerId -eq "anthropic" -and -not $modelId.Contains("haiku")) {
            $match = [regex]::Match($modelId, '^claude-(?:opus|sonnet|fable)-(\d+)(?:-(\d+))?(?:-|$)')
            if ($match.Success) {
                $major = [int]$match.Groups[1].Value
                $minor = $match.Groups[2].Success -and $match.Groups[2].Value.Length -lt 8 ? [int]$match.Groups[2].Value : 0
                $toolReferences = $major -gt 4 -or ($major -eq 4 -and $minor -ge 5)
            }
        }
        $compatibility["supportsToolReferences"] = $toolReferences
    } elseif ($apiId -eq "bedrock-converse-stream") {
        $compatibility["supportsStrictMode"] = [bool]($model.structured_output ?? $false)
    } elseif ($apiId -in @("google-generative-ai", "google-vertex")) {
        $compatibility["useLegacyOpenApiToolSchemas"] = $false
    }
    return $compatibility
}

function Get-ProviderEndpoint([string]$providerId, [object]$provider) {
    if ($provider.api) { return [string]$provider.api }
    switch ($providerId) {
        "cerebras" { return "https://api.cerebras.ai/v1" }
        "cloudflare-ai-gateway" { return 'https://gateway.ai.cloudflare.com/v1/${CLOUDFLARE_ACCOUNT_ID}/${CLOUDFLARE_GATEWAY_ID}/compat' }
        "groq" { return "https://api.groq.com/openai/v1" }
        "togetherai" { return "https://api.together.ai/v1" }
        "xai" { return "https://api.x.ai/v1" }
        default { return $null }
    }
}

function Get-InputCapabilities([object]$model) {
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($model.modalities.input)) {
        if ($value -in @("text", "image") -and -not $values.Contains($value)) {
            $values.Add($value)
        }
    }
    if ($values.Count -eq 0) { $values.Add("text") }
    if (-not $values.Contains("structured")) { $values.Add("structured") }
    return @($values)
}

function Get-OutputCapabilities([object]$model) {
    $values = [System.Collections.Generic.List[string]]::new()
    foreach ($value in @($model.modalities.output)) {
        if ($value -eq "text" -and -not $values.Contains($value)) {
            $values.Add($value)
        }
    }
    if ($values.Count -eq 0) { $values.Add("text") }
    if ($model.structured_output) { $values.Add("structured") }
    $values.Add("tools")
    if ($model.reasoning) { $values.Add("reasoning") }
    return @($values)
}

function Get-Metadata([object]$model) {
    $metadata = [ordered]@{}
    foreach ($pair in @(
        @("description", $model.description),
        @("family", $model.family),
        @("knowledge", $model.knowledge),
        @("releaseDate", $model.release_date),
        @("lastUpdated", $model.last_updated)
    )) {
        if (-not [string]::IsNullOrWhiteSpace([string]$pair[1])) { $metadata[$pair[0]] = [string]$pair[1] }
    }
    if ($null -ne $model.open_weights) { $metadata["openWeights"] = ([bool]$model.open_weights).ToString().ToLowerInvariant() }
    return $metadata
}

$providers = [System.Collections.Generic.List[object]]::new()
foreach ($providerId in $providerIds) {
    $property = $source.PSObject.Properties[$providerId]
    if ($null -eq $property) { continue }
    $provider = $property.Value
    $providerEndpoint = Get-ProviderEndpoint $providerId $provider
    $models = [System.Collections.Generic.List[object]]::new()
    foreach ($modelProperty in @($provider.models.PSObject.Properties | Sort-Object Name)) {
        $model = $modelProperty.Value
        if (-not $model.tool_call -or $model.status -eq "deprecated") { continue }
        if (-not (@($model.modalities.input) -contains "text") -or
            -not (@($model.modalities.output) -contains "text")) { continue }
        if ($providerId -eq "openai" -and
            $modelProperty.Name.Contains("realtime", [StringComparison]::OrdinalIgnoreCase)) { continue }
        if ($providerId -eq "google-vertex" -and
            (-not $modelProperty.Name.StartsWith("gemini-", [StringComparison]::Ordinal) -or
             $modelProperty.Name -eq "gemini-3.1-flash-lite-preview")) { continue }
        if ($providerId -eq "minimax" -or $providerId -eq "minimax-cn") {
            if ($modelProperty.Name -notin @("MiniMax-M2.7", "MiniMax-M2.7-highspeed", "MiniMax-M3")) { continue }
        }
        if ($providerId -eq "xai" -and $modelProperty.Name -in @(
            "grok-3", "grok-3-fast", "grok-4.20-0309-non-reasoning",
            "grok-4.20-0309-reasoning", "grok-code-fast-1")) { continue }
        if ($providerId -eq "nvidia" -and $modelProperty.Name.ToLowerInvariant() -in @(
            "abacusai/dracarys-llama-3.1-70b-instruct", "bytedance/seed-oss-36b-instruct",
            "deepseek-ai/deepseek-v4-flash", "deepseek-ai/deepseek-v4-pro", "google/gemma-2-2b-it",
            "google/gemma-3n-e2b-it", "google/gemma-3n-e4b-it", "google/gemma-4-31b-it",
            "meta/llama-3.2-1b-instruct", "meta/llama-4-maverick-17b-128e-instruct",
            "microsoft/phi-4-mini-instruct", "minimaxai/minimax-m2.7", "mistralai/mistral-nemotron",
            "nvidia/nemotron-mini-4b-instruct", "qwen/qwen3-next-80b-a3b-instruct",
            "qwen/qwen3.5-397b-a17b", "sarvamai/sarvam-m", "upstage/solar-10.7b-instruct")) { continue }
        $apiId = Get-ApiId $providerId $provider $modelProperty.Name
        $modelEndpoint = Get-ModelEndpoint $providerId $apiId $providerEndpoint
        if ([string]::IsNullOrWhiteSpace($modelEndpoint)) { $modelEndpoint = $null }
        $contextWindow = [int64]($model.limit.context ?? 0)
        $maximumOutput = [int64]($model.limit.output ?? 0)
        if ($contextWindow -gt [int]::MaxValue -or $maximumOutput -gt [int]::MaxValue) { continue }
        if ($contextWindow -gt 0 -and $maximumOutput -ge $contextWindow) {
            $maximumOutput = [Math]::Max(0, $contextWindow - 1)
        }

        $cost = [ordered]@{
            input = [decimal]($model.cost.input ?? 0)
            output = [decimal]($model.cost.output ?? 0)
            cacheRead = [decimal]($model.cost.cache_read ?? 0)
            cacheWrite = [decimal]($model.cost.cache_write ?? 0)
        }
        $costTiers = [System.Collections.Generic.List[object]]::new()
        $seenTierThresholds = [System.Collections.Generic.HashSet[long]]::new()
        foreach ($tier in @($model.cost.tiers)) {
            if ($null -eq $tier -or $tier.tier.type -ne "context" -or $null -eq $tier.tier.size) { continue }
            $threshold = [long]$tier.tier.size
            if ($threshold -le 0 -or -not $seenTierThresholds.Add($threshold)) { continue }
            $costTiers.Add([ordered]@{
                above = $threshold
                input = [decimal]($tier.input ?? 0)
                output = [decimal]($tier.output ?? 0)
                cacheRead = [decimal]($tier.cache_read ?? 0)
                cacheWrite = [decimal]($tier.cache_write ?? 0)
            })
        }
        if ($costTiers.Count -gt 0) { $cost["tiers"] = @($costTiers) }
        $reasoning = Get-ReasoningProfile $providerId $apiId $modelProperty.Name $model
        $compatibility = Get-ModelCompatibility $providerId $apiId $modelEndpoint $modelProperty.Name $model

        $models.Add([ordered]@{
            id = [string]$model.id
            name = [string]$model.name
            api = $apiId
            baseUrl = $modelEndpoint
            contextWindow = [int]$contextWindow
            maximumOutput = [int]$maximumOutput
            input = @(Get-InputCapabilities $model)
            output = @(Get-OutputCapabilities $model)
            reasoning = @($reasoning.Levels)
            reasoningValues = $reasoning.Values
            cost = $cost
            metadata = Get-Metadata $model
            headers = Get-ModelHeaders $providerId
            compatibility = $compatibility
        })
    }

    if ($models.Count -eq 0) { continue }
    $providerMetadata = [ordered]@{}
    if ($provider.doc) { $providerMetadata["documentation"] = [string]$provider.doc }
    if ($provider.env) { $providerMetadata["environmentVariables"] = (@($provider.env) -join ",") }
    $providers.Add([ordered]@{
        id = $providerId
        name = [string]$provider.name
        endpoint = $providerEndpoint
        metadata = $providerMetadata
        models = @($models)
    })
}

$payload = [ordered]@{
    version = (Get-Date).ToUniversalTime().ToString("yyyy-MM-dd")
    generatedAt = (Get-Date).ToUniversalTime().ToString("o")
    providers = @($providers)
}

$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Path $directory -Force | Out-Null
$payload | ConvertTo-Json -Depth 16 -Compress | Set-Content -Path $OutputPath -Encoding utf8NoBOM
$modelCount = ($providers | ForEach-Object { $_.models.Count } | Measure-Object -Sum).Sum
Write-Host "Generated $modelCount models across $($providers.Count) providers at $OutputPath"
