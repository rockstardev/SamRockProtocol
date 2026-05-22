using System;
using Newtonsoft.Json;

namespace SamRockProtocol.Models;

public class SamRockProtocolResponse(bool success, string message, Exception exception)
{
    public bool Success { get; set; } = success;
    public string Message { get; set; } = message;
    public string Error { get; set; } = exception?.Message;

    [JsonIgnore]
    public Exception Exception { get; set; } = exception;
}
