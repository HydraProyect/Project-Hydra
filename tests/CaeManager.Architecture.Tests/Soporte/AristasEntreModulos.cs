using System.Text;

namespace CaeManager.Architecture.Tests.Soporte;

/// <summary>
/// Las aristas entre módulos medidas en el IL (ADR-013 § 4.3, test 2; paso M0b). Cada arista es «tipo raíz de origen ->
/// tipo raíz de destino» con el módulo de cada uno según <see cref="RegistroDeModulos"/>. Un tipo anidado, una clausura
/// o una máquina de estado cuenta como su tipo raíz; el auxiliar <c>__Blazor.</c> de un componente Razor, como el
/// componente. Las referencias a un tipo del mismo módulo y a tipos fuera de <c>CaeManager.*</c> no son aristas (las
/// segundas se guardan aparte por origen, para la regla de bibliotecas de Facturación).
///
/// <para>
/// Esta clase solo mide. Qué pares están prohibidos y qué deuda hay congelada lo dice
/// <c>DependenciasEntreModulosTests</c>; el instrumento de lectura es <see cref="ReferenciasIl"/>.
/// </para>
/// </summary>
internal sealed class AristasEntreModulos
{
    private readonly Dictionary<(Modulo Origen, Modulo Destino), SortedDictionary<string, SortedSet<string>>> _pares = new();
    private readonly Dictionary<string, Modulo> _moduloDeOrigen = new(StringComparer.Ordinal);
    private readonly SortedDictionary<string, SortedSet<string>> _externas = new(StringComparer.Ordinal);
    private readonly SortedSet<string> _sinModulo = new(StringComparer.Ordinal);

    /// <summary>Cuántos tipos raíz se miraron como origen, por módulo.</summary>
    public SortedDictionary<Modulo, int> OrigenesPorModulo { get; } = new();

    /// <summary>Tipos raíz de origen sin módulo en el registro (debe ser vacío: lo exige ModulosRegistradosTests).</summary>
    public IReadOnlyCollection<string> OrigenesSinModulo => _origenesSinModulo;

    private readonly SortedSet<string> _origenesSinModulo = new(StringComparer.Ordinal);

    /// <summary>Tipos de <c>CaeManager.*</c> referidos que el registro no clasifica (debe ser vacío).</summary>
    public IReadOnlyCollection<string> DestinosSinModulo => _sinModulo;

    public IEnumerable<(Modulo Origen, Modulo Destino)> Pares => _pares.Keys.OrderBy(p => p.Origen).ThenBy(p => p.Destino);

    /// <summary>Origen -> destinos de un par de módulos (vacío si no hay aristas).</summary>
    public IReadOnlyDictionary<string, SortedSet<string>> De(Modulo origen, Modulo destino) =>
        _pares.TryGetValue((origen, destino), out var d) ? d : new SortedDictionary<string, SortedSet<string>>();

    /// <summary>Los tipos raíz de un módulo que se midieron como origen.</summary>
    public IEnumerable<string> OrigenesDe(Modulo modulo) =>
        _moduloDeOrigen.Where(p => p.Value == modulo).Select(p => p.Key).Order(StringComparer.Ordinal);

    /// <summary>Namespaces fuera de <c>CaeManager.*</c> que referencia cada tipo raíz de origen.</summary>
    public IReadOnlyDictionary<string, SortedSet<string>> ExternasPorOrigen => _externas;

    public int TiposDeOrigen(Modulo modulo) => OrigenesPorModulo.GetValueOrDefault(modulo);

    public (int Origenes, int Aristas) Cifras(Modulo origen, Modulo destino)
    {
        var d = De(origen, destino);
        return (d.Count, d.Values.Sum(v => v.Count));
    }

    /// <summary>Mide las aristas de los tipos dados (todos los tipos, anidados incluidos).</summary>
    public static AristasEntreModulos Medir(RegistroDeModulos registro, IEnumerable<Type> tipos)
    {
        var resultado = new AristasEntreModulos();
        var cacheDeDestino = new Dictionary<Type, (string Nombre, Modulo? Modulo)>();

        foreach (var tipo in tipos.Where(t => !registro.EsAjeno(t)))
        {
            var raiz = RegistroDeModulos.RaizDe(tipo);
            if (RegistroDeModulos.TiposAClasificar([raiz]).Count == 0)
                continue;

            var origen = NombreDeLista(raiz);
            if (registro.Clasificar(raiz) is not { } clasificacion)
            {
                resultado._origenesSinModulo.Add(origen);
                continue;
            }

            var moduloOrigen = clasificacion.Modulo;
            if (resultado._moduloDeOrigen.TryAdd(origen, moduloOrigen))
                resultado.OrigenesPorModulo[moduloOrigen] = resultado.OrigenesPorModulo.GetValueOrDefault(moduloOrigen) + 1;

            foreach (var referido in ReferenciasIl.De(tipo))
            {
                var ns = RegistroDeModulos.NamespaceDe(referido);
                if (!ns.StartsWith("CaeManager.", StringComparison.Ordinal))
                {
                    if (referido.Namespace is not null)
                        resultado.Externa(origen, ns);
                    continue;
                }

                var raizDestino = RegistroDeModulos.RaizDe(referido);
                if (!cacheDeDestino.TryGetValue(raizDestino, out var destino))
                {
                    destino = (NombreDeLista(raizDestino), registro.Clasificar(raizDestino)?.Modulo);
                    cacheDeDestino[raizDestino] = destino;
                }

                if (destino.Modulo is not { } moduloDestino)
                {
                    resultado._sinModulo.Add(destino.Nombre);
                    continue;
                }

                if (moduloDestino == moduloOrigen || destino.Nombre == origen)
                    continue;

                var par = (moduloOrigen, moduloDestino);
                if (!resultado._pares.TryGetValue(par, out var porOrigen))
                    resultado._pares[par] = porOrigen = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                if (!porOrigen.TryGetValue(origen, out var destinos))
                    porOrigen[origen] = destinos = new SortedSet<string>(StringComparer.Ordinal);
                destinos.Add(destino.Nombre);
            }
        }

        return resultado;
    }

