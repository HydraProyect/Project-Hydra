using Bunit;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Commands.EliminarCentros;
using CaeManager.Application.Centros.Commands.RestaurarCentro;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Domain.Documentos;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Centros;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Centros contra su mockup Gen 2 (Lista Centros TALVEG.dc.html).
///
/// <para>
/// Lo que se comprueba aquí es lo que la PÁGINA pinta. El acordeón de
/// asignaciones se sustituye por un stub: sus enlaces condicionales a Centro
/// 360 son precisamente el defecto que el mockup señala, y el enlace nuevo no
/// puede depender de ellos. Si el test viera el acordeón real, un enlace suyo
/// podría dar verde por el camino equivocado. Que esos enlaces del acordeón no
/// se sumen al de la página —un solo camino por fila— lo mide, con el acordeón
/// real, <see cref="CentrosEnlaceUnicoCentro360Tests"/>.
/// </para>
/// </summary>
public class CentrosListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public CentrosListaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        this.ConRolDeEscritura();
        ComponentFactories.AddStub<AcordeonAsignacionesCentro>();
        // Centros pinta con IStringLocalizer<TextosCentros> (el aviso de «Deshacer» del lote).
        Services.AddLocalization();
    }

    private sealed class MediatorPorTipo : IMediator
    {
        public required IReadOnlyList<CentroListaDto> Centros { get; init; }
        public List<object> Enviadas { get; } = [];
        public int? EliminadosDelLote { get; set; }
        /// <summary>Ids que el lote pide y no elimina: no entran en IdsEliminados.</summary>
        public HashSet<Guid> NoEliminables { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(Registrar(request) switch
            {
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                EliminarCentrosCommand lote => Result.Exito(new ResultadoEliminacionLoteDto(
                    EliminadosDelLote ?? lote.Ids.Count(id => !NoEliminables.Contains(id)),
                    lote.Ids.Where(NoEliminables.Contains).Select(_ => "No se pudo borrar.").ToList(),
                    EliminadosDelLote is null ? lote.Ids.Where(id => !NoEliminables.Contains(id)).ToList() : null)),
                RestaurarCentroCommand => Result.Exito(),
                // Sin documento que devolver: basta para observar qué se pidió abrir.
                ObtenerDocumentoPorIdQuery => null!,
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerClientesAutorizadosQuery => (IReadOnlyList<ClienteAutorizadoDto>)[new ClienteAutorizadoDto(Guid.NewGuid(), "Propia", EsOrigen: true)],
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(
                    Centros, Centros.Count, q.Pagina, q.TamanoPagina),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

        private object Registrar(object peticion)
        {
            Enviadas.Add(peticion);
            return peticion;
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private static CentroListaDto Centro(string nombre, EstadoCentro estado = EstadoCentro.Vigente) => new(
        Guid.NewGuid(), nombre, "C-001", Guid.NewGuid(), "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", estado,
        CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);

    private MediatorPorTipo _mediador = null!;

    private IRenderedComponent<Centros> Renderizar(params CentroListaDto[] centros) =>
        RenderizarConGruposContraidos(centros).AbrirGruposDeCentros();

    /// <summary>Como la ve el usuario al llegar: agrupada por Cliente empresarial y con los grupos contraídos.</summary>
    private IRenderedComponent<Centros> RenderizarConGruposContraidos(params CentroListaDto[] centros)
    {
        _mediador = new MediatorPorTipo { Centros = centros };
        Services.AddScoped<IMediator>(_ => _mediador);
        Services.AddScoped<ITenantActual>(_ => new SeleccionEmpresaGestionadaDePrueba());
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());

        Services.GetRequiredService<NavigationManager>().NavigateTo("centros");

        return Render<Centros>();
    }

    [Fact]
    public void El_titulo_es_la_cabecera_de_pagina_Gen_2()
    {
        var cut = Renderizar(Centro("Centro Logístico Norte"));

        cut.Find("header.cabecera-pagina h1.titulo-pagina").TextContent.Trim().Should().Be("Centros");
    }

    [Fact]
    public async Task Al_expandir_un_centro_aparece_el_camino_a_Centro_360()
    {
        var centro = Centro("Centro Logístico Norte");
        var cut = Renderizar(centro);

        // Barrera: la fila está pintada y colapsada. Sin ella, la ausencia
        // del enlace sería un verde vacío.
        cut.Markup.Should().Contain("Centro Logístico Norte");
        cut.FindAll("a.acordeon-centro-enlace-360").Should().BeEmpty(
            "el contenido expandido solo se monta al expandir la fila");

        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());

        var enlace = cut.Find("a.acordeon-centro-enlace-360");
        enlace.GetAttribute("href").Should().Be($"/centros/{centro.Id}");
        enlace.TextContent.Should().Contain("Ver el centro completo");
        cut.Find(".acordeon-centro-titulo").TextContent.Should().Be("Asignaciones con incidencias");
    }

    /// <summary>
    /// El enlace no depende de lo que haya dentro del acordeón ni del estado
    /// del centro: un centro bloqueado y uno vigente lo llevan igual, y cada
    /// uno apunta a SU ficha.
    /// </summary>
    [Fact]
    public async Task Cada_centro_expandido_enlaza_a_su_propio_Centro_360_sea_cual_sea_su_estado()
    {
        var bloqueado = Centro("Obra Valdés — Fase 2", EstadoCentro.Bloqueado);
        var vigente = Centro("Almacén Sur", EstadoCentro.Vigente);
        var cut = Renderizar(bloqueado, vigente);

        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Expandir todo").ClickAsync(new MouseEventArgs());

        cut.FindAll("a.acordeon-centro-enlace-360")
            .Select(a => a.GetAttribute("href"))
            .Should().Equal($"/centros/{bloqueado.Id}", $"/centros/{vigente.Id}");
    }

    /// <summary>
    /// El Workspace no es modal: con la ficha del centro abierta, la baja en lote
    /// se confirma desde la lista que queda detrás. La lista retira la ficha si su
    /// centro iba en el lote y cayó alguno; no la toca si era de otro centro ni si
    /// el lote no eliminó nada (la guarda «Eliminados > 0»).
    /// </summary>
    [Theory]
    [InlineData(true, null, false)]  // iba en el lote, cayó: se retira
    [InlineData(false, null, true)]  // no iba en el lote: se queda
    [InlineData(true, 0, true)]      // iba en el lote pero no cayó nada: se queda
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_solo_si_su_centro_iba_y_cayo_alguno(
        bool ibaEnElLote, int? eliminados, bool seQuedaAbierta)
    {
        var elegido = Centro("Centro Logístico Norte");
        var otro = Centro("Centro Logístico Sur");
        var cut = Renderizar(elegido, otro);
        _mediador.EliminadosDelLote = eliminados;
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var abierto = ibaEnElLote ? elegido : otro;
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Centro, abierto.Id, abierto.Nombre, "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        await cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados")
            .ClickAsync(new MouseEventArgs());
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarCentrosCommand>().Single().Ids.Should().Equal([elegido.Id],
            "el caso solo vale si el lote pidió ese centro y ninguno más");
        workspace.EstaAbierto.Should().Be(seQuedaAbierta);
    }

    /// <summary>
    /// FS-09 (auditoría UX de flujos sin salida, 2026-09-24): el aviso de una
    /// eliminación en lote ofrece «Deshacer», que restaura los centros que cayeron.
    /// </summary>
    [Fact]
    public async Task Eliminar_en_lote_ofrece_deshacer_que_restaura_solo_los_centros_eliminados()
    {
        var elegido = Centro("Centro Logístico Norte");
        var superviviente = Centro("Centro Logístico Sur");
        var cut = Renderizar(elegido, superviviente);
        _mediador.NoEliminables.Add(superviviente.Id);

        await cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Sur']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados")
            .ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("Podrás deshacer la eliminación desde el aviso que aparecerá, pero las asignaciones seguirán de baja");
        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        _mediador.Enviadas.OfType<EliminarCentrosCommand>().Single().Ids.Should().BeEquivalentTo([elegido.Id, superviviente.Id],
            "el caso solo vale si el superviviente iba en el lote");
        _mediador.Enviadas.OfType<RestaurarCentroCommand>().Select(c => c.Id).Should().Equal([elegido.Id],
            "se restaura solo lo que el lote eliminó, no lo que pidió");
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "1 centro(s) restaurado(s).");
    }

    /// <summary>
    /// D-16: la baja de UN Centro desde el «⋯» de su fila, con confirmación y «Deshacer». Pide solo ese
    /// centro, no toca la selección múltiple y el aviso restaura exactamente lo eliminado.
    /// </summary>
    [Fact]
    public async Task Eliminar_un_centro_desde_su_menu_confirma_pide_solo_ese_y_ofrece_deshacer()
    {
        var elegido = Centro("Centro Logístico Norte");
        var otro = Centro("Centro Logístico Sur");
        var cut = Renderizar(elegido, otro);
        var fila = cut.FindAll(".tarjeta-fila-acordeon").Single(f => f.TextContent.Contains("Centro Logístico Norte"));

        fila.QuerySelector(".menu-acciones-disparador")!.Click();
        await cut.FindAll(".menu-acciones-item").Single(i => i.TextContent.Trim() == "Eliminar centro").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Eliminar el centro «Centro Logístico Norte»?");
        _mediador.Enviadas.OfType<EliminarCentrosCommand>().Should().BeEmpty("barrera: hasta confirmar no se elimina nada");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<EliminarCentrosCommand>().Single().Ids.Should().Equal([elegido.Id]);
        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Single(m => m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);
        _mediador.Enviadas.OfType<RestaurarCentroCommand>().Select(c => c.Id).Should().Equal([elegido.Id]);
    }

    /// <summary>D-17: sin denominador de cumplimiento un Centro «Vigente» no se mide: «Sin datos», neutro.</summary>
    [Theory]
    [InlineData(EstadoCentro.Vigente, null, true)]
    [InlineData(EstadoCentro.Vigente, 100, false)]
    [InlineData(EstadoCentro.Vencido, null, false)]
    public void Un_centro_vigente_sin_cumplimiento_medido_figura_sin_datos(EstadoCentro estado, int? cumplimiento, bool sinDatos)
    {
        EstadoCentroUi.EsSinDatos(estado, cumplimiento).Should().Be(sinDatos);
        EstadoCentroUi.Texto(estado, cumplimiento).Should().Be(sinDatos ? "Sin datos" : EstadoCentroUi.Texto(estado));
        EstadoCentroUi.Tono(estado, cumplimiento).Should().Be(sinDatos ? TonoBadge.Neutro : EstadoCentroUi.Tono(estado));
    }
    /// <summary>
    /// D-18: la columna de recuentos mide 40 px (cabecera «VENC.» / «PRÓX.»), así que la cifra va sola, como en el
    /// mockup, y su texto completo («2 documentos vencidos») sale en el <c>title</c> (tooltip) del badge.
    /// </summary>
    [Fact]
    public void Las_cifras_de_recuento_llevan_su_texto_completo_en_el_title()
    {
        IncidenciaCentroDto Incidencia(AmbitoCausa ambito) =>
            new("Formación PRL", ambito, EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), null);
        var centro = Centro("Centro Logístico Norte") with
        {
            Recuentos = new RecuentosCentroDto(
                [Incidencia(AmbitoCausa.Empresa), Incidencia(AmbitoCausa.Trabajador)],
                [Incidencia(AmbitoCausa.Trabajador)])
        };

        var cut = Renderizar(centro);

        var badges = cut.FindAll(".ranura-recuento .badge-solo-recuento");
        badges.Select(b => b.TextContent.Trim()).Should().Equal("2", "1");
        badges.Select(b => b.GetAttribute("title")).Should().Equal("2 documentos vencidos", "1 documento próximo a vencer");
    }

    /// <summary>
    /// Listados 3/7 (decisión del 2026-10-08): la incidencia de la ventana de contexto se pulsa y
    /// abre su corrección sin salir del listado. Solo es botón la que tiene algo que abrir; la que
    /// no trae Documento ni Tipo sigue siendo una línea de texto, y entonces la ventana tampoco
    /// cambia a su modo interactivo.
    /// </summary>
    [Fact]
    public void Solo_es_pulsable_la_incidencia_que_tiene_algo_que_abrir()
    {
        var conDocumento = new IncidenciaCentroDto(
            "Aptitud médica — Sonia Cano", AmbitoCausa.Trabajador, EstadoDocumento.Vencido,
            Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid());
        var faltaDeTrabajador = new IncidenciaCentroDto(
            "Formación Art. 19 — Pedro Gil", AmbitoCausa.Trabajador, EstadoDocumento.Faltante,
            null, Guid.NewGuid(), null, Guid.NewGuid());
        var sinIdentificadores = new IncidenciaCentroDto(
            "Causa antigua sin identificadores", AmbitoCausa.Trabajador, EstadoDocumento.Proximo, null, null, null);
        var centro = Centro("Centro Logístico Norte") with
        {
            Recuentos = new RecuentosCentroDto([conDocumento, faltaDeTrabajador], [sinIdentificadores])
        };

        var cut = Renderizar(centro);

        var ventanas = cut.FindAll(".ranura-recuento .ventana-contexto");
        ventanas.Should().HaveCount(2);
        // En este listado el panel abre hacia abajo (list-page.css): el puente que deja cruzar el
        // cursor del disparador al panel tiene que ir en ese mismo lado.
        ventanas.Should().OnlyContain(v => v.ClassList.Contains("ventana-contexto-abajo"));

        ventanas[0].ClassList.Should().Contain("ventana-contexto-interactiva");
        ventanas[0].QuerySelectorAll("button.ventana-contexto-elemento").Select(b => b.TextContent.Trim())
            .Should().Equal("Aptitud médica — Sonia Cano", "Formación Art. 19 — Pedro Gil");
        ventanas[0].QuerySelector(".ventana-contexto-pie")!.TextContent.Should().Be("Clic en una para corregirla aquí");

        ventanas[1].ClassList.Should().NotContain("ventana-contexto-interactiva");
        ventanas[1].QuerySelectorAll("button").Should().BeEmpty();
        ventanas[1].QuerySelector(".ventana-linea:not(.ventana-grupo)")!.TextContent.Should().Be("Causa antigua sin identificadores");
        ventanas[1].QuerySelector(".ventana-contexto-pie").Should().BeNull();
    }

    [Fact]
    public async Task Pulsar_una_incidencia_abre_la_correccion_de_ese_documento_sin_abrir_la_fila()
    {
        this.ConServiciosDelFormularioDeDocumento();
        var documentoId = Guid.NewGuid();
        var centro = Centro("Centro Logístico Norte") with
        {
            Recuentos = new RecuentosCentroDto(
                [new IncidenciaCentroDto(
                    "Aptitud médica — Sonia Cano", AmbitoCausa.Trabajador, EstadoDocumento.Vencido,
                    documentoId, Guid.NewGuid(), null, Guid.NewGuid())],
                [])
        };
        var cut = Renderizar(centro);
        var consultasDeListaAntes = _mediador.Enviadas.OfType<ObtenerCentrosQuery>().Count();

        await cut.Find(".ranura-recuento button.ventana-contexto-elemento").ClickAsync(new MouseEventArgs());

        _mediador.Enviadas.OfType<ObtenerDocumentoPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(documentoId);
        // No navega ni recarga: la lista no se vuelve a pedir por pulsar, y el acordeón de la fila no se abre.
        _mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().HaveCount(consultasDeListaAntes);
        cut.FindAll(".tarjeta-fila-acordeon-cuerpo").Should().BeEmpty();
    }

    [Fact]
    public async Task Tras_corregir_una_incidencia_la_lista_se_vuelve_a_pedir_sin_recargar_la_pagina()
    {
        var cut = Renderizar(Centro("Centro Logístico Norte"));
        var consultasDeListaAntes = _mediador.Enviadas.OfType<ObtenerCentrosQuery>().Count();
        var correccion = cut.FindComponent<CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental>();

        await cut.InvokeAsync(() => correccion.Instance.OnCorregida.InvokeAsync());

        _mediador.Enviadas.OfType<ObtenerCentrosQuery>().Should().HaveCount(consultasDeListaAntes + 1,
            "el recuento y el estado de la fila cambian al corregir: la lista se relee en sitio");
    }
}
