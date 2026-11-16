using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.CatalogoBase.Queries;
using CaeManager.Application.Inbound.Requisitos.Queries;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound;
using CaeManager.Web.Features.Inbound.Plantillas.Pages;
using CaeManager.Web.Features.Inbound.Plantillas.Servicios;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Pantallas de plantillas de requisitos de Inbound (listado, Editar requisito y Campos del formulario): rótulos, acciones, estados
/// y estado vacío, y lo que queda como «—» o «Próximamente» sin backend. Los datos son de prueba: en runtime la pantalla solo
/// enseña lo que devuelven las Queries.
/// </summary>
public class PlantillasRequisitosPantallasTests : BunitContext
{
    private static readonly Guid PlantillaId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid ActividadId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid TipoLegalId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid TipoPropioId = Guid.Parse("cccccccc-0000-0000-0000-000000000002");
    private static readonly Guid ReqLegalId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid ReqPropioId = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    private static readonly Guid VersionPlantilla = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");

    public PlantillasRequisitosPantallasTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private static TipoDocumentoListaDto Tipo(Guid id, string nombre, AmbitoAplicacion ambito) =>
        new(id, nombre, null, false, 0, ambito, RequisitoDocumental.No, NaturalezaJuridica.RequisitoCliente, null, null, null, null,
            false, false, false, default, []);

    private static RequisitoPlantillaDto Requisito(Guid id, Guid tipoId, OrigenRequisito origen, EntidadRequisito entidad,
        ModoVigenciaRequisito modo = ModoVigenciaRequisito.Fija, int? meses = 60) =>
        new(id, tipoId, null, entidad, origen, modo, meses, null, true, null, DestinatarioRequisito.Todos, TiposContratacion.Todos, [],
            AmbitoDocumentoRequisito.GeneralTec, true);

    private sealed class MediadorDePrueba : IMediator
    {
        public IReadOnlyList<PlantillaRequisitosResumenDto> Plantillas { get; init; } = [];

        public PlantillaRequisitosDto? Detalle { get; init; }

        public bool FallaLaLista { get; init; }

        public List<object> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            Enviadas.Add(request);
            return (TResponse)(request switch
            {
                ObtenerPlantillasRequisitosQuery when FallaLaLista => throw new InvalidOperationException("sin lista"),
                ObtenerPlantillasRequisitosQuery => new ResultadoPaginado<PlantillaRequisitosResumenDto>(Plantillas, Plantillas.Count, 1, 100),
                ObtenerPlantillaRequisitosPorIdQuery => (object?)Detalle!,
                ObtenerCatalogoBaseQuery => new CatalogoBaseDto([new ActividadCatalogoDto(ActividadId, "A-1", "Mantenimiento industrial", [])], []),
                ObtenerCentrosParaSelectorQuery => Array.Empty<CentroSelectorDto>() as IReadOnlyList<CentroSelectorDto>,
                ObtenerTiposDocumentoQuery => new[]
                {
                    Tipo(TipoLegalId, "Aptitud médica", AmbitoAplicacion.Trabajador),
                    Tipo(TipoPropioId, "Registro de entrega de EPI", AmbitoAplicacion.Trabajador),
                } as IReadOnlyList<TipoDocumentoListaDto>,
                BuscarRequisitosParecidosQuery => Array.Empty<RequisitoParecidoDto>() as IReadOnlyList<RequisitoParecidoDto>,
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}."),
            });
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private MediadorDePrueba Preparar(bool conDatos = true, bool falla = false)
    {
        var detalle = new PlantillaRequisitosDto(
            PlantillaId, TipoPlantillaRequisitos.PorActividad, ActividadId, true, VersionPlantilla,
            [
                Requisito(ReqLegalId, TipoLegalId, OrigenRequisito.LegalN1, EntidadRequisito.Trabajador, ModoVigenciaRequisito.Fija, 12),
                Requisito(ReqPropioId, TipoPropioId, OrigenRequisito.PropioTenant, EntidadRequisito.Trabajador),
            ]);
        var mediador = new MediadorDePrueba
        {
            Plantillas = conDatos
                ? [new PlantillaRequisitosResumenDto(PlantillaId, TipoPlantillaRequisitos.PorActividad, ActividadId, true, 2, VersionPlantilla)]
                : [],
            Detalle = detalle,
            FallaLaLista = falla,
        };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ResolutorPlantillasRequisitos>();
        // La implementación vacía que se registra en Program: sin backend de resolución por capas, la pantalla enseña «—».
        Services.AddScoped<IUsoDePlantillasRequisitos, UsoDePlantillasRequisitosNoDisponible>();
        return mediador;
    }

