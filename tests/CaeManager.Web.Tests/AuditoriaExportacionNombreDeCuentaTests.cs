using CaeManager.Application.Common;
using CaeManager.Application.Usuarios.Queries.ObtenerNombreCuenta;
using CaeManager.Web.Features.Auditoria;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Issue #2001: la columna «Usuario» de la exportación de auditoría consulta el nombre con
/// <see cref="ObtenerNombreCuentaQuery"/>, que bajo una Sesión Privilegiada falla en cerrado si su
/// lectura no queda registrada. La exportación no se cae con un 500: la celda queda vacía.
/// </summary>
public class AuditoriaExportacionNombreDeCuentaTests
{
    [Fact]
    public async Task Con_la_lectura_registrada_la_celda_lleva_el_nombre()
    {
        var nombre = await AuditoriaEndpoints.NombreDeCuentaAsync(
            new Mediador(() => new NombreCuentaDto("Ramón Ibáñez", "ramon@example.test")), Guid.NewGuid(), CancellationToken.None);

        nombre.Should().Be("Ramón Ibáñez");
    }

    [Fact]
    public async Task Si_la_lectura_privilegiada_no_queda_registrada_la_celda_queda_vacia_sin_lanzar()
    {
        var nombre = await AuditoriaEndpoints.NombreDeCuentaAsync(
            new Mediador(() => throw new LecturaPrivilegiadaNoRegistradaException(nameof(ObtenerNombreCuentaQuery))),
            Guid.NewGuid(), CancellationToken.None);

        nombre.Should().BeEmpty("el nombre no se sirve, y la exportación sigue");
    }

    [Fact]
    public async Task Cualquier_otro_fallo_no_se_disfraza_de_nombre_vacio()
    {
        var consultar = () => AuditoriaEndpoints.NombreDeCuentaAsync(
            new Mediador(() => throw new InvalidOperationException("otro")), Guid.NewGuid(), CancellationToken.None);

        await consultar.Should().ThrowAsync<InvalidOperationException>().WithMessage("otro");
    }

    private sealed class Mediador(Func<NombreCuentaDto?> respuesta) : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            request.Should().BeOfType<ObtenerNombreCuentaQuery>();
            try
            {
                return Task.FromResult((TResponse)(object?)respuesta()!);
            }
            catch (Exception excepcion)
            {
                return Task.FromException<TResponse>(excepcion);
            }
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => throw new NotSupportedException();
    }
}
