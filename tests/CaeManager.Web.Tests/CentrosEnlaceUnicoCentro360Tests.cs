using Bunit;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros.Commands.CrearCentro;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Centros.Components;
using CaeManager.Web.Features.Centros.Pages;
using FluentAssertions;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Un solo camino a la ficha del centro (<c>/centros/{id}</c>) por fila
/// expandida de la lista de Centros — con el acordeón de asignaciones REAL.
///
/// <para>
/// <see cref="CentrosListaGen2Tests"/> sustituye el acordeón por un stub, así
/// que no podía ver que sus propios enlaces a Centro 360 («Ver los N en Centro
/// 360», «Ver Centro 360 →») se sumaban al enlace de la página: dos caminos al
/// mismo destino en la misma fila. Aquí el acordeón se monta de verdad, en los
/// tres estados en que cambia lo que pinta.
/// </para>
///
/// <para>
/// Un «camino» es un <c>&lt;a&gt;</c> con <c>href</c> a la ficha o un botón que
/// dice «Centro 360». Los del acordeón son botones que navegan por código, no
/// enlaces: contar solo <c>href</c> no los vería nunca. Que el texto
/// identifica un botón que de verdad navega a la ficha lo demuestra
/// <see cref="Por_defecto_el_acordeon_sigue_pintando_su_propio_camino_a_Centro_360"/>
/// pulsándolo.
/// </para>
/// </summary>
public class CentrosEnlaceUnicoCentro360Tests : BunitContext
{
    public CentrosEnlaceUnicoCentro360Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    public enum Escenario
    {
        /// <summary>Todos los trabajadores al día: el acordeón pinta «Todos al día».</summary>
        TodosAlDia,

        /// <summary>Uno con incidencias y otro al día oculto: nota de ocultos.</summary>
        IncidenciasConOcultos,

        /// <summary>Solo trabajadores con incidencias: el acordeón no pinta camino propio.</summary>
        IncidenciasSinOcultos
    }

