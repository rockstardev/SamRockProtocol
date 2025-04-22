#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

// Options parsed from connection string
public class BoltzOptions
{
    public required Uri ApiUrl { get; set; }
    public required string SwapToAsset { get; set; } // e.g., "L-BTC"
    public bool IsTestnet => ApiUrl?.ToString().Contains(".testnet.") ?? false;
}

// --- Request DTOs ---

public class CreateReverseSwapRequest
{
    [JsonPropertyName("from")]
    public string FromAsset { get; set; } = "BTC"; // Lightning

    [JsonPropertyName("to")]
    public string ToAsset { get; set; } = "L-BTC"; // Liquid

    [JsonPropertyName("invoiceAmount")]
    public long InvoiceAmountSat { get; set; }

    [JsonPropertyName("preimageHash")]
    public string PreimageHash { get; set; } = string.Empty;

    [JsonPropertyName("claimPublicKey")]
    public string ClaimPublicKey { get; set; } = string.Empty;

    // Optional fields like referralId, webhookUrl etc. can be added if needed
}

// --- Response DTOs ---

public class CreateReverseSwapResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("lockupAddress")]
    public string LockupAddress { get; set; } = string.Empty; // Address Boltz locks funds to

    [JsonPropertyName("refundPublicKey")]
    public string RefundPublicKey { get; set; } = string.Empty; // Boltz's key

    [JsonPropertyName("timeoutBlockHeight")]
    public int TimeoutBlockHeight { get; set; }

    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; set; } // For Liquid

    [JsonPropertyName("swapTree")]
    public SwapTree? SwapTree { get; set; }

    // Add other fields as needed, e.g., fees
    [JsonPropertyName("expectedAmount")]
    public long ExpectedAmount { get; set; }

    [JsonPropertyName("minerFees")]
    public MinerFees? MinerFees { get; set; } // Boltz fee estimation
}

public class SwapTree
{
    [JsonPropertyName("claimLeaf")]
    public Leaf ClaimLeaf { get; set; } = new();

    [JsonPropertyName("refundLeaf")]
    public Leaf RefundLeaf { get; set; } = new();
}

public class Leaf
{
    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }
}

public class MinerFees
{
    [JsonPropertyName("claim")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Claim { get; set; }

    [JsonPropertyName("lockup")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Lockup { get; set; }
}

// --- WebSocket DTOs ---

public class WebSocketRequest
{
    [JsonPropertyName("op")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("channel")]
    public string Channel { get; set; } = string.Empty;

    [JsonPropertyName("args")]
    public List<string> Args { get; set; } = new();
}

public class WebSocketResponse
{
    [JsonPropertyName("event")]
    public string Event { get; set; } = string.Empty; // e.g., "subscribe", "update", "pong"

    [JsonPropertyName("channel")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Channel { get; set; }

    [JsonPropertyName("args")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SwapStatusUpdate>? Args { get; set; }
}

public class SwapStatusUpdate
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    // Other fields from GET /swap/{id} might be present, add as needed
    [JsonPropertyName("transaction")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public BoltzTransaction? Transaction { get; set; }

    [JsonPropertyName("failureReason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FailureReason { get; set; }
}

public class BoltzTransaction
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; set; } // Transaction hash

    [JsonPropertyName("hex")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Hex { get; set; }

    [JsonPropertyName("eta")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Eta { get; set; } // Estimated confirmation time in seconds
}
