using System.Reflection;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="DrawerGestionDocumento.AbrirCrearParaFaltanteAsync"/> y
/// <see cref="DrawerGestionDocumento.AbrirCrearParaFaltanteEmpresaAsync"/> son
/// la API pública que Documentos.razor y AcordeonAsignacionesCentro.razor
/// invocan vía @ref (ver el comentario normativo de
/// DrawerGestionDocumento.razor.cs) con un Id que Documentos.razor.cs leyó de
/// la query string. Hasta 2026-09-12 lo asignaban directo a
/// _trabajadorId/_empresaId sin comprobar que ese Id estuviera en el catálogo
/// con alcance ya cargado para este usuario — mismo defecto que
/// AltaGuiada.razor.cs corregía por la misma fecha (ver
/// AltaGuiadaResolucionIdentificadoresTests): una coordenada de contexto no es
/// autoridad. Centros.razor.cs y Empresas.razor.cs ya comprobaban esto con
/// <c>.Any(x => x.Id == idEntrante)</c> contra la lista con alcance ya
/// cargada; este componente sigue ahora el mismo patrón.
/// </summary>
public class DrawerGestionDocumentoPreseleccionAlcanceTests : BunitContext
{
    private sealed class MediatorFalso : IMediator
    {
        public required IReadOnlyList<TrabajadorSelectorDto> Trabajadores { get; init; }
        public required IReadOnlyList<EmpresaSelectorDto> Empresas { get; init; }
        public List<AlcanceSelectorTrabajadores> AlcancesPedidos { get; } = [];

