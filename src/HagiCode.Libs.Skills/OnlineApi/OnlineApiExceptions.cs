using System.Net;

namespace HagiCode.Libs.Skills.OnlineApi;

public class OnlineApiException : Exception
{
    public OnlineApiException(
        OnlineApiOperation operation,
        string message,
        Uri? requestUri = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Operation = operation;
        RequestUri = requestUri;
    }

    public OnlineApiOperation Operation { get; }

    public Uri? RequestUri { get; }
}

public sealed class OnlineApiHttpException : OnlineApiException
{
    public OnlineApiHttpException(
        OnlineApiOperation operation,
        string message,
        HttpStatusCode statusCode,
        Uri requestUri,
        string? responseBody = null,
        Exception? innerException = null)
        : base(operation, message, requestUri, innerException)
    {
        StatusCode = statusCode;
        ResponseBody = responseBody;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ResponseBody { get; }
}

public sealed class OnlineApiValidationException : OnlineApiException
{
    public OnlineApiValidationException(
        OnlineApiOperation operation,
        string message,
        Uri? requestUri = null,
        Exception? innerException = null)
        : base(operation, message, requestUri, innerException)
    {
    }
}
