using System;

namespace SamRockProtocol.Models;

public class SamRockProtocolResponse(bool success, string message, Exception exception)
{
    public bool Success { get; set; } = success;
    public string Message { get; set; } = message;
    public Exception Exception { get; set; } = exception;
}
