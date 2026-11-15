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
        "IRequisitosObligatoriosIncumplidosService",
        "ILectorEstadoMaterializado",
        "ReglaBloqueoDeAcceso.ValidoParaAcceder",
        "PendientesEnPlataformaDeCentros",
        "ObtenerDocumentacionBloqueantePendienteQuery",
    ];

    /// <summary>
    /// Ruta 1 y 2 del inventario (bloqueo calculado por regla y por estado de acreditación en plataforma): los ficheros .cs de
    /// src que nombran en código el cálculo, el servicio de evaluación, sus lectores (estado materializado, requisitos incumplidos,
    /// pendientes en plataforma) o la query del bloqueo (comentarios excluidos). Son lecturas, proyecciones o el recálculo del estado materializado; ninguno es un comando.
    /// </summary>
    private static readonly string[] ConsumidoresDelCalculo =
    [
        "src/CaeManager.Application/Asignaciones/Queries/ObtenerAsignacionesDocumentacionPorCentro/ObtenerAsignacionesDocumentacionPorCentroQuery.cs",
        "src/CaeManager.Application/Bandeja/Queries/ObtenerBandejaGestor/ObtenerBandejaGestorQuery.cs",
        "src/CaeManager.Application/Bandeja/Queries/ObtenerMiTrabajoAgregado/ObtenerMiTrabajoAgregadoQuery.cs",
        "src/CaeManager.Application/Calendario/Queries/ObtenerSemanaPorEmpresaQuery.cs",
        "src/CaeManager.Application/Centros/CalculoBloqueoDeAccesoDeTrabajadores.cs",
        "src/CaeManager.Application/Centros/CalculoEstadoCentroService.cs",
        "src/CaeManager.Application/Centros/EvaluacionDeAccesoPorCentroService.cs",
        "src/CaeManager.Application/Centros/PendientesEnPlataformaDeCentros.cs",
        "src/CaeManager.Application/Centros/Queries/ObtenerDocumentacionBloqueantePendiente/ObtenerDocumentacionBloqueantePendienteQuery.cs",
        "src/CaeManager.Application/Centros/RequisitosObligatoriosIncumplidos.cs",
        "src/CaeManager.Application/Dashboard/Queries/ObtenerKpisDashboardQuery.cs",
        "src/CaeManager.Application/DependencyInjection/ApplicationServiceCollectionExtensions.cs",
        "src/CaeManager.Application/Empresas/Queries/ObtenerEmpresas/ObtenerEmpresasQuery.cs",
        "src/CaeManager.Application/EstadoMaterializado/LectorEstadoMaterializado.cs",
        "src/CaeManager.Application/EstadoMaterializado/RecalculoEstadoMaterializadoService.cs",
        "src/CaeManager.Application/Reclamaciones/Queries/ObtenerReclamacionAEmpresaEmpleadora/ObtenerReclamacionAEmpresaEmpleadoraQuery.cs",
        "src/CaeManager.Application/Subcontratas/Queries/ObtenerSubcontratas/ObtenerSubcontratasQuery.cs",
        "src/CaeManager.Application/Trabajadores/Queries/ObtenerDocumentacionPorCentroDeTrabajador/ObtenerDocumentacionPorCentroDeTrabajadorQuery.cs",
        "src/CaeManager.Application/Trabajadores/Queries/ObtenerTrabajadores/ObtenerTrabajadoresQuery.cs",
        "src/CaeManager.Web/Features/Centros/Components/AcordeonAsignacionesCentro.razor.cs",
        "src/CaeManager.Web/Features/Centros/Components/CentroWorkspacePanel.razor",
        "src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor.cs",
        "src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor.cs",
        "src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor.cs",
    ];
    /// <summary>
    /// Desde dónde se despacha la query que lleva el bloqueo y su causa a una persona: Bandeja y Mi trabajo del Gestor CAE (la vía de
    /// revisión: el test solo prueba que ese envío existe, no lo que la pantalla hace con él), Calendario, Reclamación y las pantallas de
    /// Centro, de Cliente empresarial y la ficha del Trabajador (que dice el bloqueo por Centro).
    /// </summary>
    private static readonly string[] SuperficiesQueMuestranElBloqueo =
    [
        "src/CaeManager.Application/Bandeja/Queries/ObtenerBandejaGestor/ObtenerBandejaGestorQuery.cs",
        "src/CaeManager.Application/Bandeja/Queries/ObtenerMiTrabajoAgregado/ObtenerMiTrabajoAgregadoQuery.cs",
        "src/CaeManager.Application/Calendario/Queries/ObtenerSemanaPorEmpresaQuery.cs",
        "src/CaeManager.Application/Reclamaciones/Queries/ObtenerReclamacionAEmpresaEmpleadora/ObtenerReclamacionAEmpresaEmpleadoraQuery.cs",
        "src/CaeManager.Web/Features/Centros/Components/AcordeonAsignacionesCentro.razor.cs",
        "src/CaeManager.Web/Features/Centros/Components/CentroWorkspacePanel.razor",
        "src/CaeManager.Web/Features/Centros/Pages/CentroDetalle.razor.cs",
        "src/CaeManager.Web/Features/Clientes/Pages/ClienteDetalle.razor.cs",
        "src/CaeManager.Web/Features/Trabajadores/Pages/TrabajadorDetalle.razor.cs",
    ];

    /// <summary>
    /// Servicios en segundo plano de Infrastructure que mencionan al Trabajador: actúan sin persona (actor Sistema), así que cada
    /// uno está nombrado aquí con lo que hace. Ninguno decide por la IA un documento ni bloquea; el único que da de baja es el cierre de eventos.
    /// </summary>
    private static readonly string[] ServiciosSinPersonaQueTocanAlTrabajador =
    [
        "src/CaeManager.Infrastructure/Alertas/EnvioAlertasVencimientoHostedService.cs", // resumen por correo: sin efecto de estado
        "src/CaeManager.Infrastructure/Documentos/ConciliacionAcreditacionesEmpresaHostedService.cs", // crea acreditaciones pendientes de Empresa (actor Sistema, apagado por defecto); un pendiente de tipo bloqueante bloquea a los Trabajadores de la Empresa (decisión pendiente, issue #2005)
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

        var encontrados = FicherosDeCodigo(raiz, incluirRazor: true)
            .Where(f => ReferenciasAlBloqueo.Any(r => CodigoSinComentarios(f).Contains(r, StringComparison.Ordinal)))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        encontrados.Should().Equal(
            ConsumidoresDelCalculo.OrderBy(f => f, StringComparer.Ordinal),
            "un consumidor nuevo del cálculo, de sus lectores o de la query del bloqueo es una ruta nueva (#2005): hay que inventariarla (los miembros de solo vigencia o tolerancia de la regla no se vigilan aquí)");
    }

    [Fact]
    public void Ningun_comando_consume_el_bloqueo_de_acceso_es_un_calculo_de_lectura_que_no_deniega_ni_cambia_estado_del_Trabajador()
    {
        var raiz = RaizDelRepositorio();

        var comandos = FicherosDeCodigo(raiz)
            .Where(f => Relativa(raiz, f).Contains("/Commands/", StringComparison.Ordinal) || f.EndsWith("Command.cs", StringComparison.Ordinal))
            .Where(f => ReferenciasAlBloqueo.Any(r => CodigoSinComentarios(f).Contains(r, StringComparison.Ordinal)))
            .Select(f => Relativa(raiz, f))
            .ToList();

        comandos.Should().BeEmpty(
            "hoy ningún comando de asignación, visita o acceso consulta el bloqueo: mostrarlo es informar al Gestor CAE, no denegar");
    }

    [Fact]
    public void La_query_del_bloqueo_se_despacha_solo_desde_las_superficies_inventariadas_incluidas_Bandeja_y_Mi_trabajo_del_Gestor_CAE()
    {
        var raiz = RaizDelRepositorio();

        var encontrados = FicherosDeCodigo(raiz, incluirRazor: true)
            .Where(f => CodigoSinComentarios(f).Contains("ObtenerDocumentacionBloqueantePendienteQuery", StringComparison.Ordinal)
                && !f.EndsWith("ObtenerDocumentacionBloqueantePendienteQuery.cs", StringComparison.Ordinal))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        encontrados.Should().Equal(SuperficiesQueMuestranElBloqueo.OrderBy(f => f, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(SituacionDeRequisitoBloqueante.Ausente, 0, "BloqueoSituacionAusente")]
    [InlineData(SituacionDeRequisitoBloqueante.PendienteDeSubirAPlataforma, 0, "BloqueoSituacionSinSubirAPlataforma")]
    [InlineData(SituacionDeRequisitoBloqueante.SinValidarEnPlataforma, 0, "BloqueoSituacionSinValidarEnPlataforma")]
    [InlineData(SituacionDeRequisitoBloqueante.Vencido, 0, "BloqueoSituacionVencido")]
    [InlineData(SituacionDeRequisitoBloqueante.Vencido, 15, "BloqueoSituacionFinTolerancia")]
    public void El_texto_del_motivo_de_cada_situacion_bloqueante_es_propio_de_esa_situacion(
        SituacionDeRequisitoBloqueante situacion, int toleranciaDias, string claveEsperada)
    {
        ReglaBloqueoDeAcceso.Bloquea(situacion).Should().BeTrue();

        var dto = new DocumentacionBloqueantePendienteDto(
            Guid.NewGuid(), "Centro", Guid.NewGuid(), "Ana", Guid.NewGuid(), "PSS",
            Situacion: situacion, ToleranciaDias: toleranciaDias,
            VencimientoEfectivo: situacion == SituacionDeRequisitoBloqueante.Ausente ? null : new DateOnly(2026, 10, 1));

        var linea = TextoBloqueoDeAcceso.LineaDeDocumento(new LocalizadorQueDevuelveLaClave(), dto);

        linea.Should().Be($"PSS: {claveEsperada}", "toda situación que bloquea tiene su motivo propio, no un texto genérico");
    }

    [Fact]
    public void El_vencido_sin_fecha_tiene_su_propio_texto()
    {
        var dto = new DocumentacionBloqueantePendienteDto(
            Guid.NewGuid(), "Centro", Guid.NewGuid(), "Ana", Guid.NewGuid(), "PSS", Situacion: SituacionDeRequisitoBloqueante.Vencido);

        TextoBloqueoDeAcceso.LineaDeDocumento(new LocalizadorQueDevuelveLaClave(), dto).Should().Be("PSS: BloqueoSituacionVencidoSinFecha");
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
    public void Los_servicios_sin_persona_que_nombran_al_Trabajador_o_dan_de_baja_son_los_inventariados()
    {
        var raiz = RaizDelRepositorio();
        var infraestructura = Path.Combine(raiz, "src", "CaeManager.Infrastructure");

        var encontrados = Directory.EnumerateFiles(infraestructura, "*HostedService.cs", SearchOption.AllDirectories)
            .Where(f => CodigoSinComentarios(f).Contains("Trabajador", StringComparison.OrdinalIgnoreCase)
                || CodigoSinComentarios(f).Contains("Acreditaci", StringComparison.OrdinalIgnoreCase))
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

    [Fact]
    public void Los_servicios_en_segundo_plano_que_recalculan_el_estado_derivado_del_bloqueo_son_los_inventariados()
    {
        var raiz = RaizDelRepositorio();

        var encontrados = Directory.EnumerateFiles(Path.Combine(raiz, "src", "CaeManager.Infrastructure"), "*HostedService.cs", SearchOption.AllDirectories)
            .Where(f => CodigoSinComentarios(f).Contains("EstadoMaterializado", StringComparison.Ordinal))
            .Select(f => Relativa(raiz, f))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        // Recalculan el rojo persistido de Trabajadores y Empresas (ruta 1 del inventario) sin persona: cola tras cada cambio y cambio de día.
        encontrados.Should().Equal(
        [
            "src/CaeManager.Infrastructure/EstadoMaterializado/CambioDeDiaEstadoMaterializadoHostedService.cs",
            "src/CaeManager.Infrastructure/EstadoMaterializado/ColaEstadoMaterializadoHostedService.cs",
        ]);
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

    private static readonly Dictionary<string, string> CacheDeCodigo = new();

    /// <summary>El código del fichero sin comentarios de línea (<c>//</c>, <c>///</c>), de bloque (<c>/* */</c>) ni de Razor (<c>@* *@</c>).</summary>
    private static string CodigoSinComentarios(string fichero)
    {
        if (CacheDeCodigo.TryGetValue(fichero, out var guardado)) return guardado;

        var texto = System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(fichero), @"@\*.*?\*@|(?m)^[ \t]*/\*.*?\*/", string.Empty,
            System.Text.RegularExpressions.RegexOptions.Singleline);
        var codigo = string.Join('\n', texto.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));
        lock (CacheDeCodigo) CacheDeCodigo[fichero] = codigo;
        return codigo;
    }

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
