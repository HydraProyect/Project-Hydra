using System.Reflection;

namespace CaeManager.Architecture.Tests.Soporte;

public static class ReflexionArquitecturaHelper
{
    public static Assembly CargarAssembly(string nombre) => Assembly.Load(nombre);

    public static bool DependeDeAssembly(Assembly origen, string prefijoAssemblyProhibido) =>
        origen.GetReferencedAssemblies()
            .Any(a => a.Name is not null && a.Name.StartsWith(prefijoAssemblyProhibido, StringComparison.Ordinal));

    public static IEnumerable<Type> TiposQueReferencianTipo(Assembly origen, Type tipoProhibido) =>
        TiposDe(origen).Where(t => !EsComposicionRoot(t) && ReferenciaTipo(t, tipoProhibido));

    public static IEnumerable<Type> TiposQueReferencianNamespace(Assembly origen, string namespaceProhibido) =>
        TiposDe(origen).Where(t => !EsComposicionRoot(t) && ReferenciaNamespace(t, namespaceProhibido));

    // Program.cs (top-level statements, async Main) compila a una clase
    // "Program" cuyo estado entre awaits el compilador hospeda en structs
    // anidados (p. ej. "Program+<<Main>$>d__0") con un campo por variable local
    // capturada — incluida CaeManagerDbContext, resuelto ahí a propósito como
    // composition root. No es un caso exótico: es una consecuencia estructural
    // de "async Main", así que se excluye explícitamente en vez de asumir que
    // la reflexión nunca lo alcanzaría.
    private static bool EsComposicionRoot(Type tipo)
    {
        for (var actual = tipo; actual is not null; actual = actual.DeclaringType)
            if (actual.Name == "Program")
                return true;

        return false;
    }

    // GetTypes() puede lanzar ReflectionTypeLoadException en ensamblados Blazor
    // (componentes generados que no cargan fuera del host de la app) — se
    // toman los tipos que sí resolvieron y se descartan los demás, ya que no
    // son alcanzables por inyección de dependencias de todos modos.
    private static IEnumerable<Type> TiposDe(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null)!;
        }
    }

    private static bool ReferenciaTipo(Type tipo, Type tipoProhibido)
    {
        if (tipo == tipoProhibido) return false;
        if (tipo.BaseType == tipoProhibido) return true;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        if (tipo.GetConstructors(flags).Any(c => c.GetParameters().Any(p => p.ParameterType == tipoProhibido)))
            return true;

        if (tipo.GetFields(flags).Any(f => f.FieldType == tipoProhibido))
            return true;

        if (tipo.GetProperties(flags).Any(p => p.PropertyType == tipoProhibido))
            return true;

        return false;
    }

    private static bool ReferenciaNamespace(Type tipo, string namespaceProhibido)
    {
        if (tipo.Namespace == namespaceProhibido) return false; // no se auto-reporta

        bool EsDelNamespaceProhibido(Type? candidato) => candidato?.Namespace == namespaceProhibido;

        if (EsDelNamespaceProhibido(tipo.BaseType)) return true;

        var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        if (tipo.GetConstructors(flags).Any(c => c.GetParameters().Any(p => EsDelNamespaceProhibido(p.ParameterType))))
            return true;

        if (tipo.GetFields(flags).Any(f => EsDelNamespaceProhibido(f.FieldType)))
            return true;

        if (tipo.GetProperties(flags).Any(p => EsDelNamespaceProhibido(p.PropertyType)))
            return true;

        return false;
    }
}
