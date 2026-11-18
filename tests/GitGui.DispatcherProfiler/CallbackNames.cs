using System.Reflection;
using System.Runtime.CompilerServices;

internal static class CallbackNames
{
    private static readonly ConditionalWeakTable<MethodBase, string> Names = new();

    // Reuse metadata without retaining dynamic methods or allocating a string for every span.
    public static string For(MethodBase method) => Names.GetValue(method,
        static value => value.DeclaringType?.FullName + "." + value.Name);
}
