using System.Text.RegularExpressions;
using FluentAssertions;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Todo endpoint que sirve un binario está catalogado, y el que lo sirve a partir de un Id de la ruta
/// tiene su prueba de aislamiento entre Tenants bajo <c>cae_app_runtime</c>
/// (<c>DescargasConIdDeOtroTenantBajoRuntimeTests</c>, o la del logo para <c>/tenants/{id}/logo</c>):
/// pedir el Id de un recurso de otro Tenant da 404 o 403, sin bytes y sin <c>Content-Disposition</c>.
///
/// <para>
/// Es un trinquete por fichero: cuenta las llamadas que entregan un binario (<c>Results.File</c>,
/// <c>Results.Stream</c>, <c>Results.Bytes</c>, <c>PhysicalFile</c>, <c>LocalFile</c> y
/// <c>SendFileAsync</c>) en todo <c>src</c>. Un fichero nuevo que sirva binarios, o una llamada más en uno
/// conocido, hace fallar el test hasta que alguien lo clasifique aquí: «por Id», con su ruta en la prueba
/// de integración, o «sin Id», con el motivo. Un recuento que baja también falla, para que el catálogo no
/// se quede con entradas que ya no existen.
/// </para>
/// </summary>
public class EndpointsQueSirvenBinariosEstanCatalogadosTests
{
    private static readonly Regex LlamadaBinaria = new(
        @"\b(?:Typed)?Results\.(?:File|Stream|Bytes|PhysicalFile|LocalFile)\s*\(|\bSendFileAsync\s*\(",
        RegexOptions.Compiled);

    /// <summary>Literal de ruta con un parámetro: <c>"/algo/{id:guid}/archivo"</c>.</summary>
    private static readonly Regex RutaConParametro = new(@"""(?<ruta>/[^""\s]*\{[^""\s]*)""", RegexOptions.Compiled);

    private const string PruebaDeDescargas = "tests/CaeManager.IntegrationTests/Tenants/DescargasConIdDeOtroTenantBajoRuntimeTests.cs";
    private const string PruebaDelLogo = "tests/CaeManager.IntegrationTests/Tenants/LogoTenantEndpointBajoRlsTests.cs";

    /// <summary>Ruta → prueba de integración que la recorre con el Id de otro Tenant.</summary>
    private static readonly Dictionary<string, string> RutasPorId = new()
    {
        ["/documentos/{id:guid}/archivo"] = PruebaDeDescargas,
        ["/subcontratas/verificaciones/{id:guid}/evidencia"] = PruebaDeDescargas,
        ["/auditoria/{id:guid}/archivo-anterior"] = PruebaDeDescargas,
        ["/visitas/{id:guid}/paquete-documental.zip"] = PruebaDeDescargas,
        ["/comunicaciones/adjuntos/{id:guid}/archivo"] = PruebaDeDescargas,
        ["/requisitos-documentales/{id:guid}/archivo"] = PruebaDeDescargas,
        ["/empresas/{id:guid}/sello/archivo"] = PruebaDeDescargas,
        ["/tenants/{id:guid}/logo"] = PruebaDelLogo,
    };