    private static IRenderedComponent<T> Cargada<T>(IRenderedComponent<T> cut) where T : IComponent
    {
        cut.WaitForState(() => !cut.Markup.Contains("esqueleto-lista"), TimeSpan.FromSeconds(5));
        return cut;
    }

    // ------------------------------------------------------------ Listado (tarjeta 17)

    [Fact]
    public void El_listado_enseña_la_plantilla_su_nivel_los_requisitos_y_sin_dato_de_contratos()
    {
        Preparar();

        var cut = Cargada(Render<PlantillasRequisitos>());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));

        cut.Markup.Should().Contain("Plantillas de requisitos");
        cut.Markup.Should().Contain("Mantenimiento industrial");
        cut.Markup.Should().Contain("Actividad");
        // Requisito legal: lo pone TALVEG, sin Editar; el propio sí se edita. Contratos: sin dato hasta B-15.
        cut.Markup.Should().Contain("Lo pone TALVEG");
        cut.Markup.Should().Contain("No admite No aplica");
        cut.Markup.Should().Contain("5 años");
        cut.Markup.Should().Contain("Aptitud médica");
        cut.Markup.Should().Contain("1 año");
        cut.FindAll("a[href*='?editar=']").Should().HaveCount(1);
        cut.Markup.Should().Contain($"/inbound/plantillas/{PlantillaId}/requisitos?anadir=true");
        cut.Markup.Should().Contain("Contratos de acceso que la usan: sin dato");
        cut.Markup.Should().NotContain("Usada en 0");
    }

    [Fact]
    public void Sin_plantillas_enseña_el_estado_vacio_con_su_accion()
    {
        Preparar(conDatos: false);

        var cut = Cargada(Render<PlantillasRequisitos>());

        cut.Markup.Should().Contain("Todavía no hay plantillas de requisitos");
        cut.Markup.Should().Contain("Añadir");
    }

    [Fact]
    public void Si_la_lectura_falla_dice_que_no_se_pudo_cargar_y_ofrece_reintentar()
    {
        Preparar(falla: true);

        var cut = Cargada(Render<PlantillasRequisitos>());

        cut.Markup.Should().Contain("No se han podido cargar las plantillas");
        cut.Markup.Should().Contain("Reintentar");
    }

    // ------------------------------------------------------------ Editar requisito (tarjeta 9)

    [Fact]
    public void Editar_requisito_lista_los_requisitos_y_el_panel_marca_lo_que_aun_no_tiene_backend()
    {
        Preparar();

        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));

        cut.Markup.Should().Contain("Plantilla «Mantenimiento industrial»");
        cut.Markup.Should().Contain("Lo pone TALVEG");
        cut.Markup.Should().Contain("Datos que lee la IA");

        cut.Find("button[aria-label='Editar requisito Registro de entrega de EPI']").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tolerancia tras el vencimiento"));
        cut.Markup.Should().Contain("Criterio de aceptación");
        cut.Markup.Should().Contain("Formulario del Centro");
        cut.Markup.Should().Contain("Contratos a los que se aplica");
        cut.FindAll("[data-pieza='insignia-proximamente']").Count.Should().BeGreaterOrEqualTo(3);
    }

    [Fact]
    public void Editar_requisito_sin_requisitos_enseña_el_estado_vacio()
    {
        var mediador = Preparar();
        Services.AddScoped<IMediator>(_ => new MediadorDePrueba
        {
            Detalle = new PlantillaRequisitosDto(PlantillaId, TipoPlantillaRequisitos.PorActividad, ActividadId, true, VersionPlantilla, []),
        });
        _ = mediador;

        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Esta plantilla todavía no tiene requisitos."));
    }

    // ------------------------------------------------------------ Campos del formulario

    [Fact]
    public void Campos_del_formulario_dice_que_el_formulario_del_Centro_no_esta_disponible()
    {
        Preparar();

        var cut = Cargada(Render<FormularioCamposRequisito>(p => p
            .Add(c => c.Id, PlantillaId)
            .Add(c => c.RequisitoId, ReqPropioId)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Campos del formulario"));
        cut.Markup.Should().Contain("El formulario del Centro todavía no está disponible");
        cut.Markup.Should().Contain("Registro de entrega de EPI");
        cut.Find("[data-pieza='insignia-proximamente']");
    }
}
