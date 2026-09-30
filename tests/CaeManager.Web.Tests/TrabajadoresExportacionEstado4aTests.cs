using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using CaeManager.Web.Features.Trabajadores;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// La página de Trabajadores no muestra los datos del Tenant de origen en el estado 4a
/// («Selecciona una empresa de tu cartera»); el endpoint de exportación aplica la
/// misma condición (<see cref="ClientesAutorizados.PideElegirEmpresa"/>; la página la resuelve con
/// <c>ContextoEmpresaActiva.ResolverAsync</c>, que compone las mismas dos) y no los exporta.
/// </summary>
public class TrabajadoresExportacionEstado4aTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Externo = Guid.NewGuid();

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class MediatorFalso(IReadOnlyList<ClienteAutorizadoDto> autorizados) : IMediator
    {
        public int ConsultasDeTrabajadores { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object respuesta = request switch
            {
                ObtenerClientesAutorizadosQuery => autorizados,
                ObtenerTrabajadoresQuery q => Contar(q),
                _ => throw new NotSupportedException(request.GetType().Name),
            };
            return Task.FromResult((TResponse)respuesta);
        }

        private ResultadoPaginado<TrabajadorListaDto> Contar(ObtenerTrabajadoresQuery q)
        {
            ConsultasDeTrabajadores++;
            return new ResultadoPaginado<TrabajadorListaDto>([], 0, q.Pagina, q.TamanoPagina);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private static ClienteAutorizadoDto Propio(bool gestionado = false) =>
        new(Origen, "Operador CAE", EsOrigen: true, EsGestionadoPorOperacion: gestionado);

    private static ClienteAutorizadoDto Cartera(Guid? id = null) =>
        new(id ?? Externo, "Tenant beneficiario", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true);

    [Fact]
    public async Task En_el_estado_4a_redirige_a_la_pagina_y_no_exporta_el_origen()
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);

        var resultado = await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Origen), default);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/trabajadores");
        mediador.ConsultasDeTrabajadores.Should().Be(0, "no se piden los trabajadores de la organización de origen");
    }

    [Fact]
    public async Task Con_una_empresa_de_la_cartera_elegida_exporta()
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);

        var resultado = await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Externo), default);

        resultado.Should().NotBeOfType<RedirectHttpResult>();
        mediador.ConsultasDeTrabajadores.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Con_el_origen_gestionado_o_sin_cartera_exporta_el_origen()
    {
        foreach (var autorizados in new[]
                 {
                     new[] { Propio(gestionado: true), Cartera() },
                     [Propio()],
                 })
        {
            var mediador = new MediatorFalso(autorizados);

            var resultado = await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Origen), default);

            resultado.Should().NotBeOfType<RedirectHttpResult>();
            mediador.ConsultasDeTrabajadores.Should().BeGreaterThan(0);
        }
    }
}
