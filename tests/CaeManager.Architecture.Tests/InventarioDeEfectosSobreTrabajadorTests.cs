using System.Xml.Linq;
using CaeManager.Application.Centros.Queries.ObtenerDocumentacionBloqueantePendiente;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Centros;
using FluentAssertions;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Inventario de rutas con efecto sobre un Trabajador (issue #2005, art. 22 RGPD) y prueba de lo que HOY hacen. No cambia
/// comportamiento: fija lo que existe. Decisión D-4 (aceptada por Chris el 2026-11-15): el bloqueo de acceso a un Centro por un
/// Documento caducado o ausente sigue siendo automático, con el motivo visible y la vía de revisión por el Gestor CAE.
/// Cualquier consumidor nuevo del bloqueo, o cualquier ruta sin persona nueva, exige tocar estas listas en el mismo commit.
/// </summary>
public class InventarioDeEfectosSobreTrabajadorTests
{
    private static readonly string[] ReferenciasAlBloqueo =
    [
        "IEvaluacionDeAccesoPorCentroService",
        "IEvaluacionDeAccesoDelTenant",
        "CalculoBloqueoDeAccesoDeTrabajadores.",
        "ReglaBloqueoDeAcceso.Bloquea",
        "ReglaBloqueoDeAcceso.Evaluar",
        "ReglaBloqueoDeAcceso.AplicarPendiente",
        "EvaluarCentrosAsync",
    ];

    /// <summary>
    /// Ruta 1 y 2 del inventario (bloqueo calculado por regla y por estado de acreditación en plataforma): todos los ficheros de
    /// código que calculan o consumen el bloqueo. Son lecturas, proyecciones o el recálculo del estado materializado; ninguno es un comando.
    /// </summary>
    private static readonly string[] ConsumidoresDelCalculo =
    [
        "src/CaeManager.Application/Centros/CalculoBloqueoDeAccesoDeTrabajadores.cs",
        "src/CaeManager.Application/Centros/EvaluacionDeAccesoPorCentroService.cs",
        "src/CaeManager.Application/Centros/PendientesEnPlataformaDeCentros.cs",
        "src/CaeManager.Application/Centros/RequisitosObligatoriosIncumplidos.cs",
        "src/CaeManager.Application/Centros/Queries/ObtenerDocumentacionBloqueantePendiente/ObtenerDocumentacionBloqueantePendienteQuery.cs",
        "src/CaeManager.Application/Dashboard/Queries/ObtenerKpisDashboardQuery.cs",
        "src/CaeManager.Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs",
        "src/CaeManager.Application/EstadoMaterializado/RecalculoEstadoMaterializadoService.cs",
    ];

    /// <summary>
    /// Quién enseña el bloqueo a una persona: Bandeja y Mi trabajo del Gestor CAE (vía de revisión: ahí se ve y se resuelve la
    /// causa), Alertas, Reclamación, Calendario y las pantallas de Centro y de Cliente empresarial.
    /// </summary>
    private static readonly string[] SuperficiesQueMuestranElBloqueo =
    [
        "src/CaeManager.Application/Bandeja/Queries/ObtenerBandejaGestor/ObtenerBandejaGestorQuery.cs",
        "src/CaeManager.Application/Bandeja/Queries/ObtenerMiTrabajoAgregado/ObtenerMiTrabajoAgregadoQuery.cs",
        "src/CaeManager.Application/Calendario/Queries/ObtenerSemanaPorEmpresaQuery.cs",
        "src/CaeManager.Application/Reclamaciones/Queries/ObtenerReclamacionAEmpresaEmpleadora/ObtenerReclamacionAEmpresaEmpleadoraQuery.cs",
        "src/CaeManager.Web/Features/Centros/Components/AcordeonAsignacionesCentro.razor.cs",
        "src/CaeManager.Web/Features/Centros/Components/CentroWorkspacePanel.razor",
        "src/CaeManager.Web/Features/Centros/Components/MotivoBloqueoTrabajador.razor",
        "src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor.cs",
        "src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor.cs",
    ];

    /// <summary>
    /// Servicios en segundo plano de Infrastructure que mencionan al Trabajador: actúan sin persona (actor Sistema), así que cada
    /// uno está nombrado aquí con lo que hace. Ninguno decide por la IA un documento ni bloquea; el único que da de baja es el cierre de eventos.
    /// </summary>
    private static readonly string[] ServiciosSinPersonaQueTocanAlTrabajador =
    [
        "src/CaeManager.Infrastructure/Alertas/EnvioAlertasVencimientoHostedService.cs", // resumen por correo: sin efecto de estado
        "src/CaeManager.Infrastructure/DocumentosIa/ProcesadorAnalisisDocumentoHostedService.cs", // crea revisión pendiente de persona
    ];

    /// <summary>Servicios en segundo plano que dan de baja (<c>DarDeBaja(</c>) sin persona: hoy solo el cierre de eventos terminados, sobre sus participantes.</summary>
    private static readonly string[] ServiciosSinPersonaQueDanDeBaja =
    [
        "src/CaeManager.Infrastructure/Proyectos/CierreEventosTerminadosHostedService.cs",
    ];

    [Fact]
    public void El_calculo_del_bloqueo_de_acceso_solo_lo_consumen_los_ficheros_inventariados()
    {
        var raiz = RaizDelRepositorio();

        var encontrados = FicherosDeCodigo(raiz)
            .Where(f => ReferenciasAlBloqueo.Any(r => CodigoSinComentarios(f).Contains(r, StringComparison.Ordinal)))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        encontrados.Should().Equal(
            ConsumidoresDelCalculo.OrderBy(f => f, StringComparer.Ordinal),
            "un consumidor nuevo del bloqueo de acceso es una ruta nueva con efecto sobre un Trabajador (#2005): hay que inventariarla");
    }

    [Fact]
    public void Ningun_comando_consume_el_bloqueo_de_acceso_es_un_calculo_de_lectura_que_no_deniega_ni_cambia_estado_del_Trabajador()
    {
        var raiz = RaizDelRepositorio();

        var comandos = FicherosDeCodigo(raiz)
            .Where(f => Relativa(raiz, f).Contains("/Commands/", StringComparison.Ordinal))
            .Where(f => ReferenciasAlBloqueo.Any(r => CodigoSinComentarios(f).Contains(r, StringComparison.Ordinal)))
            .Select(f => Relativa(raiz, f))
            .ToList();

        comandos.Should().BeEmpty(
            "hoy ningún comando de asignación, visita o acceso consulta el bloqueo: mostrarlo es informar al Gestor CAE, no denegar");
    }

    [Fact]
    public void La_via_de_revision_del_Gestor_CAE_existe_Bandeja_y_Mi_trabajo_enseñan_el_bloqueo_y_su_causa()
    {
        var raiz = RaizDelRepositorio();

        var encontrados = FicherosDeCodigo(raiz, incluirRazor: true)
            .Where(f => CodigoSinComentarios(f).Contains("ObtenerDocumentacionBloqueantePendienteQuery", StringComparison.Ordinal)
                && !f.EndsWith("ObtenerDocumentacionBloqueantePendienteQuery.cs", StringComparison.Ordinal)
                && !f.EndsWith("EvaluacionDeAccesoPorCentroService.cs", StringComparison.Ordinal))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        encontrados.Should().Equal(SuperficiesQueMuestranElBloqueo.OrderBy(f => f, StringComparer.Ordinal));
        encontrados.Should().Contain(
        [
            "src/CaeManager.Application/Bandeja/Queries/ObtenerBandejaGestor/ObtenerBandejaGestorQuery.cs",
            "src/CaeManager.Application/Bandeja/Queries/ObtenerMiTrabajoAgregado/ObtenerMiTrabajoAgregadoQuery.cs",
        ]);
    }

    [Theory]
    [InlineData(SituacionDeRequisitoBloqueante.Ausente, 0, "BloqueoSituacionAusente")]
    [InlineData(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma, 0, "BloqueoSituacionSinSubirAPlataforma")]
    [InlineData(SituacionDeRequisitoBloqueante.SinValidarEnPlataforma, 0, "BloqueoSituacionSinValidarEnPlataforma")]
    [InlineData(SituacionDeRequisitoBloqueante.Vencido, 0, "BloqueoSituacionVencido")]
    [InlineData(SituacionDeRequisitoBloqueante.Vencido, 15, "BloqueoSituacionFinTolerancia")]
    public void El_motivo_de_cada_situacion_bloqueante_es_visible_en_las_superficies_del_Centro(
        SituacionDeRequisitoBloqueante situacion, int toleranciaDias, string claveEsperada)
    {
        ReglaBloqueoDeAcceso.Bloquea(situacion).Should().BeTrue();

        var dto = new DocumentacionBloqueantePendienteDto(
            Guid.NewGuid(), "Centro", Guid.NewGuid(), "Ana", Guid.NewGuid(), "PSS",
            Situacion: situacion, ToleranciaDias: toleranciaDias,
            VencimientoEfectivo: situacion == SituacionDeRequisitoBloqueante.Ausente ? null : new DateOnly(2026, 10, 1));

        var linea = TextoBloqueoDeAcceso.LineaDeDocumento(new LocalizadorQueDevuelveLaClave(), dto);

        linea.Should().Contain(claveEsperada, "toda situación que bloquea tiene su motivo propio, no un texto genérico");
        linea.Should().StartWith("PSS: ");
    }

    [Fact]
    public void Toda_situacion_que_bloquea_tiene_un_caso_de_motivo_en_esta_prueba()
    {
        var cubiertas = new[]
        {
            SituacionDeRequisitoBloqueante.Ausente, SituacionDeRequisitoBloqueante.Vencido,
            SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma, SituacionDeRequisitoBloqueante.SinValidarEnPlataforma,
        };

        Enum.GetValues<SituacionDeRequisitoBloqueante>()
            .Where(ReglaBloqueoDeAcceso.Bloquea)
            .Should().BeEquivalentTo(cubiertas, "una situación bloqueante nueva necesita su motivo visible");
    }

    [Theory]
    [InlineData("TextosCentros.resx")]
    [InlineData("TextosCentros.ca-ES.resx")]
    public void Los_textos_del_motivo_del_bloqueo_existen_en_cada_idioma(string fichero)
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "Features", "Centros", "Recursos", fichero);
        var claves = XDocument.Load(ruta).Descendants("data").Select(d => (string?)d.Attribute("name")).ToHashSet();

        claves.Should().Contain(
        [
            "BloqueoSituacionAusente", "BloqueoSituacionSinSubirAPlataforma", "BloqueoSituacionSinValidarEnPlataforma",
            "BloqueoSituacionVencido", "BloqueoSituacionVencidoSinFecha", "BloqueoSituacionFinTolerancia",
        ]);
    }

    [Fact]
    public void Los_servicios_sin_persona_que_tocan_al_Trabajador_son_los_inventariados_y_ninguno_produce_aprobaciones_automaticas()
    {
        var raiz = RaizDelRepositorio();
        var infraestructura = Path.Combine(raiz, "src", "CaeManager.Infrastructure");

        var encontrados = Directory.EnumerateFiles(infraestructura, "*HostedService.cs", SearchOption.AllDirectories)
            .Where(f => CodigoSinComentarios(f).Contains("Trabajador", StringComparison.OrdinalIgnoreCase))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        encontrados.Should().Equal(
            ServiciosSinPersonaQueTocanAlTrabajador.OrderBy(f => f, StringComparer.Ordinal),
            "un servicio nuevo que actúe sin persona sobre un Trabajador es una ruta nueva del art. 22 RGPD (#2005)");

        var queDanDeBaja = Directory.EnumerateFiles(infraestructura, "*HostedService.cs", SearchOption.AllDirectories)
            .Where(f => CodigoSinComentarios(f).Contains(".DarDeBaja(", StringComparison.Ordinal))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        queDanDeBaja.Should().Equal(
            ServiciosSinPersonaQueDanDeBaja,
            "una baja sin persona es una decisión automatizada: cada servicio que la produce está inventariado");
    }

    // ---------- soporte ----------

    private sealed class LocalizadorQueDevuelveLaClave : IStringLocalizer
    {
        public LocalizedString this[string name] => new(name, name);
        public LocalizedString this[string name, params object[] arguments] => new(name, name);
        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }

    private static IEnumerable<string> FicherosDeCodigo(string raiz, bool incluirRazor = false)
    {
        var separador = Path.DirectorySeparatorChar;
        var patrones = incluirRazor ? new[] { "*.cs", "*.razor" } : ["*.cs"];

        return patrones
            .SelectMany(p => Directory.EnumerateFiles(Path.Combine(raiz, "src"), p, SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{separador}obj{separador}") && !f.Contains($"{separador}bin{separador}")
                && !f.Contains("CaeManager.Migrations.PostgreSQL"));
    }

    private static string CodigoSinComentarios(string fichero) =>
        string.Join('\n', File.ReadLines(fichero).Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static string Relativa(string raiz, string fichero) =>
        Path.GetRelativePath(raiz, fichero).Replace('\\', '/');

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        return actual?.FullName ?? throw new InvalidOperationException(
            "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory +
            " — este test necesita el árbol fuente del repositorio.");
    }
}