    private sealed class MediatorPorTipo : IMediator
    {
        public IReadOnlyList<CentroListaDto> Centros { get; init; } = [];
        public required IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores { get; init; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerClientesParaSelectorQuery => (object)Array.Empty<ClienteSelectorDto>(),
                ObtenerProximaVisitaPorCentroQuery => (IReadOnlyDictionary<Guid, IReadOnlyList<VisitaResumenDto>>)new Dictionary<Guid, IReadOnlyList<VisitaResumenDto>>(),
                ObtenerCentrosQuery q => new ResultadoPaginado<CentroListaDto>(
                    Centros, Centros.Count, q.Pagina, q.TamanoPagina),
                ObtenerAsignacionesDocumentacionPorCentroQuery => Trabajadores,
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

    private sealed class UsuarioActualFalso : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>("Administrador");
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    /// <summary>El acordeón monta DrawerGestionDocumento, que los inyecta; cerrado, nadie los toca.</summary>
    private sealed class AlmacenArchivosQueNadieDebeTocar : IFileStorageService
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Con el drawer cerrado no se abre ningún archivo; si esto salta, el acordeón cambió de camino.");

        public Task<string> GuardarAsync(Stream contenido, string nombreArchivoOriginal, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task<Stream> AbrirAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
        public Task EliminarAsync(string identificador, CancellationToken cancellationToken = default) => throw NoDeberia();
    }

    private sealed class ConversorQueNadieDebeTocar : IConversorWordPdfService
    {
        public Task<byte[]> ConvertirAPdfAsync(byte[] contenidoDocx, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Con el drawer cerrado no se convierte nada; si esto salta, el acordeón cambió de camino.");
    }

    private static TrabajadorAsignacionDocumentacionDto Trabajador(string nombre, EstadoDocumento estado) => new(
        Guid.NewGuid(), Guid.NewGuid(), nombre, new DateOnly(2026, 1, 15), estado,
        [new DocumentoRequeridoDto(Guid.NewGuid(), Guid.NewGuid(), "Formación PRL", estado, new DateOnly(2027, 1, 15))]);

    private static IReadOnlyList<TrabajadorAsignacionDocumentacionDto> Trabajadores(Escenario escenario) => escenario switch
    {
        Escenario.TodosAlDia =>
            [Trabajador("Salas Moreno, Javier", EstadoDocumento.Vigente), Trabajador("Ruiz Peña, Ana", EstadoDocumento.Vigente)],
        Escenario.IncidenciasConOcultos =>
            [Trabajador("Salas Moreno, Javier", EstadoDocumento.Vencido), Trabajador("Ruiz Peña, Ana", EstadoDocumento.Vigente)],
        Escenario.IncidenciasSinOcultos =>
            [Trabajador("Salas Moreno, Javier", EstadoDocumento.Vencido)],
        _ => throw new ArgumentOutOfRangeException(nameof(escenario))
    };

    /// <summary>
    /// Barrera por escenario: prueba que el acordeón real cargó y está en la
    /// rama que se quería medir. Sin ella, un conteo de uno podría salir de un
    /// acordeón que no llegó a pintar nada.
    /// </summary>
    private static string MarcaDelEscenario(Escenario escenario) => escenario switch
    {
        Escenario.TodosAlDia => "Todos al día",
        Escenario.IncidenciasConOcultos => "1 trabajador al día no se muestra aquí",
        Escenario.IncidenciasSinOcultos => "Salas Moreno, Javier",
        _ => throw new ArgumentOutOfRangeException(nameof(escenario))
    };

    private static CentroListaDto Centro(string nombre) => new(
        Guid.NewGuid(), nombre, "C-001", Guid.NewGuid(), "Refrielectric S.A.",
        Guid.NewGuid(), "Montajes Ebro S.L.", EstadoCentro.Vigente,
        CumplimientoPorcentaje: 100, RecuentosCentroDto.Vacio);

    private void RegistrarServicios(MediatorPorTipo mediator)
    {
        Services.AddScoped<IMediator>(_ => mediator);
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped<ICurrentUserService, UsuarioActualFalso>();
        Services.AddScoped<IValidator<CrearCentroCommand>>(_ => new InlineValidator<CrearCentroCommand>());
        Services.AddScoped<IFileStorageService, AlmacenArchivosQueNadieDebeTocar>();
        Services.AddScoped<IConversorWordPdfService, ConversorQueNadieDebeTocar>();
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    /// <summary>Caminos a la ficha de <paramref name="centroId"/> dentro de <paramref name="raiz"/>: enlaces con href y botones «Centro 360».</summary>
    private static int CaminosACentro360(AngleSharp.Dom.IElement raiz, Guid centroId) =>
        raiz.QuerySelectorAll("a").Count(a => a.GetAttribute("href") == $"/centros/{centroId}")
        + raiz.QuerySelectorAll("button").Count(b => b.TextContent.Contains("Centro 360", StringComparison.Ordinal));

    [Theory]
    [InlineData(Escenario.TodosAlDia)]
    [InlineData(Escenario.IncidenciasConOcultos)]
    [InlineData(Escenario.IncidenciasSinOcultos)]
    public async Task La_fila_expandida_de_la_lista_ofrece_un_solo_camino_a_Centro_360(Escenario escenario)
    {
        var centro = Centro("Centro Logístico Norte");
        RegistrarServicios(new MediatorPorTipo { Centros = [centro], Trabajadores = Trabajadores(escenario) });
        Services.GetRequiredService<NavigationManager>().NavigateTo("centros");
        var cut = Render<Centros>();

        await cut.Find("button.boton-expandir-fila").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find(".tarjeta-fila-acordeon-contenido").TextContent
            .Should().Contain(MarcaDelEscenario(escenario)));

        var contenido = cut.Find(".tarjeta-fila-acordeon-contenido");
        CaminosACentro360(contenido, centro.Id).Should().Be(1,
            "la página pinta su enlace único y el acordeón no debe añadir otro al mismo destino");
        contenido.QuerySelector("a.acordeon-centro-enlace-360")!.GetAttribute("href")
            .Should().Be($"/centros/{centro.Id}", "el camino que queda es el de la página");
    }

    /// <summary>
    /// Protege a los consumidores que no pasan el parámetro (CentroDetalle y
    /// cualquier otro): sin decir nada, el acordeón sigue pintando su camino a
    /// Centro 360, y ese botón navega de verdad a la ficha del centro.
    /// </summary>
    [Theory]
    [InlineData(Escenario.TodosAlDia, "Ver los 2 en Centro 360")]
    [InlineData(Escenario.IncidenciasConOcultos, "Ver Centro 360 →")]
    public async Task Por_defecto_el_acordeon_sigue_pintando_su_propio_camino_a_Centro_360(Escenario escenario, string textoBoton)
    {
        var centroId = Guid.NewGuid();
        RegistrarServicios(new MediatorPorTipo { Trabajadores = Trabajadores(escenario) });

        var cut = Render<AcordeonAsignacionesCentro>(p => p
            .Add(a => a.CentroId, centroId)
            .Add(a => a.CentroNombre, "Centro Logístico Norte")
            .Add(a => a.SoloIncidencias, true));

        cut.WaitForAssertion(() => cut.Markup.Should().Contain(MarcaDelEscenario(escenario)));

        var botones = cut.FindAll("button").Where(b => b.TextContent.Contains("Centro 360", StringComparison.Ordinal)).ToList();
        botones.Should().ContainSingle("con el valor por defecto el acordeón pinta su propio camino a la ficha");
        botones[0].TextContent.Trim().Should().Be(textoBoton);

        await botones[0].ClickAsync(new MouseEventArgs());

        new Uri(Services.GetRequiredService<NavigationManager>().Uri).AbsolutePath
            .Should().Be($"/centros/{centroId}", "el botón que se cuenta como camino navega de verdad a la ficha");
    }
}
