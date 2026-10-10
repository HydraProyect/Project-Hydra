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
        /// <summary>Visitas programadas por Centro; vacío salvo que el test las siembre.</summary>
        public Dictionary<Guid, IReadOnlyList<VisitaResumenDto>> Visitas { get; init; } = [];

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
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)Visitas,
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

    /// <summary>Visitas programadas que devolverá el mediador, por Centro. Se siembran antes de renderizar.</summary>
    private readonly Dictionary<Guid, IReadOnlyList<VisitaResumenDto>> _visitas = [];

    private IRenderedComponent<Centros> Renderizar(params CentroListaDto[] centros) =>
        RenderizarConGruposContraidos(centros).AbrirGruposDeCentros();

    /// <summary>Como la ve el usuario al llegar: agrupada por Cliente empresarial y con los grupos contraídos.</summary>
    private IRenderedComponent<Centros> RenderizarConGruposContraidos(params CentroListaDto[] centros)
    {
        _mediador = new MediatorPorTipo { Centros = centros, Visitas = _visitas };
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
    /// Patrón de listados sin menú «⋯» (2026-10-08): la baja de un Centro solo vive en la selección
    /// múltiple. «Eliminar seleccionados» confirma, pide solo el Centro marcado y el aviso ofrece
    /// «Deshacer», que restaura exactamente lo eliminado.
    /// </summary>
    [Fact]
    public async Task Eliminar_solo_esta_en_la_seleccion_multiple_pide_confirmacion_manda_esa_fila_y_ofrece_deshacer()
    {
        var elegido = Centro("Centro Logístico Norte");
        var otro = Centro("Centro Logístico Sur");
        var cut = Renderizar(elegido, otro);
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain(
            ["Eliminar centro", "Eliminar seleccionados"], "la fila no ofrece la baja y aún no hay selección");

        await cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll("button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").ClickAsync(new MouseEventArgs());
        cut.Find("[role=dialog]").TextContent.Should().Contain("¿Eliminar 1 centro(s)?");
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
    /// Los recuentos de vencidos y próximos no tienen columna (retiradas el 2026-10-09): son el motivo bajo la
    /// pastilla de estado, «2 vencidos · 1 por vencer», y cada parte es el disparador de su ventana de contexto,
    /// que conserva el nombre accesible con el reparto por ámbito.
    /// </summary>
    [Fact]
    public void Los_recuentos_son_el_motivo_bajo_la_pastilla_de_estado()
    {
        IncidenciaCentroDto Incidencia(AmbitoCausa ambito) =>
            new("Formación PRL", ambito, EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), null);
        var centro = Centro("Centro Logístico Norte", EstadoCentro.Vencido) with
        {
            Recuentos = new RecuentosCentroDto(
                [Incidencia(AmbitoCausa.Empresa), Incidencia(AmbitoCausa.Trabajador)],
                [Incidencia(AmbitoCausa.Trabajador)])
        };

        var cut = Renderizar(centro);

        var estado = cut.Find(".ranura-estado-centro .estado-fila");
        estado.QuerySelector(".estado-fila-correcto").Should().BeNull("un Centro vencido lleva pastilla de color");
        estado.TextContent.Should().Contain("Vencido");
        var motivo = estado.QuerySelector(".estado-fila-motivo")!;
        motivo.QuerySelectorAll(".motivo-recuento").Select(m => m.TextContent.Trim()).Should().Equal("2 vencidos", "1 por vencer");
        // Con incidencias corregibles la ventana es interactiva y el nombre accesible va en su botón disparador.
        motivo.QuerySelectorAll(".ventana-contexto-disparador").Select(v => v.GetAttribute("aria-label")).Should().Equal(
            "2 vencidos. 2 documentos vencidos: 1 de empresa y 1 de trabajadores",
            "1 por vencer. 1 documento próximo a vencer: 1 de trabajadores");
        // El nombre accesible empieza por lo que se ve: quien dicta «2 vencidos» activa ese disparador (WCAG 2.5.3).
        motivo.QuerySelectorAll(".ventana-contexto-disparador").Should().OnlyContain(
            d => d.GetAttribute("aria-label")!.StartsWith(d.TextContent.Trim() + ".", StringComparison.Ordinal));
        cut.FindAll(".badge-solo-recuento").Should().BeEmpty("la cifra suelta de las columnas retiradas ya no se pinta");
    }

    /// <summary>
    /// El motivo separa las causas dentro de «vencidas» (decisión del 2026-10-10): vencido por fecha, pendiente
    /// (Faltante) y bloqueado (rechazo sin vigencia), y después las próximas. Un Faltante no se cuenta como vencido.
    /// </summary>
    [Fact]
    public void El_motivo_separa_vencidos_pendientes_y_bloqueos_por_causa()
    {
        IncidenciaCentroDto Causa(EstadoDocumento? estado) =>
            new("Causa", AmbitoCausa.Trabajador, estado, Guid.NewGuid(), Guid.NewGuid(), null);
        var centro = Centro("Centro Logístico Norte", EstadoCentro.Vencido) with
        {
            Recuentos = new RecuentosCentroDto(
                [Causa(EstadoDocumento.Vencido), Causa(EstadoDocumento.Vencido), Causa(EstadoDocumento.Faltante), Causa(null)],
                [Causa(EstadoDocumento.Proximo), Causa(EstadoDocumento.Proximo), Causa(EstadoDocumento.Proximo)])
        };

        var cut = Renderizar(centro);

        var motivo = cut.Find(".ranura-estado-centro .estado-fila-motivo");
        motivo.QuerySelectorAll(".motivo-recuento").Select(m => m.TextContent.Trim())
            .Should().Equal("2 vencidos", "1 pendiente", "1 bloqueo de plataforma", "3 por vencer");
        motivo.QuerySelectorAll(".motivo-recuento-separador").Should().HaveCount(3);
    }

    [Theory]
    [InlineData(1, 0, "1 vencido")]
    [InlineData(0, 3, "3 por vencer")]
    public void El_motivo_solo_nombra_el_recuento_que_hay(int vencidas, int proximas, string esperado)
    {
        IReadOnlyList<IncidenciaCentroDto> Incidencias(int n, EstadoDocumento estado) => Enumerable.Range(0, n)
            .Select(_ => new IncidenciaCentroDto("Formación PRL", AmbitoCausa.Trabajador, estado, Guid.NewGuid(), Guid.NewGuid(), null))
            .ToList();
        var centro = Centro("Centro Logístico Norte", vencidas > 0 ? EstadoCentro.Vencido : EstadoCentro.Proximo) with
        {
            Recuentos = new RecuentosCentroDto(Incidencias(vencidas, EstadoDocumento.Vencido), Incidencias(proximas, EstadoDocumento.Proximo))
        };

        var cut = Renderizar(centro);

        var motivo = cut.Find(".ranura-estado-centro .estado-fila-motivo");
        motivo.QuerySelectorAll(".motivo-recuento").Select(m => m.TextContent.Trim()).Should().Equal(esperado);
        motivo.QuerySelector(".motivo-recuento-separador").Should().BeNull("con un solo recuento no hay nada que separar");
    }

    [Fact]
    public void Un_centro_sin_vencidos_ni_proximos_lleva_su_estado_sin_motivo()
    {
        var cut = Renderizar(Centro("Centro Logístico Norte"));

        var estado = cut.Find(".ranura-estado-centro .estado-fila");
        estado.QuerySelector(".estado-fila-correcto")!.TextContent.Should().Contain("Vigente");
        estado.QuerySelector(".estado-fila-motivo").Should().BeNull();
    }

    /// <summary>
    /// Decisión del 2026-10-09: con visita programada la fila enseña la visita Y la pastilla de estado con su
    /// motivo. Antes la visita ocupaba el sitio del estado y un Centro vencido con visita no decía que lo estaba.
    /// </summary>
    [Fact]
    public void Con_visita_programada_se_ven_la_visita_y_el_estado_con_su_motivo()
    {
        var centro = Centro("Centro Logístico Norte", EstadoCentro.Vencido) with
        {
            Recuentos = new RecuentosCentroDto(
                [new IncidenciaCentroDto("Formación PRL", AmbitoCausa.Trabajador, EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), null)],
                [])
        };
        _visitas[centro.Id] = [new VisitaResumenDto(Guid.NewGuid(), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 16), 3)];

        var cut = Renderizar(centro);

        cut.Find(".ranura-recuento-final .badge-visita").TextContent.Trim().Should().Be("Visita 15/10–16/10");
        var estado = cut.Find(".ranura-estado-centro .estado-fila");
        estado.TextContent.Should().Contain("Vencido");
        estado.QuerySelector(".estado-fila-motivo .motivo-recuento")!.TextContent.Trim().Should().Be("1 vencido");
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

        // Vencido, pendiente y próximo: cada causa abre su propia ventana (el faltante ya no va con los vencidos).
        var ventanas = cut.FindAll(".ranura-recuento .ventana-contexto");
        ventanas.Should().HaveCount(3);
        // En este listado el panel abre hacia abajo (list-page.css): el puente que deja cruzar el
        // cursor del disparador al panel tiene que ir en ese mismo lado.
        ventanas.Should().OnlyContain(v => v.ClassList.Contains("ventana-contexto-abajo"));

        ventanas[0].ClassList.Should().Contain("ventana-contexto-interactiva");
        ventanas[0].QuerySelectorAll("button.ventana-contexto-elemento").Select(b => b.TextContent.Trim())
            .Should().Equal("Aptitud médica — Sonia Cano");
        ventanas[0].QuerySelector(".ventana-contexto-pie")!.TextContent.Should().Be("Clic en una para corregirla aquí");

        // El faltante tiene Tipo de documento: se puede corregir, así que su ventana también es pulsable.
        ventanas[1].ClassList.Should().Contain("ventana-contexto-interactiva");
        ventanas[1].QuerySelectorAll("button.ventana-contexto-elemento").Select(b => b.TextContent.Trim())
            .Should().Equal("Formación Art. 19 — Pedro Gil");
        ventanas[1].QuerySelector(".ventana-contexto-pie")!.TextContent.Should().Be("Clic en una para corregirla aquí");

        ventanas[2].ClassList.Should().NotContain("ventana-contexto-interactiva");
        ventanas[2].QuerySelectorAll("button").Should().BeEmpty();
        ventanas[2].QuerySelector(".ventana-linea:not(.ventana-grupo)")!.TextContent.Should().Be("Causa antigua sin identificadores");
        ventanas[2].QuerySelector(".ventana-contexto-pie").Should().BeNull();
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
