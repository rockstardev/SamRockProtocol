#nullable enable
using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.RockstarDev.Plugins.BoltzExchanger;

// --- Request DTOs ---

public class CreateReverseSwapRequest
{
    [JsonPropertyName("address")]
    public string Address { get; set; }
    
    [JsonPropertyName("from")]
    public string From { get; set; } = "BTC"; // Lightning

    [JsonPropertyName("to")]
    public string To { get; set; } = "L-BTC";
    
    [JsonPropertyName("claimCovenant")]
    public bool ClaimCovenant { get; set; }

    [JsonPropertyName("invoiceAmount")]
    public long InvoiceAmountSat { get; set; }

    [JsonPropertyName("preimageHash")]
    public string PreimageHash { get; set; } = string.Empty;

    [JsonPropertyName("claimPublicKey")]
    public string ClaimPublicKey { get; set; } = string.Empty;
}

// --- Response DTOs ---

public class CreateReverseSwapResponse
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("invoice")]
    public string Invoice { get; set; } = string.Empty;

    [JsonPropertyName("swapTree")]
    public SwapTree? SwapTree { get; set; }

    [JsonPropertyName("lockupAddress")]
    public string LockupAddress { get; set; } = string.Empty; // Address Boltz locks funds to

    [JsonPropertyName("refundPublicKey")]
    public string RefundPublicKey { get; set; } = string.Empty; // Boltz's key

    [JsonPropertyName("refundAddress")]
    public string RefundAddress { get; set; } = string.Empty;

    [JsonPropertyName("onchainAmount")]
    public int OnchainAmount { get; set; }

    [JsonPropertyName("timeoutBlockHeight")]
    public int TimeoutBlockHeight { get; set; }

    [JsonPropertyName("blindingKey")]
    public string? BlindingKey { get; set; } // For Liquid

    [JsonPropertyName("referralId")]
    public string ReferralId { get; set; }
}

public class SwapTree
{
    [JsonPropertyName("claimLeaf")]
    public Leaf ClaimLeaf { get; set; } = new();

    [JsonPropertyName("refundLeaf")]
    public Leaf RefundLeaf { get; set; } = new();

    [JsonPropertyName("covenantClaimLeaf")]
    public Leaf? CovenantClaimLeaf { get; set; }
}

public class Leaf
{
    [JsonPropertyName("output")]
    public string Output { get; set; } = string.Empty;

    [JsonPropertyName("version")]
    public int Version { get; set; }
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
