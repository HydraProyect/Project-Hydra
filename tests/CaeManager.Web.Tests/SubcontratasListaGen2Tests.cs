using CaeManager.Infrastructure.Identity;
using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontratas;
using CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;
using CaeManager.Application.Clientes.Commands.EliminarClientes;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Subcontratas.Pages;
using AngleSharp.Dom;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Subcontratas contra su mockup Gen 2 («Subcontratas TALVEG.dc.html»).
/// Cubre lo que el rediseño añadió o cambió; lo que ya existía y se conserva
/// —vacío por filtro, su copia en singular— lo sigue probando
/// <see cref="SubcontratasVacioPorFiltroTests"/>.
///
/// <para>
/// Patrón de listados sin menú «⋯» (2026-10-08): un clic en la fila, su nombre o Enter abren
/// la vista rápida —el panel del Context Workspace—, el icono 360 del final de la fila lleva
/// a la página Subcontrata 360 y la baja solo vive en la selección múltiple. Lo que enseñaba
/// la vista previa antigua lo prueba ahora <see cref="Subcontrata360Gen2Tests"/> en el panel.
/// </para>
/// </summary>
public partial class SubcontratasListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public SubcontratasListaGen2Tests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        // Los textos de Subcontratas salen de IStringLocalizer<TextosSubcontratas>.
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<SubcontrataListaDto> Subcontratas { get; init; } = [];
        public int? EliminadosForzados { get; set; }

        /// <summary>El lote devuelve qué ids cayeron, como el handler real (los tests anteriores a «Deshacer» solo fijan el recuento).</summary>
        public bool LoteDevuelveIds { get; set; }
        public IReadOnlyList<EmpresaSelectorDto> Empresas { get; init; } = [];
        public IReadOnlyList<ClienteSelectorDto> Clientes { get; init; } = [];

        public List<object> Enviadas { get; } = [];
        /// <summary>Por defecto, un usuario mono-Tenant: sin selector ni cabecera de empresa gestionada.</summary>
        public List<ClienteAutorizadoDto> Autorizados { get; } = [new(Guid.NewGuid(), "Propia", EsOrigen: true)];
        /// <summary>Si se fija, la respuesta de la lista de Tenants autorizados espera a esta tarea (mediador asíncrono).</summary>
        public Task? RetenerAutorizados { get; set; }

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            if (request is ObtenerClientesAutorizadosQuery && RetenerAutorizados is { } espera)
                await espera;
            return (TResponse)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => (object)PerfilVocabularioTenant.Consultora,
                // Sin documento que devolver: basta para observar qué se pidió abrir.
                CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId.ObtenerDocumentoPorIdQuery => null!,
                ObtenerClientesAutorizadosQuery => Autorizados,
                ObtenerSubcontratasQuery q => FiltrarPorBusqueda(q),
                ObtenerEmpresasParaSelectorQuery => Empresas,
                ObtenerClientesParaSelectorQuery => Clientes,
                EliminarSubcontratasCommand lote => Result.Exito(new ResultadoEliminacionLoteDto(EliminadosForzados ?? lote.Ids.Count, [], LoteDevuelveIds ? lote.Ids : null)),
                RestaurarSubcontrataCommand => Result.Exito(),
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            });
        }

        /// <summary>
        /// Filtra por <c>Busqueda</c> como lo haría el handler (razón social,
        /// sin distinguir mayúsculas): si la pantalla no enviara lo escrito en
        /// <c>?q=</c>, recibiría la lista entera y el conteo lo delataría.
        /// </summary>
        private ResultadoPaginado<SubcontrataListaDto> FiltrarPorBusqueda(ObtenerSubcontratasQuery q)
        {
            // El filtro por id y la paginación, como el handler: «la fila se refresca tras guardar
            // en la vista rápida» pide UNA fila y se prueba desde la página 2.
            var coincidentes = Subcontratas
                .Where(s => q.SubcontrataId is null || s.Id == q.SubcontrataId)
                .Where(s => q.Busqueda is null || s.RazonSocial.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase))
                .Where(s => q.NivelServicio is null || s.NivelServicio == q.NivelServicio)
                .ToList();
            var pagina = coincidentes.Skip((q.Pagina - 1) * q.TamanoPagina).Take(q.TamanoPagina).ToList();
            return new ResultadoPaginado<SubcontrataListaDto>(pagina, coincidentes.Count, q.Pagina, q.TamanoPagina);
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

    private static IncidenciaSubcontrataDto Incidencia(string descripcion, EstadoDocumento estado) =>
        new(descripcion, estado, null, null, null);

    private static SubcontrataListaDto Subcontrata(
        string razonSocial, Guid? id = null, int? cumplimiento = 100,
        IReadOnlyList<IncidenciaSubcontrataDto>? vencidas = null,
        IReadOnlyList<IncidenciaSubcontrataDto>? proximas = null,
        NivelServicioSubcontrata nivel = NivelServicioSubcontrata.Gestionada) => new(
        id ?? Guid.NewGuid(), razonSocial, "B-20.774.115", new DateTime(2021, 4, 10, 0, 0, 0, DateTimeKind.Utc),
        nivel, cumplimiento, new RecuentosSubcontrataDto(vencidas ?? [], proximas ?? []));

    private SeleccionEmpresaGestionadaDePrueba Seleccion { get; set; } = new();

    /// <param name="busqueda">Valor del filtro de texto que llega por la URL (?q=).</param>
    private IRenderedComponent<Subcontratas> Renderizar(MediatorFalso mediador, string? busqueda = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => Seleccion);
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearSubcontrataCommand>>(_ => new InlineValidator<CrearSubcontrataCommand>());

        // El filtro es [SupplyParameterFromQuery]: se llega a él navegando, no
        // pasándolo como parámetro de componente.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(busqueda is null ? "subcontratas" : "subcontratas?q=" + Uri.EscapeDataString(busqueda));

        return Render<Subcontratas>();
    }

    private Task EncenderSeleccionMultiple(IRenderedComponent<Subcontratas> cut) =>
        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());

    /// <summary>
    /// Rediseño de listados, fase 1: cabecera de una línea con el contador, el ☑, el «⋯» con
    /// «Exportar a Excel» y la primaria. Sin antetítulo ni rótulo de la empresa gestionada.
    /// </summary>
    [Fact]
    public void La_cabecera_es_de_una_linea_con_contador_seleccion_menu_y_primaria()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L."), Subcontrata("Pinturas Lauburu S.A.")] });

        var cabecera = cut.Find("header.cabecera-pagina");
        cabecera.QuerySelector(".cabecera-pagina-kicker").Should().BeNull("«Negocio» ya está en las migas");
        cabecera.QuerySelector("h1")!.TextContent.Trim().Should().Be("Subcontratas");
        cabecera.QuerySelector(".cabecera-listado-contador")!.TextContent.Trim().Should().Be("2");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();

        var acciones = cabecera.QuerySelector(".acciones-cabecera")!;
        acciones.QuerySelectorAll("a").Should().BeEmpty("Exportar a Excel vive ahora dentro del «⋯»");
        acciones.QuerySelectorAll("button").Select(b => b.GetAttribute("aria-label") ?? b.TextContent.Trim())
            .Should().Equal("Selección múltiple", "Atajos de teclado", "Más acciones", "+ Nueva subcontrata");

        cut.Find("header.cabecera-pagina .menu-acciones-disparador").Click();
        cut.FindAll("header.cabecera-pagina .menu-acciones-item").Select(i => i.TextContent.Trim()).Should().SatisfyRespectively(
                vista => vista.Should().StartWith("Exportar esta vista (filas: "),
                todo => todo.Should().Be("Exportar todo"));
        cut.FindAll("header.cabecera-pagina a.menu-acciones-item").Select(i => i.GetAttribute("href"))
            .Should().Equal("/subcontratas/exportar.xlsx", "/subcontratas/exportar.xlsx");
    }

    [Fact]
    public void El_buscador_es_Filtrar_esta_pantalla_y_promete_razon_social_o_CIF()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        var buscador = cut.Find(".barra-filtros-pastillas input[type=text]");
        buscador.GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: razón social o CIF",
            "ObtenerSubcontratasQuery busca en los dos; los E2E usan este texto");
        buscador.GetAttribute("aria-label").Should().Be("Filtrar esta pantalla");
        buscador.HasAttribute("data-filtro-pantalla").Should().BeTrue("es lo que enfoca la tecla f (atajos-lista.js)");
    }

    /// <summary>
    /// La pastilla «Nivel de servicio» viaja en la consulta y en la URL, y su chip la nombra.
    /// El doble filtra por nivel: si la pantalla no lo enviara, se verían las dos.
    /// </summary>
    [Fact]
    public void La_pastilla_Nivel_de_servicio_filtra_la_consulta_y_la_url_y_su_chip_la_quita()
    {
        var mediador = new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("Andamios Bidasoa S.L.", nivel: NivelServicioSubcontrata.Gestionada),
                Subcontrata("Pinturas Lauburu S.A.", nivel: NivelServicioSubcontrata.Supervisada),
            ]
        };
        var cut = Renderizar(mediador);
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.FindAll(".barra-filtros-pastillas .menu-acciones-disparador-pastilla").Single(b => b.GetAttribute("aria-label") == "Nivel de servicio").Click();
        cut.FindAll(".barra-filtros-pastillas [role=menuitemradio]").Single(i => i.TextContent.Trim() == "Supervisada").Click();

        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().NivelServicio.Should().Be(NivelServicioSubcontrata.Supervisada);
        navegacion.Uri.Should().Contain("nivel=Supervisada");
        cut.FindAll(".lista-filas-acordeon .enlace-nombre-fila").Select(b => b.TextContent.Trim()).Should().Equal("Pinturas Lauburu S.A.");
        cut.FindAll(".chip-filtro").Select(c => c.TextContent.Trim()).Should().Equal("Nivel de servicio: Supervisada");

        cut.Find(".chip-filtro-quitar").Click();

        navegacion.Uri.Should().NotContain("nivel=");
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().NivelServicio.Should().BeNull();
        cut.FindAll(".lista-filas-acordeon .enlace-nombre-fila").Should().HaveCount(2);
    }

    /// <summary>Un nivel que no existe en la URL no filtra (no es autoridad sobre lo que hay); uno válido sí.</summary>
    [Theory]
    [InlineData("subcontratas?nivel=Gestionada", NivelServicioSubcontrata.Gestionada)]
    [InlineData("subcontratas?nivel=Inventado", null)]
    public void El_nivel_de_la_url_solo_filtra_si_es_un_nivel_que_existe(string url, NivelServicioSubcontrata? esperado)
    {
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] };
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ITenantActual>(_ => Seleccion);
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearSubcontrataCommand>>(_ => new InlineValidator<CrearSubcontrataCommand>());
        Services.GetRequiredService<NavigationManager>().NavigateTo(url);

        Render<Subcontratas>();

        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().NivelServicio.Should().Be(esperado);
    }

    /// <summary>
    /// Filas con problema (rediseño de listados, fase 1): algún vencido → peligro; si no, algún
    /// urgente → aviso; próximos sin urgencia o al corriente → sin tinte.
    /// </summary>
    [Fact]
    public void Las_filas_con_vencidos_o_urgentes_van_tintadas()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("A Vencida S.L.", vencidas: [Incidencia("Aptitud médica", EstadoDocumento.Vencido)], proximas: [Incidencia("Aptitud médica", EstadoDocumento.Urgente)]),
                Subcontrata("B Urgente S.L.", proximas: [Incidencia("Aptitud médica", EstadoDocumento.Urgente)]),
                Subcontrata("C Proxima S.L.", proximas: [Incidencia("Aptitud médica", EstadoDocumento.Proximo)]),
                Subcontrata("D Al corriente S.L."),
            ]
        });

        string Tinte(string nombre)
        {
            var clases = cut.FindAll(".tarjeta-fila-acordeon").Single(f => f.TextContent.Contains(nombre)).ClassList;
            return clases.Contains("fila-tintada-peligro") ? "peligro" : clases.Contains("fila-tintada-aviso") ? "aviso" : "ninguno";
        }

        Tinte("A Vencida").Should().Be("peligro");
        Tinte("B Urgente").Should().Be("aviso");
        Tinte("C Proxima").Should().Be("ninguno");
        Tinte("D Al corriente").Should().Be("ninguno");
    }

    /// <summary>Todo arranca contraído; «Expandir todo» vive en la fila de los filtros.</summary>
    [Fact]
    public void Todo_arranca_contraido_y_Expandir_todo_vive_en_la_barra_de_filtros()
    {
        // El contenido del desplegable no es objeto de este test (y pide servicios de Documentos).
        ComponentFactories.AddStub<CaeManager.Web.Features.Subcontratas.Components.AcordeonTrabajadoresSubcontrata>();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L."), Subcontrata("Pinturas Lauburu S.A.")] });
        cut.FindAll(".boton-expandir-fila").Select(b => b.GetAttribute("aria-expanded")).Should().Equal("false", "false");

        var boton = cut.FindAll(".barra-filtros-pastillas-fila .barra-herramientas-lista button").Single();
        boton.TextContent.Trim().Should().Be("Expandir todo");
        boton.Click();

        cut.FindAll(".boton-expandir-fila").Select(b => b.GetAttribute("aria-expanded")).Should().Equal("true", "true");
        cut.Find(".barra-herramientas-lista button").TextContent.Trim().Should().Be("Contraer todo");
    }

    [Theory]
    [InlineData(20, false)]
    [InlineData(21, true)]
    public void El_paginador_solo_aparece_con_mas_de_una_pagina(int subcontratas, bool conPaginador)
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = Enumerable.Range(1, subcontratas).Select(i => Subcontrata($"Subcontrata {i:00}")).ToList()
        });

        cut.FindAll(".paginador").Any().Should().Be(conPaginador);
    }

    /// <summary>
    /// Cabecera de columnas y fila comparten la misma rejilla: si no llevan el
    /// mismo número de celdas, las columnas no cuadran — y con selección
    /// múltiple entra una casilla más en las dos.
    /// </summary>
    [Fact]
    public void La_cabecera_de_columnas_y_la_fila_tienen_el_mismo_numero_de_celdas()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        var cabecera = cut.Find(".cabecera-columnas-subcontratas");
        cabecera.TextContent.Should().Contain("Razón social").And.Contain("Nivel de servicio")
            .And.Contain("Cumplimiento").And.Contain("Vencidos").And.Contain("Por vencer").And.NotContain("Próximos");
        cut.Find(".tarjeta-fila-acordeon-cabecera").Children.Length.Should().Be(cabecera.Children.Length,
            "sin selección múltiple");

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();

        cut.Find(".tarjeta-fila-acordeon-cabecera").Children.Length
            .Should().Be(cut.Find(".cabecera-columnas-subcontratas").Children.Length,
                "con selección múltiple la casilla entra en la fila y su hueco en la cabecera");
    }

    [Fact]
    public void Los_recuentos_se_leen_como_texto_con_su_numero_y_su_concordancia()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("Andamios Bidasoa S.L.",
                    vencidas: [Incidencia("Formación PRL — Iñaki Otaegi", EstadoDocumento.Vencido), Incidencia("Seguro RC — Iñaki Otaegi", EstadoDocumento.Faltante)],
                    proximas: [Incidencia("Reconocimiento médico — Miguel Sanz", EstadoDocumento.Proximo)]),
                Subcontrata("Transportes Argia S.A."),
                Subcontrata("Electricidad Zubia S.L.", vencidas: [Incidencia("Certificado TGSS — Luis Arana", EstadoDocumento.Vencido)]),
            ]
        });

        // Las pastillas de recuento son las de los disparadores; las del desglose (una por incidencia) van aparte.
        var badges = cut.FindAll(".celda-recuento-subcontrata .badge")
            .Where(b => b.Closest(".ventana-linea") is null).Select(b => b.TextContent.Trim()).ToList();

        badges.Should().BeEquivalentTo(["2 vencidos", "1 por vencer", "1 vencido"]);
        cut.FindAll(".celda-recuento-subcontrata [data-pieza=estado-correcto]").Select(e => e.TextContent.Trim())
            .Should().Equal(["Sin incidencias"], "sin vencidos ni por vencer, Transportes Argia no tiene nada que reclamar: sin pastilla");
        cut.FindAll(".celda-sin-recuento").Should().HaveCount(1,
            "solo Transportes Argia no tiene vencidos, y su celda se reserva con un guion");
    }

    /// <summary>
    /// Vencidas y Próximas se leen como texto literal ("N vencido(s)"/"N por
    /// vencer"): por eso Urgente vive en Próximas, no en Vencidas
    /// (ObtenerSubcontratasQuery.Desglosar). Pero el badge agregado de
    /// Próximas es siempre Advertencia (ámbar), así que sin un badge por
    /// incidencia dentro del detalle, un documento Urgente sería
    /// indistinguible de uno Próximo normal en esta lista (hallazgo de Codex,
    /// oleada 3 sobre esta PR). Desde el vocabulario único (2026-10-08) los dos
    /// se rotulan «Por vencer»: lo que los separa en el desglose es el tono de
    /// gravedad (<c>TonoDeSeveridad</c>), que conserva el rojo para lo urgente.
    /// </summary>
    [Fact]
    public void El_detalle_de_Por_vencer_distingue_Urgente_de_Proximo_por_el_tono_de_su_propio_badge()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("Andamios Bidasoa S.L.",
                    proximas: [
                        Incidencia("EPIs — Iñaki Otaegi", EstadoDocumento.Urgente),
                        Incidencia("Reconocimiento médico — Miguel Sanz", EstadoDocumento.Proximo),
                    ]),
            ]
        });

        var lineas = cut.FindAll(".ventana-contexto-panel .ventana-linea").ToList();

        var lineaUrgente = lineas.Should().ContainSingle(l => l.TextContent.Contains("EPIs — Iñaki Otaegi")).Subject;
        lineaUrgente.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Por vencer (urgente)");
        lineaUrgente.QuerySelector(".badge")!.ClassList.Should().Contain("badge-peligro",
            "donde se colorea por gravedad, lo urgente sigue en rojo: es lo único que lo separa de lo próximo");

        var lineaProxima = lineas.Should().ContainSingle(l => l.TextContent.Contains("Reconocimiento médico — Miguel Sanz")).Subject;
        lineaProxima.QuerySelector(".badge")!.TextContent.Trim().Should().Be("Por vencer");
        lineaProxima.QuerySelector(".badge")!.ClassList.Should().Contain("badge-advertencia").And.NotContain("badge-peligro");
    }

    /// <summary>
    /// <c>CumplimientoPorcentaje</c> es <c>null</c> cuando ningún trabajador
    /// tiene un documento exigido por un centro activo. Antes el nombre
    /// accesible interpolaba el valor sin mirarlo y anunciaba «% de
    /// cumplimiento…»: un número que no existe.
    /// </summary>
    [Fact]
    public void Sin_universo_de_requisitos_el_anillo_no_se_anuncia_como_un_porcentaje()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = [Subcontrata("Limpiezas Goiko S.L.U.", cumplimiento: null), Subcontrata("Soldaduras Iparra S. Coop.", cumplimiento: 84)]
        });

        cut.Find(".anillo-cumplimiento-sin-universo").GetAttribute("aria-label").Should()
            .Be("Sin trabajadores con documentos exigidos por algún centro activo");
        cut.Markup.Should().Contain("84% de cumplimiento", "con universo el anillo sigue diciendo su porcentaje");
    }

    /// <summary>
    /// El mockup explicaba el nivel como «TALVEG gestiona / vigila su
    /// documentación», y eso hace de la plataforma un Operador CAE. Quien
    /// gestiona es la organización que opera el tenant.
    /// </summary>
    [Fact]
    public void El_nivel_de_servicio_se_explica_sin_atribuir_la_gestion_a_TALVEG()
    {
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = [Subcontrata("Electricidad Zubia S.L.", nivel: NivelServicioSubcontrata.Supervisada)]
        });

        var explicacion = cut.FindAll(".tarjeta-fila-acordeon-cabecera .badge")
            .Single(b => b.TextContent.Trim() == "Supervisada").GetAttribute("title");

        explicacion.Should().Contain("solo se audita su cumplimiento", "es lo que distingue Supervisada de Gestionada");
        explicacion.Should().NotContain("TALVEG");
    }

    /// <summary>
    /// Patrón de listados sin menú «⋯» (2026-10-08): un clic en la fila abre la vista rápida
    /// —el panel del Context Workspace, no la vista previa antigua— y a la página Subcontrata
    /// 360 se va con el icono 360 del final de la fila, que es un enlace real.
    /// </summary>
    [Fact]
    public async Task El_clic_en_la_fila_abre_la_vista_rapida_y_el_icono_360_enlaza_a_la_pagina()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)] });

        cut.FindAll(".lista-filas-acordeon .menu-acciones-disparador").Should().BeEmpty("la fila no lleva menú «⋯»");
        cut.Find(".lista-filas-acordeon a.boton-360-pagina").GetAttribute("href").Should().Be($"/subcontratas/{id}");

        await cut.Find(".fila-pulsable").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual
            .Should().Be(new WorkspaceFrame(EntidadWorkspace.Subcontrata, id, "Andamios Bidasoa S.L.", "informacion"));
        cut.FindAll("aside.drawer-preview-subcontrata").Should().BeEmpty("la vista previa antigua ya no existe");
        cut.Find(".tarjeta-fila-acordeon").ClassList.Should().Contain("fila-en-vista-previa", "la fila del panel abierto queda marcada");
    }

    /// <summary>
    /// El nombre es el destino de teclado del clic en la fila (la fila entera no se alcanza
    /// con Tab): un botón con nombre accesible propio, no un enlace a la página.
    /// </summary>
    [Fact]
    public async Task El_nombre_de_la_fila_es_un_boton_que_abre_la_vista_rapida()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)] });

        var nombre = cut.Find(".celda-identidad-subcontrata .enlace-nombre-fila");
        nombre.TagName.Should().Be("BUTTON");
        nombre.ClassList.Should().Contain("nombre-abre-vista-rapida");
        nombre.GetAttribute("aria-label").Should().Be("Abrir la vista rápida de Andamios Bidasoa S.L.");

        await nombre.ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual
            .Should().Be(new WorkspaceFrame(EntidadWorkspace.Subcontrata, id, "Andamios Bidasoa S.L.", "informacion"));
    }

    /// <summary>
    /// Los controles de dentro de la fila hacen lo suyo y no dejan subir el clic: copiar el
    /// CIF no abre además la vista rápida.
    /// <para>
    /// El desplegable NO se prueba aquí: su manejador repinta la lista, bUnit pierde entonces
    /// el manejador de la fila y el test quedaría en verde aunque se quitara el corte (medido
    /// por mutación en Empresas, 2026-10-09). Esa propiedad la prueba
    /// <c>SubcontratasFilaSinMenuE2ETests</c> en un navegador real.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Copiar_el_cif_no_abre_la_vista_rapida()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        await cut.Find(".lista-filas-acordeon .boton-copiar-en-linea").ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual
            .Should().BeNull("el clic se queda en el control; solo el resto de la fila abre el panel");
    }

    /// <summary>
    /// La casilla (que reacciona al cambio, no al clic) y el icono 360 (que navega el
    /// navegador) no tienen manejador de clic propio y cortan la subida: bUnit lo dice con
    /// «nadie recibe este clic». Sin el corte, el clic llegaría a la fila —que sí lo
    /// atiende— y no habría excepción.
    /// </summary>
    [Theory]
    [InlineData("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']")]
    [InlineData("a.boton-360-pagina")]
    public async Task La_casilla_y_el_icono_360_cortan_el_clic_antes_de_la_fila(string selector)
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });
        await EncenderSeleccionMultiple(cut);

        var clic = () => cut.Find(".lista-filas-acordeon " + selector).ClickAsync(new MouseEventArgs());

        await clic.Should().ThrowAsync<MissingEventHandlerException>();
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull();
    }

    /// <summary>
    /// Una incidencia corregible de la ventana de «Vencidos» abre su corrección y nada más:
    /// la ventana interactiva corta la subida del clic, así que la fila no abre además su panel.
    /// </summary>
    [Fact]
    public async Task Pulsar_una_incidencia_de_la_ventana_no_abre_la_vista_rapida()
    {
        this.ConServiciosDelFormularioDeDocumento();
        var vencida = new IncidenciaSubcontrataDto(
            "Aptitud médica — Sonia Cano", EstadoDocumento.Vencido, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid());
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60, vencidas: [vencida])] };
        var cut = Renderizar(mediador);

        await cut.Find(".celda-recuento-subcontrata button.ventana-contexto-elemento").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId.ObtenerDocumentoPorIdQuery>()
            .Should().ContainSingle("control positivo: el clic llegó a la incidencia");
        Services.GetRequiredService<ContextWorkspaceService>().FrameActual.Should().BeNull();
    }

    /// <summary>El CIF se copia con un clic desde la fila, con nombre accesible que dice qué copia.</summary>
    [Fact]
    public void El_cif_de_la_fila_es_copiable_y_una_subcontrata_sin_cif_no_ofrece_copiar_nada()
    {
        var conCif = Subcontrata("Andamios Bidasoa S.L.");
        var sinCif = Subcontrata("Pinturas Lauburu S.A.") with { Cif = null };
        var cut = Renderizar(new MediatorFalso { Subcontratas = [conCif, sinCif] });

        var copiables = cut.FindAll(".lista-filas-acordeon .boton-copiar-en-linea");
        copiables.Should().ContainSingle("solo hay un CIF que copiar");
        copiables[0].TextContent.Trim().Should().Be(conCif.Cif);
        copiables[0].GetAttribute("aria-label").Should().Be($"Copiar el CIF {conCif.Cif}");
    }

    /// <summary>
    /// La baja solo vive en la selección múltiple (patrón de listados sin menú «⋯»): la fila no
    /// la ofrece. Pide confirmación y manda el lote con ESA fila, no el comando individual.
    /// </summary>
    [Fact]
    public async Task Eliminar_solo_esta_en_la_seleccion_multiple_pide_confirmacion_y_manda_esa_fila()
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id), Subcontrata("Pinturas Lauburu S.A.")] };
        var cut = Renderizar(mediador);

        cut.FindAll(".lista-filas-acordeon button").Select(b => b.TextContent.Trim())
            .Should().NotContain("Eliminar", "la fila no ofrece la baja");

        await EncenderSeleccionMultiple(cut);
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']").ChangeAsync(new ChangeEventArgs { Value = true });
        await cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados")
            .ClickAsync(new MouseEventArgs());

        cut.FindAll("[role=dialog]").Should().NotBeEmpty("el diálogo de confirmación es la barrera");
        mediador.Enviadas.OfType<EliminarSubcontratasCommand>().Should().BeEmpty("pulsar «Eliminar seleccionados» no puede borrar sin confirmar");

        await cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<EliminarSubcontratasCommand>().Single().Ids.Should().Equal([id], "se elimina la fila marcada, y solo esa");
    }

    /// <summary>En lote, un único «Deshacer» para todo el lote, y restaura solo las que el lote sí eliminó.</summary>
    [Fact]
    public async Task Eliminar_en_lote_ofrece_un_unico_Deshacer_que_restaura_las_que_cayeron()
    {
        var elegida = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            LoteDevuelveIds = true,
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: elegida), Subcontrata("Pinturas Lauburu S.A.")]
        };
        var cut = Renderizar(mediador);

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']")
            .ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").Click();
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").Click();

        var avisos = Services.GetRequiredService<ToastService>();
        var aviso = avisos.Mensajes.Single(m => m.TextoAccion == "Deshacer");

        await cut.InvokeAsync(() => avisos.EjecutarAccionAsync(aviso.Id));

        mediador.Enviadas.OfType<RestaurarSubcontrataCommand>().Select(c => c.Id).Should().Equal([elegida]);
    }

    /// <summary>
    /// La búsqueda vuelve por <c>OnParametersSet</c> desde <c>?q=</c>: quitarla
    /// solo en memoria dejaría la URL con el filtro, y la siguiente pasada de
    /// parámetros (recargar, compartir el enlace, volver atrás) lo repondría.
    /// </summary>
    [Fact]
    public void Quitar_la_busqueda_desde_el_chip_la_quita_tambien_de_la_url()
    {
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] };
        var cut = Renderizar(mediador, busqueda: "Bidasoa");
        var navegacion = Services.GetRequiredService<NavigationManager>();

        cut.Find(".chip-filtro").TextContent.Should().Contain("Bidasoa", "el filtro activo se ve");
        navegacion.Uri.Should().Contain("q=Bidasoa", "es el punto de partida de este caso");

        cut.Find(".chip-filtro-quitar").Click();

        navegacion.Uri.Should().NotContain("q=");
        cut.FindAll(".chip-filtro").Should().BeEmpty();
        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().Busqueda.Should().BeNull();
    }

    [Fact]
    public void Quitar_la_busqueda_desde_el_estado_vacio_la_quita_tambien_de_la_url()
    {
        var cut = Renderizar(new MediatorFalso(), busqueda: "Nervión");
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.Markup.Should().Contain("Ninguna subcontrata con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        navegacion.Uri.Should().NotContain("q=");
    }

    /// <summary>
    /// El doble filtra por <c>ObtenerSubcontratasQuery.Busqueda</c>: de las
    /// tres, solo una coincide con «iparra». Si la pantalla no enviara lo
    /// escrito en <c>?q=</c>, el conteo diría 3, no 1.
    /// </summary>
    [Fact]
    public void El_conteo_dice_cuantas_se_ven_de_cuantas_coinciden_con_la_busqueda()
    {
        var mediador = new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("Andamios Bidasoa S.L."),
                Subcontrata("Soldaduras Iparra S. Coop."),
                Subcontrata("Transportes Argia S.A."),
            ]
        };
        var cut = Renderizar(mediador, busqueda: "iparra");

        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Last().Busqueda.Should().Be("iparra",
            "la búsqueda escrita en ?q= es la que tiene que llegar a la consulta");
        cut.Find(".conteo-subcontratas").TextContent.Trim().Should().Be("1 de 1 subcontrata con estos filtros");
    }

    [Fact]
    public void Sin_busqueda_el_conteo_no_habla_de_ningun_filtro()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        cut.Find(".conteo-subcontratas").TextContent.Trim().Should().Be("1 de 1 subcontrata");
    }

    [Fact]
    public async Task Enter_sobre_la_fila_enfocada_abre_la_vista_rapida_como_el_clic()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)] });
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("Enter"));

        Services.GetRequiredService<ContextWorkspaceService>().FrameActual
            .Should().Be(new WorkspaceFrame(EntidadWorkspace.Subcontrata, id, "Andamios Bidasoa S.L.", "informacion"));
    }

    /// <summary>
    /// Tecla «x»: marcar una fila enciende la selección múltiple. Una fila marcada sin casilla
    /// a la vista sería selección invisible justo antes de «Eliminar seleccionados».
    /// </summary>
    [Fact]
    public async Task La_tecla_x_enciende_la_seleccion_multiple_y_marca_la_fila_enfocada()
    {
        const string casilla = "input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']";
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });
        cut.FindAll(casilla).Should().BeEmpty("punto de partida: sin casillas");

        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;
        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("x"));

        cut.Find(casilla).HasAttribute("checked").Should().BeTrue();
        cut.FindAll(".barra-acciones-lote button").Select(b => b.TextContent.Trim()).Should().Contain("Eliminar seleccionados");
    }

    /// <summary>
    /// Tecla «e»: la vista rápida de la fila enfocada, ya en edición. La petición queda en el
    /// servicio para que el panel la atienda (y solo para esa ficha).
    /// </summary>
    [Fact]
    public async Task La_tecla_e_abre_la_vista_rapida_de_la_fila_enfocada_pidiendo_edicion()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)] });
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        await cut.InvokeAsync(() => atajos.RecibirAtajo("e"));
        workspace.FrameActual.Should().BeNull("sin fila enfocada ni panel abierto, «e» no tiene qué editar");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("e"));

        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Subcontrata, id, "Andamios Bidasoa S.L.", "informacion"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Subcontrata, id).Should().BeTrue("la edición quedó pedida para esa ficha");
    }

    /// <summary>
    /// Sin fila enfocada, «e» edita la ficha que esté abierta, aunque su Subcontrata no esté en
    /// la página (el filtro la dejó fuera o la lista está vacía): el nombre sale del frame abierto.
    /// </summary>
    [Fact]
    public async Task La_tecla_e_sin_fila_enfocada_edita_la_ficha_abierta_aunque_no_este_en_la_lista()
    {
        var fueraDeLaLista = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso());
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Subcontrata, fueraDeLaLista, "Pinturas Lauburu S.A.", "supervision"));

        await cut.InvokeAsync(() => cut.FindComponent<AtajosListaTeclado>().Instance.RecibirAtajo("e"));

        workspace.FrameActual.Should().Be(new WorkspaceFrame(EntidadWorkspace.Subcontrata, fueraDeLaLista, "Pinturas Lauburu S.A.", "informacion"));
        workspace.ConsumirEdicionSolicitada(EntidadWorkspace.Subcontrata, fueraDeLaLista).Should().BeTrue();
    }

    /// <summary>
    /// El Workspace no es modal: con la ficha de la subcontrata abierta, la baja se confirma
    /// desde la lista que queda detrás, y es la lista quien retira la ficha. Solo se retira si
    /// su subcontrata iba en el lote.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Eliminar_en_lote_retira_la_ficha_abierta_solo_si_su_subcontrata_iba_en_el_lote(bool ibaEnElLote)
    {
        var elegida = Guid.NewGuid();
        var otra = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: elegida), Subcontrata("Pinturas Lauburu S.A.", id: otra)]
        };
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(
            EntidadWorkspace.Subcontrata, ibaEnElLote ? elegida : otra, "Ficha abierta", "informacion"));
        workspace.EstaAbierto.Should().BeTrue("control positivo: la ficha estaba abierta");

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']")
            .ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").Click();
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").Click();

        mediador.Enviadas.OfType<EliminarSubcontratasCommand>().Single().Ids.Should().Equal([elegida],
            "el caso solo vale si el lote pidió esa subcontrata y ninguna otra");
        workspace.EstaAbierto.Should().Be(!ibaEnElLote);
    }

    /// <summary>La guarda de la retirada: un lote que no eliminó NADA no toca la ficha abierta de una subcontrata que iba en él.</summary>
    [Fact]
    public async Task Un_lote_que_no_elimina_nada_no_retira_la_ficha_abierta()
    {
        var elegida = Guid.NewGuid();
        var mediador = new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: elegida), Subcontrata("Pinturas Lauburu S.A.")],
            EliminadosForzados = 0
        };
        var cut = Renderizar(mediador);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();
        await cut.InvokeAsync(() => workspace.AbrirAsync(EntidadWorkspace.Subcontrata, elegida, "Andamios Bidasoa S.L.", "informacion"));

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']").ChangeAsync(new ChangeEventArgs { Value = true });
        cut.FindAll(".barra-acciones-lote button").Single(b => b.TextContent.Trim() == "Eliminar seleccionados").Click();
        cut.FindAll("[role=dialog] button").Single(b => b.TextContent.Trim() == "Eliminar").Click();

        mediador.Enviadas.OfType<EliminarSubcontratasCommand>().Single().Ids.Should().Equal([elegida], "el caso solo vale si el lote pidió esa subcontrata");
        workspace.EstaAbierto.Should().BeTrue("no cayó nada: no hay nada muerto que retirar");
    }

    [Fact]
    public async Task Consulta_puede_marcar_filas_pero_no_se_le_ofrece_eliminar_las_seleccionadas()
    {
        // EliminarSubcontratasCommand es ICommand que AutorizacionEscrituraBehavior deniega a
        // Consulta. El ☑ «Selección múltiple» de la cabecera sigue (seleccionar es lectura),
        // así que se marca una fila para que la barra de lote tuviera motivo para salir.
        this.ConRolDeEscritura(Roles.Consulta);
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        cut.Find("header.cabecera-pagina button.cabecera-listado-icono[aria-label='Selección múltiple']").Click();
        await cut.Find("input[aria-label='Seleccionar la empresa Andamios Bidasoa S.L.']").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.Markup.Should().Contain("Andamios Bidasoa S.L.", "la lista es lectura: la fila se ve");
        cut.FindAll(".barra-acciones-lote").Should().BeEmpty();
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Should().NotContain("Eliminar seleccionados");
    }

    // --- Empresa gestionada activa (lote 3 del selector de Tenant beneficiario) ----------------

    private static readonly Guid Origen = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid EmpresaNorte = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid EmpresaSur = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");

    private static MediatorFalso ConCartera(bool origenGestionado)
    {
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] };
        mediador.Autorizados.Clear();
        mediador.Autorizados.AddRange(
        [
            new ClienteAutorizadoDto(Origen, "Operador de prueba", EsOrigen: true, EsGestionadoPorOperacion: origenGestionado),
            new ClienteAutorizadoDto(EmpresaNorte, "Empresa Norte", EsOrigen: false, EsGestionadoPorOperacion: true),
            new ClienteAutorizadoDto(EmpresaSur, "Empresa Sur", EsOrigen: false, EsGestionadoPorOperacion: true),
        ]);
        return mediador;
    }

    private static int ConsultasDeLista(MediatorFalso mediador) => mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Count();

    /// <summary>
    /// Rediseño de listados, fase 1: la cabecera de una línea ya no repite qué empresa
    /// gestionada está activa —lo dice el selector de la barra lateral—, pero la lista sí es la
    /// de esa empresa.
    /// </summary>
    [Fact]
    public void Con_varias_empresas_gestionadas_la_lista_es_la_de_la_activa_sin_rotulo_en_la_cabecera()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);

        var cut = Renderizar(ConCartera(origenGestionado: false));

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Find("header.cabecera-pagina .cabecera-listado-contador").TextContent.Trim().Should().Be("1");
        cut.Markup.Should().Contain("Andamios Bidasoa S.L.");
    }

    [Fact]
    public void Un_usuario_mono_Tenant_no_ve_cabecera_de_empresa_gestionada()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().Contain("Andamios Bidasoa S.L.", "la lista se pinta como siempre");
    }

    [Fact]
    public void Sin_empresa_elegida_y_con_el_origen_sin_gestionar_pide_elegir_y_no_muestra_datos_del_origen()
    {
        var mediador = ConCartera(origenGestionado: false);

        var cut = Renderizar(mediador);

        cut.Markup.Should().Contain("Selecciona una empresa de tu cartera");
        cut.Markup.Should().NotContain("Andamios Bidasoa S.L.");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty("no hay empresa activa que nombrar en el estado 4a");
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("su «Exportar a Excel» exportaría los datos del origen");
        cut.FindAll("header.cabecera-pagina .cabecera-listado-contador").Should().BeEmpty();
        ConsultasDeLista(mediador).Should().Be(0, "no se piden las subcontratas de la organización de origen");
    }

    [Fact]
    public void Con_el_origen_gestionado_y_sin_empresa_elegida_la_lista_es_la_del_origen()
    {
        var cut = Renderizar(ConCartera(origenGestionado: true));

        cut.Markup.Should().NotContain("Selecciona una empresa de tu cartera");
        cut.FindAll(".cabecera-empresa-activa").Should().BeEmpty();
        cut.Markup.Should().Contain("Andamios Bidasoa S.L.");
    }

    [Fact]
    public void Mientras_se_resuelve_la_empresa_activa_no_se_monta_la_lista_ni_se_ofrece_la_exportacion()
    {
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        var cut = Renderizar(mediador);
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty("el «⋯» lleva «Exportar a Excel»");
        ConsultasDeLista(mediador).Should().Be(0);

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Selecciona una empresa de tu cartera"));
        cut.FindAll("header.cabecera-pagina .menu-acciones").Should().BeEmpty();
        ConsultasDeLista(mediador).Should().Be(0, "ni antes ni después de resolverse se pide la lista del origen");
    }

    [Fact]
    public void Con_el_contexto_resuelto_a_una_empresa_la_lista_se_monta_tras_la_carga()
    {
        Seleccion = new SeleccionEmpresaGestionadaDePrueba(EmpresaSur);
        var mediador = ConCartera(origenGestionado: false);
        var puerta = new TaskCompletionSource();
        mediador.RetenerAutorizados = puerta.Task;

        var cut = Renderizar(mediador);
        ConsultasDeLista(mediador).Should().Be(0);
        cut.Markup.Should().NotContain("Andamios Bidasoa S.L.");

        puerta.SetResult();

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Andamios Bidasoa S.L."));
    }

    /// <summary>
    /// S12 (lote 2a): el alta usa el kit DrawerFormulario, que pregunta al «Cancelar» como la X (D-05); la salida por navegación la fijan las pruebas de aviso de la pantalla.
    /// Esta prueba fija que la pantalla le pasa su «hay cambios» y su estado: sin cambios cierra, con la razón social
    /// escrita pregunta.
    /// </summary>
    [Fact]
    public async Task El_alta_pregunta_al_cancelar_con_datos_escritos_y_sin_cambios_cierra()
    {
        var cut = Renderizar(new MediatorFalso());
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("+ Nueva subcontrata"));

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nueva subcontrata").ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.ComprobarQueCancelarSinCambiosCierraAsync(".drawer-pie", ".drawer-panel");

        await cut.FindAll("button").First(b => b.TextContent.Trim() == "+ Nueva subcontrata").ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().NotBeEmpty());
        await cut.Find(".drawer-cuerpo input.campo-input").InputAsync(new ChangeEventArgs { Value = "Andamios Nuevos S.L." });
        await cut.PulsarCancelarDelPieAsync(".drawer-pie");

        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }

    /// <summary>
    /// Listados 3/7 (decisión del 2026-10-08): la incidencia de la ventana de contexto se pulsa y
    /// abre su corrección sin salir del listado. Toda incidencia de una Subcontrata es de un
    /// Trabajador: es pulsable si trae el Documento, o el Tipo junto con el Trabajador.
    /// </summary>
    [Fact]
    public async Task La_incidencia_de_la_ventana_abre_la_correccion_de_su_documento()
    {
        this.ConServiciosDelFormularioDeDocumento();
        var documentoId = Guid.NewGuid();
        var vencida = new IncidenciaSubcontrataDto(
            "Aptitud médica — Sonia Cano", EstadoDocumento.Vencido, documentoId, Guid.NewGuid(), null, Guid.NewGuid());
        var urgente = new IncidenciaSubcontrataDto(
            "Entrega de EPI — Carla Molina", EstadoDocumento.Urgente, Guid.NewGuid(), Guid.NewGuid(), null, Guid.NewGuid());
        var mediador = new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60, vencidas: [vencida], proximas: [urgente])]
        };
        var cut = Renderizar(mediador);

        var ventanas = cut.FindAll(".celda-recuento-subcontrata .ventana-contexto");
        ventanas.Should().HaveCount(2);
        ventanas.Should().OnlyContain(v => v.ClassList.Contains("ventana-contexto-interactiva"));
        // El panel abre hacia abajo (Subcontratas.razor.css): el puente del cursor va en ese lado.
        ventanas.Should().OnlyContain(v => v.ClassList.Contains("ventana-contexto-abajo"));
        // En «Próximos» el botón conserva el badge que distingue Urgente de Próximo.
        ventanas[1].QuerySelector("button.ventana-contexto-elemento")!.TextContent.Should().Contain("Entrega de EPI — Carla Molina");
        ventanas[1].QuerySelector("button.ventana-contexto-elemento .badge").Should().NotBeNull();

        await ventanas[0].QuerySelector("button.ventana-contexto-elemento")!.ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Queries.ObtenerDocumentoPorId.ObtenerDocumentoPorIdQuery>()
            .Should().ContainSingle().Which.Id.Should().Be(documentoId);
    }

    [Fact]
    public void Una_incidencia_sin_trabajador_ni_documento_sigue_siendo_texto()
    {
        var mediador = new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60,
                vencidas: [Incidencia("Aptitud médica — Sonia Cano", EstadoDocumento.Vencido)])]
        };

        var cut = Renderizar(mediador);

        var ventana = cut.Find(".celda-recuento-subcontrata .ventana-contexto");
        ventana.ClassList.Should().NotContain("ventana-contexto-interactiva");
        ventana.QuerySelectorAll("button").Should().BeEmpty();
        ventana.QuerySelector(".ventana-linea")!.TextContent.Should().Be("Aptitud médica — Sonia Cano");
    }

    [Fact]
    public async Task Tras_corregir_una_incidencia_la_lista_se_vuelve_a_pedir_sin_recargar_la_pagina()
    {
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", cumplimiento: 60)] };
        var cut = Renderizar(mediador);
        var consultasDeListaAntes = mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Count();
        var correccion = cut.FindComponent<CaeManager.Web.Features.Documentos.Components.CorreccionIncidenciaDocumental>();

        await cut.InvokeAsync(() => correccion.Instance.OnCorregida.InvokeAsync());

        mediador.Enviadas.OfType<ObtenerSubcontratasQuery>().Should().HaveCount(consultasDeListaAntes + 1,
            "el recuento y el cumplimiento de la fila cambian al corregir: la lista se relee en sitio");
    }
}
