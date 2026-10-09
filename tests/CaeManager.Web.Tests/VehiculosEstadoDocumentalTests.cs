using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.Commands.RenovarDocumento;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Application.Vehiculos.Commands.CrearVehiculo;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculos;
using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculosParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using CaeManager.Web.Features.Vehiculos.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Listado de Vehículos, cierre de la maqueta de listados: el orden y los rótulos de las columnas, y el motivo
/// bajo la pastilla de «Estado documental» con su desglose corregible sin salir del listado.
///
/// <para>
/// El doble del mediador guarda los vehículos y APLICA lo que recibe de la consulta de lista: el filtro por
/// <c>VehiculoId</c>, el de estado, el orden por nombre y la paginación. No reproduce la búsqueda, el filtro
/// de empleador ni el alcance de cartera: ningún test de aquí depende de ellos (el alcance y el contenido del
/// desglose se miden bajo RLS en <c>DesgloseDocumentalDeVehiculosBajoRlsTests</c>).
/// </para>
///
/// <para>
/// Lo que estos tests NO observan: que el clic en una incidencia no abra además la vista rápida de la fila.
/// Ese clic de fila lo atiende <c>atajos-lista.js</c>, que bUnit no ejecuta; aquí solo se fija que el botón
/// vive dentro de <c>.ventana-contexto-panel</c>, que es lo que ese guion excluye.
/// </para>
/// </summary>
public class VehiculosEstadoDocumentalTests : BunitContext
{
    private static readonly Guid EmpresaPropia = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>QuickGrid y AtajosListaTeclado importan sus módulos JS al montarse.</summary>
    public VehiculosEstadoDocumentalTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private sealed class MediadorFalso : IMediator
    {
        public List<VehiculoListaDto> Almacen { get; } = [];
        public List<object> Enviadas { get; } = [];

        /// <summary>Lo que devuelve la lectura del documento que abre el formulario de corrección.</summary>
        public DocumentoDetalleDto? Documento { get; set; }

        /// <summary>Con una sola Empresa y perfil Cliente Directo, el alta no pide elegir empleador.</summary>
        public bool AltaSinElegirEmpleador { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(request switch
            {
                ObtenerClientesAutorizadosQuery => (object)(IReadOnlyList<ClienteAutorizadoDto>)[],
                ObtenerPerfilVocabularioActualQuery =>
                    AltaSinElegirEmpleador ? PerfilVocabularioTenant.ClienteDirecto : PerfilVocabularioTenant.Consultora,
                ObtenerEmpresasParaSelectorQuery => AltaSinElegirEmpleador
                    ? (IReadOnlyList<EmpresaSelectorDto>)[new EmpresaSelectorDto(EmpresaPropia, "Montajes Ebro S.L.")]
                    : (IReadOnlyList<EmpresaSelectorDto>)[],
                ObtenerSubcontratasParaSelectorQuery => Array.Empty<SubcontrataSelectorDto>(),
                ObtenerVehiculosQuery q => Filtrar(q),
                ObtenerAlcanceCeroQuery => false,
                ObtenerCandidatosIncorporacionCarteraQuery => Result.Exito<IReadOnlyList<CandidatoIncorporacionCarteraDto>>([]),
                CrearVehiculoCommand => Result.Exito(Guid.NewGuid()),
                // El formulario de corrección (DrawerGestionDocumento).
                ObtenerDocumentoPorIdQuery => Documento!,
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[],
                ObtenerVehiculosParaSelectorQuery => (IReadOnlyList<VehiculoSelectorDto>)[],
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                RenovarDocumentoCommand renovar => Result.Exito(renovar.Id),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            }));
        }