        private IReadOnlyList<TrabajadorSelectorDto> Registrar(ObtenerTrabajadoresParaSelectorQuery consulta)
        {
            AlcancesPedidos.Add(consulta.Alcance);
            return Trabajadores;
        }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerTrabajadoresParaSelectorQuery consulta => Registrar(consulta),
                ObtenerEmpresasParaSelectorQuery => Empresas,
                ObtenerTiposDocumentoQuery => (IReadOnlyList<TipoDocumentoListaDto>)[],
                _ => throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.")
            }));

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

    /// <summary>Ningún test de esta suite sube ni descarta un archivo — si alguno lo hiciera, sería una regresión de camino, no de esta comprobación.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Este test no sube ni descarta ningún archivo; si esto salta, el camino cambió.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Este test no convierte nada; si esto salta, el camino cambió.");
    }

    private IRenderedComponent<DrawerGestionDocumento> Renderizar(MediatorFalso mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));

        return Render<DrawerGestionDocumento>();
    }

    /// <summary>
    /// _trabajadorId/_empresaId son privados y el markup no distingue "vacío"
    /// de "un Id que no resolvió" (CampoBuscarSelect solo pinta texto para un
    /// Id que SÍ está en sus Opciones) — el propio valor interno es la única
    /// forma de observar la propiedad, igual que Centro360Gen2Tests con
    /// _generacion.
    /// </summary>
    private static string? LeerCampoPrivado(DrawerGestionDocumento instancia, string nombreCampo)
    {
        var campo = typeof(DrawerGestionDocumento).GetField(nombreCampo, BindingFlags.Instance | BindingFlags.NonPublic);
        campo.Should().NotBeNull($"el test necesita leer {nombreCampo} directamente");
        return (string?)campo!.GetValue(instancia);
    }

    [Fact]
    public async Task Un_trabajadorId_ajeno_al_catalogo_cargado_no_preselecciona_nada()
    {
        var trabajadorAjeno = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Trabajadores = [], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(trabajadorAjeno, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_trabajadorId").Should().BeEmpty(
            "un Id que no está en el catálogo con alcance ya cargado no puede quedar preseleccionado");
    }

    [Fact]
    public async Task Un_trabajadorId_presente_en_el_catalogo_cargado_si_se_preselecciona()
    {
        var trabajador = new TrabajadorSelectorDto(Guid.NewGuid(), "Ruiz Peña, Ana", null);
        var cut = Renderizar(new MediatorFalso { Trabajadores = [trabajador], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(trabajador.Id, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_trabajadorId").Should().Be(trabajador.Id.ToString(),
            "un Id que sí está en el catálogo cargado para este alcance debe seguir preseleccionándose, igual que antes del fix");
    }

    /// <summary>
    /// El selector de Trabajador del drawer cuelga un Documento de un Trabajador ya existente:
    /// pide el catálogo acotado a la cartera, nunca la base general del Tenant. Los mocks del resto
    /// de la suite aceptan la consulta sea cual sea su alcance; este test es el que lo fija.
    /// </summary>
    [Fact]
    public async Task El_drawer_pide_el_selector_de_Trabajadores_acotado_a_la_cartera()
    {
        var mediator = new MediatorFalso { Trabajadores = [], Empresas = [] };
        var cut = Renderizar(mediator);

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearAsync());

        mediator.AlcancesPedidos.Should().Equal(AlcanceSelectorTrabajadores.Cartera);
    }

    /// <summary>
    /// Un faltante de un Trabajador fuera de la cartera (p. ej. desde Visitas, que usa la base
    /// general) no deja el selector vacío sin explicación: el drawer lo dice, con el mismo texto
    /// exista o no el Trabajador fuera del alcance, sin revelar su nombre ni su DNI.
    /// </summary>
    [Fact]
    public async Task Un_trabajadorId_ajeno_al_catalogo_cargado_avisa_de_que_no_lo_encontramos()
    {
        var visible = new TrabajadorSelectorDto(Guid.NewGuid(), "Ruiz Peña, Ana", null);
        var cut = Renderizar(new MediatorFalso { Trabajadores = [visible], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(Guid.NewGuid(), Guid.NewGuid()));

        cut.Find("[role=alert]").TextContent.Should().Contain("No encontramos este trabajador");
    }

    /// <summary>
    /// P4 (2026-09-23): el selector de Trabajador del drawer, de alcance Cartera, no enseña el DNI:
    /// ni en las opciones del buscador ni en el texto del Trabajador preseleccionado. Los homónimos
    /// se distinguen por el Alias. El fake siembra un DNI conocido en el origen
    /// (<see cref="TrabajadorSelectorFalso"/>).
    /// </summary>
    [Fact]
    public async Task El_selector_de_Trabajador_no_muestra_el_DNI_y_distingue_homonimos_por_alias()
    {
        var conAlias = TrabajadorSelectorFalso.Crear(Guid.NewGuid(), "Ana Ruiz Peña", alias: "Anita");
        var sinAlias = TrabajadorSelectorFalso.Crear(Guid.NewGuid(), "Ana Ruiz Peña");
        var cut = Renderizar(new MediatorFalso { Trabajadores = [conAlias, sinAlias], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(conAlias.Id, Guid.NewGuid()));

        var buscador = cut.FindComponents<CampoBuscarSelect>().Single(c => c.Instance.Etiqueta == "Trabajador");
        buscador.Instance.Opciones.Select(o => o.Texto).Should().Equal("Ana Ruiz Peña — Anita", "Ana Ruiz Peña");
        cut.Markup.Should().Contain("Ana Ruiz Peña — Anita").And.NotContain(TrabajadorSelectorFalso.DniSembrado);
    }

    [Fact]
    public async Task Un_trabajadorId_presente_en_el_catalogo_cargado_no_pinta_ningun_aviso()
    {
        var trabajador = new TrabajadorSelectorDto(Guid.NewGuid(), "Ruiz Peña, Ana", null);
        var cut = Renderizar(new MediatorFalso { Trabajadores = [trabajador], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteAsync(trabajador.Id, Guid.NewGuid()));

        cut.FindAll("[role=alert]").Should().NotContain(a => a.TextContent.Contains("No encontramos este trabajador"));
    }

    [Fact]
    public async Task Un_empresaId_ajeno_al_catalogo_cargado_no_preselecciona_nada()
    {
        var empresaAjena = Guid.NewGuid();
        var cut = Renderizar(new MediatorFalso { Trabajadores = [], Empresas = [] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteEmpresaAsync(empresaAjena, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_empresaId").Should().BeEmpty(
            "un Id que no está en el catálogo con alcance ya cargado no puede quedar preseleccionado");
    }

    [Fact]
    public async Task Un_empresaId_presente_en_el_catalogo_cargado_si_se_preselecciona()
    {
        var empresa = new EmpresaSelectorDto(Guid.NewGuid(), "Montajes Ebro S.L.");
        var cut = Renderizar(new MediatorFalso { Trabajadores = [], Empresas = [empresa] });

        await cut.InvokeAsync(() => cut.Instance.AbrirCrearParaFaltanteEmpresaAsync(empresa.Id, Guid.NewGuid()));

        LeerCampoPrivado(cut.Instance, "_empresaId").Should().Be(empresa.Id.ToString(),
            "un Id que sí está en el catálogo cargado para este alcance debe seguir preseleccionándose, igual que antes del fix");
    }
}
