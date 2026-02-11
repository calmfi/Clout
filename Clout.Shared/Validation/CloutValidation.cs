using System.ComponentModel.DataAnnotations;
using Clout.Shared.Exceptions;
using Quartz;

namespace Clout.Shared.Validation;

/// <summary>
/// Validation utilities for common Clout constraints.
/// </summary>
public static class CloutValidation
{
    /// <summary>
    /// Validates that a string is a valid blob ID (non-empty, reasonable length).
    /// </summary>
    public static void ValidateBlobId(string? blobId)
    {
        if (string.IsNullOrWhiteSpace(blobId))
            throw new Exceptions.ValidationException("blobId", "Blob ID cannot be empty.");
        
        if (blobId.Length > 256)
            throw new Exceptions.ValidationException("blobId", "Blob ID must not exceed 256 characters.");
    }

    /// <summary>
    /// Validates that a string is a valid queue name (non-empty, alphanumeric + hyphens/underscores).
    /// </summary>
    public static void ValidateQueueName(string? queueName)
    {
        if (string.IsNullOrWhiteSpace(queueName))
            throw new Exceptions.ValidationException("queueName", "Queue name cannot be empty.");
        
        if (queueName.Length > 256)
            throw new Exceptions.ValidationException("queueName", "Queue name must not exceed 256 characters.");
        
        if (!System.Text.RegularExpressions.Regex.IsMatch(queueName, @"^[a-zA-Z0-9_-]+$"))
            throw new Exceptions.ValidationException("queueName", "Queue name must contain only alphanumeric characters, hyphens, and underscores.");
    }

    /// <summary>
    /// Validates that a string is a valid function name (non-empty, reasonable length).
    /// </summary>
    public static void ValidateFunctionName(string? functionName)
    {
        if (string.IsNullOrWhiteSpace(functionName))
            throw new Exceptions.ValidationException("functionName", "Function name cannot be empty.");
        
        if (functionName.Length > 256)
            throw new Exceptions.ValidationException("functionName", "Function name must not exceed 256 characters.");
    }

    /// <summary>
    /// Validates that a file exists at the given path.
    /// </summary>
    public static void ValidateFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new Exceptions.ValidationException("filePath", "File path cannot be empty.");
        
        if (!System.IO.File.Exists(filePath))
            throw new Exceptions.ValidationException("filePath", $"File not found at path: {filePath}");
    }

    /// <summary>
    /// Validates that a byte size is within acceptable limits.
    /// </summary>
    public static void ValidateByteSize(long bytes, long maxBytes)
    {
        if (bytes < 0)
            throw new Exceptions.ValidationException("size", "Byte size cannot be negative.");
        
        if (bytes > maxBytes)
            throw new Exceptions.ValidationException("size", $"Byte size ({bytes}) exceeds maximum allowed ({maxBytes}).");
    }

    /// <summary>
    /// Validates a cron expression.
    /// </summary>
    public static void ValidateCronExpression(string? cron)
    {
        if (string.IsNullOrWhiteSpace(cron))
            throw new Exceptions.ValidationException("cron", "Cron expression cannot be empty.");
        
        if (!CronHelper.TryParseSchedule(cron, out _))
        {
            throw new Exceptions.ValidationException("cron", "Invalid cron expression.");
        }
    }
}

/// <summary>
/// Attribute to validate a cron expression.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class CronExpressionAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null or string { Length: 0 })
            return ValidationResult.Success;

        try
        {
            if (!CronHelper.TryParseSchedule((string)value, out _))
            {
                return new ValidationResult("Invalid cron expression.");
            }
            return ValidationResult.Success;
        }
        catch (Exception ex)
        {
            return new ValidationResult($"Invalid cron expression: {ex.Message}");
        }
    }
}

/// <summary>
/// Attribute to validate a queue name.
/// </summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class QueueNameAttribute : ValidationAttribute
{
    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null or string { Length: 0 })
            return ValidationResult.Success;

        var name = (string)value;
        if (name.Length > 256)
            return new ValidationResult("Queue name must not exceed 256 characters.");

        if (!System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-zA-Z0-9_-]+$"))
            return new ValidationResult("Queue name must contain only alphanumeric characters, hyphens, and underscores.");

        return ValidationResult.Success;
    }
}
