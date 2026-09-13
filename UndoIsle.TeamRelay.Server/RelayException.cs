namespace UndoIsle.TeamRelay.Server;

public sealed class RelayException(
    string code,
    string message,
    int statusCode = StatusCodes.Status400BadRequest) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
