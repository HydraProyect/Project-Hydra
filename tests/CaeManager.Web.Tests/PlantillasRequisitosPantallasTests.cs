using System.Security.Claims;
using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.CatalogoBase.Queries;
using CaeManager.Application.Inbound.Requisitos.Queries;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
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
    private static readonly Guid TipoLibreId = Guid.Parse("cccccccc-0000-0000-0000-000000000003");
    private static readonly Guid TipoArchivadoId = Guid.Parse("cccccccc-0000-0000-0000-000000000004");
    private static readonly Guid TipoClienteId = Guid.Parse("cccccccc-0000-0000-0000-000000000005");
    private static readonly Guid TipoVehiculoId = Guid.Parse("cccccccc-0000-0000-0000-000000000006");
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

        public PlantillaRequisitosDto? Detalle { get; set; }

        public bool FallaLaLista { get; init; }

        /// <summary>Lo que devuelve cualquier Command de escritura (por defecto, éxito).</summary>
        public Result Escritura { get; set; } = Result.Exito();

        public IReadOnlyList<TipoDocumentoListaDto> Tipos { get; set; } =
        [
            Tipo(TipoLegalId, "Aptitud médica", AmbitoAplicacion.Trabajador),
            Tipo(TipoPropioId, "Registro de entrega de EPI", AmbitoAplicacion.Trabajador),
            Tipo(TipoLibreId, "Formación en altura", AmbitoAplicacion.Trabajador),
            Tipo(TipoArchivadoId, "Tipo archivado", AmbitoAplicacion.Trabajador),
            Tipo(TipoClienteId, "Tipo de Cliente", AmbitoAplicacion.Cliente),
            Tipo(TipoVehiculoId, "Revisión de la grúa", AmbitoAplicacion.Vehiculo),
        ];

        /// <summary>Los tipos que no salen con <c>ParaAltaNueva</c>, como los archivados en el catálogo real.</summary>
        public HashSet<Guid> Archivados { get; } = [TipoArchivadoId];

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
                ObtenerTiposDocumentoQuery { ParaAltaNueva: true } => Tipos.Where(t => !Archivados.Contains(t.Id)).ToList() as IReadOnlyList<TipoDocumentoListaDto>,
                ObtenerTiposDocumentoQuery => Tipos,
                CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand
                    or CaeManager.Application.Inbound.Requisitos.Commands.CrearRequisitoPropio.CrearRequisitoPropioCommand =>
                    (object)(Escritura.EsExitoso ? Result.Exito(Guid.NewGuid()) : Result.Fallo<Guid>(Escritura.Error)),
                CaeManager.Application.Inbound.Requisitos.Commands.EditarRequisitoPlantilla.EditarRequisitoPlantillaCommand
                    or CaeManager.Application.Inbound.Requisitos.Commands.RetirarRequisitoPlantilla.RetirarRequisitoPlantillaCommand
                    or CaeManager.Application.Inbound.Requisitos.Commands.RetirarPlantillaRequisitos.RetirarPlantillaRequisitosCommand => Escritura,
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
    public void Anadir_un_requisito_del_catalogo_envia_origen_PropioTenant_y_la_Version_leida()
    {
        var mediador = Preparar();
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir").Click();
        cut.WaitForAssertion(() => cut.FindAll("button[role='option']").Should().NotBeEmpty());
        // Los tipos que ya están en la plantilla salen deshabilitados; «Formación en altura» es libre.
        cut.Find("input[type='search']").Input("altura");
        cut.WaitForAssertion(() => cut.FindAll("button[role='option']").Should().ContainSingle());
        cut.Find("button[role='option']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Se añadirá"));
        cut.WaitForAssertion(() => cut.FindAll("button").Where(b => b.TextContent.Trim() == "Guardar").Should().NotBeEmpty());
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Guardar").Click();

        cut.WaitForAssertion(() => mediador.Enviadas
            .OfType<CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand>()
            .Should().ContainSingle());
        var enviado = mediador.Enviadas
            .OfType<CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand>().Single();
        enviado.Atributos.Origen.Should().Be(OrigenRequisito.PropioTenant);
        enviado.Version.Should().Be(VersionPlantilla);
        enviado.TipoDocumentoId.Should().Be(TipoLibreId);
    }

    [Fact]
    public void Editar_requisito_sin_requisitos_enseña_el_estado_vacio()
    {
        var mediador = Preparar();
        mediador.Detalle = new PlantillaRequisitosDto(PlantillaId, TipoPlantillaRequisitos.PorActividad, ActividadId, true, VersionPlantilla, []);

        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Esta plantilla todavía no tiene requisitos."));
    }

    // ------------------------------------------------------------ Escritura: errores, versión y opciones que admite el dominio

    private IRenderedComponent<RequisitosPlantilla> AbrirEdicionDeEpi()
    {
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));
        cut.Find("button[aria-label='Editar requisito Registro de entrega de EPI']").Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tolerancia tras el vencimiento"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "7 días").Click();
        return cut;
    }

    private static void Guardar(IRenderedComponent<RequisitosPlantilla> cut) =>
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Guardar").Click();

    [Fact]
    public void Editar_envia_la_Version_leida_y_un_error_distinto_del_conflicto_deja_el_panel_con_el_mensaje()
    {
        var mediador = Preparar();
        mediador.Escritura = Result.Fallo(Error.Crear("Requisito.Otro", "No se puede guardar este requisito."));
        var cut = AbrirEdicionDeEpi();

        Guardar(cut);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No se puede guardar este requisito."));
        var comando = mediador.Enviadas.OfType<CaeManager.Application.Inbound.Requisitos.Commands.EditarRequisitoPlantilla.EditarRequisitoPlantillaCommand>().Single();
        comando.Version.Should().Be(VersionPlantilla).And.NotBe(Guid.Empty);
        comando.Atributos.ToleranciaDias.Should().Be(7);
        // El panel sigue abierto con lo escrito y la plantilla no se relee.
        cut.Markup.Should().Contain("Criterio de aceptación");
        mediador.Enviadas.OfType<ObtenerPlantillaRequisitosPorIdQuery>().Should().ContainSingle();
    }

    [Fact]
    public void Un_conflicto_de_version_relee_la_plantilla()
    {
        var mediador = Preparar();
        mediador.Escritura = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "La plantilla cambió mientras la editabas."));
        var cut = AbrirEdicionDeEpi();

        Guardar(cut);

        cut.WaitForAssertion(() => mediador.Enviadas.OfType<ObtenerPlantillaRequisitosPorIdQuery>().Should().HaveCount(2));
    }

    [Fact]
    public void Retirar_un_requisito_envia_la_Version_y_un_error_ajeno_al_conflicto_se_queda_en_el_dialogo()
    {
        var mediador = Preparar();
        mediador.Escritura = Result.Fallo(Error.Crear("Requisito.Otro", "No se puede retirar este requisito."));
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));

        cut.Find("button[aria-label='Retirar requisito Registro de entrega de EPI']").Click();
        cut.WaitForAssertion(() => cut.FindAll("button").Count(b => b.TextContent.Trim() == "Retirar").Should().BeGreaterThan(1));
        cut.FindAll("button").Last(b => b.TextContent.Trim() == "Retirar").Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No se puede retirar este requisito."));
        var comando = mediador.Enviadas.OfType<CaeManager.Application.Inbound.Requisitos.Commands.RetirarRequisitoPlantilla.RetirarRequisitoPlantillaCommand>().Single();
        comando.Version.Should().Be(VersionPlantilla).And.NotBe(Guid.Empty);
        mediador.Enviadas.OfType<ObtenerPlantillaRequisitosPorIdQuery>().Should().ContainSingle();
    }

    [Fact]
    public void El_panel_no_ofrece_tipos_archivados_ni_de_ambito_Cliente_o_Proyecto()
    {
        Preparar();
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));

        cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir").Click();

        cut.WaitForAssertion(() => cut.FindAll("button[role='option']").Should().NotBeEmpty());
        var opciones = string.Join("|", cut.FindAll("button[role='option']").Select(b => b.TextContent));
        opciones.Should().Contain("Formación en altura").And.Contain("Revisión de la grúa");
        opciones.Should().NotContain("Tipo archivado").And.NotContain("Tipo de Cliente");
    }

    [Fact]
    public void Un_vehiculo_o_maquina_solo_admite_Todos_como_destinatario()
    {
        var mediador = Preparar();
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir").Click();
        cut.WaitForAssertion(() => cut.FindAll("button[role='option']").Should().NotBeEmpty());
        cut.FindAll("button[role='option']").First(b => b.TextContent.Contains("grúa")).Click();

        cut.WaitForAssertion(() => cut.FindAll("button[role='radio']").First(b => b.TextContent.Trim() == "Nacionales").HasAttribute("disabled").Should().BeTrue());
        cut.FindAll("button[role='radio']").First(b => b.TextContent.Trim() == "Extranjeros").HasAttribute("disabled").Should().BeTrue();
        Guardar(cut);

        cut.WaitForAssertion(() => mediador.Enviadas
            .OfType<CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand>().Should().ContainSingle());
        mediador.Enviadas.OfType<CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand>()
            .Single().Atributos.SePideA.Should().Be(DestinatarioRequisito.Todos);
    }

    [Fact]
    public void Un_requisito_propio_no_ofrece_tolerancia_y_no_la_envia()
    {
        var mediador = Preparar();
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir").Click();
        cut.WaitForAssertion(() => cut.FindAll("input[type='search']").Should().NotBeEmpty());
        cut.Find("input[type='search']").Input("Documento nuevo del Centro");
        cut.WaitForAssertion(() => cut.FindAll("button[aria-label='Crear el requisito propio Documento nuevo del Centro']").Should().NotBeEmpty());
        cut.Find("button[aria-label='Crear el requisito propio Documento nuevo del Centro']").Click();

        cut.WaitForAssertion(() => cut.FindAll("button").First(b => b.TextContent.Trim() == "7 días").HasAttribute("disabled").Should().BeTrue());
        Guardar(cut);

        cut.WaitForAssertion(() => mediador.Enviadas
            .OfType<CaeManager.Application.Inbound.Requisitos.Commands.CrearRequisitoPropio.CrearRequisitoPropioCommand>().Should().ContainSingle());
        mediador.Enviadas.OfType<CaeManager.Application.Inbound.Requisitos.Commands.CrearRequisitoPropio.CrearRequisitoPropioCommand>()
            .Single().Atributos.ToleranciaDias.Should().BeNull();
    }

    [Fact]
    public void Sin_ningun_tipo_de_contratacion_marcado_no_se_puede_guardar_y_dice_por_que()
    {
        Preparar();
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir").Click();
        cut.WaitForAssertion(() => cut.FindAll("button[role='option']").Should().NotBeEmpty());
        cut.FindAll("button[role='option']").First(b => b.TextContent.Contains("Formación en altura")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Tipo de contratación"));

        foreach (var nombre in new[] { "Cuenta ajena", "Autónomo", "Voluntario" })
            cut.FindAll("button").First(b => b.TextContent.Trim() == nombre).Click();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Marca al menos un tipo de contratación."));
    }

    [Fact]
    public void En_una_plantilla_de_exigencias_del_Cliente_el_requisito_nuevo_es_una_exigencia_N3()
    {
        var mediador = Preparar();
        mediador.Detalle = new PlantillaRequisitosDto(
            PlantillaId, TipoPlantillaRequisitos.ExigenciasClienteEmpresarial, Guid.NewGuid(), true, VersionPlantilla, []);
        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Esta plantilla todavía no tiene requisitos."));
        cut.FindAll("button").First(b => b.TextContent.Trim() == "Añadir").Click();
        cut.WaitForAssertion(() => cut.FindAll("button[role='option']").Should().NotBeEmpty());
        cut.FindAll("button[role='option']").First(b => b.TextContent.Contains("Formación en altura")).Click();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Se añadirá"));
        Guardar(cut);

        cut.WaitForAssertion(() => mediador.Enviadas
            .OfType<CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand>().Should().ContainSingle());
        mediador.Enviadas.OfType<CaeManager.Application.Inbound.Requisitos.Commands.AnadirRequisitoAPlantilla.AnadirRequisitoAPlantillaCommand>()
            .Single().Atributos.Origen.Should().Be(OrigenRequisito.ExigenciaN3);
    }

    [Fact]
    public void Una_plantilla_retirada_no_ofrece_Anadir_Editar_ni_Retirar()
    {
        var mediador = Preparar();
        mediador.Detalle = mediador.Detalle! with { Activa = false };

        var cut = Cargada(Render<RequisitosPlantilla>(p => p.Add(c => c.Id, PlantillaId)));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Registro de entrega de EPI"));
        cut.FindAll("button").Where(b => b.TextContent.Trim() is "Añadir" or "Editar" or "Retirar").Should().BeEmpty();
    }

    // ------------------------------------------------------------ Menú y autorización

    private static ContextoMenuLateral ContextoMenu(string rol) => new(
        new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, rol)], "prueba")),
        null, true, false, PerfilVocabularioTenant.Consultora, false, LineaInboundVigente: true);

    [Fact]
    public void Plantillas_de_requisitos_cuelga_de_Configuracion_y_su_enlace_coincide_con_los_roles_de_sus_paginas()
    {
        foreach (var pagina in new[] { typeof(PlantillasRequisitos), typeof(RequisitosPlantilla), typeof(FormularioCamposRequisito) })
        {
            var roles = pagina.GetCustomAttributes(typeof(Microsoft.AspNetCore.Authorization.AuthorizeAttribute), true)
                .Cast<Microsoft.AspNetCore.Authorization.AuthorizeAttribute>().Single().Roles!.Split(',');
            foreach (var rol in new[] { Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta })
            {
                var grupos = CatalogoMenuLateral.Visibles(ContextoMenu(rol));
                var enlace = grupos.SelectMany(g => g.Enlaces).SingleOrDefault(e => e.Ruta == "inbound/plantillas");
                (enlace is not null).Should().Be(roles.Contains(rol), $"el menú y el [Authorize] de {pagina.Name} deben coincidir para {rol}");
                if (enlace is not null)
                    grupos.Single(g => g.Enlaces.Contains(enlace)).Grupo.Id.Should().Be("inbound-config");
            }
        }
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
