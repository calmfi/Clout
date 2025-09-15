
# Gemini Code Understanding

This document provides an overview of the Clout project, its architecture, and how to build, run, and interact with it.

## Project Overview

Clout is a .NET-based application that provides a local cloud-like environment for managing file blobs and executing .NET functions. It consists of a RESTful API, a command-line client, a web-based UI, and a shared library for common data structures.

### Key Components

*   **`Clout.Host`**: An ASP.NET Core Minimal API that serves as the backend for blob storage and function management. It exposes endpoints for uploading, downloading, and managing blobs, as well as registering, scheduling, and executing .NET functions.
*   **`Clout.Client`**: A command-line interface (CLI) that allows users to interact with the `Clout.Host` from the terminal. It provides commands for all the major functionalities of the API.
*   **`Clout.UI`**: A Blazor-based web application that provides a graphical user interface for interacting with the Clout system. Users can view, upload, and manage blobs and functions through their web browser.
*   **`Cloud.Shared`**: A shared class library that contains the data models (e.g., `BlobInfo`, `BlobMetadata`) and the `IBlobStorage` interface, which defines the contract for blob storage operations. This library is used by both the API and the client.
*   **`Clout.Host.IntegrationTests`**: A suite of integration tests for the `Clout.Host` project, ensuring the reliability of the API endpoints.
*   **`Sample.Function`**: An example of a .NET function that can be registered and executed by the Clout system.

## Building and Running

### Build

To build the entire solution, run the following command from the root directory:

```bash
dotnet build
```

### Run the API

To run the API, use the following command:

```bash
dotnet run --project Clout.Api/Clout.Host.csproj
```

The API will be available at `http://localhost:5000`, and the Swagger UI can be accessed at `http://localhost:5000/swagger`.

### Run the Client

The client can be used to interact with the API from the command line. Here are some examples:

*   **List blobs:**
    ```bash
    dotnet run --project Clout.Client -- blob list
    ```
*   **Upload a file:**
    ```bash
    dotnet run --project Clout.Client -- blob upload <path-to-file>
    ```

For a full list of commands, run:
```bash
dotnet run --project Clout.Client
```

### Run the UI

To run the web UI, use the following command:

```bash
dotnet run --project Clout.UI
```

The UI will be available at `http://localhost:5100`.

## Development Conventions

*   **API Design**: The API follows a RESTful design, with clear and consistent endpoint naming.
*   **Asynchronous Operations**: The codebase makes extensive use of `async` and `await` for non-blocking I/O operations.
*   **Dependency Injection**: The API uses dependency injection to manage dependencies, such as the `IBlobStorage` implementation.
*   **Testing**: The project includes integration tests for the API, which are written using xUnit.
