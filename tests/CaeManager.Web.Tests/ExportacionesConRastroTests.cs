using System.Reflection;
using CaeManager.Application.Common;
using CaeManager.Application.Facturacion.Queries.ObtenerResumenFacturacion;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Incidencias;
using CaeManager.Web.Features.Asignaciones;
using CaeManager.Web.Features.Asignaciones.Recursos;
using CaeManager.Web.Features.Auditoria;
using CaeManager.Web.Features.Facturacion;
using CaeManager.Web.Features.Incidencias;
using CaeManager.Web.Features.Incidencias.Recursos;
using CaeManager.Web.Reportes;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Xunit;

namespace CaeManager.Web.Tests;

/// <summary>
/// Las exportaciones con datos de un Tenant dejan rastro antes de entregar el fichero, y sin rastro
/// no sale nada (SEG-10, ASVS 4.0.3 V7.1.3 y V7.2.2). Que la fila llegue a la auditoría real lo
/// prueban los tests de integración de <c>RegistroExportacionService</c>.
/// </summary>
public class ExportacionesConRastroTests
{
    public ExportacionesConRastroTests() => LectorInformeVigenciaExportado.RegistrarFuente();

    private sealed class SinRastroException : Exception;

    private sealed class RegistroFalso(bool falla = false) : IRegistroExportacionService
    {
        public List<(string Tipo, int Filas, IReadOnlyDictionary<string, string> Criterios)> Registros { get; } = [];

        public Task RegistrarAsync(string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            if (falla) throw new SinRastroException();
            Registros.Add((entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    /// <summary>Página vacía a cualquier listado paginado; a los informes y al resumen, un resultado con una fila.</summary>
    private sealed class MediatorFalso : IMediator
    {
        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            object? respuesta = request switch
            {
                GenerarInformeVigenciaQuery => new InformeVigenciaDto("Tenant", []),
                GenerarInformeAsignacionesQuery => new InformeAsignacionesDto(
                    "Tenant", [new FilaInformeAsignacionDto("Ana Ruiz", "Centro", new DateOnly(2026, 1, 1))]),
                ObtenerResumenFacturacionQuery => new ResumenFacturacionDto("Cliente", [], []),
                _ => null,
            };
            if (respuesta is not null) return Task.FromResult((TResponse)respuesta);

            var tipo = typeof(TResponse);
            if (!tipo.IsGenericType || tipo.GetGenericTypeDefinition() != typeof(ResultadoPaginado<>))
                throw new NotSupportedException(request.GetType().Name);

            var vacia = Array.CreateInstance(tipo.GetGenericArguments()[0], 0);
            return Task.FromResult((TResponse)Activator.CreateInstance(tipo, vacia, 0, 1, 1)!);
        }

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest => throw new NotSupportedException();
        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private class AlcanceProxy : DispatchProxy
    {
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            targetMethod!.Name == nameof(IAlcanceDatosService.ObtenerClienteIdsVisiblesAsync)
                ? Task.FromResult<IReadOnlyList<Guid>?>(null)
                : throw new NotSupportedException(targetMethod.Name);
    }

    private static readonly ServiceProvider Servicios = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();

    private static readonly Dictionary<string, Func<IRegistroExportacionService, Task<IResult>>> Exportaciones = new()
    {
        ["incidencias"] = r => IncidenciasEndpoints.ExportarAsync(
            new MediatorFalso(), r, Servicios.GetRequiredService<IStringLocalizer<TextosIncidencias>>(), default),
        ["auditoria"] = r => AuditoriaEndpoints.ExportarAsync(null, new MediatorFalso(), r, default),
        ["asignaciones"] = r => AsignacionesEndpoints.ExportarAsync(
            new MediatorFalso(), r, Servicios.GetRequiredService<IStringLocalizer<TextosAsignaciones>>(), default),
        ["resumenFacturacion"] = r => FacturacionEndpoints.ExportarResumenAsync(
            Guid.NewGuid(), 2026, 10, new MediatorFalso(), new DefaultHttpContext(),
            DispatchProxy.Create<IAlcanceDatosService, AlcanceProxy>(), r, default),
        ["reporteVigencia.xlsx"] = r => ReportesEndpoints.VigenciaXlsxAsync(new MediatorFalso(), r, null, null, true, default),
        ["reporteVigencia.pdf"] = r => ReportesEndpoints.VigenciaPdfAsync(new MediatorFalso(), r, null, null, true, default),
        ["reporteAsignaciones.xlsx"] = r => ReportesEndpoints.AsignacionesXlsxAsync(new MediatorFalso(), r, null, null, default),
        ["reporteAsignaciones.pdf"] = r => ReportesEndpoints.AsignacionesPdfAsync(new MediatorFalso(), r, null, null, default),
    };

    public static TheoryData<string, string, int> Casos => new()
    {
        { "incidencias", nameof(Incidencia), 0 },
        { "auditoria", nameof(RegistroAuditoria), 0 },
        { "asignaciones", nameof(Asignacion), 0 },
        { "resumenFacturacion", nameof(TarifaCliente), 0 },
        { "reporteVigencia.xlsx", nameof(Documento), 0 },
        { "reporteVigencia.pdf", nameof(Documento), 0 },
        { "reporteAsignaciones.xlsx", nameof(Asignacion), 1 },
        { "reporteAsignaciones.pdf", nameof(Asignacion), 1 },
    };

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task La_exportacion_deja_rastro_de_lo_que_entrega(string exportacion, string entidad, int filas)
    {
        var registro = new RegistroFalso();

        var resultado = await Exportaciones[exportacion](registro);

        resultado.Should().Match<IResult>(r => r is FileStreamHttpResult || r is FileContentHttpResult);
        var rastro = registro.Registros.Should().ContainSingle().Subject;
        rastro.Tipo.Should().Be(entidad);
        rastro.Filas.Should().Be(filas);
        rastro.Criterios.Should().ContainKey("listado");
        rastro.Criterios["listado"].Should().Be(exportacion.Split('.')[0]);
    }

    [Theory]
    [MemberData(nameof(Casos))]
    public async Task Sin_rastro_no_se_entrega_el_fichero(string exportacion, string entidad, int filas)
    {
        _ = (entidad, filas);

        var accion = () => Exportaciones[exportacion](new RegistroFalso(falla: true));

        await accion.Should().ThrowAsync<SinRastroException>();
    }

    [Fact]
    public async Task El_informe_deja_el_formato_y_sus_filtros_en_el_rastro()
    {
        var registro = new RegistroFalso();
        var clienteId = Guid.NewGuid();

        await ReportesEndpoints.VigenciaPdfAsync(new MediatorFalso(), registro, clienteId, null, true, default);

        registro.Registros.Single().Criterios.Should().Contain("formato", "pdf")
            .And.Contain("cliente", clienteId.ToString())
            .And.Contain("incluirVigentes", "true")
            .And.NotContainKey("centro");
    }

    [Fact]
    public async Task El_filtro_de_la_auditoria_exportada_solo_llega_al_rastro_si_es_un_nombre()
    {
        var registro = new RegistroFalso();

        await AuditoriaEndpoints.ExportarAsync("12345678Z", new MediatorFalso(), registro, default);

        registro.Registros.Single().Criterios.Should().NotContainKey("entidad");
    }
}
