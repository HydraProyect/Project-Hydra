using System.Reflection;
using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Reclamaciones;
using CaeManager.Application.Reclamaciones.Queries.ObtenerLoteReclamacion;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesEnviadas;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

[CollectionDefinition(Nombre, DisableParallelization = true)]
public sealed class ZonaDelServidorFijadaCollection
{
    /// <summary>
    /// Los tests que cambian <see cref="TimeZoneInfo.Local"/> del proceso corren
    /// solos: mientras dura el cambio, cualquier otro test vería la zona fijada.
    /// </summary>
    public const string Nombre = "Zona del servidor fijada";
}

/// <summary>
/// Toda hora que ve el usuario se pinta en hora peninsular (Europe/Madrid) con
/// <see cref="DiaDeNegocio.EnHoraPeninsular"/>, nunca con la zona del servidor:
/// producción y CI corren en UTC, y con <c>ToLocalTime()</c> la hora de un
/// registro salía una o dos horas atrasada (y, entre las 22:00 y las 24:00 UTC,
/// con la fecha del día anterior).
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> que dos componentes de patrón distinto —la
/// pestaña de historial que comparten las fichas 360 y la lista de
/// reclamaciones de Documentos— pintan la fecha y la hora de Madrid para
/// instantes en la frontera del día (22:30Z en verano, 23:30Z en invierno),
/// con la zona del servidor fijada en UTC como en los contenedores. Fijarla es
/// lo que hace sensible el test en una máquina de desarrollo en hora de Madrid,
/// donde <c>ToLocalTime()</c> daría por casualidad el mismo texto.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> los otros 30-y-pico sitios uno a uno. De que
/// ninguno vuelva a <c>ToLocalTime()</c> se encarga el trinquete
/// <c>DiaDeNegocioUnicaFuenteTests</c> (tolerancia cero), que además prohíbe
/// formatear en crudo un instante cuyo nombre acaba en <c>Utc</c>. Un instante
/// que no lleve ese sufijo y se pinte sin convertir sigue sin vigilancia: hueco
/// declarado.
/// </para>
/// </summary>
[Collection(ZonaDelServidorFijadaCollection.Nombre)]
public sealed class HoraPeninsularEnPantallaTests : BunitContext
{
    private readonly IDisposable _zonaDelServidor = ZonaDelServidorEnUtc();

    public HoraPeninsularEnPantallaTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        this.ConRolDeEscritura();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _zonaDelServidor.Dispose();
    }

    /// <summary>Instante UTC → fecha y hora que tiene que leer el usuario (Madrid).</summary>
    public static TheoryData<DateTime, string> FronterasDelDia => new()
    {
        // Verano (UTC+2): en UTC todavía es el 15 a las 22:30; en Madrid ya es el 16.
        { new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Utc), "16/07/2026 00:30" },
        // Invierno (UTC+1): en UTC todavía es el 14 a las 23:30; en Madrid ya es el 15.
        { new DateTime(2026, 1, 14, 23, 30, 0, DateTimeKind.Utc), "15/01/2026 00:30" },
    };

    [Theory]
    [MemberData(nameof(FronterasDelDia))]
    public void El_historial_de_la_ficha_360_pinta_la_hora_de_Madrid(DateTime instanteUtc, string esperado)
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(instanteUtc));
        var entidadId = Guid.NewGuid();
        var filas = new List<RegistroAuditoriaListaDto>
        {
            new(Guid.NewGuid(), "Cliente", entidadId, "Modificado", UsuarioId: null, instanteUtc, false, false),
        };
        Services.AddScoped<IMediator>(_ => new Mediador(consulta => consulta switch
        {
            ObtenerAuditoriaQuery q => new ResultadoPaginado<RegistroAuditoriaListaDto>(filas, filas.Count, q.Pagina, q.TamanoPagina),
            _ => null,
        }));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuariosVacio(), null!, null!, null!, null!, null!, null!, null!, null!));

        var cut = Render<PestanaHistorial>(p => p
            .Add(x => x.EntidadTipo, "Cliente")
            .Add(x => x.EntidadId, entidadId));

        cut.WaitForAssertion(() =>
            cut.Find(".workspace-historial-fecha").TextContent.Trim().Should().Be(esperado,
                "la hora de un registro se enseña en hora peninsular, no en la del servidor (UTC)"));
    }

    [Theory]
    [MemberData(nameof(FronterasDelDia))]
    public void El_historial_de_reclamaciones_pinta_la_hora_de_Madrid(DateTime instanteUtc, string esperado)
    {
        using var reloj = DiaDeNegocio.FijarRelojEnEsteFlujo(new RelojFijo(instanteUtc));
        var reclamacion = new ReclamacionEnviadaDto(
            Guid.NewGuid(), Guid.NewGuid(), "Refrielectric SL", AmbitoAplicacion.Cliente, "contacto@refrielectric.example",
            instanteUtc, 1, null, null, [Guid.NewGuid()]);
        Services.AddScoped<IMediator>(_ => new Mediador(consulta => consulta switch
        {
            ObtenerReclamacionesEnviadasQuery q =>
                new ResultadoPaginado<ReclamacionEnviadaDto>([reclamacion], 1, q.Pagina, q.TamanoPagina),
            ObtenerLoteReclamacionQuery => (IReadOnlyList<LoteReclamacionClienteDto>)[],
            GuardarContactoAgendaCommand => Result.Exito(Guid.NewGuid()),
            _ => null,
        }));
        Services.AddScoped<ToastService>();

        var cut = Render<ReclamacionesTab>();

        cut.WaitForAssertion(() =>
            cut.FindAll("[role=cell]").Select(c => c.TextContent.Trim()).Should().Contain(esperado,
                "la fecha de envío de una reclamación se enseña en hora peninsular, no en la del servidor (UTC)"));
    }

    /// <summary>
    /// Fija <see cref="TimeZoneInfo.Local"/> en UTC, como en los contenedores de
    /// producción y CI, y la devuelve a la del sistema al liberarse. No hay API
    /// pública para esto: se sustituye la zona en la caché interna de
    /// <see cref="TimeZoneInfo"/>, y se comprueba que surtió efecto para que, si
    /// un runtime futuro cambia esa caché, el test falle en vez de dejar de observar.
    /// </summary>
    private static IDisposable ZonaDelServidorEnUtc()
    {
        var cache = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
        var zonaLocal = cache?.GetType().GetField("_localTimeZone", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        zonaLocal.Should().NotBeNull("sin poder fijar la zona del servidor, este test no distingue Madrid de ToLocalTime() en una máquina en hora de Madrid");

        zonaLocal!.SetValue(cache, TimeZoneInfo.Utc);
        try
        {
            TimeZoneInfo.Local.BaseUtcOffset.Should().Be(TimeSpan.Zero, "la zona del servidor tiene que quedar fijada en UTC");
            new DateTime(2026, 7, 15, 22, 30, 0, DateTimeKind.Utc).ToLocalTime().Hour.Should().Be(22,
                "ToLocalTime() tiene que comportarse como en los contenedores");
        }
        catch
        {
            // Si el constructor lanza, xUnit no llama a Dispose: se restaura aquí
            // para no dejar la zona fijada al resto de la ejecución.
            TimeZoneInfo.ClearCachedData();
            throw;
        }

        return new Restaurar();
    }

    private sealed class Restaurar : IDisposable
    {
        public void Dispose() => TimeZoneInfo.ClearCachedData();
    }

    private sealed class RelojFijo(DateTime ahoraUtc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(ahoraUtc, TimeSpan.Zero);

        public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
    }

    private sealed class Mediador(Func<object, object?> responder) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(responder(request)
                ?? throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")));

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

    /// <summary>Los registros del test no tienen usuario: la pestaña no busca a nadie.</summary>
    private sealed class AlmacenUsuariosVacio : IUserStore<ApplicationUser>
    {
        private static Exception NoPrevisto() => new NotSupportedException("El test no tiene usuarios que buscar.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoPrevisto();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoPrevisto();
        public void Dispose() { }
    }
}
