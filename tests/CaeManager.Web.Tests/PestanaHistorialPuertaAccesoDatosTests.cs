using System.Collections.Concurrent;
using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// <c>PestanaHistorial</c> resuelve el nombre de quien hizo cada cambio con
/// <c>UserManager</c>, que no pasa por MediatR y por tanto no entra solo por
/// <see cref="PuertaAccesoDatos"/>. En Blazor Server el DbContext es scoped y
/// lo comparte todo el circuito: una búsqueda de usuario fuera de la puerta
/// puede solaparse con otra operación del circuito y lanzar «A second operation
/// was started on this context», que la pestaña se traga en su catch y enseña
/// como «No pudimos cargar el historial».
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> que, con la puerta ocupada por otra
/// operación del circuito, la pestaña no llega a llamar a
/// <c>FindByIdAsync</c>, y que sí lo hace —y pinta el nombre— cuando la
/// puerta se suelta. El test ocupa la puerta como lo haría cualquier otro
/// componente del circuito: entrando y no saliendo.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la colisión real sobre Npgsql. El doble de
/// almacén no toca ningún DbContext; la propiedad que se prueba es la
/// precondición (esperar la puerta), no la excepción de EF.
/// </para>
/// </summary>
public class PestanaHistorialPuertaAccesoDatosTests : BunitContext
{
    private sealed class MediatorHistorial(IReadOnlyList<RegistroAuditoriaListaDto> filas) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            request is ObtenerAuditoriaQuery consulta
                ? Task.FromResult((TResponse)(object)new ResultadoPaginado<RegistroAuditoriaListaDto>(
                    filas, filas.Count, consulta.Pagina, consulta.TamanoPagina))
                : throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.");

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

    /// <summary>Solo responde a <c>FindByIdAsync</c>, que es lo único que la pestaña pide; anota cada búsqueda.</summary>
    private sealed class AlmacenUsuarios(Dictionary<string, ApplicationUser> usuarios) : IUserStore<ApplicationUser>
    {
        public ConcurrentQueue<string> Buscados { get; } = new();

        private static Exception NoPrevisto() => new NotSupportedException("La pestaña solo busca usuarios por Id.");

        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken)
        {
            Buscados.Enqueue(userId);
            return Task.FromResult(usuarios.GetValueOrDefault(userId));
        }

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

    [Fact]
    public async Task Con_la_puerta_de_datos_ocupada_la_pestana_espera_para_buscar_al_usuario_y_lo_busca_al_soltarla()
    {
        var usuarioId = Guid.NewGuid();
        var entidadId = Guid.NewGuid();
        var almacen = new AlmacenUsuarios(new()
        {
            [usuarioId.ToString()] = new ApplicationUser { Id = usuarioId, NombreCompleto = "Marta Arcos" },
        });
        var filas = new List<RegistroAuditoriaListaDto>
        {
            new(Guid.NewGuid(), "Cliente", entidadId, "Modificado", usuarioId, DateTime.UtcNow, false, false),
        };
        Services.AddScoped<IMediator>(_ => new MediatorHistorial(filas));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            almacen, null!, null!, null!, null!, null!, null!, null!, null!));

        // Otra operación del circuito tiene la puerta: entra y no sale hasta que el test la suelte.
        var puerta = Services.GetRequiredService<PuertaAccesoDatos>();
        var ocupante = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ocupada = puerta.EjecutarAsync(() => ocupante.Task);

        var cut = Render<PestanaHistorial>(p => p
            .Add(x => x.EntidadTipo, "Cliente")
            .Add(x => x.EntidadId, entidadId));

        almacen.Buscados.Should().BeEmpty(
            "con la puerta ocupada por otra operación del circuito, la búsqueda de usuario tiene que esperar a que se suelte");
        cut.FindAll(".workspace-historial-usuario").Should().BeEmpty("la pestaña sigue cargando mientras espera la puerta");

        ocupante.SetResult();
        await ocupada.WaitAsync(TimeSpan.FromSeconds(10));
        // La puerta atiende por orden de llegada: al pasar el test, la pestaña ya pasó antes.
        await puerta.EjecutarAsync(() => Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));

        cut.WaitForAssertion(() =>
            cut.Find(".workspace-historial-usuario").TextContent.Should().Be("Marta Arcos"));
        almacen.Buscados.Should().Equal(usuarioId.ToString());
    }
}
