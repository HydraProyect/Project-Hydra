using System.Reflection;

namespace CaeManager.Architecture.Tests.Soporte;

public static class ReflexionArquitecturaHelper
{
    public static Assembly CargarAssembly(string nombre) => Assembly.Load(nombre);

    public static bool DependeDeAssembly(Assembly origen, string prefijoAssemblyProhibido) =>
        origen.GetReferencedAssemblies()
            .Any(a => a.Name is not null && a.Name.StartsWith(prefijoAssemblyProhibido, StringComparison.Ordinal));

    public static IEnumerable<Type> TiposQueReferencianTipo(Assembly origen, Type tipoProhibido) =>
        TiposDe(origen).Where(t => !EsComposicionRoot(t) && t != tipoProhibido
                                   && ReferenciaAlgo(t, c => c == tipoProhibido));

    public static IEnumerable<Type> TiposQueReferencianNamespace(Assembly origen, string namespaceProhibido) =>
        TiposDe(origen).Where(t => !EsComposicionRoot(t) && t.Namespace != namespaceProhibido
                                   && ReferenciaAlgo(t, c => c.Namespace == namespaceProhibido));

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

    /// <summary>
    /// Las superficies por las que un tipo puede depender de otro.
    ///
    /// <para>
    /// La primera versión miraba clase base, parámetros de <b>constructor</b>,
    /// campos y propiedades. Tres formas la esquivaban compilando, demostradas
    /// por mutación sobre <c>CaeManager.Web</c> y el <c>DbContext</c> concreto:
    /// un <b>parámetro de método</b> normal, un <b>tipo de retorno</b>, y un
    /// campo cuyo tipo <b>envuelve</b> al prohibido en un genérico
    /// —<c>IDbContextFactory&lt;T&gt;</c>, que además es el patrón canónico de
    /// Blazor Server para obtener un contexto—.
    /// </para>
    ///
    /// <para>
    /// El único caso de los probados que sí se detectaba lo hacía <b>por
    /// accidente</b>: un método <c>async</c> que sostiene el contexto entre dos
    /// <c>await</c> hace que el compilador aloje el local como campo de la
    /// máquina de estados, y los campos sí se miraban. La detección dependía
    /// de si el método era asíncrono, que no tiene nada que ver con la
    /// propiedad vigilada.
    /// </para>
    ///
    /// <para>
    /// El recorrido de genéricos es recursivo a propósito: la dependencia es
    /// igual de real dentro de <c>Task&lt;List&lt;T&gt;&gt;</c> que en <c>T</c>
    /// pelado, y también a través de arrays y referencias.
    /// </para>
    /// </summary>
    private static bool ReferenciaAlgo(Type tipo, Func<Type, bool> esProhibido)
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic
                                   | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        if (Alcanza(tipo.BaseType, esProhibido)) return true;

        if (tipo.GetInterfaces().Any(i => Alcanza(i, esProhibido))) return true;

        if (tipo.GetFields(Flags).Any(f => Alcanza(f.FieldType, esProhibido))) return true;

        if (tipo.GetProperties(Flags).Any(p => Alcanza(p.PropertyType, esProhibido))) return true;

        if (tipo.GetConstructors(Flags).Any(c => c.GetParameters().Any(p => Alcanza(p.ParameterType, esProhibido))))
            return true;

        return tipo.GetMethods(Flags).Any(m =>
            Alcanza(m.ReturnType, esProhibido)
            || m.GetParameters().Any(p => Alcanza(p.ParameterType, esProhibido)));
    }

    /// <summary>
    /// Un tipo "alcanza" al prohibido si lo es, o si lo envuelve por cualquier
    /// número de capas de genérico, array o referencia.
    /// </summary>
    private static bool Alcanza(Type? candidato, Func<Type, bool> esProhibido)
    {
        if (candidato is null) return false;
        if (esProhibido(candidato)) return true;

        if (candidato.HasElementType && Alcanza(candidato.GetElementType(), esProhibido)) return true;

        return candidato.IsGenericType
               && candidato.GetGenericArguments().Any(a => Alcanza(a, esProhibido));
    }
}
