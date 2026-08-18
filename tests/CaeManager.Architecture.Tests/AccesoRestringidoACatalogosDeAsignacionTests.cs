using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Capa 2 de la política de lectura de los catálogos globales de asignación
/// operativa (plano 2 del ADR-011, endurecimiento E1 del plan de migración).
///
/// <c>AsignacionesOperacion</c> y <c>AsignacionesCartera</c> están
/// <b>deliberadamente fuera del filtro global de tenant</b>: una asignación
/// cruza fronteras por naturaleza (el operador puede ser otro tenant), así que
/// no puede llevar <c>TenantId</c>. Pero estar fuera del filtro no las hace
/// legibles sin restricción: cada fila revela quién opera para quién y sobre
/// qué ámbito, que es metadata empresarial.
///
/// Toda consulta debe acotarse a la posición del llamante — propietario por
/// <c>PropietarioTenantId</c>, operador por <c>OperadorTenantId</c> = su tenant
/// de ORIGEN — y eso no se puede comprobar leyendo texto. Lo que sí se puede es
/// mantener corta y explícita la lista de sitios donde se toca: si el acceso
/// vive en seis archivos revisados, revisar la política es leer seis archivos.
/// Un handler nuevo que consulte estos DbSets sin filtro de posición hace
/// fallar este test y obliga a justificarlo.
///
/// Mismo mecanismo de ratchet por texto que
/// <see cref="ProhibicionSqlCrudoYFiltrosIgnoradosTests"/>, y por el mismo
/// motivo: son accesos a propiedad, no dependencias de tipo, así que la
/// reflexión sobre el ensamblado no los ve sin desensamblar cada método.
/// </summary>
public class AccesoRestringidoACatalogosDeAsignacionTests
{
    private static readonly Regex PatronAcceso = new(
        @"\bAsignacionesOperacion\b|\bAsignacionesCartera\b",
        RegexOptions.Compiled);

    /// <summary>
    /// Los puntos autorizados, con el papel que cumple cada uno. Añadir uno
    /// nuevo es una decisión de diseño que se revisa en el mismo commit, no un
    /// descuido que se cuela.
    /// </summary>
    private static readonly HashSet<string> ArchivosAutorizados =
    [
        // El contrato mismo y su implementación: el único escritor.
        "src/CaeManager.Application/Operaciones/IOperacionesQueryContext.cs",
        "src/CaeManager.Application/Operaciones/IAsignacionesOperativasWriter.cs",
        "src/CaeManager.Infrastructure/Operaciones/AsignacionesOperativasWriter.cs",

        // Job de expiración de vigencias: catálogo global por naturaleza, sin
        // posición de llamante (no hay sesión en un job de fondo).
        "src/CaeManager.Infrastructure/Operaciones/ExpiracionAsignacionesHostedService.cs",

        // Backfill de F1: recorre todos los tenants una vez, al arrancar.
        "src/CaeManager.Infrastructure/Persistence/Seed/AsignacionesOperativasBackfillSeeder.cs",

        // Registro de los DbSet y de las interfaces de consulta.
        "src/CaeManager.Infrastructure/Persistence/CaeManagerDbContext.cs",

        // Único punto de autorización fina: filtra por propietario Y por
        // operador de origen.
        "src/CaeManager.Infrastructure/Autorizacion/AlcanceDatosService.cs",

        // Configuraciones EF de las dos tablas.
        "src/CaeManager.Infrastructure/Persistence/Configurations/AsignacionOperacionConfiguration.cs",
        "src/CaeManager.Infrastructure/Persistence/Configurations/AsignacionCarteraConfiguration.cs",

        // Selección de workspace: filtra por PropietarioTenantId = el tenant
        // que se quiere abrir Y por OperadorTenantId = tenant de origen del
        // usuario, exige cartera vigente y excluye la raíz.
        "src/CaeManager.Web/Features/Tenants/ClienteActivoEndpoints.cs",

        // Rol efectivo dentro del workspace: acotado a la operación que el
        // token identifica y al propietario que ese mismo token declara.
        "src/CaeManager.Web/Services/CurrentUserService.cs",

        // Revalidación por petición: comprueba la coherencia token↔operación y
        // exige cartera vigente del usuario.
        "src/CaeManager.Web/Services/RevalidacionClienteActivoMiddleware.cs",
    ];

    [Fact]
    public void Solo_los_puntos_autorizados_tocan_los_catalogos_de_asignacion()
    {
        var raiz = RaizDelRepositorio();
        var carpetas = new[]
        {
            "src/CaeManager.Application", "src/CaeManager.Infrastructure", "src/CaeManager.Web"
        };

        var infractores = new List<string>();

        foreach (var carpeta in carpetas)
        {
            var directorio = Path.Combine(raiz, carpeta.Replace('/', Path.DirectorySeparatorChar));

            foreach (var archivo in Directory.EnumerateFiles(directorio, "*.cs", SearchOption.AllDirectories))
            {
                var rutaRelativa = Path.GetRelativePath(raiz, archivo).Replace(Path.DirectorySeparatorChar, '/');
                if (ArchivosAutorizados.Contains(rutaRelativa)) continue;

                if (File.ReadLines(archivo).Any(linea => PatronAcceso.IsMatch(linea)))
                    infractores.Add(rutaRelativa);
            }
        }

        string.Join("\n", infractores.OrderBy(x => x)).Should().BeEmpty(
            "los catálogos de asignación están fuera del filtro global de tenant, así que cada consulta debe " +
            "acotarse a mano a la posición del llamante (propietario por PropietarioTenantId, operador por " +
            "OperadorTenantId = tenant de ORIGEN, ver ADR-011 § 2.7 y el endurecimiento E1 del plan) — si el " +
            "acceso listado está justificado, añádelo a ArchivosAutorizados en este mismo commit explicando qué " +
            "filtro de posición aplica");
    }

    /// <summary>
    /// Guarda del propio test: si el escaneo dejara de encontrar los accesos ya
    /// conocidos (carpeta movida, tipos renombrados, regex roto), lo notaría en
    /// vez de pasar en falso vacío.
    /// </summary>
    [Fact]
    public void Hay_accesos_autorizados_que_inspeccionar()
    {
        var raiz = RaizDelRepositorio();

        var encontrados = ArchivosAutorizados.Count(ruta =>
        {
            var archivo = Path.Combine(raiz, ruta.Replace('/', Path.DirectorySeparatorChar));
            return File.Exists(archivo) && File.ReadLines(archivo).Any(l => PatronAcceso.IsMatch(l));
        });

        encontrados.Should().BeGreaterThan(5,
            "si la lista de autorizados dejara de corresponderse con el código, este test estaría vigilando un " +
            "patrón que ya no existe y no detectaría nada");
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
                " — este test necesita el árbol fuente del repositorio, no solo los ensamblados compilados.");

        return actual.FullName;
    }
}
