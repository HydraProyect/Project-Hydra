using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Subcontratas.Commands.CrearSubcontrata;
using CaeManager.Application.Subcontratas.Commands.EliminarSubcontrata;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratas;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
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
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Lista de Subcontratas contra su mockup Gen 2 («Subcontratas TALVEG.dc.html»).
/// Cubre lo que el rediseño añadió o cambió; lo que ya existía y se conserva
/// —vacío por filtro, su copia en singular— lo sigue probando
/// <see cref="SubcontratasVacioPorFiltroTests"/>.
///
/// <para>
/// El cambio de más peso es de navegación: el nombre de la fila y «Detalles»
/// abren ahora una vista previa, y Subcontrata 360 queda detrás de «Operar →»
/// y del menú de la fila — mismo patrón que Empresas y Vehículos.
/// </para>
/// </summary>
public class SubcontratasListaGen2Tests : BunitContext
{
    /// <summary>La página monta AtajosListaTeclado, que importa ./js/atajos-lista.js.</summary>
    public SubcontratasListaGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private sealed class MediatorFalso : IMediator
    {
        public IReadOnlyList<SubcontrataListaDto> Subcontratas { get; init; } = [];
        public SubcontrataDetalleDto? Detalle { get; init; }

        /// <summary>
        /// Solo la consulta de trabajadores filtrada por ESTA subcontrata devuelve
        /// el recuento: si la vista previa filtrara por otro campo, recibiría 0.
        /// </summary>
        public Guid SubcontrataConTrabajadores { get; init; }
        public int TrabajadoresDeEsaSubcontrata { get; init; }

        public IReadOnlyList<EmpresaSelectorDto> Empresas { get; init; } = [];
        public IReadOnlyList<ClienteSelectorDto> Clientes { get; init; } = [];
        public Result ResultadoEliminar { get; init; } = Result.Exito();

        public List<object> Enviadas { get; } = [];

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviadas.Add(request);
            return Task.FromResult((TResponse)(request switch
            {
                ObtenerPerfilVocabularioActualQuery => (object)PerfilVocabularioTenant.Consultora,
                ObtenerSubcontratasQuery q => FiltrarPorBusqueda(q),
                ObtenerSubcontrataPorIdQuery q => Detalle is not null && Detalle.Id == q.Id ? Detalle : null!,
                ObtenerTrabajadoresQuery q => new ResultadoPaginado<TrabajadorListaDto>(
                    [], q.SubcontrataId == SubcontrataConTrabajadores ? TrabajadoresDeEsaSubcontrata : 0, 1, 1),
                ObtenerEmpresasParaSelectorQuery => Empresas,
                ObtenerClientesParaSelectorQuery => Clientes,
                EliminarSubcontrataCommand => ResultadoEliminar,
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));
        }

        /// <summary>
        /// Filtra por <c>Busqueda</c> como lo haría el handler (razón social,
        /// sin distinguir mayúsculas): si la pantalla no enviara lo escrito en
        /// <c>?q=</c>, recibiría la lista entera y el conteo lo delataría.
        /// </summary>
        private ResultadoPaginado<SubcontrataListaDto> FiltrarPorBusqueda(ObtenerSubcontratasQuery q)
        {
            var coincidentes = q.Busqueda is null
                ? Subcontratas
                : Subcontratas.Where(s => s.RazonSocial.Contains(q.Busqueda, StringComparison.OrdinalIgnoreCase)).ToList();
            return new ResultadoPaginado<SubcontrataListaDto>(coincidentes, coincidentes.Count, q.Pagina, q.TamanoPagina);
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
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
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

    private static SubcontrataDetalleDto Detalle(Guid id, params Guid[] empresaIds) => new(
        id, "Andamios Bidasoa S.L.", "B-20.774.115", new DateTime(2021, 4, 10, 0, 0, 0, DateTimeKind.Utc),
        ClienteIds: [], EmpresaIds: empresaIds, Guid.NewGuid(), NivelServicioSubcontrata.Gestionada);

    /// <param name="busqueda">Valor del filtro de texto que llega por la URL (?q=).</param>
    private IRenderedComponent<Subcontratas> Renderizar(MediatorFalso mediador, string? busqueda = null)
    {
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearSubcontrataCommand>>(_ => new InlineValidator<CrearSubcontrataCommand>());

        // El filtro es [SupplyParameterFromQuery]: se llega a él navegando, no
        // pasándolo como parámetro de componente.
        Services.GetRequiredService<NavigationManager>()
            .NavigateTo(busqueda is null ? "subcontratas" : "subcontratas?q=" + Uri.EscapeDataString(busqueda));

        return Render<Subcontratas>();
    }

    /// <summary>Valor de una celda de la rejilla de Información de la vista previa, por su rótulo.</summary>
    private static string Celda(IElement panel, string rotulo) =>
        panel.QuerySelectorAll(".celda-info-preview-subcontrata")
            .Where(c => c.QuerySelector("span")?.TextContent.Trim() == rotulo)
            .Select(c => c.QuerySelector("strong")?.TextContent.Trim() ?? string.Empty)
            .Should().ContainSingle($"la rejilla tiene que tener exactamente una celda «{rotulo}»").Subject;

    private static void AbrirMenuYPulsar(IRenderedComponent<Subcontratas> cut, string item)
    {
        // MenuAcciones no pinta sus ítems hasta que se abre.
        cut.Find(".menu-acciones-disparador").Click();
        cut.FindAll(".menu-acciones-item").Single(b => b.TextContent.Trim() == item).Click();
    }

    [Fact]
    public void La_cabecera_lleva_el_kicker_de_su_grupo_y_las_acciones_de_la_pagina()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        cut.Find("header.cabecera-pagina .cabecera-pagina-kicker").TextContent.Trim().Should().Be("Negocio",
            "el kicker es el grupo del menú al que pertenece la pantalla");
        cut.Find("header.cabecera-pagina h1").TextContent.Trim().Should().Be("Subcontratas");
        cut.Find("header.cabecera-pagina .acciones-cabecera").TextContent.Should()
            .Contain("Exportar a Excel")
            .And.Contain("+ Nueva subcontrata", "el alta sube a la cabecera, fuera de la barra que actúa sobre la lista");
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
            .And.Contain("Cumplimiento").And.Contain("Vencidos").And.Contain("Próximos");
        cut.Find(".tarjeta-fila-acordeon-cabecera").Children.Length.Should().Be(cabecera.Children.Length,
            "sin selección múltiple");

        cut.FindAll(".barra-herramientas-lista button").Single(b => b.TextContent.Contains("Selección múltiple")).Click();

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

        var badges = cut.FindAll(".celda-recuento-subcontrata .badge").Select(b => b.TextContent.Trim()).ToList();

        badges.Should().Contain("2 vencidos").And.Contain("1 próximo").And.Contain("1 vencido")
            .And.Contain("Al corriente", "sin vencidos ni próximos la subcontrata está al corriente");
        cut.FindAll(".celda-sin-recuento").Should().HaveCount(1,
            "solo Transportes Argia no tiene vencidos, y su celda se reserva con un guion");
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

    [Fact]
    public void El_nombre_de_la_fila_abre_la_vista_previa_y_no_Subcontrata_360()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)],
            Detalle = Detalle(id)
        });

        cut.Find(".celda-identidad-subcontrata .enlace-nombre-fila").Click();

        cut.Find("aside.drawer-preview-subcontrata").TextContent.Should().Contain("Consulta · Subcontrata");
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse(
            "Subcontrata 360 se abre desde «Operar →» o desde el menú, no desde el nombre");
    }

    [Fact]
    public void La_vista_previa_cuenta_los_trabajadores_de_esa_subcontrata_y_reutiliza_los_datos_de_la_fila()
    {
        var id = Guid.NewGuid();
        var refrielectric = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id, cumplimiento: 58)],
            Detalle = Detalle(id, refrielectric),
            SubcontrataConTrabajadores = id,
            TrabajadoresDeEsaSubcontrata = 3,
            Empresas = [new EmpresaSelectorDto(refrielectric, "Refrielectric S.A.")]
        });

        cut.Find(".celda-identidad-subcontrata .enlace-nombre-fila").Click();
        var panel = cut.Find("aside.drawer-preview-subcontrata");

        Celda(panel, "Trabajadores").Should().Be("3", "el recuento sale de los trabajadores filtrados por ESTA subcontrata");
        Celda(panel, "CIF").Should().Be("B-20.774.115");
        Celda(panel, "Cumplimiento").Should().Be("58%", "es el mismo dato que enseña la fila");
        Celda(panel, "Presta servicio a").Should().Be("Refrielectric S.A.");
    }

    /// <summary>
    /// Prueba solo presentación, contra un doble del mediador: si la relación
    /// trae un Id cuyo nombre no llega en las listas de los selectores, se
    /// cuenta como «y N más» en vez de omitirse — omitirlo haría parecer
    /// completa una lista que no lo es.
    /// <para>
    /// NO prueba ninguna garantía de alcance: qué nombres llegan lo deciden los
    /// handlers de los selectores, y aquí los sustituye el doble. (Hoy el de
    /// Empresas ni siquiera acota por cartera; ver el comentario de
    /// <c>SubcontrataPreviewDrawer.ResolverPrestaServicioAAsync</c>.)
    /// </para>
    /// </summary>
    [Fact]
    public void Presta_servicio_a_cuenta_como_y_N_mas_las_relaciones_cuyo_nombre_no_llega_en_vez_de_omitirlas()
    {
        var id = Guid.NewGuid();
        var refrielectric = Guid.NewGuid();
        var sinNombreEnLaLista = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)],
            Detalle = Detalle(id, refrielectric, sinNombreEnLaLista),
            Empresas = [new EmpresaSelectorDto(refrielectric, "Refrielectric S.A.")]
        });

        cut.Find(".celda-identidad-subcontrata .enlace-nombre-fila").Click();

        Celda(cut.Find("aside.drawer-preview-subcontrata"), "Presta servicio a").Should().Be("Refrielectric S.A. y 1 más");
    }

    [Fact]
    public void La_documentacion_de_la_vista_previa_lista_vencidos_y_proximos_de_sus_trabajadores()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso
        {
            Subcontratas =
            [
                Subcontrata("Andamios Bidasoa S.L.", id: id,
                    vencidas: [Incidencia("Formación PRL — Iñaki Otaegi", EstadoDocumento.Vencido)],
                    proximas: [Incidencia("Reconocimiento médico — Miguel Sanz", EstadoDocumento.Proximo)])
            ],
            Detalle = Detalle(id)
        });

        cut.Find(".celda-identidad-subcontrata .enlace-nombre-fila").Click();
        cut.FindAll("aside.drawer-preview-subcontrata [role=tab]").Single(t => t.TextContent.Trim() == "Documentación").Click();

        cut.FindAll(".fila-incidencia-preview-subcontrata").Select(f => f.TextContent).Should().HaveCount(2)
            .And.Contain(t => t.Contains("Formación PRL — Iñaki Otaegi"))
            .And.Contain(t => t.Contains("Reconocimiento médico — Miguel Sanz"));
    }

    [Fact]
    public void Eliminar_desde_el_menu_pide_confirmacion_y_despues_manda_el_comando_de_esa_fila()
    {
        var id = Guid.NewGuid();
        var mediador = new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)] };
        var cut = Renderizar(mediador);

        AbrirMenuYPulsar(cut, "Eliminar");

        cut.Markup.Should().Contain("¿Eliminar a Andamios Bidasoa S.L.?", "el diálogo de confirmación es la barrera");
        mediador.Enviadas.OfType<EliminarSubcontrataCommand>().Should().BeEmpty("pulsar el menú no puede borrar sin confirmar");

        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Eliminar").Click();

        mediador.Enviadas.OfType<EliminarSubcontrataCommand>().Select(c => c.Id).Should().Equal([id],
            "se elimina la fila cuyo menú se abrió, y solo esa");
    }

    [Fact]
    public void Si_el_comando_rechaza_la_eliminacion_se_ensena_su_motivo()
    {
        const string motivo = "No puedes eliminar una subcontrata con trabajadores. Da de baja a sus trabajadores primero.";
        var mediador = new MediatorFalso
        {
            Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")],
            ResultadoEliminar = Result.Fallo(Error.Crear("Subcontrata.TieneTrabajadores", motivo))
        };
        var cut = Renderizar(mediador);

        AbrirMenuYPulsar(cut, "Eliminar");
        cut.FindAll("button").Single(b => b.TextContent.Trim() == "Eliminar").Click();

        Services.GetRequiredService<ToastService>().Mensajes.Should()
            .ContainSingle(m => m.Mensaje == motivo && m.Tono == TonoToast.Error,
                "el motivo lo da el comando, y es lo único que le dice al usuario qué hacer");
    }

    [Fact]
    public void Abrir_Subcontrata_360_desde_el_menu_abre_el_workspace_de_esa_fila()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)] });

        AbrirMenuYPulsar(cut, "Abrir Subcontrata 360");

        var frame = Services.GetRequiredService<ContextWorkspaceService>().FrameActual;
        frame.Should().NotBeNull("el menú ofrece el 360 directamente, sin pasar por la vista previa");
        frame!.Tipo.Should().Be(EntidadWorkspace.Subcontrata);
        frame.EntidadId.Should().Be(id);
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
        cut.Markup.Should().Contain("Ninguna subcontrata con esta búsqueda", "es el punto de partida de este caso");

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
        cut.Find(".conteo-subcontratas").TextContent.Trim().Should().Be("1 de 1 subcontrata con esta búsqueda");
    }

    [Fact]
    public void Sin_busqueda_el_conteo_no_habla_de_ningun_filtro()
    {
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.")] });

        cut.Find(".conteo-subcontratas").TextContent.Trim().Should().Be("1 de 1 subcontrata");
    }

    [Fact]
    public async Task Enter_sobre_la_fila_enfocada_abre_la_vista_previa_como_el_nombre()
    {
        var id = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Subcontratas = [Subcontrata("Andamios Bidasoa S.L.", id: id)], Detalle = Detalle(id) });
        var atajos = cut.FindComponent<AtajosListaTeclado>();

        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("j"));
        await cut.InvokeAsync(() => atajos.Instance.OnAtajo.InvokeAsync("Enter"));

        cut.FindAll("aside.drawer-preview-subcontrata").Should().ContainSingle(
            "Enter hace lo mismo que pulsar el nombre de la fila enfocada");
        Services.GetRequiredService<ContextWorkspaceService>().EstaAbierto.Should().BeFalse();
    }
}