    /// <summary>Suma las aristas de otra medición (el control positivo añade tipos sintéticos a la medida real).</summary>
    public AristasEntreModulos Con(AristasEntreModulos otra)
    {
        var suma = new AristasEntreModulos();
        foreach (var fuente in new[] { this, otra })
        {
            foreach (var (par, porOrigen) in fuente._pares)
            foreach (var (origen, destinos) in porOrigen)
            {
                if (!suma._pares.TryGetValue(par, out var p))
                    suma._pares[par] = p = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
                if (!p.TryGetValue(origen, out var d))
                    p[origen] = d = new SortedSet<string>(StringComparer.Ordinal);
                d.UnionWith(destinos);
            }

            foreach (var (origen, modulo) in fuente._moduloDeOrigen)
                suma._moduloDeOrigen[origen] = modulo;
            foreach (var (origen, ns) in fuente._externas)
                suma._externas[origen] = [.. ns];
            suma._sinModulo.UnionWith(fuente._sinModulo);
            suma._origenesSinModulo.UnionWith(fuente._origenesSinModulo);
        }

        foreach (var modulo in suma._moduloDeOrigen.Values)
            suma.OrigenesPorModulo[modulo] = suma.OrigenesPorModulo.GetValueOrDefault(modulo) + 1;
        return suma;
    }

    private void Externa(string origen, string ns)
    {
        if (!_externas.TryGetValue(origen, out var set))
            _externas[origen] = set = new SortedSet<string>(StringComparer.Ordinal);
        set.Add(ns);
    }

    /// <summary>
    /// El nombre de un tipo raíz en las listas: relativo a <c>CaeManager.</c>, sin aridad genérica; el auxiliar de Razor
    /// (<c>__Blazor.&lt;componente&gt;.TypeInference</c>) se cuenta como el componente.
    /// </summary>
    public static string NombreDeLista(Type raiz) =>
        (raiz.Namespace ?? string.Empty).StartsWith("__Blazor.", StringComparison.Ordinal)
            ? Sin(RegistroDeModulos.NamespaceDe(raiz))
            : RegistroDeModulos.NombreRelativo(raiz);

    private static string Sin(string ns) => ns.StartsWith("CaeManager.", StringComparison.Ordinal) ? ns["CaeManager.".Length..] : ns;

    // ---- Informe (modo informe del paso M0b) ----

    /// <summary>
    /// Las cifras por par de módulos: tipos de origen con alguna arista y aristas «tipo -> tipo» (una por pareja de tipos
    /// raíz, no por referencia), más los tipos medidos por módulo. Es lo que se pega en la PR para corregir § 2.4.
    /// </summary>
    public string Informe(string configuracion)
    {
        var sb = new StringBuilder();
        sb.Append("Aristas entre módulos medidas en el IL (ADR-013 § 4.3, M0b). Configuración de compilación: ").Append(configuracion).Append('\n');
        sb.Append("Tipos raíz medidos como origen: ")
            .Append(string.Join(", ", OrigenesPorModulo.Select(p => $"{p.Key} {p.Value}"))).Append('\n');
        sb.Append("Una arista es una pareja «tipo raíz de origen -> tipo raíz de destino»; un tipo raíz es un fichero de C# salvo los auxiliares de Razor y los tipos secundarios de un fichero.\n\n");
        sb.Append("Origen -> Destino : tipos de origen / aristas\n");
        foreach (var (origen, destino) in Pares)
        {
            var (o, a) = Cifras(origen, destino);
            sb.Append($"  {origen} -> {destino} : {o} / {a}\n");
        }

        sb.Append("\nDestinos sin módulo: ").Append(_sinModulo.Count).Append(", orígenes sin módulo: ").Append(_origenesSinModulo.Count).Append('\n');
        return sb.ToString();
    }

    /// <summary>Todos los tipos de los ensamblados de <c>src</c>, anidados incluidos, sin el código ajeno a TALVEG.</summary>
    public static IReadOnlyList<Type> TiposDeSrc(RegistroDeModulos registro) =>
        RegistroDeModulos.EnsambladosDeSrc()
            .SelectMany(ReflexionArquitecturaHelper.TiposDe)
            .Where(t => !registro.EsAjeno(t))
            .ToList();
}
