using System.Reflection;
using System.Text.Json;

namespace Clout.Host.Functions;

internal static class FunctionRunner
{
    private static readonly TimeSpan ExecutionTimeout = TimeSpan.FromMinutes(5);

    public static async Task RunAsync(string assemblyPath, string methodName, JsonDocument? payload, CancellationToken cancellationToken)
    {
        // Load in isolated context to avoid locking and enable unloading
        var alc = new IsolatedLoadContext(assemblyPath);
        try
        {
            var asm = alc.LoadFromAssemblyPath(assemblyPath);
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            var hasPayload = payload is not null;

            foreach (var t in types)
            {
                // Gather all public methods with the requested name.
                var candidates = t.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance)
                    .Where(m => string.Equals(m.Name, methodName, StringComparison.Ordinal))
                    .ToArray();

                if (candidates.Length == 0) continue;

                MethodInfo? selected = null;
                object?[] arguments = Array.Empty<object?>();

                if (hasPayload)
                {
                    foreach (var candidate in candidates)
                    {
                        var parameters = candidate.GetParameters();
                        if (parameters.Length != 1) continue;

                        var parameterType = parameters[0].ParameterType;
                        if (parameterType == typeof(JsonElement))
                        {
                            selected = candidate;
                            arguments = new object?[] { payload!.RootElement };
                            break;
                        }

                        if (parameterType == typeof(JsonDocument))
                        {
                            selected = candidate;
                            arguments = new object?[] { payload };
                            break;
                        }

                        if (parameterType == typeof(string))
                        {
                            var root = payload!.RootElement;
                            var stringValue = root.ValueKind == JsonValueKind.String ? root.GetString() : root.GetRawText();
                            selected = candidate;
                            arguments = new object?[] { stringValue };
                            break;
                        }
                    }
                }

                // Fallback to parameterless invocation when no single-parameter match was found.
                selected ??= candidates.FirstOrDefault(m => m.GetParameters().Length == 0);

                if (selected is null) continue;

                if (selected.GetParameters().Length == 0)
                {
                    arguments = Array.Empty<object?>();
                }

                object? instance = null;
                if (!selected.IsStatic)
                {
                    var ctor = t.GetConstructor(Type.EmptyTypes);
                    if (ctor is null) continue;
                    instance = Activator.CreateInstance(t);
                }

                var result = selected.Invoke(instance, arguments);
                // If the function returns a Task, await it with a timeout.
                if (result is Task task)
                {
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    cts.CancelAfter(ExecutionTimeout);
                    await task.WaitAsync(cts.Token).ConfigureAwait(false);
                }
                return;
            }

            throw new MissingMethodException($"No public method named '{methodName}' was found with a compatible signature.");
        }
        finally
        {
            try
            {
                alc.Unload();
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch { /* ignore */ }
        }
    }
}