        private ResultadoPaginado<VehiculoListaDto> Filtrar(ObtenerVehiculosQuery q)
        {
            var estados = string.IsNullOrWhiteSpace(q.EstadoDocumental)
                ? null
                : q.EstadoDocumental.Split(',', StringSplitOptions.TrimEntries);
            var coincidentes = Almacen
                .Where(v => q.VehiculoId is null || v.Id == q.VehiculoId)
                .Where(v => estados is null || estados.Contains(v.EstadoDocumental?.ToString()))
                .OrderBy(v => v.Nombre, StringComparer.Ordinal).ThenBy(v => v.Id)
                .ToList();
            // Como el handler: sin pedir el desglose, las filas llegan sin incidencias.
            var pagina = coincidentes
                .Skip((q.Pagina - 1) * q.TamanoPagina)
                .Take(q.TamanoPagina)
                .Select(v => q.ConDesgloseDocumental ? v : v with { Incidencias = [] })
                .ToList();
            return new ResultadoPaginado<VehiculoListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
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

    private static VehiculoListaDto Vehiculo(
        string nombre, EstadoDocumento? estado = EstadoDocumento.Vigente, params IncidenciaDocumentalDto[] incidencias) =>
        new(Guid.NewGuid(), nombre, "Iveco Daily", "9012GHI", "Montajes Ebro S.L.", estado) { Incidencias = incidencias };

    private static IncidenciaDocumentalDto IncidenciaDe(string tipo, EstadoDocumento estado, int? diasHastaVencer = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tipo, estado,
            diasHastaVencer is { } dias ? DiaDeNegocio.Hoy().AddDays(dias) : null);

    private IRenderedComponent<Vehiculos> Renderizar(MediadorFalso mediador, string url = "vehiculos", string rol = Roles.GestorCae)
    {
        this.ConRolDeEscritura(rol);
        this.ConServiciosDelFormularioDeDocumento();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<IValidator<CrearVehiculoCommand>>(_ => new InlineValidator<CrearVehiculoCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);
        var cut = Render<Vehiculos>();
        cut.WaitForState(() => cut.FindAll("tbody a.boton-360-pagina").Count > 0);
        return cut;
    }

    /// <summary>Filas con datos (QuickGrid rellena la página con filas vacías).</summary>
    private static IReadOnlyList<IElement> Filas(IRenderedComponent<Vehiculos> cut) =>
        cut.FindAll("tbody tr:has(button.enlace-nombre-fila)");

    private static List<string> Nombres(IRenderedComponent<Vehiculos> cut) =>
        Filas(cut).Select(tr => tr.QuerySelector("button.enlace-nombre-fila")!.TextContent.Trim()).ToList();

    private static IElement CeldaDeEstado(IRenderedComponent<Vehiculos> cut, int fila = 0) =>
        Filas(cut)[fila].QuerySelector("td.col-estado")!;

    private static string? Motivo(IRenderedComponent<Vehiculos> cut, int fila = 0) =>
        CeldaDeEstado(cut, fila).QuerySelector(".estado-fila-motivo .motivo-incidencias-texto")?.TextContent.Trim();

    private static List<string> Cabeceras(IRenderedComponent<Vehiculos> cut) =>
        cut.FindAll("thead th").Select(th => th.QuerySelector(".col-title-text")?.TextContent.Trim() ?? string.Empty).ToList();

    private static int ConsultasDeLista(MediadorFalso mediador) => mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Count();

    private static ObtenerVehiculosQuery UltimaConsulta(MediadorFalso mediador) => mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Last();

    private static Task AlternarSeleccionMultiple(IRenderedComponent<Vehiculos> cut) =>
        cut.Find(".cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());

    private static Task MarcarAsync(IRenderedComponent<Vehiculos> cut, string nombre) =>
        CasillaDe(cut, nombre).ChangeAsync(new ChangeEventArgs { Value = true });

    private static IElement CasillaDe(IRenderedComponent<Vehiculos> cut, string nombre) =>
        Filas(cut).Single(tr => tr.QuerySelector("button.enlace-nombre-fila")!.TextContent.Trim() == nombre)
            .QuerySelector("input[type=checkbox]")!;

    private static Task CorregirAsync(IRenderedComponent<Vehiculos> cut) =>
        cut.InvokeAsync(() => cut.FindComponent<CorreccionIncidenciaDocumental>().Instance.OnCorregida.InvokeAsync());

    // --- Columnas: orden y rótulos -------------------------------------------------------------

    [Fact]
    public async Task Las_columnas_van_en_el_orden_de_la_maqueta_con_sus_rotulos()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Almacen = { Vehiculo("Camión grúa", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -10)) }
        });

        Cabeceras(cut).Should().Equal("Vehículo · Nombre", "Empleador", "Matrícula", "Estado documental", "");

        // Las celdas siguen a sus cabeceras: identidad, empleador, matrícula copiable, estado e icono 360.
        var celdas = Filas(cut)[0].QuerySelectorAll("td").ToList();
        celdas.Should().HaveCount(5);
        celdas[0].QuerySelector("button.enlace-nombre-fila")!.TextContent.Trim().Should().Be("Camión grúa");
        celdas[0].QuerySelector(".celda-vehiculo-modelo")!.TextContent.Trim().Should().Be("Iveco Daily");
        celdas[1].TextContent.Trim().Should().Be("Montajes Ebro S.L.");
        celdas[2].QuerySelector(".celda-vehiculo-matricula")!.TextContent.Should().Contain("9012GHI");
        celdas[2].QuerySelector(".celda-vehiculo-matricula button").Should().NotBeNull("la matrícula se sigue pudiendo copiar");
        celdas[3].ClassList.Should().Contain("col-estado");
        celdas[4].QuerySelector("a.boton-360-pagina")!.GetAttribute("href").Should().StartWith("/vehiculos/");

        // Con la selección múltiple, la casilla va delante y el resto no se mueve.
        await AlternarSeleccionMultiple(cut);
        Cabeceras(cut).Should().Equal("", "Vehículo · Nombre", "Empleador", "Matrícula", "Estado documental", "");
        cut.Find("thead th input[type=checkbox]").Should().NotBeNull();
    }

    // --- Estado documental: pastilla y motivo --------------------------------------------------

    [Fact]
    public void Con_una_incidencia_el_motivo_bajo_la_pastilla_dice_el_documento_que_la_causa()
    {
        var mediador = new MediadorFalso
        {
            Almacen =
            {
                Vehiculo("Camión grúa", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -10)),
                Vehiculo("Furgoneta", EstadoDocumento.Urgente, IncidenciaDe("Seguro del vehículo", EstadoDocumento.Urgente, 11))
            }
        };
        var cut = Renderizar(mediador);

        CeldaDeEstado(cut).QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido", "la pastilla se pinta siempre");
        Motivo(cut).Should().Be("ITV");
        CeldaDeEstado(cut, 1).QuerySelector(".badge")!.TextContent.Trim().Should().Be("Por vencer");
        Motivo(cut, 1).Should().Be("Seguro del vehículo · Caduca en 11 días");
        UltimaConsulta(mediador).ConDesgloseDocumental.Should().BeTrue("la carga de página es quien pide el desglose que pinta");
    }

    [Fact]
    public void Con_varias_incidencias_el_motivo_las_cuenta_por_clase_y_el_desglose_lleva_una_linea_por_documento()
    {
        var grua = Vehiculo("Camión grúa", EstadoDocumento.Vencido,
            IncidenciaDe("ITV", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Tarjeta de transporte", EstadoDocumento.Vencido, -3),
            IncidenciaDe("Seguro del vehículo", EstadoDocumento.Proximo, 25));
        var cut = Renderizar(new MediadorFalso { Almacen = { grua } });

        var celda = CeldaDeEstado(cut);
        celda.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Vencido");
        Motivo(cut).Should().Be("2 vencidos · 1 por vencer");
        celda.QuerySelectorAll("button.ventana-contexto-elemento .motivo-incidencias-tipo").Select(e => e.TextContent.Trim())
            .Should().Equal("ITV", "Tarjeta de transporte", "Seguro del vehículo");
    }

    [Fact]
    public void Sin_incidencias_la_celda_lleva_el_estado_y_ningun_motivo()
    {
        var cut = Renderizar(new MediadorFalso
        {
            Almacen =
            {
                Vehiculo("Camión grúa", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -10)),
                Vehiculo("Furgoneta", EstadoDocumento.Vigente),
                Vehiculo("Remolque", estado: null)
            }
        });

        Motivo(cut).Should().Be("ITV", "control positivo: la fila con incidencias sí lleva motivo");
        foreach (var fila in new[] { 1, 2 })
        {
            var celda = CeldaDeEstado(cut, fila);
            celda.QuerySelector(".estado-fila").Should().NotBeNull("el estado se pinta siempre");
            celda.QuerySelector(".estado-fila")!.TextContent.Trim().Should().NotBeEmpty();
            celda.QuerySelectorAll(".estado-fila-motivo").Should().BeEmpty("sin incidencias no hay nada que explicar");
            celda.QuerySelectorAll(".ventana-contexto").Should().BeEmpty();
        }
    }

    [Fact]
    public void Las_filas_con_incidencias_no_comparten_claves_aunque_haya_varias_en_la_pagina()
    {
        // Dos filas con desglose a la vez: un @key repetido entre hermanos tumba el circuito («wrong pooled»).
        var cut = Renderizar(new MediadorFalso
        {
            Almacen =
            {
                Vehiculo("Camión grúa", EstadoDocumento.Vencido,
                    IncidenciaDe("ITV", EstadoDocumento.Vencido, -1), IncidenciaDe("Seguro del vehículo", EstadoDocumento.Vencido, -2)),
                Vehiculo("Furgoneta", EstadoDocumento.Vencido,
                    IncidenciaDe("ITV", EstadoDocumento.Vencido, -1), IncidenciaDe("Seguro del vehículo", EstadoDocumento.Vencido, -2))
            }
        });

        Filas(cut).Should().HaveCount(2);
        cut.FindAll("td.col-estado button.ventana-contexto-elemento").Should().HaveCount(4);
    }

    [Fact]
    public void Quien_solo_consulta_ve_el_desglose_sin_botones()
    {
        var grua = Vehiculo("Camión grúa", EstadoDocumento.Vencido,
            IncidenciaDe("ITV", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Permiso de circulación", EstadoDocumento.SinConfirmar));
        var cut = Renderizar(new MediadorFalso { Almacen = { grua } }, rol: Roles.Consulta);

        var celda = CeldaDeEstado(cut);
        Motivo(cut).Should().Be("1 vencido · 1 sin confirmar", "control positivo: el motivo se ve igual");
        celda.QuerySelectorAll("button").Should().BeEmpty("no se ofrece un formulario que el comando va a denegar");
        celda.QuerySelectorAll(".ventana-linea").Should().HaveCount(2, "control positivo: el desglose se sigue viendo");
    }

    // --- Corrección en la misma pantalla -------------------------------------------------------

    [Fact]
    public async Task Pulsar_una_incidencia_abre_la_correccion_de_ese_documento_sin_recargar_la_lista()
    {
        var vencida = IncidenciaDe("ITV", EstadoDocumento.Vencido, -12);
        var urgente = IncidenciaDe("Seguro del vehículo", EstadoDocumento.Urgente, 5);
        var mediador = new MediadorFalso { Almacen = { Vehiculo("Camión grúa", EstadoDocumento.Vencido, vencida, urgente) } };
        var cut = Renderizar(mediador);
        var consultasDeListaAntes = ConsultasDeLista(mediador);
        var boton = CeldaDeEstado(cut).QuerySelectorAll("button.ventana-contexto-elemento")[1];
        boton.Closest(".ventana-contexto-panel").Should().NotBeNull(
            "es lo que «pulsarFila» de atajos-lista.js excluye para no abrir además la vista rápida");

        await boton.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(urgente.DocumentoId, "se corrige el documento de la línea pulsada, no el primero");
        ConsultasDeLista(mediador).Should().Be(consultasDeListaAntes, "pulsar no recarga");
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull("ni abre la vista rápida de la fila");
    }

    /// <summary>
    /// La corrección no recibe el Vehículo: le basta el Id del documento. El formulario lee de él a quién
    /// pertenece (ámbito y propietario, que enseña en solo lectura) y guarda con el comando de renovación, que
    /// va por Id de documento. Al guardar, la página se relee en sitio y lo marcado sigue marcado.
    /// </summary>
    [Fact]
    public async Task La_correccion_de_un_documento_de_Vehiculo_ensena_su_propietario_y_al_guardar_renueva_ese_documento_y_relee_la_pagina()
    {
        var vencida = IncidenciaDe("ITV", EstadoDocumento.Vencido, -12);
        var version = Guid.NewGuid();
        var mediador = new MediadorFalso
        {
            Almacen = { Vehiculo("Camión grúa", EstadoDocumento.Vencido, vencida), Vehiculo("Furgoneta") },
            Documento = new DocumentoDetalleDto(
                vencida.DocumentoId, AmbitoAplicacion.Vehiculo, "Camión grúa (9012GHI)", "ITV",
                TipoDocumentoAplicaVencimientoAutomatico: true, DiaDeNegocio.Hoy().AddYears(-1), vencida.FechaVencimiento,
                EstadoVigenciaDocumento.VenceEnFecha, ArchivoUrl: null, Comentarios: null, null, null, null, null,
                version, PerfilDocumentoOficial.Ninguno, EmpresaId: null)
        };
        var cut = Renderizar(mediador);
        await AlternarSeleccionMultiple(cut);
        await MarcarAsync(cut, "Furgoneta");
        var pregunta = UltimaConsulta(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        await CeldaDeEstado(cut).QuerySelector("button.ventana-contexto-elemento")!.ClickAsync(new MouseEventArgs());

        var formulario = cut.FindComponent<DrawerGestionDocumento>();
        formulario.Markup.Should().Contain("Renovar documento", "con Documento se renueva ese, no se da de alta otro");
        formulario.FindAll(".campo").Select(c => c.TextContent).Should()
            .Contain(t => t.Contains("Ámbito") && t.Contains(nameof(AmbitoAplicacion.Vehiculo)))
            .And.Contain(t => t.Contains("Propietario") && t.Contains("Camión grúa (9012GHI)"))
            .And.Contain(t => t.Contains("Tipo de documento") && t.Contains("ITV"));
        mediador.Enviadas.OfType<ObtenerVehiculosParaSelectorQuery>().Should().BeEmpty(
            "en renovación el propietario viene del documento: no se carga el catálogo de Vehículos para elegirlo");

        await formulario.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        var renovacion = mediador.Enviadas.OfType<RenovarDocumentoCommand>().Should().ContainSingle().Subject;
        renovacion.Id.Should().Be(vencida.DocumentoId);
        renovacion.Version.Should().Be(version);
        cut.WaitForAssertion(() => ConsultasDeLista(mediador).Should().Be(consultasAntes + 1,
            "el estado y el motivo de la fila cambian al corregir: la página se relee en sitio"));
        UltimaConsulta(mediador).Should().Be(pregunta, "con los mismos filtros, orden y página");
        cut.WaitForAssertion(() => CasillaDe(cut, "Furgoneta").HasAttribute("checked").Should().BeTrue(
            "corregir un documento no es cambiar de lista: lo marcado sigue marcado"));
    }

    [Fact]
    public async Task Tras_corregir_se_vuelve_a_pedir_la_misma_pagina_y_la_seleccion_se_conserva()
    {
        var mediador = new MediadorFalso { Almacen = { Vehiculo("Camión grúa"), Vehiculo("Furgoneta") } };
        var cut = Renderizar(mediador, "vehiculos?estado=Vigente");
        await AlternarSeleccionMultiple(cut);
        await MarcarAsync(cut, "Camión grúa");
        var antes = UltimaConsulta(mediador);
        var consultasDeListaAntes = ConsultasDeLista(mediador);

        await CorregirAsync(cut);

        ConsultasDeLista(mediador).Should().Be(consultasDeListaAntes + 1,
            "el estado y el desglose de la fila cambian al corregir: la página se relee en sitio");
        UltimaConsulta(mediador).Should().Be(antes, "con los mismos filtros, orden y página");
        cut.WaitForAssertion(() => CasillaDe(cut, "Camión grúa").HasAttribute("checked").Should().BeTrue(
            "corregir un documento no es cambiar de lista: lo marcado sigue marcado"));
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");
    }

    /// <summary>
    /// Conservar la selección vale para el refresco tras corregir y para nada más: una recarga posterior que
    /// repite la misma pregunta sin venir de una corrección (aquí, la del alta de otro Vehículo) la suelta.
    /// </summary>
    [Fact]
    public async Task La_seleccion_conservada_tras_corregir_no_sobrevive_a_una_recarga_posterior_con_la_misma_pregunta()
    {
        var mediador = new MediadorFalso
        {
            AltaSinElegirEmpleador = true,
            Almacen = { Vehiculo("Camión grúa"), Vehiculo("Furgoneta") }
        };
        var cut = Renderizar(mediador);
        await AlternarSeleccionMultiple(cut);
        await MarcarAsync(cut, "Camión grúa");
        var pregunta = UltimaConsulta(mediador);

        await CorregirAsync(cut);
        cut.WaitForAssertion(() => CasillaDe(cut, "Camión grúa").HasAttribute("checked").Should().BeTrue(
            "control positivo: la recarga tras corregir conserva lo marcado"));
        var consultasTrasCorregir = ConsultasDeLista(mediador);

        // Segunda recarga, con la misma pregunta, que no viene de una corrección: el alta de otro Vehículo.
        await cut.FindAll(".cabecera-pagina button").Single(b => b.TextContent.Trim() == "+ Nuevo vehículo").ClickAsync(new MouseEventArgs());
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CrearVehiculoCommand>().Should().ContainSingle("control: el alta se guardó")
            .Which.EmpresaId.Should().Be(EmpresaPropia);
        ConsultasDeLista(mediador).Should().BeGreaterThan(consultasTrasCorregir, "control: el alta recargó la lista");
        UltimaConsulta(mediador).Should().Be(pregunta, "control: con la misma pregunta que el refresco tras corregir");
        cut.WaitForAssertion(() => cut.FindAll(".barra-acciones-lote").Should().BeEmpty(
            "una recarga que no viene de corregir limpia la selección aunque repita la pregunta"));
        CasillaDe(cut, "Camión grúa").HasAttribute("checked").Should().BeFalse();
    }

    /// <summary>
    /// Si la corrección saca la fila corregida del filtro activo, el total cambia y QuickGrid vuelve a pedir la
    /// misma página por su cuenta: esa segunda petición es el mismo refresco, y lo marcado en las demás filas
    /// sigue marcado.
    /// </summary>
    [Fact]
    public async Task Tras_corregir_la_seleccion_se_conserva_aunque_la_fila_corregida_salga_del_filtro_y_cambie_el_total()
    {
        var grua = Vehiculo("Camión grúa", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -10));
        var mediador = new MediadorFalso
        {
            Almacen = { grua, Vehiculo("Furgoneta", EstadoDocumento.Vencido, IncidenciaDe("ITV", EstadoDocumento.Vencido, -4)) }
        };
        var cut = Renderizar(mediador, "vehiculos?estado=Vencido");
        Filas(cut).Should().HaveCount(2, "punto de partida: las dos filas están en el filtro");
        await AlternarSeleccionMultiple(cut);
        await MarcarAsync(cut, "Furgoneta");
        var pregunta = UltimaConsulta(mediador);
        var consultasAntes = ConsultasDeLista(mediador);

        // Lo que deja la corrección: la ITV vencida del camión se renovó y el camión ya no está «Vencido».
        mediador.Almacen[mediador.Almacen.FindIndex(v => v.Id == grua.Id)] =
            grua with { EstadoDocumental = EstadoDocumento.Vigente, Incidencias = [] };
        await CorregirAsync(cut);

        cut.WaitForAssertion(() => Nombres(cut).Should().Equal("Furgoneta"));
        var peticiones = mediador.Enviadas.OfType<ObtenerVehiculosQuery>().Skip(consultasAntes).ToList();
        peticiones.Should().HaveCount(2, "control del escenario: al cambiar el total, QuickGrid pide la página una segunda vez");
        peticiones.Should().OnlyContain(q => q == pregunta);
        CasillaDe(cut, "Furgoneta").HasAttribute("checked").Should().BeTrue(
            "la segunda petición es el mismo refresco: lo marcado sigue marcado");
        cut.Find(".barra-acciones-lote-cantidad").TextContent.Trim().Should().Be("1 seleccionado en esta página");
    }

    /// <summary>
    /// La fila que se refresca en sitio tras guardar en la vista rápida (consulta por id, ver
    /// VehiculosFilaSeRefrescaTests) pide también el desglose y pinta el que vuelve: no pierde el motivo.
    /// </summary>
    [Fact]
    public async Task La_fila_refrescada_tras_guardar_en_la_vista_rapida_conserva_el_motivo()
    {
        var grua = Vehiculo("Camión grúa", EstadoDocumento.Vencido,
            IncidenciaDe("ITV", EstadoDocumento.Vencido, -12),
            IncidenciaDe("Permiso de circulación", EstadoDocumento.SinConfirmar));
        var mediador = new MediadorFalso { Almacen = { grua, Vehiculo("Furgoneta") } };
        var cut = Renderizar(mediador);
        Motivo(cut).Should().Be("1 vencido · 1 sin confirmar", "punto de partida");

        // Lo que deja el guardado: el vencido se renovó y queda solo el sin confirmar.
        mediador.Almacen[mediador.Almacen.FindIndex(v => v.Id == grua.Id)] = grua with
        {
            EstadoDocumental = EstadoDocumento.SinConfirmar,
            Incidencias = [IncidenciaDe("Permiso de circulación", EstadoDocumento.SinConfirmar)]
        };
        await cut.InvokeAsync(() =>
            Services.GetRequiredService<ContextWorkspaceService>().NotificarEntidadGuardada(EntidadWorkspace.Vehiculo, grua.Id));

        cut.WaitForAssertion(() => Motivo(cut).Should().Be("Permiso de circulación", "la fila refrescada pinta el motivo que vuelve"));
        CeldaDeEstado(cut).QuerySelector(".badge")!.TextContent.Trim().Should().Be("Sin confirmar");
        UltimaConsulta(mediador).VehiculoId.Should().Be(grua.Id, "control: fue la consulta por id, no una recarga de página");
        UltimaConsulta(mediador).ConDesgloseDocumental.Should().BeTrue("sin pedirlo, la fila refrescada volvería sin motivo");
    }
}
