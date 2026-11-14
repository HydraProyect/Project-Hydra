using System.Reflection;
using System.Runtime.CompilerServices;

namespace CaeManager.Architecture.Tests.Soporte;

/// <summary>Los módulos del monolito modular (ADR-013 § 3 y § 4.1; Facturación por D-M1).</summary>
public enum Modulo
{
    Nucleo,
    Outbound,
    Inbound,
    Facturacion,

    /// <summary>Punto de composición (ADR-013 § 4.1, regla 4): puede referenciar todos los módulos.</summary>
    Composicion,
}

/// <summary>Una línea del registro: qué regla es, a qué clave se aplica y qué módulo da.</summary>
internal sealed record EntradaDeRegistro(string Clase, string Clave, Modulo? Modulo, bool Provisional, string Motivo, int Linea)
{
    /// <summary>
    /// Área que aún no tiene tipos y se admite vacía: el sitio donde nace lo nuevo de un módulo (ADR-013 § 4.7, tabla de
    /// ubicación). Cualquier otra entrada sin tipos es obsoleta.
    /// </summary>
    public bool Reservada { get; init; }

    public override string ToString() => $"línea {Linea}: {Clase} {Clave}" + (Modulo is { } m ? $" = {m}" : string.Empty);
}

/// <summary>El módulo de un tipo y la entrada del registro que lo decide (<c>null</c> si lo decide una derivación).</summary>
internal readonly record struct Clasificacion(Modulo Modulo, EntradaDeRegistro? Entrada, string Regla);

/// <summary>
/// Registro cerrado de módulos de ADR-013 (paso M0a, § 4.3 test 1 y § 4.7 mecanismo 1). Asigna cada tipo de <c>src</c> a
/// un <see cref="Modulo"/> sin mover ningún fichero. El dato vive en <c>Congelados/Modulos-registro.txt</c>; aquí solo
/// está la mecánica.
///
/// <para>
/// <b>Cuatro clases de línea</b>, por orden de precedencia al clasificar:
/// </para>
/// <list type="number">
///   <item><c>excepcion &lt;Capa.Area.….Tipo o subnamespace&gt; = Modulo</c>: un tipo concreto o un subnamespace que no
///   sigue al módulo de su carpeta (por ejemplo <c>Domain.Documentos.EstadoAcreditacion</c>, Outbound dentro de un área
///   del Núcleo). Si varias casan, gana la clave más larga.</item>
///   <item><c>derivar &lt;Capa.Area.Subarea&gt;</c>: el módulo de cada tipo de ese namespace se deriva de la entidad que
///   configura (<c>IEntityTypeConfiguration&lt;T&gt;</c>) o del puerto que implementa (la interfaz más derivada de
///   TALVEG). Es la regla de <c>Persistence</c> (B-1): el namespace no dice el módulo. Si la derivación no da un único
///   módulo, el tipo queda sin módulo y hace falta una <c>excepcion</c>.</item>
///   <item><c>area &lt;Capa.Area&gt; = Modulo</c>: el prefijo de primer nivel de cada capa (<c>Domain.Empresas</c>,
///   <c>Web.Features.Clientes</c>, <c>Web.Components</c>). Vale porque namespace = carpeta (test aparte).</item>
///   <item><c>ajeno &lt;namespace&gt;</c>: código que genera la cadena de herramientas en un namespace que no es de
///   TALVEG (expresiones regulares generadas, atributos embebidos). No se clasifica; cualquier otro tipo fuera de
///   <c>CaeManager.*</c> queda sin módulo.</item>
/// </list>
///
/// <para>
/// Las claves no llevan el prefijo <c>CaeManager.</c>. <c>provisional</c> tras el módulo marca lo que ADR-013 § 3 deja
/// como propuesta o lo que no tiene fila en § 3; exige un comentario <c>#</c> con el motivo. <c>reservada</c> admite un
/// área todavía vacía (los <c>*.Inbound</c> donde nace lo nuevo de Inbound).
/// </para>
/// </summary>
internal sealed class RegistroDeModulos
{
    public const string NombreDeLista = "Modulos-registro";
    private const string Raiz = "CaeManager.";
    private const string PrefijoBlazor = "__Blazor.";

    private readonly Dictionary<string, EntradaDeRegistro> _areas = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntradaDeRegistro> _excepciones = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EntradaDeRegistro> _derivaciones = new(StringComparer.Ordinal);
    private readonly List<EntradaDeRegistro> _ajenos = [];

    public IReadOnlyCollection<EntradaDeRegistro> Entradas =>
        _areas.Values.Concat(_excepciones.Values).Concat(_derivaciones.Values).Concat(_ajenos).OrderBy(e => e.Linea).ToList();

    public static RegistroDeModulos Versionado() => Leer(File.ReadAllText(ListaCongelada.RutaDeLista(NombreDeLista)));

    /// <summary>Los cinco ensamblados de <c>src</c>.</summary>
    public static IReadOnlyList<Assembly> EnsambladosDeSrc() =>
    [
        ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Domain"),
        ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application"),
        ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Infrastructure"),
        ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Migrations.PostgreSQL"),
        ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Web"),
    ];

