# Clout Service Tests

This project contains comprehensive service-level tests for the Clout application's core services.

## Test Coverage

### Storage Services
- **FileBlobStorageTests** - 24 test cases covering:
  - Blob creation and storage
  - Content persistence and retrieval
  - Metadata management
  - Replace and delete operations
  - Large file handling
  - Concurrent access patterns
  - Error handling and edge cases

### Queue Services
- **DiskBackedAmqpQueueServerTests** - 26 test cases covering:
  - Queue creation and management
  - Enqueue/dequeue operations
  - FIFO ordering guarantees
  - Quota enforcement (message size, queue size, message count)
  - Overflow policies (Reject, DropOldest)
  - Queue purging
  - Thread safety and concurrent operations
  - Queue persistence across server restarts
  - Cancellation token support

### Function Services
- **FunctionExecutorTests** - 12 test cases covering:
  - Function execution flow
  - Blob resolution and loading
  - Parameter validation
  - Error handling and exception wrapping
  - Resource cleanup and disposal
  - Cancellation support
  - Concurrent execution handling

- **QueueTriggerDispatcherTests** - 13 test cases covering:
  - Worker lifecycle management
  - Activation and deactivation
  - Multiple worker coordination
  - Graceful shutdown
  - Cancellation handling
  - Concurrent operations
  - Parameter validation

## Running Tests

```bash
dotnet test tests/Clout.ServiceTests
```

### Run specific test class:
```bash
dotnet test tests/Clout.ServiceTests --filter FullyQualifiedName~FileBlobStorageTests
```

### Run with detailed output:
```bash
dotnet test tests/Clout.ServiceTests --logger "console;verbosity=detailed"
```

## Test Patterns

### Service-Level Focus
These tests focus on:
- Business logic and service behavior
- Integration between components
- Error handling and resilience
- Resource management and cleanup
- Thread safety and concurrency
- State persistence

### Test Structure
- **Arrange**: Set up mocks, test data, and initial conditions
- **Act**: Execute the service operation
- **Assert**: Verify behavior using FluentAssertions

### Dependencies
- **xUnit**: Test framework
- **Moq**: Mocking framework for dependencies
- **FluentAssertions**: Fluent assertion library for readable tests

## Key Testing Strategies

1. **Isolation**: Each test class uses temporary directories/resources that are cleaned up
2. **Mocking**: External dependencies are mocked to focus on service logic
3. **Concurrency**: Tests verify thread-safe behavior where applicable
4. **Edge Cases**: Tests cover error conditions, empty data, and boundary conditions
5. **Resource Cleanup**: Tests ensure proper disposal and cleanup of resources

## Contributing

When adding new service tests:
1. Follow the existing test structure (Arrange-Act-Assert)
2. Use descriptive test names that explain the scenario
3. Include both happy path and error scenarios
4. Test concurrent access patterns for shared resources
5. Verify resource cleanup with `IDisposable` test fixtures
6. Use FluentAssertions for readable assertions
