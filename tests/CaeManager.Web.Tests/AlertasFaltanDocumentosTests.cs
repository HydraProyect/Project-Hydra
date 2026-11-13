using Bunit;
using CaeManager.Application.Alertas.Queries.ObtenerAlertas;
using CaeManager.Application.Alertas.Queries.ObtenerSugerenciasPreventivas;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Alertas.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Sección «Faltan documentos» de /alertas (PR5 del lienzo «Documentos · listado sin pestañas», P-2b): sustituye a
/// «Patrones de falta» de la pestaña Preventivo de Documentos, con los mismos datos
/// (<see cref="ObtenerSugerenciasPreventivasQuery"/>) y el mismo destino de «Crear documento».
///
/// <para>
/// <b>Lo que SÍ observa:</b> el marcado: una tarjeta por Centro y tipo de Documento, su recuento, el pill de Centros,
/// el destino del enlace, que Consulta no lo vea, los estados cargando, error y vacío, el lugar de la sección en la
/// pantalla y que la pestaña Preventivo ya no pinte los patrones.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el umbral de 2 Trabajadores ni el alcance (los aplica el handler, probado en
/// <c>ObtenerSugerenciasPreventivasQueryTests</c>), el aspecto visual ni que la autorización de crear siga en el
/// comando.
/// </para>
/// </summary>
public class AlertasFaltanDocumentosTests : BunitContext
{
    public AlertasFaltanDocumentosTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddScoped<ToastService>();
    }

    private static readonly Guid CentroNorte = Guid.Parse("c0000000-0000-0000-0000-000000000001");
    private static readonly Guid CentroBerriz = Guid.Parse("c0000000-0000-0000-0000-000000000002");
    private static readonly Guid TipoReconocimiento = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid TipoAltura = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid Lucia = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid Raul = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid Inigo = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private static readonly Guid Carmen = Guid.Parse("a0000000-0000-0000-0000-000000000004");

    private static IReadOnlyList<SugerenciaPreventivaDto> DosCentros() =>
    [
        new(CentroNorte, "Centro Norte", TipoReconocimiento, "Reconocimiento médico",
            [new(Lucia, "Lucía Ferrer Mora"), new(Raul, "Raúl Mena Costa"), new(Inigo, "Íñigo Sala Pons")]),
        new(CentroBerriz, "Nave Berriz", TipoAltura, "Formación específica en trabajos en altura",
            [new(Carmen, "Carmen Duarte Vela"), new(Raul, "Raúl Mena Costa")])
    ];

    /// <summary>Respuesta de ObtenerSugerenciasPreventivasQuery: un valor, una excepción o una Task ya tipada.</summary>
    private sealed class MediatorFaltan(Func<object> sugerencias, IReadOnlyList<AlertaDto>? alertas = null) : IMediator
    {
        public int Cargas { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta;
            switch (request)
            {
                case ObtenerSugerenciasPreventivasQuery:
                    Cargas++;
                    respuesta = sugerencias();
                    break;
                case ObtenerAlertasQuery:
                    respuesta = alertas ?? (IReadOnlyList<AlertaDto>)[];
                    break;
                case ObtenerAlcanceCeroQuery:
                    respuesta = false;
                    break;
                case ObtenerCandidatosIncorporacionCarteraQuery:
                    respuesta = Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]);
                    break;
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }

            return respuesta is Task<TResponse> tarea ? tarea : Task.FromResult((TResponse)respuesta);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private MediatorFaltan Registrar(Func<object> sugerencias, IReadOnlyList<AlertaDto>? alertas = null)
    {
        var mediator = new MediatorFaltan(sugerencias, alertas);
        Services.AddScoped<IMediator>(_ => mediator);
        return mediator;
    }

    private IRenderedComponent<FaltanDocumentosAlertas> RenderizarSeccion(
        string rol = Roles.GestorCae, IReadOnlyList<SugerenciaPreventivaDto>? sugerencias = null)
    {
        this.ConRolDeEscritura(rol);
        Registrar(() => sugerencias ?? DosCentros());
        return Render<FaltanDocumentosAlertas>();
    }

    // ------------------------------------------------------------------ la sección

    [Fact]
    public void Pinta_una_tarjeta_por_Centro_y_tipo_de_Documento_con_su_titulo_y_su_recuento()
    {
        var cut = RenderizarSeccion();

        cut.Markup.Should().Contain("Faltan documentos");
        var grupos = cut.FindAll(".faltan-documentos-grupo");
        grupos.Should().HaveCount(2);

        grupos[0].QuerySelector("h3")!.TextContent.Trim().Should().Be("Centro Norte · Reconocimiento médico");
        grupos[0].QuerySelector(".faltan-documentos-recuento")!.TextContent.Trim()
            .Should().Be("3 Trabajadores con Asignación activa a este Centro no tienen este Documento.");
        grupos[0].QuerySelectorAll("li").Select(li => li.QuerySelector("span")!.TextContent.Trim())
            .Should().Equal("Lucía Ferrer Mora", "Raúl Mena Costa", "Íñigo Sala Pons");

        grupos[1].QuerySelector("h3")!.TextContent.Trim().Should().Be("Nave Berriz · Formación específica en trabajos en altura");
        grupos[1].QuerySelector(".faltan-documentos-recuento")!.TextContent.Trim()
            .Should().Be("2 Trabajadores con Asignación activa a este Centro no tienen este Documento.");
        grupos[1].QuerySelectorAll("li").Should().HaveCount(2);
    }

    [Fact]
    public void Dice_Trabajadores_y_nunca_tecnicos_y_lleva_la_nota_explicativa_del_artboard()
    {
        var cut = RenderizarSeccion();

        cut.Find(".faltan-documentos-nota").TextContent.Trim().Should().Be(
            "Documentos que deberían existir en un Centro y no existen: nunca se subieron, así que no son una fila del listado de Documentos. " +
            "Cada tarjeta es un Centro y un tipo de Documento; cada Trabajador listado tiene una Asignación activa a ese Centro y no lo tiene.");
        cut.Markup.Should().NotContainEquivalentOf("técnicos");
    }

    [Fact]
    public void El_pill_cuenta_los_Centros_distintos_no_las_tarjetas()
    {
        var cut = RenderizarSeccion();
        cut.Find(".seccion-colapsable-encabezado .badge").TextContent.Trim().Should().Be("2 Centros");
    }

    [Fact]
    public void Con_dos_tarjetas_del_mismo_Centro_el_pill_dice_1_Centro()
    {
        var cut = RenderizarSeccion(sugerencias:
        [
            new(CentroNorte, "Centro Norte", TipoReconocimiento, "Reconocimiento médico", [new(Lucia, "Lucía Ferrer Mora"), new(Raul, "Raúl Mena Costa")]),
            new(CentroNorte, "Centro Norte", TipoAltura, "Formación en altura", [new(Lucia, "Lucía Ferrer Mora"), new(Raul, "Raúl Mena Costa")])
        ]);

        cut.FindAll(".faltan-documentos-grupo").Should().HaveCount(2);
        cut.Find(".seccion-colapsable-encabezado .badge").TextContent.Trim().Should().Be("1 Centro");
    }

    [Fact]
    public void Crear_documento_lleva_a_Documentos_con_el_Trabajador_y_el_tipo_ya_elegidos()
    {
        var cut = RenderizarSeccion();

        var enlaces = cut.FindAll("a.faltan-documentos-crear");
        enlaces.Should().HaveCount(5, "uno por cada Trabajador de cada tarjeta");
        enlaces.Should().OnlyContain(a => a.TextContent.Trim() == "Crear documento");
        enlaces[0].GetAttribute("href").Should().Be($"/documentos?TrabajadorId={Lucia}&TipoDocumentoId={TipoReconocimiento}");
        enlaces[3].GetAttribute("href").Should().Be($"/documentos?TrabajadorId={Carmen}&TipoDocumentoId={TipoAltura}");
        enlaces[0].GetAttribute("aria-label").Should().Contain("Crear documento").And.Contain("Lucía Ferrer Mora");
    }

    [Theory]
    [InlineData(Roles.GestorCae, 5)]
    [InlineData(Roles.CoordinadorCae, 5)]
    [InlineData(Roles.Consulta, 0)]
    public void Crear_documento_solo_se_ofrece_a_los_roles_con_escritura_y_Consulta_ve_la_seccion_sin_el_enlace(string rol, int enlaces)
    {
        var cut = RenderizarSeccion(rol);

        cut.FindAll("a.faltan-documentos-crear").Should().HaveCount(enlaces);
        cut.FindAll(".faltan-documentos-grupo").Should().HaveCount(2, "Consulta sigue viendo qué falta");
        cut.FindAll(".faltan-documentos-lista li").Should().HaveCount(5);
        cut.Markup.Should().Contain("Lucía Ferrer Mora");
        cut.Find(".seccion-colapsable-encabezado .badge").TextContent.Trim().Should().Be("2 Centros");
    }

    [Fact]
    public void Sin_grupos_la_seccion_no_se_pinta()
    {
        var cut = RenderizarSeccion(sugerencias: []);

        cut.Markup.Should().NotContain("Faltan documentos", "sin patrones no hay nada que reclamar aquí: ni título, ni pill, ni estado vacío");
        cut.FindAll(".seccion-colapsable, .estado-vacio, .badge").Should().BeEmpty();
    }

    [Fact]
    public void Mientras_carga_pinta_la_seccion_con_el_estado_de_carga()
    {
        this.ConRolDeEscritura();
        var pendiente = new TaskCompletionSource<IReadOnlyList<SugerenciaPreventivaDto>>();
        Registrar(() => pendiente.Task);

        var cut = Render<FaltanDocumentosAlertas>();

        cut.Markup.Should().Contain("Faltan documentos");
        cut.FindAll(".faltan-documentos-grupo").Should().BeEmpty();
        cut.FindAll("[aria-busy='true']").Should().ContainSingle();

        pendiente.SetResult(DosCentros());
        cut.WaitForAssertion(() => cut.FindAll(".faltan-documentos-grupo").Should().HaveCount(2));
    }

    [Fact]
    public void Si_la_carga_falla_lo_dice_y_Reintentar_vuelve_a_pedir_los_datos()
    {
        this.ConRolDeEscritura();
        var fallar = true;
        var mediator = Registrar(() => fallar ? throw new InvalidOperationException("caída") : DosCentros());

        var cut = Render<FaltanDocumentosAlertas>();

        cut.Markup.Should().Contain("No se han podido cargar los documentos que faltan.");
        cut.FindAll(".faltan-documentos-grupo").Should().BeEmpty();

        fallar = false;
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Reintentar").Click();

        cut.WaitForAssertion(() => cut.FindAll(".faltan-documentos-grupo").Should().HaveCount(2));
        mediator.Cargas.Should().Be(2);
    }

    // ------------------------------------------------------------------ en /alertas

    private IRenderedComponent<Features.Alertas.Pages.Alertas> RenderizarPantalla(string rol = Roles.GestorCae)
    {
        this.ConRolDeEscritura(rol);
        var alerta = new AlertaDto(Guid.NewGuid(), Guid.NewGuid(), "Juan Pérez", Guid.NewGuid(), "Reconocimiento médico",
            new DateOnly(2026, 10, 1), EstadoDocumento.Vencido, ArchivoUrl: null, CentroNombre: "Centro Zorrotzaurre");
        Registrar(() => DosCentros(), [alerta]);
        Services.GetRequiredService<NavigationManager>().NavigateTo("alertas");
        return Render<Features.Alertas.Pages.Alertas>();
    }

    [Fact]
    public void En_Alertas_la_seccion_va_tras_Reclamar_y_antes_de_la_barra_de_filtros_y_de_la_lista()
    {
        var cut = RenderizarPantalla();

        var cabeceras = cut.FindAll(".seccion-colapsable-cabecera").Select(c => c.TextContent.Trim()).ToList();
        cabeceras.Should().HaveCount(3);
        cabeceras[0].Should().Contain("¿De dónde sale esta lista?");
        cabeceras[1].Should().Be("Reclamar por tipo de documento y ámbito");
        cabeceras[2].Should().StartWith("Faltan documentos").And.Contain("2 Centros");

        var html = cut.Markup;
        var posReclamar = html.IndexOf("Reclamar por tipo de documento y ámbito", StringComparison.Ordinal);
        var posFaltan = html.IndexOf("faltan-documentos-nota", StringComparison.Ordinal);
        var posFiltros = html.IndexOf("alertas-barra-filtros", StringComparison.Ordinal);
        var posLista = html.IndexOf("alertas-grupos", StringComparison.Ordinal);
        posReclamar.Should().BeLessThan(posFaltan);
        posFaltan.Should().BeLessThan(posFiltros);
        posFiltros.Should().BeLessThan(posLista);
    }

    [Fact]
    public void En_Alertas_la_seccion_pide_los_patrones_una_vez_y_Consulta_no_ve_Crear_documento()
    {
        var cut = RenderizarPantalla(Roles.Consulta);

        cut.FindAll(".faltan-documentos-grupo").Should().HaveCount(2);
        cut.FindAll("a.faltan-documentos-crear").Should().BeEmpty();
        cut.Services.GetRequiredService<IMediator>().Should().BeOfType<MediatorFaltan>().Which.Cargas.Should().Be(1);
    }

    [Fact]
    public void La_cabecera_de_Alertas_lleva_el_kicker_el_titulo_y_la_descripcion_del_artboard()
    {
        var cut = RenderizarPantalla();

        cut.Markup.Should().Contain("Reclamación");
        cut.Find("h1").TextContent.Trim().Should().Be("Alertas");
        cut.Markup.Should().Contain(
            "La documentación de tus Trabajadores que hay que reclamar o subir, incluida la que todavía no es urgente, " +
            "para que puedas anticiparte antes del vencimiento en vez de esperar a que se vuelva crítica.");
    }

    [Fact]
    public void Alertas_conserva_la_procedencia_exacta_de_main_y_el_enlace_a_la_cola_de_trabajo()
    {
        var cut = RenderizarPantalla();

        // La frase del artboard («nadie la escribe a mano» como única explicación) es inexacta: «Falta» sale de
        // IDocumentosFaltantesService. Se conserva el texto de procedencia vigente, que dice las dos fuentes.
        cut.FindAll(".seccion-colapsable-cabecera").Single(b => b.TextContent.Contains("¿De dónde sale esta lista?")).Click();
        cut.Find(".alertas-procedencia").TextContent.Should()
            .Contain("a partir del vencimiento de cada documento y de los documentos que se piden en cada centro");

        var enlace = cut.FindAll("a").Single(a => a.TextContent.Contains("Ver tu cola de trabajo en Mi trabajo"));
        enlace.GetAttribute("href").Should().Be("/bandeja");
    }

    [Fact]
    public void Alertas_sigue_mostrando_la_barra_de_filtros_la_lista_agrupada_y_el_paginador()
    {
        var cut = RenderizarPantalla();

        cut.Find(".alertas-barra-filtros").TextContent.Should().Contain("Estado").And.Contain("Agrupar por");
        cut.FindAll(".alertas-grupo").Should().ContainSingle();
        cut.Find(".alertas-grupo-titulo").TextContent.Trim().Should().Be("Vencido");
        cut.Markup.Should().Contain("Reclamar por tipo de documento y ámbito");
    }
}