    /// <summary>
    /// Fichero → llamadas binarias y por qué. «Por Id» quiere decir que alguna de sus rutas recibe el Id
    /// de un recurso; el resto de sus llamadas son exportaciones o plantillas generadas en memoria, que
    /// leen bajo el Tenant de la petición y no reciben el Id de ningún recurso.
    /// </summary>
    private static readonly Dictionary<string, (int Llamadas, string Motivo)> Catalogo = new()
    {
        ["src/CaeManager.Web/Features/Documentos/DocumentosEndpoints.cs"] = (3, "por Id: /documentos/{id}/archivo; además plantilla.xlsx y exportar.xlsx, generadas"),
        ["src/CaeManager.Web/Features/Subcontratas/SubcontratasEndpoints.cs"] = (2, "por Id: evidencia de la verificación; además exportar.xlsx"),
        ["src/CaeManager.Web/Features/Auditoria/AuditoriaEndpoints.cs"] = (2, "por Id: archivo-anterior; además exportar.xlsx"),
        ["src/CaeManager.Web/Features/Visitas/VisitasEndpoints.cs"] = (2, "por Id: paquete-documental.zip; además exportar.xlsx"),
        ["src/CaeManager.Web/Features/Comunicaciones/ComunicacionesEndpoints.cs"] = (1, "por Id: adjunto de correo"),
        ["src/CaeManager.Web/Features/Centros/RequisitosDocumentalesEndpoints.cs"] = (1, "por Id: plantilla del requisito del Centro"),
        ["src/CaeManager.Web/Features/Documentos/FirmasGuardadasEndpoints.cs"] = (2, "por Id: sello de la Empresa; /mi-firma/archivo es la del usuario actual, sin Id"),
        ["src/CaeManager.Web/Features/Tenants/LogoTenantEndpoints.cs"] = (1, "por Id: logo del Tenant"),
        ["src/CaeManager.Web/Features/Asignaciones/AsignacionesEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Centros/CentrosEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Clientes/ClientesEndpoints.cs"] = (3, "sin Id: exportación y dos plantillas"),
        ["src/CaeManager.Web/Features/CuadroDireccion/CuadroDireccionEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Empresas/EmpresasEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Facturacion/FacturacionEndpoints.cs"] = (2, "sin Id: exportaciones"),
        ["src/CaeManager.Web/Features/Gestiones/GestionesEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Incidencias/IncidenciasEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Proyectos/ProyectosEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Trabajadores/TrabajadoresEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Features/Vehiculos/VehiculosEndpoints.cs"] = (1, "sin Id: exportación"),
        ["src/CaeManager.Web/Reportes/ReportesEndpoints.cs"] = (4, "sin Id: informes generados"),
    };

    [Fact]
    public void Cada_fichero_que_sirve_binarios_esta_en_el_catalogo_con_su_recuento()
    {
        var medido = FicherosQueSirvenBinarios();

        medido.Should().NotBeEmpty("control positivo: el patrón encuentra las descargas que ya existen");
        medido.Should().ContainKey("src/CaeManager.Web/Features/Documentos/DocumentosEndpoints.cs", "control positivo");

        var esperado = Catalogo.ToDictionary(p => p.Key, p => p.Value.Llamadas);
        medido.Should().Equal(esperado,
            "un endpoint nuevo que sirva un binario se clasifica aquí; si recibe el Id de un recurso, su ruta va "
            + "en RutasPorId y se recorre en DescargasConIdDeOtroTenantBajoRuntimeTests con el Id de otro Tenant");
    }

    [Fact]
    public void Toda_ruta_con_parametro_de_un_fichero_que_sirve_binarios_tiene_prueba_entre_Tenants()
    {
        var raiz = RaizDelRepositorio();
        var rutas = FicherosQueSirvenBinarios().Keys
            .SelectMany(fichero => RutaConParametro.Matches(File.ReadAllText(Path.Combine(raiz, fichero)))
                .Select(m => m.Groups["ruta"].Value))
            .ToHashSet();

        rutas.Should().Contain("/documentos/{id:guid}/archivo", "control positivo: el patrón de ruta lee los literales");
        rutas.Should().BeSubsetOf(RutasPorId.Keys,
            "una ruta con parámetro en un fichero que sirve binarios recibe el Id de un recurso: necesita su "
            + "prueba con el Id de otro Tenant");
    }

    [Fact]
    public void Cada_ruta_por_Id_aparece_en_su_prueba_de_integracion()
    {
        var raiz = RaizDelRepositorio();
        foreach (var (ruta, prueba) in RutasPorId)
        {
            var fuente = File.ReadAllText(Path.Combine(raiz, prueba));
            fuente.Should().Contain($"\"{ruta}\"", $"{prueba} recorre {ruta} con el Id de un recurso de otro Tenant");
        }
    }

    private static Dictionary<string, int> FicherosQueSirvenBinarios()
    {
        var raiz = RaizDelRepositorio();
        var src = Path.Combine(raiz, "src");
        return Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !EnCarpetaGenerada(f))
            .Select(f => (Fichero: Path.GetRelativePath(raiz, f).Replace('\\', '/'), Llamadas: LlamadaBinaria.Matches(File.ReadAllText(f)).Count))
            .Where(x => x.Llamadas > 0)
            .ToDictionary(x => x.Fichero, x => x.Llamadas);
    }

    private static bool EnCarpetaGenerada(string fichero)
    {
        var partes = fichero.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return partes.Contains("bin") || partes.Contains("obj");
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && !File.Exists(Path.Combine(directorio.FullName, "CaeManager.slnx")))
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró CaeManager.slnx.");
    }
}
