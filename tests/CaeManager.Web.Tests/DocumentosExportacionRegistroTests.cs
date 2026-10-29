using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CaeManager.Web.Tests;

public class DocumentosExportacionRegistroTests
{
    private sealed class RegistroFalso(bool falla = false) : IRegistroExportacionService
    {
        public List<(string Tipo, int Filas, IReadOnlyDictionary<string, string> Criterios)> Registros { get; } = [];

        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            if (falla) throw new InvalidOperationException("sin rastro");
            Registros.Add((entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    private sealed class MediadorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var consulta = (ObtenerDocumentosQuery)(object)request;
            DocumentoListaDto[] documentos =
            [
                new(Guid.NewGuid(), default, "Uno", "Tipo", new DateOnly(2026, 1, 1), null, default, null, []),
                new(Guid.NewGuid(), default, "Dos", "Tipo", new DateOnly(2026, 1, 1), null, default, null, []),
            ];
            return Task.FromResult((TResponse)(object)new ResultadoPaginado<DocumentoListaDto>(
                documentos, documentos.Length, consulta.Pagina, consulta.TamanoPagina));
        }
        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default) where TNotification : INotification => throw new NotSupportedException();
    }

    [Fact]
    public async Task Registra_las_filas_y_los_criterios_sanitizados_antes_de_entregar()
    {
        var registro = new RegistroFalso();
        var tipo = Guid.NewGuid();
        var resultado = await DocumentosEndpoints.ExportarAsync(new DefaultHttpContext(), new MediadorFalso(), registro, default,
            q: "01234567Z", estado: "Vencido,01234567Z", tipo: tipo.ToString(), propietario: "01234567Z",
            orden: "Nombre; 01234567Z", agrupar: "ambito", desc: true);

        resultado.Should().BeOfType<FileStreamHttpResult>();
        var entrada = registro.Registros.Should().ContainSingle().Subject;
        entrada.Tipo.Should().Be("Documento");
        entrada.Filas.Should().Be(2);
        entrada.Criterios.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["busqueda"] = "true", ["estado"] = "Vencido", ["tipo"] = tipo.ToString(),
            ["agrupar"] = "ambito", ["desc"] = "true"
        });
    }

    [Fact]
    public async Task Si_falla_el_registro_no_entrega_el_archivo()
    {
        var exportar = () => DocumentosEndpoints.ExportarAsync(
            new DefaultHttpContext(), new MediadorFalso(), new RegistroFalso(falla: true), default);

        await exportar.Should().ThrowAsync<InvalidOperationException>().WithMessage("sin rastro");
    }
}
