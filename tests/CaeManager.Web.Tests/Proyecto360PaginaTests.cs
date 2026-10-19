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
using CaeManager.Application.Proyectos.Queries.ObtenerEmpresasProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVehiculosProyecto;
using CaeManager.Application.Proyectos.Queries.ObtenerVisitasProyecto;
using CaeManager.Application.Proyectos.Commands.RequisitosEvento;
using CaeManager.Application.Proyectos.Queries.RequisitosEvento;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Proyectos;
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
        JSInterop.SetupModule("./js/cajas-ficha.js").Setup<double[]>("medirAlturas", _ => true).SetResult([]);
        Services.AddLocalization();
        ComponentFactories.AddStub<PestanaDocumentacion>();
        ComponentFactories.AddStub<PestanaHistorial>();
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
        Notas: "Acceso por muelle 3.", TecnicosActivos: 1, DocumentosGestionados: 3, Version: VersionAbierto, NotaInternaVisible: true);

    private static readonly ProyectoDetalleDto Cerrado = new(
        CerradoId, ClienteId, "Umbrella Corporation Ibérica S.A.", CentroId, "Sede Sevilla", "Sustitución de red contra incendios",
        new DateOnly(2026, 1, 9), null, new DateOnly(2026, 3, 4), EstaAbierto: false,
        Notas: null, TecnicosActivos: 1, DocumentosGestionados: 0, Version: Guid.NewGuid(), NotaInternaVisible: true);

    /// <summary>
    /// Responde por tipo y registra lo enviado. Una petición que case con una
    /// <see cref="Retener"/> no responde hasta que el test la resuelva.
    /// </summary>
    private sealed class MediadorProyecto : IMediator
    {
        public Dictionary<Guid, ProyectoDetalleDto> Detalles { get; } = new() { [AbiertoId] = Abierto, [CerradoId] = Cerrado };
        public Dictionary<Guid, IReadOnlyList<EmpresaProyectoDto>> Empresas { get; } = [];
        public Dictionary<Guid, IReadOnlyList<VisitaProyectoDto>> Visitas { get; } = [];
        public Dictionary<Guid, IReadOnlyList<VehiculoProyectoDto>> Vehiculos { get; } = [];
        public bool FallarDetalle { get; set; }
        public bool SinTecnicos { get; set; }
        public bool FallarTecnicos { get; set; }
        public List<RequisitoEventoDto> Requisitos { get; } = [];
        public List<TipoDocumentoListaDto> TiposDocumento { get; } = [];

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
                ObtenerEmpresasProyectoQuery q => Empresas.GetValueOrDefault(q.ProyectoId) ?? [],
                ObtenerVisitasProyectoQuery q => Visitas.GetValueOrDefault(q.ProyectoId) ?? [],
                ObtenerVehiculosProyectoQuery q => Vehiculos.GetValueOrDefault(q.ProyectoId) ?? [],
                ObtenerTrabajadoresParaSelectorQuery => (IReadOnlyList<TrabajadorSelectorDto>)[new TrabajadorSelectorDto(TrabajadorSelectorId, "Ibáñez Soto, Marta", null, null)],
                AsignarTecnicoProyectoCommand => Result.Exito(Guid.NewGuid()),
                DesasignarTecnicoProyectoCommand => Result.Exito(),
                EliminarProyectoCommand => Result.Exito(),
                RestaurarProyectoCommand => Result.Exito(),
                CerrarProyectoCommand => Result.Exito(),
                ReabrirProyectoCommand => Result.Exito(),
                ActualizarProyectoCommand => Result.Exito(),
                ObtenerRequisitosEventoQuery => (IReadOnlyList<RequisitoEventoDto>)Requisitos.ToList(),
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)TiposDocumento.ToList(),
                AnadirRequisitoEventoCommand => Result.Exito(),
                QuitarRequisitoEventoCommand => Result.Exito(),
                GuardarPlantillaRequisitosEventoCommand => Result.Exito(Guid.NewGuid()),
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
        cut.Find(".breadcrumb, nav[aria-label='Ruta de Proyecto 360']").TextContent.Should().Contain("Proyectos");
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
    }

    [Fact]
    public void El_plazo_de_un_proyecto_cerrado_dice_cuando_se_cerro_y_cuanto_estuvo_abierto()
    {
        var cut = Renderizar(CerradoId, "?pestana=ficha");

        cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain("Cerrado el 04/03/2026").And.Contain("55 días abierto");
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
        sinProyecto.FindAll(".boton-primario").Should().ContainSingle();

        _mediador.FallarDetalle = true;
        var conFallo = Render<ProyectoDetalle>(p => p.Add(x => x.ProyectoId, AbiertoId));
        conFallo.Find(".estado-vacio").TextContent.Should().Be(textoSinProyecto);
    }

    // Decisión del 2026-10-09: el lateral lleva dos o tres cajas como mucho; «Plazo» vive en «Ficha».
    [Fact]
    public void Lateral_informacion_y_notas_sin_el_plazo()
    {
        var cut = Renderizar(AbiertoId);
        var lateral = cut.Find("[data-pieza=lateral]");

        lateral.QuerySelectorAll("[data-pieza=tarjeta] .tarjeta-titulo").Select(t => t.TextContent.Trim())
            .Should().Equal("Información", "Nota interna");
        lateral.TextContent.Should().Contain("Cliente").And.NotContain("Cliente empresarial").And.Contain("Sede Sevilla")
            .And.Contain("Acceso por muelle 3.").And.NotContain("Los días abiertos son la base de la facturación del proyecto.");
        Botones(cut, "[data-pieza=lateral] button").Should().Equal("Editar →", "Editar →");
        cut.FindAll("[data-pieza=cajas-ficha]").Should().BeEmpty("sin pedir la pestaña, la ficha abre en Técnicos");
    }

    [Fact]
    public void La_pestana_ficha_va_la_ultima_llega_por_la_url_y_lleva_el_plazo()
    {
        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        var pestanas = cut.FindAll("[role=tab]");
        pestanas.Should().HaveCount(7);
        pestanas[5].TextContent.Trim().Should().Be("Ficha");
        pestanas[5].GetAttribute("aria-selected").Should().Be("true");
        pestanas[6].TextContent.Trim().Should().Be("Historial");

        var cajas = cut.FindAll("[data-pieza=cajas-ficha] > [data-caja]");
        cajas.Select(c => c.GetAttribute("data-caja")).Should().Equal("datos", "plazo");
        cajas[1].QuerySelector(".tarjeta-titulo")!.TextContent.Trim().Should().Be("Plazo");
        cajas[1].TextContent.Should().Contain("Los días abiertos son la base de la facturación del proyecto.");
        cut.FindAll("[data-pieza=fila]").Should().BeEmpty("la pestaña «Ficha» no pinta la lista de técnicos");
    }

    [Fact]
    public void Pulsar_ficha_la_deja_en_la_url()
    {
        var cut = Renderizar(AbiertoId);

        cut.FindAll("[role=tab]").Single(p => p.TextContent.Trim() == "Ficha").Click();

        Navegacion.Uri.Should().EndWith($"proyectos/{AbiertoId}?pestana=ficha");
    }

    [Theory]
    [InlineData(Roles.Administrador, true)]
    [InlineData(Roles.DireccionCae, true)]
    [InlineData(Roles.CoordinadorCae, true)]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public async Task La_ficha_conserva_los_datos_y_las_acciones_segun_el_rol(string rol, bool puedeEscribir)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        var datos = cut.Find("[data-caja=datos]");
        datos.QuerySelectorAll("dt").Select(e => e.TextContent.Trim()).Should()
            .Equal("Nombre", "Cliente", "Centro", "Estado", "Inicio", "Fin previsto");
        datos.QuerySelectorAll("dd").Select(e => e.TextContent.Trim()).Should()
            .Equal("Reforma nave Sevilla", "Umbrella Corporation Ibérica S.A.", "Sede Sevilla", "Abierto", "12/03/2026", "30/11/2026");
        cut.Find($"a[href='/centros/{CentroId}']").TextContent.Should().Be("Sede Sevilla");
        cut.Find($"a[href='/clientes/{ClienteId}']").TextContent.Should().Be("Umbrella Corporation Ibérica S.A.");
        cut.Find(".proyecto360-meta").TextContent.Should().NotContain("Inicio:").And.NotContain("Fin previsto:");
        cut.FindAll("[data-pieza=anillo]").Should().BeEmpty();

        if (puedeEscribir)
        {
            datos.QuerySelectorAll("button").Select(e => e.TextContent.Trim()).Should().Contain("Editar →");
            Botones(cut).Should().Contain("Asignar técnico");
            (await AbrirMenuAsync(cut)).Should().Equal("Editar proyecto", "Cerrar proyecto…", "Eliminar proyecto");
        }
        else
        {
            datos.QuerySelectorAll("button").Should().BeEmpty();
            Botones(cut).Should().NotContain(["Asignar técnico", "Editar →", "Dar de baja", "Reabrir"]);
            cut.FindAll(".menu-acciones-disparador").Should().BeEmpty();
        }
    }

    [Fact]
    public async Task Editar_desde_la_ficha_solo_abre_el_formulario_sin_enviar_un_comando()
    {
        var cut = Renderizar(AbiertoId, "?pestana=ficha");
        await cut.Find("[data-caja=datos] button").ClickAsync(new MouseEventArgs());

        cut.FindComponents<DrawerFormulario>().Should().Contain(c => c.Instance.Visible);
        _mediador.Enviados.OfType<ActualizarProyectoCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task La_carga_muestra_cinco_filas_sin_una_tarjeta_adicional()
    {
        var respuesta = _mediador.Retener(r => r is ObtenerProyectoPorIdQuery);
        var cut = Renderizar(AbiertoId);

        cut.Find("[aria-busy=true]").QuerySelectorAll(".esqueleto-fila").Should().HaveCount(5);
        cut.FindAll("[data-pieza=tarjeta]").Should().BeEmpty();

        await cut.InvokeAsync(() => respuesta.SetResult(Abierto));
        cut.WaitForAssertion(() => cut.Find(".lista-relaciones-encabezado").TextContent.Trim().Should().Be("1 técnico activo"));
    }

    [Fact]
    public void Sin_notas_lo_dice()
    {
        var cut = Renderizar(CerradoId);
        cut.Find("[data-pieza=lateral] [data-pieza=nota-interna] [data-vacia=true]").TextContent.Should().Be("Sin nota interna.");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void La_nota_interna_se_lee_una_vez_en_el_lateral_y_solo_se_edita_con_escritura(string rol, bool puedeEscribir)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(AbiertoId);

        cut.FindAll("[data-pieza=nota-interna]").Should().ContainSingle();
        var nota = cut.Find("[data-pieza=lateral] [data-pieza=nota-interna]");
        nota.QuerySelector(".tarjeta-titulo")!.TextContent.Should().Be("Nota interna");
        nota.TextContent.Should().Contain("Acceso por muelle 3.").And.Contain("No la ve el cliente.");
        nota.QuerySelectorAll("button").Should().HaveCount(puedeEscribir ? 1 : 0);
        if (puedeEscribir)
        {
            nota.QuerySelector("button")!.Click();
            cut.Find(".drawer-panel").TextContent.Should().Contain("Notas");
        }
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
        pestanas[3].TextContent.Should().Contain("Documentos").And.Contain("3");
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

    private static ProyectoDetalleDto EventoDe(Guid id, DateOnly inicio, DateOnly fin, DateOnly? cierre) => Cerrado with
    {
        Id = id,
        Tipo = TipoProyecto.Evento,
        FechaInicio = inicio,
        FechaFinPrevista = fin,
        FechaCierreReal = cierre,
        EstaAbierto = cierre is null,
    };

    /// <summary>
    /// Un evento cerrado por el proceso programado al pasar su fin (decisión 2026-10-16) se reabre con una fecha de fin
    /// nueva, de hoy o posterior, pedida en el mismo diálogo: sin ella no se envía nada.
    /// </summary>
    [Fact]
    public async Task Reabrir_un_evento_terminado_pide_la_nueva_fecha_de_fin_y_la_envia()
    {
        var hoy = DiaDeNegocio.Hoy();
        var eventoId = Guid.NewGuid();
        _mediador.Detalles[eventoId] = EventoDe(eventoId, hoy.AddDays(-10), hoy.AddDays(-3), hoy.AddDays(-3));
        var cut = Renderizar(eventoId);
        await BotonConTexto(cut, "[data-pieza=cabecera-identidad] button", "Reabrir").ClickAsync(new MouseEventArgs());

        CampoNuevaFechaFin(cut).Instance.Type.Should().Be("date");

        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());
        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty("sin fecha de fin nueva no se envía nada");
        CampoNuevaFechaFin(cut).Instance.MensajeError.Should().Be("Introduce una fecha de fin de hoy o posterior.");

        await CampoNuevaFechaFin(cut).Find("input").InputAsync(new ChangeEventArgs { Value = hoy.AddDays(-1).ToString("yyyy-MM-dd") });
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());
        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().BeEmpty("una fecha pasada tampoco vale");

        var nuevoFin = hoy.AddDays(5);
        await CampoNuevaFechaFin(cut).Find("input").InputAsync(new ChangeEventArgs { Value = nuevoFin.ToString("yyyy-MM-dd") });
        CampoNuevaFechaFin(cut).Instance.MensajeError.Should().BeNull("escribir limpia el aviso");
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.Should().Be(new ReabrirProyectoCommand(eventoId, NuevaFechaFinPrevista: nuevoFin));
    }

    [Fact]
    public async Task Reabrir_una_obra_cerrada_no_pide_fecha_de_fin()
    {
        var cut = Renderizar(CerradoId);
        await BotonConTexto(cut, "[data-pieza=cabecera-identidad] button", "Reabrir").ClickAsync(new MouseEventArgs());

        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Etiqueta == "Nueva fecha de fin");
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.NuevaFechaFinPrevista.Should().BeNull();
    }

    [Fact]
    public async Task Reabrir_un_evento_cerrado_a_mano_antes_de_su_fin_no_pide_fecha_de_fin()
    {
        var hoy = DiaDeNegocio.Hoy();
        var eventoId = Guid.NewGuid();
        _mediador.Detalles[eventoId] = EventoDe(eventoId, hoy.AddDays(-10), hoy.AddDays(4), hoy.AddDays(-1));
        var cut = Renderizar(eventoId);
        await BotonConTexto(cut, "[data-pieza=cabecera-identidad] button", "Reabrir").ClickAsync(new MouseEventArgs());

        cut.FindComponents<CampoTexto>().Should().NotContain(c => c.Instance.Etiqueta == "Nueva fecha de fin");
        await BotonConTexto(cut, "[role=dialog] .modal-pie button", "Reabrir proyecto").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<ReabrirProyectoCommand>().Should().ContainSingle()
            .Which.NuevaFechaFinPrevista.Should().BeNull();
    }

    private static IRenderedComponent<CampoTexto> CampoNuevaFechaFin(IRenderedComponent<ProyectoDetalle> cut) =>
        cut.FindComponents<CampoTexto>().Single(c => c.Instance.Etiqueta == "Nueva fecha de fin");

    /// <summary>
    /// Un evento abierto cuyo fin ya pasó (el proceso programado aún no lo ha cerrado) se pinta ya como cerrado en su fin:
    /// «Cerrado el {fin}», sin «fin previsto superado». Una obra en el mismo caso sigue abierta y con retraso.
    /// </summary>
    [Fact]
    public void El_plazo_de_un_evento_abierto_pasado_de_su_fin_dice_cerrado_el_dia_del_fin()
    {
        var hoy = DiaDeNegocio.Hoy();
        var fin = hoy.AddDays(-3);
        var eventoId = Guid.NewGuid();
        _mediador.Detalles[eventoId] = EventoDe(eventoId, hoy.AddDays(-10), fin, cierre: null);

        var cut = Renderizar(eventoId, "?pestana=ficha");

        cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain($"Cerrado el {fin:dd/MM/yyyy}").And.NotContain("superado");
    }

    [Fact]
    public void El_plazo_de_una_obra_abierta_pasada_de_su_fin_sigue_diciendo_fin_superado()
    {
        var hoy = DiaDeNegocio.Hoy();
        var obraId = Guid.NewGuid();
        _mediador.Detalles[obraId] = EventoDe(obraId, hoy.AddDays(-10), hoy.AddDays(-3), cierre: null) with { Tipo = TipoProyecto.Obra };

        var cut = Renderizar(obraId, "?pestana=ficha");

        cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain("superado hace 3 días").And.NotContain("Cerrado el");
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

    [Theory]
    [InlineData(true, false, "Empresa empleadora directa")]
    [InlineData(false, true, "Subcontrata")]
    [InlineData(true, true, "Empresa empleadora directa · Subcontrata")]
    public async Task Empresas_conserva_ambos_papeles_y_abre_su_ficha_contextual(bool desdeEmpresaDirecta, bool desdeRelacionSubcontrata, string papel)
    {
        var empresaId = Guid.NewGuid();
        _mediador.Empresas[AbiertoId] = [new(empresaId, "Empresa empleadora A", 2, desdeEmpresaDirecta, desdeRelacionSubcontrata)];
        var cut = Renderizar(AbiertoId, "?pestana=empresas&extra=conservar");
        var fila = cut.Find(".lista-relaciones-tarjeta .fila-relacion");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("1 Empresa empleadora");
        cut.FindComponents<FilaRelacion>().Should().ContainSingle();
        var patron = cut.FindComponents<FilaRelacion>().Single().Instance;
        fila.TextContent.Should().Contain("Empresa empleadora A").And.Contain(papel).And.Contain("2 técnicos activos en el Proyecto");
        cut.Markup.Should().Contain("Empresas empleadoras derivadas de los técnicos activos del Proyecto.");
        _mediador.Enviados.OfType<ObtenerEmpresasProyectoQuery>().Should().ContainSingle().Which.ProyectoId.Should().Be(AbiertoId);
        await fila.QuerySelector(".fila-relacion-nombre")!.ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().Be(new WorkspaceFrame(desdeEmpresaDirecta ? EntidadWorkspace.Empresa : EntidadWorkspace.Subcontrata, empresaId, "Empresa empleadora A", "informacion"));
        var consultas = _mediador.Enviados.Count;
        await cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Visitas").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("pestana=visitas").And.Contain("extra=conservar");
        _mediador.Enviados.Should().HaveCount(consultas);
        await cut.InvokeAsync(() => patron.OnAbrir360.InvokeAsync());
        Navegacion.Uri.Should().EndWith($"/{(desdeEmpresaDirecta ? "empresas" : "subcontratas")}/{empresaId}");
    }

    [Fact]
    public void Empresas_no_presenta_fraccion_estado_ni_tinte_documental()
    {
        _mediador.Empresas[AbiertoId] = [new(Guid.NewGuid(), "Empresa empleadora", 1, true, false)];
        var cut = Renderizar(AbiertoId, "?pestana=empresas");
        var fila = cut.Find(".lista-relaciones-tarjeta .fila-relacion");
        fila.TextContent.Should().Contain("1 técnico activo en el Proyecto").And.NotContain("0 de 0").And.NotContain("100");
        var patron = cut.FindComponents<FilaRelacion>().Single().Instance;
        patron.Cifra.Should().BeNull();
        patron.Estado.Should().BeNull();
        patron.Tono.Should().BeNull();
    }

    [Fact]
    public async Task Visitas_muestra_sus_fechas_y_Programar_lleva_el_Centro_sin_crear_relaciones()
    {
        var visitaId = Guid.NewGuid();
        _mediador.Visitas[AbiertoId] = [new(visitaId, new(2026, 10, 4), new(2026, 10, 5), 3)];
        var cut = Renderizar(AbiertoId, "?pestana=visitas");
        var fila = cut.Find(".lista-relaciones-tarjeta .fila-relacion");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("1 visita");
        fila.TextContent.Should().Contain("04/10/2026 – 05/10/2026").And.Contain("3 trabajadores en la Visita");
        fila.QuerySelector("a")!.GetAttribute("href").Should().Be($"/visitas/{visitaId}");
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Programar visita").ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"/visitas?centroId={CentroId}");
        _mediador.Enviados.OfType<ObtenerVisitasProyectoQuery>().Should().ContainSingle().Which.ProyectoId.Should().Be(AbiertoId);
    }

    [Fact]
    public void Visitas_pone_la_nota_y_Programar_visita_al_pie_despues_de_la_lista_como_la_maqueta()
    {
        _mediador.Visitas[AbiertoId] = [new(Guid.NewGuid(), new(2026, 10, 4), new(2026, 10, 5), 3)];
        var cut = Renderizar(AbiertoId, "?pestana=visitas");
        const string nota = "Una visita no pertenece a un proyecto. Estas son las del mismo Centro dentro de las fechas del proyecto.";

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(nota));
        var lista = cut.Markup.IndexOf("lista-relaciones-tarjeta", StringComparison.Ordinal);
        var posNota = cut.Markup.IndexOf(nota, StringComparison.Ordinal);
        var posBoton = cut.Markup.IndexOf("Programar visita", StringComparison.Ordinal);
        lista.Should().BeGreaterThan(-1);
        posNota.Should().BeGreaterThan(lista);
        posBoton.Should().BeGreaterThan(posNota);
    }

    [Fact]
    public void Proyecto_cerrado_conserva_las_visitas_sin_ofrecer_programar()
    {
        var visitaId = Guid.NewGuid();
        _mediador.Visitas[CerradoId] = [new(visitaId, new(2026, 2, 4), new(2026, 2, 5), 3)];
        var cut = Renderizar(CerradoId, "?pestana=visitas");

        cut.WaitForAssertion(() => cut.FindAll("a").Should().Contain(a => a.GetAttribute("href") == $"/visitas/{visitaId}"));
        Botones(cut).Should().NotContain("Programar visita");
    }

    [Fact]
    public void Visitas_sin_resultados_declara_el_periodo_visible()
    {
        var cut = Renderizar(AbiertoId, "?pestana=visitas");
        cut.Markup.Should().Contain("Sin visitas visibles del Centro en este periodo.");
    }

    [Fact]
    public async Task La_respuesta_tardia_de_Empresas_no_pasa_al_siguiente_Proyecto()
    {
        var retenida = _mediador.Retener(q => q is ObtenerEmpresasProyectoQuery { ProyectoId: var id } && id == AbiertoId);
        _mediador.Empresas[CerradoId] = [new(Guid.NewGuid(), "Empresa del segundo Proyecto", 1, true, false)];
        var cut = Renderizar(AbiertoId, "?pestana=empresas");
        cut.Render(p => p.Add(x => x.ProyectoId, CerradoId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Empresa del segundo Proyecto"));
        await cut.InvokeAsync(() => retenida.SetResult((IReadOnlyList<EmpresaProyectoDto>)[new(Guid.NewGuid(), "Respuesta antigua", 1, true, false)]));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Respuesta antigua").And.Contain("Empresa del segundo Proyecto"));
    }

    // ------------------------------------------------------------------ Vehículos, Historial y lo que no existe

    [Fact]
    public void Las_pestanas_incluyen_Vehiculos_y_Historial_con_Ficha_justo_antes_del_Historial()
    {
        var cut = Renderizar(AbiertoId);

        var rotulos = cut.FindAll("[role=tab]").Select(t => t.TextContent.Trim()).ToList();
        rotulos.Should().HaveCount(7);
        rotulos[0].Should().StartWith("Técnicos");
        rotulos[1].Should().Be("Empresas");
        rotulos[2].Should().Be("Vehículos");
        rotulos[3].Should().StartWith("Documentos");
        rotulos.Skip(4).Should().Equal("Visitas", "Ficha", "Historial");
    }

    [Fact]
    public async Task Vehiculos_muestra_los_de_las_Empresas_participantes_con_su_dueno_y_abre_panel_y_ficha()
    {
        var vehiculoId = Guid.NewGuid();
        _mediador.Vehiculos[AbiertoId] = [new(vehiculoId, "Furgoneta 1", "Ford Transit", "1234 ABC", Guid.NewGuid(), "Limpiezas Raccoon S.L.")];
        var cut = Renderizar(AbiertoId, "?pestana=vehiculos");

        var fila = cut.Find(".lista-relaciones-tarjeta .fila-relacion");
        cut.Find(".lista-relaciones-encabezado").TextContent.Should().Contain("1 vehículo");
        fila.TextContent.Should().Contain("Furgoneta 1").And.Contain("1234 ABC").And.Contain("Ford Transit").And.Contain("Limpiezas Raccoon S.L.");
        cut.Markup.Should().Contain("Un vehículo no se asigna a un Proyecto");
        _mediador.Enviados.OfType<ObtenerVehiculosProyectoQuery>().Should().ContainSingle().Which.ProyectoId.Should().Be(AbiertoId);
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(t => t.Contains("Asignar vehículo"),
            "no hay relación Vehículo-Proyecto que escribir");

        await fila.QuerySelector(".fila-relacion-nombre")!.ClickAsync(new MouseEventArgs());
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual
            .Should().Be(new WorkspaceFrame(EntidadWorkspace.Vehiculo, vehiculoId, "Furgoneta 1", "informacion"));

        var patron = cut.FindComponents<FilaRelacion>().Single().Instance;
        patron.Cifra.Should().BeNull("no se inventa una fracción documental del vehículo");
        patron.Estado.Should().BeNull();
        await cut.InvokeAsync(() => patron.OnAbrir360.InvokeAsync());
        Navegacion.Uri.Should().EndWith($"/vehiculos/{vehiculoId}");
    }

    [Fact]
    public void Vehiculos_sin_resultados_lo_declara_y_con_el_Proyecto_cerrado_sigue_en_solo_lectura()
    {
        var cut = Renderizar(CerradoId, "?pestana=vehiculos");

        cut.Markup.Should().Contain("Sin vehículos visibles de las Empresas empleadoras con técnicos activos.");
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(t => t.Contains("Asignar vehículo"));
    }

    [Fact]
    public void Vehiculos_con_la_lectura_fallida_muestra_el_error_sin_romper_las_otras_pestanas()
    {
        _mediador.QueLanzan.Add(typeof(ObtenerVehiculosProyectoQuery));
        var cut = Renderizar(AbiertoId, "?pestana=vehiculos");

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("No se han podido cargar los vehículos de las Empresas empleadoras."));
        cut.FindAll("[role=tab]").Single(t => t.TextContent.Trim() == "Empresas").Click();
        cut.Markup.Should().NotContain("No se han podido cargar las Empresas empleadoras.");
    }

    [Fact]
    public async Task La_respuesta_tardia_de_Vehiculos_no_pasa_al_siguiente_Proyecto()
    {
        var retenida = _mediador.Retener(q => q is ObtenerVehiculosProyectoQuery { ProyectoId: var id } && id == AbiertoId);
        _mediador.Vehiculos[CerradoId] = [new(Guid.NewGuid(), "Vehículo del segundo Proyecto", "Modelo", "1111 AAA", Guid.NewGuid(), "Empresa B")];
        var cut = Renderizar(AbiertoId, "?pestana=vehiculos");
        cut.Render(p => p.Add(x => x.ProyectoId, CerradoId));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Vehículo del segundo Proyecto"));
        await cut.InvokeAsync(() => retenida.SetResult(
            (IReadOnlyList<VehiculoProyectoDto>)[new(Guid.NewGuid(), "Respuesta antigua", "Modelo", "2222 BBB", Guid.NewGuid(), "Empresa A")]));
        cut.WaitForAssertion(() => cut.Markup.Should().NotContain("Respuesta antigua").And.Contain("Vehículo del segundo Proyecto"));
    }

    [Fact]
    public void Historial_es_la_auditoria_del_propio_Proyecto_y_llega_por_la_url()
    {
        var cut = Renderizar(AbiertoId, "?pestana=historial");

        var historial = cut.FindComponent<Stub<PestanaHistorial>>().Instance.Parameters;
        historial.Get(x => x.EntidadTipo).Should().Be("Proyecto");
        historial.Get(x => x.EntidadId).Should().Be(AbiertoId);
        cut.FindAll("[role=tab]").Single(t => t.GetAttribute("aria-selected") == "true").TextContent.Trim().Should().Be("Historial");
        cut.FindAll("[data-pieza=fila]").Should().BeEmpty("el historial no pinta la lista de técnicos ni inventa eventos propios");
    }

    [Fact]
    public void Pulsar_historial_lo_deja_en_la_url()
    {
        var cut = Renderizar(AbiertoId);

        cut.FindAll("[role=tab]").Single(p => p.TextContent.Trim() == "Historial").Click();

        Navegacion.Uri.Should().EndWith($"proyectos/{AbiertoId}?pestana=historial");
    }

    /// <summary>
    /// Decisión del propietario (2026-10-16): el Proyecto no tiene porcentaje global; solo cada Empresa dentro de él.
    /// El único porcentaje de la ficha es el del plazo transcurrido, que es otra medida y vive en la caja «Plazo».
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("?pestana=empresas")]
    [InlineData("?pestana=vehiculos")]
    [InlineData("?pestana=documentos")]
    [InlineData("?pestana=visitas")]
    [InlineData("?pestana=ficha")]
    public void El_Proyecto_no_muestra_porcentaje_global_ni_anillo_de_cumplimiento(string consulta)
    {
        _mediador.Empresas[AbiertoId] = [new(Guid.NewGuid(), "Empresa empleadora A", 2, true, false)];
        _mediador.Vehiculos[AbiertoId] = [new(Guid.NewGuid(), "Furgoneta 1", "Ford Transit", "1234 ABC", Guid.NewGuid(), "Empresa A")];
        _mediador.Visitas[AbiertoId] = [new(Guid.NewGuid(), new(2026, 10, 4), new(2026, 10, 5), 3)];
        var cut = Renderizar(AbiertoId, consulta);

        cut.FindAll("[data-pieza=anillo], .anillo").Should().BeEmpty();
        cut.Markup.Should().NotContain("de cumplimiento").And.NotContain("Cumplimiento del proyecto");
        if (consulta == "?pestana=ficha")
        {
            cut.Find("[data-caja=datos]").TextContent.Should().NotContain("%");
            cut.FindAll("[data-caja=plazo] .proyecto360-plazo-barra").Should().ContainSingle("solo el plazo mide un porcentaje");
        }
        else
            cut.Markup.Should().NotContain("%", "fuera de la caja «Plazo» de la ficha no hay ningún porcentaje");
    }

    /// <summary>
    /// Figuras del Proyecto y adhesión al plan de seguridad no tienen dato en el modelo (sin esquema): la ficha no
    /// las pinta ni con valores inventados. Se retiran de aquí cuando exista su dato.
    /// </summary>
    [Fact]
    public void La_ficha_no_pinta_figuras_ni_adhesion_sin_dato_que_las_respalde()
    {
        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        cut.FindAll("[data-pieza=cajas-ficha] > [data-caja]").Select(c => c.GetAttribute("data-caja")).Should().Equal("datos", "plazo");
        cut.Markup.Should().NotContain("Figuras del proyecto").And.NotContain("Recurso preventivo")
            .And.NotContain("Jefe de obra").And.NotContain("Adhesión");
    }

    // ------------------------------------------------------------------ pestañas y carrera de cargas

    [Fact]
    public void La_pestana_documentos_llega_por_la_url_y_es_la_del_ambito_proyecto()
    {
        var cut = Renderizar(AbiertoId, "?pestana=documentos");

        var documentacion = cut.FindComponent<Stub<PestanaDocumentacion>>().Instance.Parameters;
        documentacion.Get(x => x.Ambito).Should().Be(AmbitoAplicacion.Proyecto);
        documentacion.Get(x => x.PropietarioId).Should().Be(AbiertoId);
        documentacion.Get(x => x.EnPagina).Should().BeTrue();
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
        PlazoProyecto.DiasAbiertos(TipoProyecto.Obra, DateOnly.Parse(inicio), null, cierre is null ? null : DateOnly.Parse(cierre), DateOnly.Parse(hoy))
            .Should().Be(esperado);

    [Fact]
    public void Dias_abiertos_sin_valor_si_no_ha_empezado() =>
        PlazoProyecto.DiasAbiertos(TipoProyecto.Obra, new DateOnly(2026, 12, 1), null, null, new DateOnly(2026, 10, 8)).Should().BeNull();

    /// <summary>
    /// La obra sin cerrar sigue abierta aunque haya pasado su fin previsto: sus días llegan a hoy, no al fin
    /// (decisión 2026-10-16: la ventana acotada es solo del evento).
    /// </summary>
    [Fact]
    public void Una_obra_pasada_de_su_fin_previsto_sin_cerrar_sigue_contando_hasta_hoy() =>
        PlazoProyecto.DiasAbiertos(TipoProyecto.Obra, new DateOnly(2026, 3, 12), new DateOnly(2026, 5, 1), null, new DateOnly(2026, 10, 8))
            .Should().Be(211);

    /// <summary>
    /// Un evento cuenta solo entre sus fechas (decisión 2026-10-16, «Centro temporal»): del inicio al fin
    /// previsto, o al cierre real si llega antes, o a hoy mientras está en curso. Cuenta inclusiva.
    /// </summary>
    [Theory]
    [InlineData("2026-10-20", "2026-10-25", null, "2026-10-08", null)]         // futuro: sin valor
    [InlineData("2026-10-01", "2026-10-20", null, "2026-10-08", 8)]            // en curso: hasta hoy
    [InlineData("2026-10-01", "2026-10-20", null, "2026-10-01", 1)]            // el día de inicio ya cuenta
    [InlineData("2026-10-01", "2026-10-20", null, "2026-10-20", 20)]           // el último día todavía está en curso
    [InlineData("2026-03-12", "2026-03-20", null, "2026-10-08", 9)]            // terminado sin cierre: se queda en el fin
    [InlineData("2026-03-12", "2026-03-20", "2026-03-15", "2026-10-08", 4)]    // cerrado antes del fin: hasta el cierre
    [InlineData("2026-03-12", "2026-03-20", "2026-04-01", "2026-10-08", 9)]    // cerrado tarde: el evento ya había terminado
    [InlineData("2026-03-12", "2026-03-20", "2026-03-20", "2026-10-08", 9)]    // cerrado el mismo día del fin
    public void Dias_abiertos_de_un_evento_solo_cuentan_entre_sus_fechas(
        string inicio, string fin, string? cierre, string hoy, int? esperado) =>
        PlazoProyecto.DiasAbiertos(TipoProyecto.Evento, DateOnly.Parse(inicio), DateOnly.Parse(fin),
                cierre is null ? null : DateOnly.Parse(cierre), DateOnly.Parse(hoy))
            .Should().Be(esperado);

    /// <summary>El porcentaje del plazo de un evento terminado y sin cerrar se queda en 100 y el de uno en curso avanza.</summary>
    [Theory]
    [InlineData("2026-10-01", "2026-10-20", "2026-10-10", 50)]   // 10 de 20 días
    [InlineData("2026-10-01", "2026-10-20", "2026-12-31", 100)]  // terminado sin cerrar
    public void El_porcentaje_del_plazo_de_un_evento_sin_cerrar_llega_a_100_y_no_lo_supera(string inicio, string fin, string hoy, int esperado) =>
        PlazoProyecto.PorcentajeDelPlazo(DateOnly.Parse(inicio), DateOnly.Parse(fin), null, DateOnly.Parse(hoy))
            .Should().Be(esperado);

    [Fact]
    public void El_plazo_de_un_evento_terminado_y_sin_cerrar_en_la_ficha_se_queda_en_la_duracion_del_evento()
    {
        var hoy = DiaDeNegocio.Hoy();
        _mediador.Detalles[AbiertoId] = Abierto with
        {
            Tipo = TipoProyecto.Evento,
            FechaInicio = hoy.AddDays(-20),
            FechaFinPrevista = hoy.AddDays(-12),
            FechaCierreReal = null,
        };

        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        cut.WaitForAssertion(() => cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain("9 días abierto", "del inicio al fin del evento, no hasta hoy (21 días)").And.NotContain("21 días"));
    }

    [Fact]
    public void El_plazo_de_un_evento_cerrado_antes_de_su_fin_cuenta_hasta_el_cierre_y_no_lleva_la_nota_de_facturacion()
    {
        var hoy = DiaDeNegocio.Hoy();
        _mediador.Detalles[AbiertoId] = Abierto with
        {
            Tipo = TipoProyecto.Evento,
            EstaAbierto = false,
            FechaInicio = hoy.AddDays(-20),
            FechaFinPrevista = hoy.AddDays(-12),
            FechaCierreReal = hoy.AddDays(-17),
        };

        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        cut.WaitForAssertion(() => cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain("4 días abierto", "del inicio al cierre, que llegó antes que el fin previsto")
            .And.NotContain("facturación", "un evento no se factura"));
    }

    [Fact]
    public void El_plazo_de_un_evento_futuro_dice_que_todavia_no_ha_empezado()
    {
        var hoy = DiaDeNegocio.Hoy();
        _mediador.Detalles[AbiertoId] = Abierto with
        {
            Tipo = TipoProyecto.Evento,
            FechaInicio = hoy.AddDays(10),
            FechaFinPrevista = hoy.AddDays(12),
            FechaCierreReal = null,
        };

        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        cut.WaitForAssertion(() => cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain("Todavía no ha empezado"));
    }

    [Fact]
    public void Una_obra_pasada_de_su_fin_previsto_sigue_sumando_dias_en_la_ficha()
    {
        var hoy = DiaDeNegocio.Hoy();
        _mediador.Detalles[AbiertoId] = Abierto with
        {
            FechaInicio = hoy.AddDays(-20),
            FechaFinPrevista = hoy.AddDays(-12),
            FechaCierreReal = null,
        };

        var cut = Renderizar(AbiertoId, "?pestana=ficha");

        cut.WaitForAssertion(() => cut.Find("[data-pieza=cajas-ficha] [data-caja=plazo]").TextContent
            .Should().Contain("21 días abierto").And.Contain("Los días abiertos son la base de la facturación del proyecto."));
    }

    [Theory]
    [InlineData("2026-03-12", "2026-11-30", null, "2026-10-08", 80)]
    [InlineData("2026-03-12", "2026-11-30", null, "2027-01-15", 100)]  // pasado el fin previsto no supera 100
    [InlineData("2026-03-12", null, null, "2026-10-08", null)]         // sin fin previsto no hay porcentaje
    [InlineData("2026-12-01", "2026-12-31", null, "2026-10-08", null)] // sin empezar
    public void Porcentaje_del_plazo(string inicio, string? fin, string? cierre, string hoy, int? esperado) =>
        PlazoProyecto.PorcentajeDelPlazo(DateOnly.Parse(inicio), fin is null ? null : DateOnly.Parse(fin),
                cierre is null ? null : DateOnly.Parse(cierre), DateOnly.Parse(hoy))
            .Should().Be(esperado);

    // ------------------------------------------------------------------ requisitos de un evento (2026-10-15)

    private static readonly Guid TipoSeguroId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid TipoFormacionId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc");

    private static TipoDocumentoListaDto TipoDocumento(Guid id, string nombre) => new(
        id, nombre, VigenciaMeses: 12, AplicaVencimientoAutomatico: false, Orden: 0, AmbitoAplicacion.Trabajador,
        default, default, null, null, null, null, false, false, false, default, []);

    /// <summary>El Proyecto abierto como evento, con un requisito ya incluido y otro tipo de documento por añadir.</summary>
    private IRenderedComponent<ProyectoDetalle> RenderizarEvento()
    {
        _mediador.Detalles[AbiertoId] = Abierto with { Tipo = TipoProyecto.Evento };
        _mediador.Requisitos.Add(new RequisitoEventoDto(TipoSeguroId, "Seguro de accidentes", AmbitoAplicacion.Trabajador));
        _mediador.TiposDocumento.AddRange([TipoDocumento(TipoSeguroId, "Seguro de accidentes"), TipoDocumento(TipoFormacionId, "Formación de evacuación")]);
        return Renderizar(AbiertoId);
    }

    [Fact]
    public void Un_evento_lleva_su_pastilla_y_sus_requisitos_en_el_lateral()
    {
        var cut = RenderizarEvento();

        cut.WaitForAssertion(() => cut.Find("[data-pieza=lateral]").QuerySelectorAll("[data-pieza=tarjeta] .tarjeta-titulo")
            .Select(t => t.TextContent.Trim()).Should().Equal("Información", "Requisitos del evento", "Nota interna"));
        cut.FindAll("[data-pieza=pastilla]").Select(p => p.TextContent.Trim()).Should().Contain("Evento");
        cut.Find("ul.proyecto360-requisitos").TextContent.Should().Contain("Seguro de accidentes");
        cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Añadir requisito").FindAll("option")
            .Select(o => o.TextContent.Trim()).Should().Equal(["Elige un tipo de documento", "Formación de evacuación"],
                "un tipo ya incluido no se ofrece otra vez");
    }

    [Fact]
    public void Una_obra_no_pide_ni_pinta_requisitos_de_evento()
    {
        var cut = Renderizar(AbiertoId);

        cut.FindAll("ul.proyecto360-requisitos").Should().BeEmpty();
        cut.Markup.Should().NotContain("Requisitos del evento");
        _mediador.Enviados.OfType<ObtenerRequisitosEventoQuery>().Should().BeEmpty();
    }

    [Fact]
    public async Task Anadir_y_quitar_un_requisito_envian_el_evento_y_el_tipo_de_documento()
    {
        var cut = RenderizarEvento();
        cut.WaitForAssertion(() => cut.Find("ul.proyecto360-requisitos"));

        await cut.FindComponents<CampoSelect>().Single(c => c.Instance.Etiqueta == "Añadir requisito").Find("select")
            .ChangeAsync(new ChangeEventArgs { Value = TipoFormacionId.ToString() });
        await BotonConTexto(cut, "[data-pieza=lateral] button", "Añadir").ClickAsync(new MouseEventArgs());
        _mediador.Enviados.OfType<AnadirRequisitoEventoCommand>().Should().ContainSingle()
            .Which.Should().Be(new AnadirRequisitoEventoCommand(AbiertoId, TipoFormacionId));

        await cut.Find("button[aria-label='Quitar Seguro de accidentes de los requisitos']").ClickAsync(new MouseEventArgs());
        _mediador.Enviados.OfType<QuitarRequisitoEventoCommand>().Should().ContainSingle()
            .Which.Should().Be(new QuitarRequisitoEventoCommand(AbiertoId, TipoSeguroId));
    }

    [Fact]
    public async Task Guardar_los_requisitos_como_plantilla_envia_el_evento_y_el_nombre()
    {
        var cut = RenderizarEvento();
        cut.WaitForAssertion(() => cut.Find("ul.proyecto360-requisitos"));

        await EscribirAsync(cut, "Nombre de la plantilla", "Partido estándar");
        await BotonConTexto(cut, "[data-pieza=lateral] button", "Guardar como plantilla").ClickAsync(new MouseEventArgs());

        _mediador.Enviados.OfType<GuardarPlantillaRequisitosEventoCommand>().Should().ContainSingle()
            .Which.Should().Be(new GuardarPlantillaRequisitosEventoCommand(AbiertoId, "Partido estándar"));
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(t => t.Mensaje == "Plantilla guardada.");
    }
}