    /// <summary>
    /// Los tipos que se clasifican: el tipo raíz de cada tipo de los ensamblados (los anidados, las clausuras y las
    /// máquinas de estado siguen a su contenedor), sin los que el compilador genera sin nombre de TALVEG.
    /// </summary>
    public static IReadOnlyList<Type> TiposAClasificar(IEnumerable<Assembly> ensamblados) =>
        TiposAClasificar(ensamblados.SelectMany(ReflexionArquitecturaHelper.TiposDe));

    public static IReadOnlyList<Type> TiposAClasificar(IEnumerable<Type> tipos) =>
        tipos.Select(RaizDe)
            .Where(t => !EsGeneradoSinNombre(t))
            .Distinct()
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToList();

    public static RegistroDeModulos Leer(string texto)
    {
        var registro = new RegistroDeModulos();
        var numero = 0;
        foreach (var cruda in texto.Split('\n'))
        {
            numero++;
            var almohadilla = cruda.IndexOf('#');
            var motivo = almohadilla >= 0 ? cruda[(almohadilla + 1)..].Trim() : string.Empty;
            var linea = (almohadilla >= 0 ? cruda[..almohadilla] : cruda).Trim();
            if (linea.Length == 0)
                continue;

            var t = linea.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var entrada = t[0] switch
            {
                "area" or "excepcion" => ConModulo(t, numero, motivo, linea),
                "derivar" or "ajeno" when t.Length == 2 => new EntradaDeRegistro(t[0], t[1], null, false, motivo, numero),
                _ => throw MalFormada(numero, linea),
            };

            var destino = entrada.Clase switch
            {
                "area" => registro._areas,
                "excepcion" => registro._excepciones,
                "derivar" => registro._derivaciones,
                _ => null,
            };
            if (destino is null)
                registro._ajenos.Add(entrada);
            else if (!destino.TryAdd(entrada.Clave, entrada))
                throw new FormatException($"Línea {numero} del registro de módulos repetida: '{linea}'.");
        }

        return registro;
    }

    private static EntradaDeRegistro ConModulo(string[] t, int numero, string motivo, string linea)
    {
        var marcas = t.Skip(4).ToList();
        if (t.Length < 4 || t[2] != "=" || !Enum.TryParse<Modulo>(t[3], ignoreCase: false, out var modulo)
            || marcas.Any(m => m is not ("provisional" or "reservada")) || marcas.Distinct().Count() != marcas.Count)
            throw MalFormada(numero, linea);
        if (t[1].StartsWith(Raiz, StringComparison.Ordinal))
            throw new FormatException($"Línea {numero} del registro de módulos: la clave va sin el prefijo '{Raiz}': '{linea}'.");

        var provisional = marcas.Contains("provisional");
        var reservada = marcas.Contains("reservada");
        if (reservada && t[0] != "area")
            throw new FormatException($"Línea {numero} del registro de módulos: solo un área puede estar reservada: '{linea}'.");
        if (provisional && motivo.Length == 0)
            throw new FormatException(
                $"Línea {numero} del registro de módulos: una entrada provisional lleva su motivo en un comentario '#': '{linea}'.");
        if (t[0] == "area" && t[1].Split('.').Length > (t[1].StartsWith("Web.Features.", StringComparison.Ordinal) ? 3 : 2))
            throw new FormatException(
                $"Línea {numero} del registro de módulos: un área es el prefijo de primer nivel; lo más hondo es una excepcion: '{linea}'.");

        return new EntradaDeRegistro(t[0], t[1], modulo, provisional, motivo, numero) { Reservada = reservada };
    }

    private static FormatException MalFormada(int numero, string linea) => new(
        $"Línea {numero} del registro de módulos mal formada: '{linea}'. Formatos: 'area <Capa.Area> = <Modulo> [provisional]', " +
        "'excepcion <tipo o subnamespace> = <Modulo> [provisional]', 'derivar <namespace>', 'ajeno <namespace>'; " +
        $"módulos: {string.Join(", ", Enum.GetNames<Modulo>())}.");

    /// <summary>¿Lo genera la cadena de herramientas fuera de TALVEG? (entonces no lleva módulo).</summary>
    public bool EsAjeno(Type tipo)
    {
        var ns = NamespaceDe(tipo);
        return !ns.StartsWith(Raiz, StringComparison.Ordinal)
               && _ajenos.Any(a => ns == a.Clave || ns.StartsWith(a.Clave + ".", StringComparison.Ordinal));
    }

    /// <summary>El módulo de un tipo raíz, o <c>null</c> si el registro no lo cubre (lo que pone el test en rojo).</summary>
    public Clasificacion? Clasificar(Type tipo) => Clasificar(RaizDe(tipo), usarExcepciones: true, derivar: true);

    /// <summary>La clasificación que tendría el tipo sin su excepción: sirve para detectar excepciones redundantes.</summary>
    public Clasificacion? ClasificarSinExcepciones(Type tipo) => Clasificar(RaizDe(tipo), usarExcepciones: false, derivar: true);

