using Bunit;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Tipos de Documento no tenía <b>ningún</b> estado vacío: con filtros puestos
/// y cero resultados salían las cabeceras de la tabla y ninguna fila, y arriba
/// seguía el botón «+ Nuevo tipo». Quien acababa de escribir mal un alias no
/// tenía forma de saber si el tipo no existe o si es el filtro, y lo único que
/// se le ofrecía era crear uno que probablemente ya está dado de alta.
///
/// <para>
/// La entrada de la deuda congelada de <c>ListasDistinguenVacioPorFiltroTests</c>
/// decía «ya tiene chips de filtro, le falta el estado». <b>Era falso</b>: los
/// <c>chips-filtros</c> de esta página son los alias del formulario de edición,
/// dentro del Drawer. Una anotación no comprobada vale menos que el código.
/// </para>
///
/// <para>
/// <b>Los filtros no viajan por la URL</b>, a diferencia de Empresas o
/// Auditoría: se ponen desde la barra, así que aquí se accionan como el
/// usuario. Eso obliga a elegir el control con cuidado — ver
/// <see cref="Buscar"/> y <see cref="FiltrarPorCliente"/>.
/// </para>
/// </summary>
public class TiposDocumentoVacioPorFiltroTests : BunitContext
{
    private static readonly ClienteSelectorDto ClienteDePrueba =
        new(Guid.NewGuid(), "Refrielectric S.A.");

    /// <summary>
    /// Devuelve los tipos solo cuando la consulta llega SIN filtros; con
    /// cualquier filtro puesto devuelve la lista vacía, que es el escenario que
    /// estos casos miran. Registra la última consulta para comprobar que
    /// «Quitar los filtros» los limpia de verdad y no solo repinta el estado.
    /// </summary>
    private sealed class MediatorQueFiltraDeVerdad(IReadOnlyList<TipoDocumentoListaDto> tipos) : IMediator
    {
        public ObtenerTiposDocumentoQuery? UltimaConsulta { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            switch (request)
            {
                case ObtenerClientesParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<ClienteSelectorDto>)[ClienteDePrueba]);

                case ObtenerEmpresasParaSelectorQuery:
                    return Task.FromResult((TResponse)(object)(IReadOnlyList<EmpresaSelectorDto>)[]);

                case ObtenerTiposDocumentoQuery q:
                    UltimaConsulta = q;
                    var hayFiltro = q.ClienteId is not null || q.EmpresaId is not null
                        || q.CentroId is not null || !string.IsNullOrWhiteSpace(q.Texto);
                    return Task.FromResult((TResponse)(object)(hayFiltro ? [] : tipos));

                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
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

    private static TipoDocumentoListaDto Tipo(string nombre) => new(
        Guid.NewGuid(), nombre, VigenciaMeses: 12, AplicaVencimientoAutomatico: true, Orden: 1,
        AmbitoAplicacion.Empresa, RequisitoDocumental.Si, NaturalezaJuridica.ObligacionLegal,
        Descripcion: null, CriteriosValidacion: null, SeSolicitaA: null, Observaciones: null,
        LecturaIaActiva: false, DeteccionTrabajadoresActiva: false, VerificacionIaActiva: false,
        PerfilDocumentoOficial.Ninguno, Aliases: []);

    private MediatorQueFiltraDeVerdad _mediator = null!;

    private IRenderedComponent<Features.TiposDocumento.Pages.TiposDocumento> Renderizar(
        params TipoDocumentoListaDto[] tipos)
    {
        _mediator = new MediatorQueFiltraDeVerdad(tipos);
        Services.AddScoped<IMediator>(_ => _mediator);
        Services.AddScoped<ToastService>();

        return Render<Features.TiposDocumento.Pages.TiposDocumento>();
    }

    /// <summary>
    /// El buscador rebota 300 ms y el <c>blur</c> cancela el rebote notificando
    /// ya mismo, así que esto deja la pantalla filtrada de inmediato.
    ///
    /// <para>
    /// <b>Pero deja vivo un temporizador de 300 ms</b>, y eso lo hace
    /// inservible para un caso que después pulse un botón: medido, tres de cada
    /// seis pases de la suite completa se tragaban el clic —el manejador no
    /// llegaba a ejecutarse, sin excepción y sin tercera consulta— y con 500 ms
    /// de espera por medio pasaba 6 de 6. Un caso que interactúe después del
    /// filtro usa <see cref="FiltrarPorCliente"/>, que no rebota. Y ojo: el
    /// fallo NO aparecía ejecutando esta clase con <c>--filter</c>; hizo falta
    /// la suite entera para verlo.
    /// </para>
    /// </summary>
    private static void Buscar(IRenderedComponent<Features.TiposDocumento.Pages.TiposDocumento> cut, string texto)
    {
        var buscador = cut.Find(".barra-filtros input[type=text]");
        buscador.Input(texto);
        buscador.Blur();
    }

