using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Components.Layout;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// El <c>&lt;select&gt;</c> del selector de ámbito lleva el rótulo accesible
/// «Contexto operativo actual» (decisión 2026-09-20): «Cliente activo» a secas
/// es ambiguo según el contrato de terminología, y «Contexto de trabajo activo»
/// colisiona con <c>ContextWorkspacePanel</c>. Solo cambia el texto que oye un
/// lector de pantalla; el tipo <c>ClienteActivo…</c> y la ruta
/// <c>/cuenta/cliente-activo</c> no se tocan.
/// </summary>
public class SelectorClienteActivoRotuloTests : BunitContext
{
    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class Seleccion : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class Mediador(IReadOnlyList<ClienteAutorizadoDto> clientes) : IMediator
    {
        public Task<T> Send<T>(IRequest<T> request, CancellationToken cancellationToken = default) =>
            request is ObtenerClientesAutorizadosQuery
                ? Task.FromResult((T)(object)clientes)
                : throw new NotSupportedException(request.GetType().Name);

        public Task Send<T>(T request, CancellationToken cancellationToken = default) where T : IRequest => throw new NotSupportedException();
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<T> CreateStream<T>(IStreamRequest<T> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<T>(T notification, CancellationToken cancellationToken = default) where T : INotification => Task.CompletedTask;
    }

    [Fact]
    public void El_select_se_anuncia_como_contexto_operativo_actual()
    {
        Services.AddScoped<IMediator>(_ => new Mediador(
        [
            new ClienteAutorizadoDto(Guid.NewGuid(), "Propia", EsOrigen: true),
            new ClienteAutorizadoDto(Guid.NewGuid(), "Organización Norte", EsOrigen: false),
        ]));
        Services.AddScoped<IClienteActivoSeleccionado, Seleccion>();
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<Microsoft.Extensions.Logging.ILogger<ExcepcionDeCircuitoDesconectado>>(
            NullLogger<ExcepcionDeCircuitoDesconectado>.Instance);

        var cut = Render<SelectorClienteActivo>();

        var selects = cut.FindAll("select.selector-cliente-activo");
        selects.Should().ContainSingle("el control positivo evita comprobar un atributo en una colección vacía");
        selects[0].GetAttribute("aria-label").Should().Be("Contexto operativo actual");
    }
}
