using System.Reflection;

namespace Clout.Api;

internal static class FunctionAssemblyInspector
{
    public static bool ContainsPublicMethod(string assemblyPath, string methodName, out string? declaringType)
    {
        declaringType = null;
        // Load in an isolated, collectible context so the file isn't permanently locked.
        var alc = new IsolatedLoadContext(assemblyPath);
        try
        {
            var asm = alc.LoadFromAssemblyPath(assemblyPath);
            Type[] types;
            try
            {
                types = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
            }

            foreach (var t in types)
            {
                // Public methods only; include static and instance.
                var methods = t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
                foreach (var m in methods)
                {
                    if (string.Equals(m.Name, methodName, StringComparison.Ordinal))
                    {
                        declaringType = t.FullName;
                        return true;
                    }
                }
            }
            return false;
        }
        catch
        {
            return false;
        }
        finally
        {
            try
            {
                alc.Unload();
                // Encourage unload to release file handles.
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
            catch { /* ignore */ }
        }
    }


}
