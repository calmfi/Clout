using System.Reflection;
using System.Runtime.Loader;

namespace Clout.Api.Functions;

internal static class FunctionRunner
{
    public static async Task RunAsync(string assemblyPath, string methodName, CancellationToken cancellationToken)
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

            foreach (var t in types)
            {
                // Prefer static, then instance methods; require zero parameters for now.
                var method = t.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null);
                if (method is null) continue;

                object? instance = null;
                if (!method.IsStatic)
                {
                    var ctor = t.GetConstructor(Type.EmptyTypes);
                    if (ctor is null) continue;
                    instance = Activator.CreateInstance(t);
                }

                var result = method.Invoke(instance, null);
                // If the function returns a Task, await it.
                if (result is Task task)
                {
                    using var reg = cancellationToken.Register(() => { /* cooperative cancel not possible post-invoke */ });
                    await task.ConfigureAwait(false);
                }
                return;
            }

            throw new MissingMethodException($"No public parameterless method named '{methodName}' was found.");
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

    private sealed class IsolatedLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public IsolatedLoadContext(string mainAssemblyPath) : base(isCollectible: true)
        {
            _resolver = new AssemblyDependencyResolver(mainAssemblyPath);
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var path = _resolver.ResolveAssemblyToPath(assemblyName);
            return path is null ? null : LoadFromAssemblyPath(path);
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            var path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path is null ? IntPtr.Zero : LoadUnmanagedDllFromPath(path);
        }
    }
}

