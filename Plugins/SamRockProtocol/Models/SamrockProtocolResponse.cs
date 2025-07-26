using System;

namespace SamRockProtocol.Models;

public class SamRockProtocolResponse
{
    public SamRockProtocolResponse(bool success, string message, Exception exception)
    {
        Success = success;
        Message = message;
        Exception = exception;
    }

    public bool Success { get; set; }
    public string Message { get; set; }
    public Exception Exception { get; set; }
}