    private Clasificacion? Clasificar(Type tipo, bool usarExcepciones, bool derivar)
    {
        var nombre = NombreRelativo(tipo);
        var ns = NamespaceRelativo(tipo);

        if (usarExcepciones)
        {
            var excepcion = _excepciones.Values
                .Where(e => nombre == e.Clave || ns == e.Clave || ns.StartsWith(e.Clave + ".", StringComparison.Ordinal))
                .MaxBy(e => e.Clave.Length);
            if (excepcion is not null)
                return new Clasificacion(excepcion.Modulo!.Value, excepcion, excepcion.ToString());
        }

        if (derivar)
        {
            var derivacion = _derivaciones.Values
                .Where(d => ns == d.Clave || ns.StartsWith(d.Clave + ".", StringComparison.Ordinal))
                .MaxBy(d => d.Clave.Length);
            if (derivacion is not null)
                return Derivar(tipo, derivacion);
        }

        return _areas.TryGetValue(AreaDe(ns), out var area)
            ? new Clasificacion(area.Modulo!.Value, area, area.ToString())
            : null;
    }

    /// <summary>
    /// B-1: una configuración EF sigue a su entidad; un repositorio, al puerto que implementa. Solo vale si da un único
    /// módulo; si no, el tipo queda sin módulo y hace falta una <c>excepcion</c>.
    /// </summary>
    private Clasificacion? Derivar(Type tipo, EntradaDeRegistro derivacion)
    {
        var fuentes = FuentesDeDerivacion(tipo);
        var modulos = fuentes
            .Select(f => (Fuente: f, Clasificacion: Clasificar(RaizDe(f), usarExcepciones: true, derivar: false)))
            .ToList();
        if (modulos.Count == 0 || modulos.Any(m => m.Clasificacion is null))
            return null;

        var distintos = modulos.Select(m => m.Clasificacion!.Value.Modulo).Distinct().ToList();
        if (distintos.Count != 1)
            return null;

        return new Clasificacion(distintos[0], null,
            $"{derivacion}: derivado de {string.Join(", ", modulos.Select(m => NombreRelativo(RaizDe(m.Fuente))))}");
    }

    public static IReadOnlyList<Type> FuentesDeDerivacion(Type tipo)
    {
        var interfaces = tipo.GetInterfaces();
        var entidades = interfaces
            .Where(i => i.IsGenericType
                        && i.GetGenericTypeDefinition().FullName == "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1")
            .Select(i => i.GetGenericArguments()[0])
            .Where(a => !a.IsGenericParameter)
            .Distinct()
            .ToList();
        if (entidades.Count > 0)
            return entidades;

        var propias = interfaces.Where(i => (i.Namespace ?? string.Empty).StartsWith(Raiz, StringComparison.Ordinal)).ToList();
        return propias.Where(i => !propias.Any(o => o != i && o.GetInterfaces().Contains(i)))
            .Select(i => i.IsGenericType ? i.GetGenericTypeDefinition() : i)
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// El prefijo de primer nivel de un namespace relativo: <c>Domain.Documentos</c>, <c>Web.Features.Clientes</c>,
    /// <c>Web.Components</c>; <c>Web</c> para la raíz de Web.
    /// </summary>
    public static string AreaDe(string namespaceRelativo)
    {
        var s = namespaceRelativo.Split('.');
        if (s.Length >= 3 && s[0] == "Web" && s[1] == "Features")
            return string.Join('.', s[..3]);
        return s.Length >= 2 ? $"{s[0]}.{s[1]}" : s[0];
    }

    public static Type RaizDe(Type tipo)
    {
        while (tipo.DeclaringType is { } contenedor)
            tipo = contenedor;
        return tipo;
    }

    /// <summary>Tipos anónimos y detalles de implementación: el compilador los genera sin nombre del programa.</summary>
    private static bool EsGeneradoSinNombre(Type tipo) =>
        tipo.Name.StartsWith('<') || (tipo.Namespace is null && tipo.IsDefined(typeof(CompilerGeneratedAttribute), false));

    /// <summary>El namespace, con el envoltorio <c>__Blazor.</c> de los auxiliares de Razor quitado.</summary>
    public static string NamespaceDe(Type tipo)
    {
        var ns = tipo.Namespace ?? string.Empty;
        return ns.StartsWith(PrefijoBlazor, StringComparison.Ordinal) ? ns[PrefijoBlazor.Length..] : ns;
    }

    private static string NamespaceRelativo(Type tipo) => SinRaiz(NamespaceDe(tipo));

    /// <summary>Nombre completo sin <c>CaeManager.</c> ni aridad genérica: la forma de las claves del registro.</summary>
    public static string NombreRelativo(Type tipo)
    {
        var nombre = tipo.Name;
        var acento = nombre.IndexOf('`');
        if (acento >= 0)
            nombre = nombre[..acento];
        var ns = NamespaceRelativo(tipo);
        return ns.Length == 0 ? nombre : $"{ns}.{nombre}";
    }

    private static string SinRaiz(string ns) => ns.StartsWith(Raiz, StringComparison.Ordinal) ? ns[Raiz.Length..] : ns;
}
