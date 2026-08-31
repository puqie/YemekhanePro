namespace Yemekhane.Application.Common;

public sealed class RequestValidationException(string message) : Exception(message);
public sealed class EntityNotFoundException(string message) : Exception(message);
public sealed class EntityConflictException(string message) : Exception(message);