    /// <summary>El selector de cliente no rebota: su <c>onchange</c> filtra en el acto.</summary>
    private static void FiltrarPorCliente(IRenderedComponent<Features.TiposDocumento.Pages.TiposDocumento> cut)
        => cut.Find(".barra-filtros select").Change(ClienteDePrueba.Id.ToString());

    [Fact]
    public void Buscando_algo_que_no_aparece_no_se_invita_a_crear_el_primero()
    {
        var cut = Renderizar(Tipo("TC2"));

        Buscar(cut, "TC-dos");

        cut.Markup.Should().Contain("Ningún tipo con estos filtros");
        cut.Markup.Should().Contain("Quitar los filtros");
        cut.Markup.Should().NotContain("Aún no hay tipos de documento",
            "ofrecer «crea el primero» a quien acaba de teclear mal un alias lo manda a duplicar un tipo que ya existe");
    }

    [Fact]
    public void Antes_de_filtrar_la_tabla_con_datos_no_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(Tipo("TC2"));

        cut.Find("table.tabla-datos").TextContent.Should().Contain("TC2");
        cut.Markup.Should().NotContain("Ningún tipo con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay tipos de documento");
    }

    [Fact]
    public void Sin_filtros_y_sin_ningun_tipo_sigue_invitando_a_crear_el_primero()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("Aún no hay tipos de documento");
        cut.Markup.Should().NotContain("Ningún tipo con estos filtros");
    }

    [Fact]
    public void Filtrando_por_cliente_tampoco_se_invita_a_crear_el_primero()
    {
        var cut = Renderizar(Tipo("TC2"));

        FiltrarPorCliente(cut);

        cut.Markup.Should().Contain("Ningún tipo con estos filtros");
        cut.Markup.Should().NotContain("Aún no hay tipos de documento");
    }

    /// <summary>
    /// El filtrado es de servidor: la consulta devuelve el total YA filtrado,
    /// así que la pantalla no sabe cuántos tipos hay sin filtro y la copia no
    /// puede afirmarlo. La barrera va delante — si la rama no se activara, una
    /// aserción de ausencia sola sería verde vacío.
    /// </summary>
    [Fact]
    public void La_copia_no_afirma_cuantos_tipos_hay_dados_de_alta()
    {
        var cut = Renderizar(Tipo("TC2"));

        Buscar(cut, "TC-dos");

        cut.Markup.Should().Contain("Ningún tipo con estos filtros");
        cut.Markup.Should().NotContain("Hay tipos de documento dados de alta");
    }

    [Fact]
    public void Quitar_los_filtros_los_limpia_en_la_consulta_y_devuelve_la_lista()
    {
        var cut = Renderizar(Tipo("TC2"));
        FiltrarPorCliente(cut);
        cut.Markup.Should().Contain("Ningún tipo con estos filtros", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        _mediator.UltimaConsulta!.ClienteId.Should().BeNull(
            "«Quitar los filtros» tiene que limpiar el filtro de verdad, no solo repintar el estado");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("TC2");
        cut.Markup.Should().NotContain("Ningún tipo con estos filtros");
    }

    /// <summary>
    /// Los <c>chips-filtros</c> que la deuda congelada daba por filtros de esta
    /// página son en realidad los alias del formulario, dentro del Drawer, y el
    /// Drawer arranca cerrado. Este caso ancla el hecho para que la anotación
    /// falsa no vuelva.
    /// </summary>
    [Fact]
    public void La_barra_de_filtros_no_tiene_chips_ningunos()
    {
        var cut = Renderizar(Tipo("TC2"));

        FiltrarPorCliente(cut);

        // Barrera: la barra existe y tiene sus cuatro controles. Sin ella, la
        // ausencia de chips daría verde igual si la barra no se hubiera pintado.
        cut.Find(".barra-filtros").Children.Length.Should().Be(4);
        cut.FindAll(".barra-filtros .chip-filtro").Should().BeEmpty();
    }
}
