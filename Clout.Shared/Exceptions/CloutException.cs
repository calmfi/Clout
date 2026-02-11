namespace Clout.Shared.Exceptions;

/// <summary>
/// Base exception for all Clout-specific errors.
/// </summary>
public class CloutException : Exception
{
    public string ErrorCode { get; }

    public CloutException(string message, string errorCode = "CLOUT_ERROR") : base(message)
    {
        ErrorCode = errorCode;
    }

    public CloutException(string message, Exception innerException, string errorCode = "CLOUT_ERROR")
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}

/// <summary>
/// Exception thrown when a blob operation fails.
/// </summary>
public class BlobOperationException : CloutException
{
    public string BlobId { get; }

    public BlobOperationException(string blobId, string message, string errorCode = "BLOB_OPERATION_FAILED")
        : base(message, errorCode)
    {
        BlobId = blobId;
    }

    public BlobOperationException(string blobId, string message, Exception innerException, string errorCode = "BLOB_OPERATION_FAILED")
        : base(message, innerException, errorCode)
    {
        BlobId = blobId;
    }
}

/// <summary>
/// Exception thrown when a blob is not found.
/// </summary>
public class BlobNotFoundException : CloutException
{
    public string BlobId { get; }

    public BlobNotFoundException(string blobId)
        : base($"Blob '{blobId}' not found.", "BLOB_NOT_FOUND")
    {
        BlobId = blobId;
    }
}

/// <summary>
/// Exception thrown when a queue operation fails.
/// </summary>
public class QueueOperationException : CloutException
{
    public string QueueName { get; }

    public QueueOperationException(string queueName, string message, string errorCode = "QUEUE_OPERATION_FAILED")
        : base(message, errorCode)
    {
        QueueName = queueName;
    }

    public QueueOperationException(string queueName, string message, Exception innerException, string errorCode = "QUEUE_OPERATION_FAILED")
        : base(message, innerException, errorCode)
    {
        QueueName = queueName;
    }
}

/// <summary>
/// Exception thrown when a queue quota is exceeded.
/// </summary>
public class QueueQuotaExceededException : CloutException
{
    public string QueueName { get; }
    public long MaxBytes { get; }

    public QueueQuotaExceededException(string queueName, long maxBytes)
        : base($"Queue '{queueName}' has exceeded the maximum size quota of {maxBytes} bytes.", "QUEUE_QUOTA_EXCEEDED")
    {
        QueueName = queueName;
        MaxBytes = maxBytes;
    }
}

/// <summary>
/// Exception thrown when a function execution fails.
/// </summary>
public class FunctionExecutionException : CloutException
{
    public string FunctionName { get; }
    public string BlobId { get; }

    public FunctionExecutionException(string functionName, string blobId, string message, string errorCode = "FUNCTION_EXECUTION_FAILED")
        : base(message, errorCode)
    {
        FunctionName = functionName;
        BlobId = blobId;
    }

    public FunctionExecutionException(string functionName, string blobId, string message, Exception innerException, string errorCode = "FUNCTION_EXECUTION_FAILED")
        : base(message, innerException, errorCode)
    {
        FunctionName = functionName;
        BlobId = blobId;
    }
}

/// <summary>
/// Exception thrown when input validation fails.
/// </summary>
public class ValidationException : CloutException
{
    public Dictionary<string, string[]> Errors { get; }

    public ValidationException(Dictionary<string, string[]> errors)
        : base("One or more validation errors occurred.", "VALIDATION_FAILED")
    {
        Errors = errors;
    }

    public ValidationException(string fieldName, string error)
        : base($"Validation failed for field '{fieldName}'.", "VALIDATION_FAILED")
    {
        Errors = new Dictionary<string, string[]> { { fieldName, new[] { error } } };
    }
}
