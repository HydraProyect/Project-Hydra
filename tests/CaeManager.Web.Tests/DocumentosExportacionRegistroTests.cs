using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Web.Features.Documentos;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;

namespace CaeManager.Web.Tests;

public class DocumentosExportacionRegistroTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Externo = Guid.NewGuid();

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

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

    private sealed class MediadorFalso(IReadOnlyList<ClienteAutorizadoDto>? autorizados = null) : IMediator
    {
        public int ConsultasDeDocumentos { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            if (request is ObtenerClientesAutorizadosQuery)
                return Task.FromResult((TResponse)(object)(autorizados ?? Array.Empty<ClienteAutorizadoDto>()));

            ConsultasDeDocumentos++;
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
        var resultado = await DocumentosEndpoints.ExportarAsync(new DefaultHttpContext(), new MediadorFalso(), new TenantActualFalso(Origen), registro, default,
            q: "01234567Z", estado: "Vencido,01234567Z", tipo: tipo.ToString(), propietario: "01234567Z",
            orden: "Nombre; 01234567Z", agrupar: "ambito", desc: true);

        resultado.Should().BeOfType<FileStreamHttpResult>();
        var entrada = registro.Registros.Should().ContainSingle().Subject;
        entrada.Tipo.Should().Be("Documento");
        entrada.Filas.Should().Be(2);
        entrada.Criterios.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["busqueda"] = "true",
            ["estado"] = "Vencido",
            ["tipo"] = tipo.ToString(),
            ["agrupar"] = "ambito",
            ["desc"] = "true"
        });
    }

    [Fact]
    public async Task El_ambito_y_la_plataforma_solo_llegan_al_rastro_si_son_validos()
    {
        var registro = new RegistroFalso();
        var plataforma = Guid.NewGuid();
        await DocumentosEndpoints.ExportarAsync(new DefaultHttpContext(), new MediadorFalso(), new TenantActualFalso(Origen), registro, default,
            ambito: "Empresa", plataforma: plataforma.ToString());
        // Un entero no definido en el enumerado parsea, pero no es un ámbito: no llega al rastro.
        await DocumentosEndpoints.ExportarAsync(new DefaultHttpContext(), new MediadorFalso(), new TenantActualFalso(Origen), registro, default,
            ambito: "12345678", plataforma: "01234567Z");

        registro.Registros.Should().HaveCount(2);
        registro.Registros[0].Criterios.Should().BeEquivalentTo(new Dictionary<string, string>
        {
            ["ambito"] = "Empresa",
            ["plataforma"] = plataforma.ToString()
        });
        registro.Registros[1].Criterios.Should().BeEmpty();
    }

    [Fact]
    public async Task Si_falla_el_registro_no_entrega_el_archivo()
    {
        var exportar = () => DocumentosEndpoints.ExportarAsync(
            new DefaultHttpContext(), new MediadorFalso(), new TenantActualFalso(Origen), new RegistroFalso(falla: true), default);

        await exportar.Should().ThrowAsync<InvalidOperationException>().WithMessage("sin rastro");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task La_exportacion_exige_elegir_empresa_y_registra_solo_si_se_permite(bool empresaElegida)
    {
        var mediador = new MediadorFalso([
            new(Origen, "Operador CAE", EsOrigen: true, EsGestionadoPorOperacion: false),
            new(Externo, "Tenant beneficiario", EsOrigen: false, EsGestionadoPorOperacion: true, EsCarteraGestorCae: true)
        ]);
        var registro = new RegistroFalso();

        var resultado = await DocumentosEndpoints.ExportarAsync(new DefaultHttpContext(), mediador,
            new TenantActualFalso(empresaElegida ? Externo : Origen), registro, default);

        if (!empresaElegida)
        {
            resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/documentos");
            mediador.ConsultasDeDocumentos.Should().Be(0);
            registro.Registros.Should().BeEmpty();
        }
        else
        {
            resultado.Should().BeOfType<FileStreamHttpResult>();
            mediador.ConsultasDeDocumentos.Should().BeGreaterThan(0);
            registro.Registros.Should().ContainSingle().Which.Filas.Should().Be(2);
        }
    }
}
