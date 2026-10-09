using AngleSharp.Dom;
using Bunit;
using Bunit.TestDoubles;
using CaeManager.Application.Common;
using CaeManager.Application.Proyectos.Commands.ActualizarProyecto;
using CaeManager.Application.Proyectos.Commands.AsignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.CerrarProyecto;
using CaeManager.Application.Proyectos.Commands.DesasignarTecnicoProyecto;
using CaeManager.Application.Proyectos.Commands.EliminarProyecto;
using CaeManager.Application.Proyectos.Commands.ReabrirProyecto;
using CaeManager.Application.Proyectos.Commands.RestaurarProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerProyectoPorId;
using CaeManager.Application.Proyectos.Queries.ObtenerTecnicosProyecto;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Proyectos;
using CaeManager.Web.Features.Proyectos.Pages;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Proyecto 360, primer incremento (<c>/proyectos/{id}</c>): la página conserva las
/// acciones, confirmaciones y permisos del panel de detalle del listado de Proyectos.
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué se ofrece en Abierto, en Cerrado y con el rol
/// Consulta; que no encontrado y fallo se ven igual; el orden y el contenido de la lista
/// de técnicos; qué comandos llegan al mediador y con qué datos; a dónde navega tras
/// eliminar; y que la respuesta tardía de otro proyecto no se pinta.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> el aspecto (el CSS aislado no se aplica en bUnit; lo mide el
/// comparador de fidelidad en E2E), la autorización de los comandos —vive en sus handlers
/// y en <c>AutorizacionEscrituraBehavior</c>—, ni el contenido de la pestaña Documentos,
/// que delega en <c>PestanaDocumentacion</c> (aquí sustituida por un doble).
/// </para>
/// </summary>
public class Proyecto360PaginaTests : BunitContext
{
    public Proyecto360PaginaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        ComponentFactories.AddStub<PestanaDocumentacion>();
        ComponentFactories.AddStub<EnlaceProfundoOtraEmpresa>();
        this.ConRolDeEscritura();
    }

    private static readonly Guid AbiertoId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid CerradoId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ClienteId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid CentroId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid TecnicoActivoId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid TecnicoDeBajaId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid TrabajadorActivoId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid TrabajadorSelectorId = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly Guid VersionAbierto = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    private static readonly ProyectoDetalleDto Abierto = new(
        AbiertoId, ClienteId, "Umbrella Corporation Ibérica S.A.", CentroId, "Sede Sevilla", "Reforma nave Sevilla",
        new DateOnly(2026, 3, 12), new DateOnly(2026, 11, 30), null, EstaAbierto: true,
        Notas: "Acceso por muelle 3.", TecnicosActivos: 1, DocumentosGestionados: 3, Version: VersionAbierto);

    private static readonly ProyectoDetalleDto Cerrado = new(
        CerradoId, ClienteId, "Umbrella Corporation Ibérica S.A.", CentroId, "Sede Sevilla", "Sustitución de red contra incendios",
        new DateOnly(2026, 1, 9), null, new DateOnly(2026, 3, 4), EstaAbierto: false,
        Notas: null, TecnicosActivos: 1, DocumentosGestionados: 0, Version: Guid.NewGuid());

    /// <summary>
    /// Responde por tipo y registra lo enviado. Una petición que case con una
    /// <see cref="Retener"/> no responde hasta que el test la resuelva.
    /// </summary>
    private sealed class MediadorProyecto : IMediator
    {
        public Dictionary<Guid, ProyectoDetalleDto> Detalles { get; } = new() { [AbiertoId] = Abierto, [CerradoId] = Cerrado };
        public bool FallarDetalle { get; set; }
        public bool SinTecnicos { get; set; }
        public bool FallarTecnicos { get; set; }

        /// <summary>Comandos que responden con un <see cref="Result"/> fallido, por tipo.</summary>
        public HashSet<Type> Fallidos { get; } = [];

        /// <summary>Comandos que lanzan, por tipo.</summary>
        public HashSet<Type> QueLanzan { get; } = [];

        private static readonly Error Rechazo = Error.Crear("Proyecto.Rechazado", "El servidor lo ha rechazado.");
        public List<object> Enviados { get; } = [];

        private readonly List<(Func<object, bool> Cuando, TaskCompletionSource<object?> Respuesta)> _retenciones = [];
        private readonly HashSet<Guid> _dadosDeBaja = [];

        public TaskCompletionSource<object?> Retener(Func<object, bool> cuando)
        {
            var respuesta = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _retenciones.Add((cuando, respuesta));
            return respuesta;
        }

        private static async Task<TResponse> Esperar<TResponse>(Task<object?> respuesta) => (TResponse)(await respuesta)!;

        // De baja primero a propósito: la página es la que pone los activos delante.
        private IReadOnlyList<TecnicoProyectoDto> Tecnicos() => SinTecnicos ? [] :
        [
            new TecnicoProyectoDto(TecnicoDeBajaId, Guid.NewGuid(), "Duarte, Ana",
                new DateOnly(2026, 6, 9), new DateOnly(2026, 7, 31), EstaActivo: false),
            _dadosDeBaja.Contains(TecnicoActivoId)
                ? new TecnicoProyectoDto(TecnicoActivoId, TrabajadorActivoId, "Salas Moreno, Javier",
                    new DateOnly(2026, 6, 2), DiaDeNegocio.Hoy(), EstaActivo: false)
                : new TecnicoProyectoDto(TecnicoActivoId, TrabajadorActivoId, "Salas Moreno, Javier",
                    new DateOnly(2026, 6, 2), null, EstaActivo: true),
        ];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);

            var retencion = _retenciones.FirstOrDefault(r => r.Cuando(request));
            if (retencion.Respuesta is not null)
            {
                _retenciones.Remove(retencion);
                return Esperar<TResponse>(retencion.Respuesta.Task);
            }

            if (QueLanzan.Contains(request.GetType()))
                throw new InvalidOperationException("fallo simulado del comando");

            if (Fallidos.Contains(request.GetType()))
                return Task.FromResult((TResponse)(object)Result.Fallo(Rechazo));

            if (request is DesasignarTecnicoProyectoCommand baja)
                _dadosDeBaja.Add(baja.Id);

            object? respuesta = request switch
            {
                ObtenerProyectoPorIdQuery when FallarDetalle => throw new InvalidOperationException("fallo simulado de la consulta"),
                ObtenerProyectoPorIdQuery q => Detalles.GetValueOrDefault(q.Id),
                ObtenerTecnicosProyectoQuery when FallarTecnicos => throw new InvalidOperationException("fallo simulado de los técnicos"),
                ObtenerTecnicosProyectoQuery => Tecnicos(),
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new TrabajadorSelectorDto(TrabajadorSelectorId, "Ibáñez Soto, Marta", null, null)],
                AsignarTecnicoProyectoCommand => Result.Exito(Guid.NewGuid()),
                DesasignarTecnicoProyectoCommand => Result.Exito(),
                EliminarProyectoCommand => Result.Exito(),
                RestaurarProyectoCommand => Result.Exito(),
                CerrarProyectoCommand => Result.Exito(),
                ReabrirProyectoCommand => Result.Exito(),
                ActualizarProyectoCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };

            return Task.FromResult((TResponse)respuesta!);
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private readonly MediadorProyecto _mediador = new();

    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private IRenderedComponent<ProyectoDetalle> Renderizar(Guid proyectoId, string consulta = "")
    {
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Navegacion.NavigateTo($"proyectos/{proyectoId}{consulta}");
        return Render<ProyectoDetalle>(p => p.Add(x => x.ProyectoId, proyectoId));
    }

    private static IElement BotonConTexto(IRenderedComponent<ProyectoDetalle> cut, string selector, string texto) =>
        cut.FindAll(selector).Single(b => b.TextContent.Trim() == texto);

    private static List<string> Botones(IRenderedComponent<ProyectoDetalle> cut, string selector = "button") =>
        cut.FindAll(selector).Select(b => b.TextContent.Trim()).ToList();

    private static async Task<List<string>> AbrirMenuAsync(IRenderedComponent<ProyectoDetalle> cut)
    {
        await cut.Find(".menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        return Botones(cut, "[role=menuitem]");
    }

    private static async Task ElegirDelMenuAsync(IRenderedComponent<ProyectoDetalle> cut, string opcion)
    {
        await AbrirMenuAsync(cut);
        await BotonConTexto(cut, "[role=menuitem]", opcion).ClickAsync(new MouseEventArgs());
    }

    private static Task EscribirAsync(IRenderedComponent<ProyectoDetalle> cut, string etiqueta, string valor) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == etiqueta)
            .Find("input").InputAsync(new ChangeEventArgs { Value = valor });

    // ------------------------------------------------------------------ cabecera y estados

    [Fact]
    public void Abierto_con_escritura_pinta_identidad_y_ofrece_asignar_y_el_menu()
    {
        var cut = Renderizar(AbiertoId);

        cut.Find("h1").TextContent.Trim().Should().Be("Reforma nave Sevilla");
        var cabecera = cut.Find("[data-pieza=cabecera-identidad]");
        cabecera.TextContent.Should().Contain("Proyecto").And.Contain("Abierto").And.Contain("12/03/2026").And.Contain("30/11/2026");
        cabecera.QuerySelector($"a[href='/centros/{CentroId}']")!.TextContent.Should().Be("Sede Sevilla");
        cabecera.QuerySelector($"a[href='/clientes/{ClienteId}']")!.TextContent.Should().Be("Umbrella Corporation Ibérica S.A.");
        cut.FindAll("[data-pieza=anillo]").Should().BeEmpty("el primer incremento no lleva anillo: no hay cifra que medir todavía");

        Botones(cut, "[data-pieza=cabecera-identidad] button").Should().Contain("Asignar técnico").And.NotContain("Reabrir");
        cut.Find(".breadcrumb, nav[aria-label='Ruta de Proyecto 360']").TextContent.Should().Contain("Proyectos (obras)");
    }

    [Fact]
    public async Task Abierto_el_menu_ofrece_editar_cerrar_y_eliminar()
    {
        var cut = Renderizar(AbiertoId);
        (await AbrirMenuAsync(cut)).Should().Equal("Editar proyecto", "Cerrar proyecto…", "Eliminar proyecto");
    }

    [Fact]
    public async Task Cerrado_ofrece_reabrir_y_el_menu_pierde_cerrar()
    {
        var cut = Renderizar(CerradoId);

        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().Contain("Cerrado (04/03/2026)");
        Botones(cut, "[data-pieza=cabecera-identidad] button").Should().Contain("Reabrir").And.NotContain("Asignar técnico");
        (await AbrirMenuAsync(cut)).Should().Equal("Editar proyecto", "Eliminar proyecto");
        cut.Find("[data-pieza=lateral]").TextContent.Should().Contain("Cerrado el 04/03/2026").And.Contain("55 días abierto");
    }

    /// <summary>Con el proyecto cerrado las listas quedan en solo lectura, como dibuja el mockup.</summary>
    [Fact]
    public void Cerrado_no_ofrece_dar_de_baja()
    {
        var cut = Renderizar(CerradoId);
        Botones(cut).Should().NotContain("Dar de baja").And.NotContain("+ Asignar técnico");
    }

    [Fact]
    public void Consulta_no_ve_ninguna_accion_de_escritura_y_conserva_la_consulta()
    {
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(AbiertoId);

        Botones(cut).Should().NotContain(["Asignar técnico", "Reabrir", "Editar →", "Dar de baja"]);
        cut.FindAll(".menu-acciones-disparador").Should().BeEmpty("sin escritura no hay menú de acciones");
        cut.FindAll(".boton-360").Should().HaveCount(2, "ir a la ficha del Trabajador no es escritura");
        Botones(cut, ".fila-relacion-nombre-boton").Should().Contain("Salas Moreno, Javier");
    }

    [Fact]
    public void No_encontrado_y_fallo_de_carga_se_ven_igual()
    {
        var sinProyecto = Renderizar(Guid.NewGuid());
        var textoSinProyecto = sinProyecto.Find(".estado-vacio").TextContent;
        textoSinProyecto.Should().Contain("No pudimos cargar este proyecto").And.Contain("Puede que ya no exista o que no tengas acceso.");
        Botones(sinProyecto, ".estado-vacio button").Should().Contain("Reintentar");

        _mediador.FallarDetalle = true;
        var conFallo = Render<ProyectoDetalle>(p => p.Add(x => x.ProyectoId, AbiertoId));
        conFallo.Find(".estado-vacio").TextContent.Should().Be(textoSinProyecto);
    }

    [Fact]
    public void Lateral_informacion_plazo_y_notas()
    {
        var cut = Renderizar(AbiertoId);
        var lateral = cut.Find("[data-pieza=lateral]");

        lateral.QuerySelectorAll("[data-pieza=tarjeta] .tarjeta-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Información", "Plazo", "Notas");
        lateral.TextContent.Should().Contain("Cliente").And.NotContain("Cliente empresarial").And.Contain("Sede Sevilla")
            .And.Contain("Acceso por muelle 3.").And.Contain("Los días abiertos son la base de la facturación del proyecto.");
        Botones(cut, "[data-pieza=lateral] button").Should().Equal("Editar →", "Editar →");
    }

    [Fact]
    public void Sin_notas_lo_dice()
    {
        var cut = Renderizar(CerradoId);
        cut.Find("[data-pieza=lateral]").TextContent.Should().Contain("Sin notas.");
    }

    // ------------------------------------------------------------------ técnicos

    [Fact]
    public void Tecnicos_activos_primero_y_los_de_baja_en_su_grupo()
    {
        var cut = Renderizar(AbiertoId);

        var filas = cut.FindAll("[data-pieza=fila]");
        filas.Select(f => f.QuerySelector(".fila-relacion-nombre")!.TextContent.Trim())
            .Should().Equal("Salas Moreno, Javier", "Duarte, Ana");
        filas[0].TextContent.Should().Contain("alta 02/06/2026").And.Contain("Activo").And.Contain("Dar de baja");
        filas[1].TextContent.Should().Contain("alta 09/06/2026 · baja 31/07/2026").And.Contain("De baja");
        filas[1].QuerySelectorAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Dar de baja");
        cut.Find(".proyecto360-grupo").TextContent.Trim().Should().Be("De baja · 1");

        var pestanas = cut.FindAll("[role=tab]");
        pestanas[0].TextContent.Should().Contain("Técnicos").And.Contain("1");
        pestanas[1].TextContent.Should().Contain("Documentos").And.Contain("3");
    }

    [Fact]
    public void Sin_tecnicos_el_vacio_ofrece_asignar()
    {
        _mediador.SinTecnicos = true;
        var cut = Renderizar(AbiertoId);

        cut.Find(".estado-vacio").TextContent.Should().Contain("Sin técnicos asignados");
        Botones(cut, ".estado-vacio button").Should().Equal("+ Asignar técnico");
    }

    [Fact]
    public async Task El_nombre_abre_el_panel_y_el_boton_360_va_a_la_ficha_del_trabajador()
    {
        var cut = Renderizar(AbiertoId);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        await BotonConTexto(cut, ".fila-relacion-nombre-boton", "Salas Moreno, Javier").ClickAsync(new MouseEventArgs());
        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Trabajador, TrabajadorActivoId, "Salas Moreno, Javier", "informacion"));

        await cut.FindAll(".boton-360")[0].ClickAsync(new MouseEventArgs());
        Navegacion.Uri.Should().EndWith($"/trabajadores/{TrabajadorActivoId}");
    }

    [Fact]
    public async Task Dar_de_baja_pide_confirmacion_y_envia_la_fecha_de_hoy()
    {
        var cut = Renderizar(AbiertoId);

        await BotonConTexto(cut, "[data-pieza=fila] button", "Dar de baja").ClickAsync(new MouseEventArgs());
        _mediador.Enviados.OfType<DesasignarTecnicoProyectoCommand>().Should().BeEmpty("la baja se confirma antes de enviarse");
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Dar de baja a este técnico?")
            .And.Contain("Salas Moreno, Javier").And.Contain("no se puede deshacer");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Dar de baja").ClickAsync(new MouseEventArgs());

        var comando = _mediador.Enviados.OfType<DesasignarTecnicoProyectoCommand>().Should().ContainSingle().Subject;
        comando.Id.Should().Be(TecnicoActivoId);
        comando.FechaBaja.Should().Be(DiaDeNegocio.Hoy());
        cut.Find(".proyecto360-grupo").TextContent.Trim().Should().Be("De baja · 2", "tras la baja se recargan los técnicos");
        cut.FindAll("[role=tab]")[0].TextContent.Should().Contain("0");
    }

    [Fact]
    public async Task Asignar_sin_tecnico_avisa_y_con_tecnico_envia_el_alta_de_hoy()
    {
        var cut = Renderizar(AbiertoId);
        await BotonConTexto(cut, "[data-pieza=cabecera-identidad] button", "Asignar técnico").ClickAsync(new MouseEventArgs());

        cut.Find(".drawer-panel select option").TextContent.Should().Be("Selecciona un técnico…");
        await BotonConTexto(cut, ".drawer-panel button", "Asignar").ClickAsync(new MouseEventArgs());
        cut.Find(".drawer-panel").TextContent.Should().Contain("Selecciona un técnico.");
        _mediador.Enviados.OfType<AsignarTecnicoProyectoCommand>().Should().BeEmpty();

        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = TrabajadorSelectorId.ToString() });
        await BotonConTexto(cut, ".drawer-panel button", "Asignar").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<AsignarTecnicoProyectoCommand>().Should().ContainSingle()
            .Which.Should().Be(new AsignarTecnicoProyectoCommand(AbiertoId, TrabajadorSelectorId, DiaDeNegocio.Hoy()));
        cut.FindAll(".drawer-panel").Should().BeEmpty("asignar cierra el formulario");
        _mediador.Enviados.OfType<ObtenerTecnicosProyectoQuery>().Should().HaveCount(2, "tras asignar se recarga la lista");
    }

    // ------------------------------------------------------------------ editar, cerrar, reabrir, eliminar

    [Fact]
    public async Task Editar_guarda_con_la_version_y_recarga()
    {
        var cut = Renderizar(AbiertoId);
        await ElegirDelMenuAsync(cut, "Editar proyecto");

        cut.FindComponents<CampoTexto>().Select(c => c.Instance.Etiqueta)
            .Should().Equal("Nombre", "Fin previsto (opcional)", "Notas");

        await EscribirAsync(cut, "Nombre", "Reforma nave Sevilla II");
        await EscribirAsync(cut, "Fin previsto (opcional)", "");
        await EscribirAsync(cut, "Notas", "   ");
        await BotonConTexto(cut, ".drawer-panel button", "Guardar").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => _mediador.Enviados.OfType<ActualizarProyectoCommand>().Should().ContainSingle()
            .Which.Should().Be(new ActualizarProyectoCommand(AbiertoId, "Reforma nave Sevilla II", null, null, VersionAbierto)));
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().BeEmpty());
        _mediador.Enviados.OfType<ObtenerProyectoPorIdQuery>().Should().HaveCount(2, "tras guardar se recarga el detalle");
    }

    [Fact]
    public async Task Editar_tambien_desde_el_lateral_y_con_el_proyecto_cerrado()
    {
        var cut = Renderizar(CerradoId);
        await cut.FindAll("[data-pieza=lateral] button").First(b => b.TextContent.Trim() == "Editar →").ClickAsync(new MouseEventArgs());
        cut.Find(".drawer-panel").TextContent.Should().Contain("Editar proyecto");
    }

    [Fact]
    public async Task Cerrar_muestra_el_aviso_de_facturacion_y_envia_la_fecha_de_hoy()
    {
        var cut = Renderizar(AbiertoId);
        await ElegirDelMenuAsync(cut, "Cerrar proyecto…");

        cut.Find("[role=dialog]").TextContent.Should().Contain("dejará de contar como abierto para la facturación por días");
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<CerrarProyectoCommand>().Should().ContainSingle()
            .Which.Should().Be(new CerrarProyectoCommand(AbiertoId, DiaDeNegocio.Hoy()));
    }

    [Fact]
    public async Task Cerrar_con_fecha_vacia_avisa_y_no_envia()
    {
        var cut = Renderizar(AbiertoId);
        await ElegirDelMenuAsync(cut, "Cerrar proyecto…");
        await EscribirAsync(cut, "Fecha de cierre", "");

        cut.WaitForAssertion(() => cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Fecha de cierre").Instance.Valor.Should().BeEmpty());
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Cerrar proyecto").ClickAsync(new MouseEventArgs());

        cut.Find("[role=dialog]").TextContent.Should().Contain("Introduce una fecha de cierre válida.");
        _mediador.Enviados.OfType<CerrarProyectoCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Reabrir_pide_confirmacion_nombrando_la_fecha_de_cierre()
    {
        var cut = Renderizar(CerradoId);
        await BotonConTexto(cut, "[data-pieza=cabecera-identidad] button", "Reabrir").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty();
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Reabrir el proyecto?")
            .And.Contain("Sustitución de red contra incendios").And.Contain("04/03/2026");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());
        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle().Which.Id.Should().Be(CerradoId);
    }

    [Fact]
    public async Task Eliminar_pide_confirmacion_y_vuelve_al_listado()
    {
        var cut = Renderizar(AbiertoId);
        await ElegirDelMenuAsync(cut, "Eliminar proyecto");

        _mediador.Enviados.OfType<EliminarProyectoCommand>().Should().BeEmpty();
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Eliminar este proyecto?").And.Contain("Reforma nave Sevilla");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<EliminarProyectoCommand>().Should().ContainSingle().Which.Id.Should().Be(AbiertoId);
        Navegacion.Uri.Should().EndWith("/proyectos");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t => t.Mensaje == "Proyecto eliminado.");
    }

    /// <summary>El aviso de eliminado ofrece «Deshacer», como el listado; al restaurar se vuelve a la página.</summary>
    [Fact]
    public async Task Eliminar_ofrece_deshacer_y_al_restaurar_vuelve_a_la_pagina_del_proyecto()
    {
        var cut = Renderizar(AbiertoId);
        await ElegirDelMenuAsync(cut, "Eliminar proyecto");
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(t => t.Mensaje == "Proyecto eliminado.");
        aviso.TextoAccion.Should().Be("Deshacer");
        _mediador.Enviados.OfType<RestaurarProyectoCommand>().Should().BeEmpty();

        await cut.InvokeAsync(() => aviso.OnAccion!());

        _mediador.Enviados.OfType<RestaurarProyectoCommand>().Should().ContainSingle().Which.Id.Should().Be(AbiertoId);
        Navegacion.Uri.Should().EndWith($"/proyectos/{AbiertoId}");
    }

    // ------------------------------------------------------------------ pestañas y carrera de cargas

    [Fact]
    public void La_pestana_documentos_llega_por_la_url_y_es_la_del_ambito_proyecto()
    {
        var cut = Renderizar(AbiertoId, "?pestana=documentos");

        var documentacion = cut.FindComponent<Stub<PestanaDocumentacion>>().Instance.Parameters;
        documentacion.Get(x => x.Ambito).Should().Be(AmbitoAplicacion.Proyecto);
        documentacion.Get(x => x.PropietarioId).Should().Be(AbiertoId);
        cut.Find("a[href='/documentos?Ambito=Proyecto']").TextContent.Should().Be("Ir a Documentos →");
        cut.FindAll("[data-pieza=fila]").Should().BeEmpty();
    }

    /// <summary>
    /// Equivalente en la página de la guarda <c>_versionDetalle</c> del panel: se pasa de un
    /// proyecto a otro con la primera consulta todavía en vuelo, y su respuesta llega después.
    /// </summary>
    [Fact]
    public async Task La_respuesta_tardia_de_otro_proyecto_no_se_pinta()
    {
        var tardia = _mediador.Retener(r => r is ObtenerProyectoPorIdQuery q && q.Id == AbiertoId);
        var cut = Renderizar(AbiertoId);

        cut.Render(p => p.Add(x => x.ProyectoId, CerradoId));
        cut.Find("h1").TextContent.Trim().Should().Be("Sustitución de red contra incendios");

        await cut.InvokeAsync(() => tardia.SetResult(Abierto));

        cut.WaitForAssertion(() => cut.Find("h1").TextContent.Trim().Should().Be("Sustitución de red contra incendios"));
        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().NotContain("Reforma nave Sevilla");
    }

    /// <summary>La misma carrera, en la consulta de técnicos: los del proyecto anterior no se pintan en el nuevo.</summary>
    [Fact]
    public async Task Los_tecnicos_tardios_de_otro_proyecto_no_se_pintan()
    {
        var tardios = _mediador.Retener(r => r is ObtenerTecnicosProyectoQuery q && q.ProyectoId == AbiertoId);
        var cut = Renderizar(AbiertoId);

        _mediador.SinTecnicos = true;
        cut.Render(p => p.Add(x => x.ProyectoId, CerradoId));
        cut.FindAll("[data-pieza=fila]").Should().BeEmpty();

        IReadOnlyList<TecnicoProyectoDto> ajenos =
            [new TecnicoProyectoDto(Guid.NewGuid(), Guid.NewGuid(), "Ajeno, Técnico", new DateOnly(2026, 6, 2), null, EstaActivo: true)];
        await cut.InvokeAsync(() => tardios.SetResult(ajenos));

        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Ajeno, Técnico"));
    }

    [Fact]
    public async Task Al_cambiar_de_proyecto_se_cierra_la_confirmacion_que_estaba_abierta()
    {
        var cut = Renderizar(AbiertoId);
        await BotonConTexto(cut, "[data-pieza=fila] button", "Dar de baja").ClickAsync(new MouseEventArgs());
        cut.FindAll("[role=dialog]").Should().ContainSingle();

        cut.Render(p => p.Add(x => x.ProyectoId, CerradoId));

        cut.FindAll("[role=dialog]").Should().BeEmpty("la confirmación era de un técnico del proyecto anterior");
    }

    /// <summary>
    /// Una eliminación que termina cuando ya se está viendo otro proyecto borra el que se pidió,
    /// pero no saca al usuario de la página en la que está ahora.
    /// </summary>
    [Fact]
    public async Task Una_eliminacion_que_termina_tras_cambiar_de_proyecto_no_saca_de_la_pagina_nueva()
    {
        var cut = Renderizar(AbiertoId);
        var enVuelo = _mediador.Retener(r => r is EliminarProyectoCommand);
        await ElegirDelMenuAsync(cut, "Eliminar proyecto");
        var clic = BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        cut.Render(p => p.Add(x => x.ProyectoId, CerradoId));
        var uriAntes = Navegacion.Uri;
        await cut.InvokeAsync(() => enVuelo.SetResult(Result.Exito()));
        await clic;

        _mediador.Enviados.OfType<EliminarProyectoCommand>().Should().ContainSingle().Which.Id.Should().Be(AbiertoId);
        Navegacion.Uri.Should().Be(uriAntes);
        cut.Find("h1").TextContent.Trim().Should().Be("Sustitución de red contra incendios");
    }

    // ------------------------------------------------------------------ caminos de fallo

    [Fact]
    public void Si_fallan_los_tecnicos_el_detalle_sigue_y_la_lista_ofrece_reintentar()
    {
        _mediador.FallarTecnicos = true;
        var cut = Renderizar(AbiertoId);

        cut.Find("h1").TextContent.Trim().Should().Be("Reforma nave Sevilla");
        cut.Markup.Should().Contain("No pudimos cargar los técnicos");
        cut.FindAll("[data-pieza=fila]").Should().BeEmpty();

        _mediador.FallarTecnicos = false;
        BotonConTexto(cut, "button", "Reintentar").Click();
        cut.WaitForAssertion(() => cut.FindAll("[data-pieza=fila]").Should().HaveCount(2));
    }

    [Fact]
    public async Task Eliminar_rechazado_avisa_y_no_sale_de_la_pagina()
    {
        _mediador.Fallidos.Add(typeof(EliminarProyectoCommand));
        var cut = Renderizar(AbiertoId);
        var uriAntes = Navegacion.Uri;
        await ElegirDelMenuAsync(cut, "Eliminar proyecto");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().Be(uriAntes);
        var avisos = Services.GetRequiredService<ToastService>().Mensajes;
        avisos.Should().Contain(t => t.Mensaje == "El servidor lo ha rechazado.");
        avisos.Should().NotContain(t => t.Mensaje == "Proyecto eliminado.");
    }

    [Fact]
    public async Task Eliminar_que_lanza_avisa_y_no_sale_de_la_pagina()
    {
        _mediador.QueLanzan.Add(typeof(EliminarProyectoCommand));
        var cut = Renderizar(AbiertoId);
        var uriAntes = Navegacion.Uri;
        await ElegirDelMenuAsync(cut, "Eliminar proyecto");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Eliminar").ClickAsync(new MouseEventArgs());

        Navegacion.Uri.Should().Be(uriAntes);
        var avisos = Services.GetRequiredService<ToastService>().Mensajes;
        avisos.Should().NotContain(t => t.Mensaje == "Proyecto eliminado.");
        avisos.Should().Contain(t => t.Mensaje == "No pudimos eliminar el proyecto. Intenta nuevamente en unos segundos.");
    }

    [Fact]
    public async Task Dar_de_baja_rechazado_avisa_y_el_tecnico_sigue_activo()
    {
        _mediador.Fallidos.Add(typeof(DesasignarTecnicoProyectoCommand));
        var cut = Renderizar(AbiertoId);
        await BotonConTexto(cut, "[data-pieza=fila] button", "Dar de baja").ClickAsync(new MouseEventArgs());

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Dar de baja").ClickAsync(new MouseEventArgs());

        var avisos = Services.GetRequiredService<ToastService>().Mensajes;
        avisos.Should().Contain(t => t.Mensaje == "El servidor lo ha rechazado.");
        avisos.Should().NotContain(t => t.Mensaje == "Técnico dado de baja del proyecto.");
        cut.Find(".proyecto360-grupo").TextContent.Trim().Should().Be("De baja · 1");
    }

    [Fact]
    public async Task Reabrir_rechazado_avisa_y_el_proyecto_sigue_cerrado()
    {
        _mediador.Fallidos.Add(typeof(ReabrirProyectoCommand));
        var cut = Renderizar(CerradoId);
        await BotonConTexto(cut, "[data-pieza=cabecera-identidad] button", "Reabrir").ClickAsync(new MouseEventArgs());

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());

        var avisos = Services.GetRequiredService<ToastService>().Mensajes;
        avisos.Should().Contain(t => t.Mensaje == "El servidor lo ha rechazado.");
        avisos.Should().NotContain(t => t.Mensaje.StartsWith("Proyecto reabierto"));
        cut.Find("[data-pieza=cabecera-identidad]").TextContent.Should().Contain("Cerrado (04/03/2026)");
    }

    // ------------------------------------------------------------------ plazo

    [Theory]
    [InlineData("2026-03-12", null, "2026-10-08", 211)]          // inclusiva, como la facturación por días
    [InlineData("2026-01-09", "2026-03-04", "2026-10-08", 55)]   // cerrado: hasta el cierre real
    [InlineData("2026-10-08", null, "2026-10-08", 1)]
    public void Dias_abiertos_cuenta_inclusiva(string inicio, string? cierre, string hoy, int esperado) =>
        PlazoProyecto.DiasAbiertos(DateOnly.Parse(inicio), cierre is null ? null : DateOnly.Parse(cierre), DateOnly.Parse(hoy))
            .Should().Be(esperado);

    [Fact]
    public void Dias_abiertos_sin_valor_si_no_ha_empezado() =>
        PlazoProyecto.DiasAbiertos(new DateOnly(2026, 12, 1), null, new DateOnly(2026, 10, 8)).Should().BeNull();

    [Theory]
    [InlineData("2026-03-12", "2026-11-30", null, "2026-10-08", 80)]
    [InlineData("2026-03-12", "2026-11-30", null, "2027-01-15", 100)]  // pasado el fin previsto no supera 100
    [InlineData("2026-03-12", null, null, "2026-10-08", null)]         // sin fin previsto no hay porcentaje
    [InlineData("2026-12-01", "2026-12-31", null, "2026-10-08", null)] // sin empezar
    public void Porcentaje_del_plazo(string inicio, string? fin, string? cierre, string hoy, int? esperado) =>
        PlazoProyecto.PorcentajeDelPlazo(DateOnly.Parse(inicio), fin is null ? null : DateOnly.Parse(fin),
                cierre is null ? null : DateOnly.Parse(cierre), DateOnly.Parse(hoy))
            .Should().Be(esperado);
}
