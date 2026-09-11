using Bunit;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Auditoría tiene que distinguir <b>«no hay registros de auditoría»</b> de
/// <b>«ninguno con este filtro»</b>. Hasta 2026-09-08 pintaba un solo estado
/// vacío cuyo texto —«Todavía no se ha modificado ninguna entidad con este
/// filtro»— era falso en los dos casos a la vez: sin filtro hablaba de un
/// filtro que no existía, y con filtro puesto hacía creer que la auditoría no
/// registra nada, cuando lo único cierto es que no registra ESE tipo de
/// entidad.
///
/// <para>
/// Es la misma construcción que <see cref="EmpresasVacioPorFiltroTests"/> con
/// una diferencia: este estado vacío no invita a crear nada, así que el daño
/// no es un registro duplicado sino una conclusión falsa sobre si el sistema
/// audita. Por eso aquí se comprueba la COPIA, no solo que exista la rama.
/// </para>
/// </summary>
public class AuditoriaVacioPorFiltroTests : BunitContext
{
    private sealed class MediatorConRegistros(IReadOnlyList<RegistroAuditoriaListaDto> registros) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            Task.FromResult((TResponse)(object)(request switch
            {
                ObtenerAuditoriaQuery q => new ResultadoPaginado<RegistroAuditoriaListaDto>(
                    registros, registros.Count, q.Pagina, q.TamanoPagina),
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

    /// <summary>
    /// La página inyecta <c>UserManager</c> para poner nombre al autor de cada
    /// fila. Con la lista vacía —que es lo que estos casos miran— no llega a
    /// consultarse ningún usuario, así que el almacén lanza si alguien lo toca:
    /// es la forma de enterarse si un cambio futuro empieza a pedir usuarios en
    /// un camino donde no hay filas.
    /// </summary>
    private sealed class AlmacenUsuariosQueNadieDebeTocar : IUserStore<ApplicationUser>
    {
        private static Exception NoDeberia() =>
            new NotSupportedException("Sin filas no se consulta ningún usuario; si esto salta, la página cambió de camino.");

        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => throw NoDeberia();
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw NoDeberia();
        public void Dispose() { }
    }

    /// <param name="entidad">Valor del filtro que llega por la URL (?entidad=).</param>
    /// <param name="registros">Filas que devuelve la consulta, ya filtradas por el servidor.</param>
    private IRenderedComponent<Features.Auditoria.Pages.Auditoria> Renderizar(
        string? entidad = null, params RegistroAuditoriaListaDto[] registros)
    {
        Services.AddScoped<IMediator>(_ => new MediatorConRegistros(registros));
        Services.AddScoped<PuertaAccesoDatos>();
        Services.AddScoped<ToastService>();
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenUsuariosQueNadieDebeTocar(), null!, null!, null!, null!, null!, null!, null!, null!));

        // El filtro es [SupplyParameterFromQuery]: no se pasa como parámetro de
        // componente —Blazor lo rechaza explícitamente— sino navegando a la URI
        // que lo lleva, igual que en el producto.
        var nav = Services.GetRequiredService<NavigationManager>();
        nav.NavigateTo(entidad is null ? "auditoria" : "auditoria?entidad=" + Uri.EscapeDataString(entidad));

        return Render<Features.Auditoria.Pages.Auditoria>();
    }

    [Fact]
    public void Sin_registros_y_con_filtro_no_dice_que_nunca_se_modifico_nada()
    {
        var cut = Renderizar(entidad: "Documento");

        cut.Markup.Should().Contain("Ningún registro con este filtro");
        cut.Markup.Should().Contain("Quitar el filtro");
        cut.Markup.Should().NotContain("No hay registros de auditoría",
            "decírselo a quien acaba de filtrar por «Documento» le hace creer que la auditoría no registra nada");
    }

    [Fact]
    public void Sin_filtro_y_sin_registros_sigue_diciendo_que_no_hay_ninguno()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("No hay registros de auditoría");
        cut.Markup.Should().NotContain("Ningún registro con este filtro");
    }

    /// <summary>
    /// La frase «con este filtro» vivía en el estado SIN filtrar, y ahí era
    /// falsa: no había filtro del que hablar. La barrera va delante — una
    /// aserción de ausencia sin ella es verde vacío.
    /// </summary>
    [Fact]
    public void El_estado_sin_filtro_ya_no_habla_de_un_filtro_que_no_existe()
    {
        var cut = Renderizar();

        cut.Markup.Should().Contain("No hay registros de auditoría");
        cut.Markup.Should().NotContain("con este filtro");
    }

    [Fact]
    public void Quitar_el_filtro_devuelve_la_pagina_al_estado_sin_filtrar()
    {
        var cut = Renderizar(entidad: "Documento");
        cut.Markup.Should().Contain("Ningún registro con este filtro", "es el punto de partida de este caso");

        cut.Find(".estado-vacio button").Click();

        cut.Markup.Should().Contain("No hay registros de auditoría");
        cut.Markup.Should().NotContain("Ningún registro con este filtro");
    }

    [Fact]
    public void Con_registros_no_se_pinta_ningun_estado_vacio()
    {
        var cut = Renderizar(entidad: "Documento", registros: new RegistroAuditoriaListaDto(
            Guid.NewGuid(), "Documento", Guid.NewGuid(), "Creado", UsuarioId: null, DateTime.UtcNow,
            PuedeRestaurar: false, TieneArchivoAnterior: false));

        cut.Markup.Should().NotContain("Ningún registro con este filtro");
        cut.Markup.Should().NotContain("No hay registros de auditoría");
        cut.Find("table.tabla-datos").TextContent.Should().Contain("Creado");
    }
}
