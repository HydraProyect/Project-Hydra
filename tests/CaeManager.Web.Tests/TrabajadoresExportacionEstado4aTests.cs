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
///
/// <para>
/// También mide lo que el endpoint hace con los criterios de «Exportar esta vista» (viajan a
/// la misma <see cref="ObtenerTrabajadoresQuery"/> del listado) y el rastro de la descarga, que
/// se escribe antes de entregar el fichero. No pasa por el routing de ASP.NET: el enlace de los
/// parámetros de consulta lo miden los tests de integración de exportación.
/// </para>
/// </summary>
public class TrabajadoresExportacionEstado4aTests
{
    private static readonly Guid Origen = Guid.NewGuid();
    private static readonly Guid Externo = Guid.NewGuid();

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId { get; } = tenantId;
    }

    private sealed class RegistroFalso(bool falla = false) : IRegistroExportacionService
    {
        public List<(string EntidadTipo, int Filas, IReadOnlyDictionary<string, string> Criterios)> Registros { get; } = [];

        public Task RegistrarAsync(
            string entidadTipo, int filas, IReadOnlyDictionary<string, string> criterios,
            CancellationToken cancellationToken = default)
        {
            if (falla)
                throw new InvalidOperationException("sin rastro");
            Registros.Add((entidadTipo, filas, criterios));
            return Task.CompletedTask;
        }
    }

    private sealed class MediatorFalso(
        IReadOnlyList<ClienteAutorizadoDto> autorizados, IReadOnlyList<TrabajadorListaDto>? trabajadores = null) : IMediator
    {
        public int ConsultasDeTrabajadores { get; private set; }
        public ObtenerTrabajadoresQuery? UltimaConsulta { get; private set; }

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
            UltimaConsulta = q;
            var todos = trabajadores ?? [];
            return new ResultadoPaginado<TrabajadorListaDto>(
                q.Pagina == 1 ? todos : [], todos.Count, q.Pagina, q.TamanoPagina);
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
        var registro = new RegistroFalso();

        var resultado = await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Origen), registro, default);

        resultado.Should().BeOfType<RedirectHttpResult>().Which.Url.Should().Be("/trabajadores");
        mediador.ConsultasDeTrabajadores.Should().Be(0, "no se piden los trabajadores de la organización de origen");
        registro.Registros.Should().BeEmpty("no salió ningún fichero");
    }

    [Fact]
    public async Task Con_una_empresa_de_la_cartera_elegida_exporta()
    {
        var mediador = new MediatorFalso([Propio(), Cartera()]);
        var registro = new RegistroFalso();

        var resultado = await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Externo), registro, default);

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
            var registro = new RegistroFalso();

            var resultado = await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Origen), registro, default);

            resultado.Should().NotBeOfType<RedirectHttpResult>();
            mediador.ConsultasDeTrabajadores.Should().BeGreaterThan(0);
        }
    }

    private static readonly TrabajadorListaDto[] DosTrabajadores =
    [
        new(Guid.NewGuid(), "Lucía", "Prieto Ramos", "01234567Z", "Refrigeración Norte, S.L."),
        new(Guid.NewGuid(), "Iker", "Sanz Olmo", "X1234567L", "Montajes Sur, S.L."),
    ];

    [Fact]
    public async Task Exportar_esta_vista_pasa_sus_criterios_a_la_consulta_del_listado()
    {
        var mediador = new MediatorFalso([Propio()], DosTrabajadores);
        var empresa = Guid.NewGuid();
        var subcontrata = Guid.NewGuid();
        var centro = Guid.NewGuid();

        await TrabajadoresEndpoints.ExportarAsync(
            mediador, new TenantActualFalso(Origen), new RegistroFalso(), default,
            q: "prieto", estado: "Vencido", empresa: empresa.ToString(), subcontrata: subcontrata.ToString(),
            orden: nameof(TrabajadorListaDto.Apellidos), desc: true, centro: centro.ToString());

        // El Excel no lleva columnas del desglose documental: se pide sin él. El «false» va escrito aquí aunque
        // sea el valor por defecto de la consulta: así este test no depende de cuál sea ese valor.
        mediador.UltimaConsulta.Should().BeEquivalentTo(
            new ObtenerTrabajadoresQuery(
                Busqueda: "prieto", EmpresaId: empresa, SubcontrataId: subcontrata,
                OrdenarPor: nameof(TrabajadorListaDto.Apellidos), Descendente: true, EstadoDocumental: "Vencido",
                CentroId: centro, ConDesgloseDocumental: false),
            o => o.Excluding(c => c.Pagina).Excluding(c => c.TamanoPagina));
    }

    [Fact]
    public async Task Exportar_todo_no_pasa_ningun_criterio()
    {
        var mediador = new MediatorFalso([Propio()], DosTrabajadores);

        await TrabajadoresEndpoints.ExportarAsync(mediador, new TenantActualFalso(Origen), new RegistroFalso(), default);

        mediador.UltimaConsulta.Should().BeEquivalentTo(
            new ObtenerTrabajadoresQuery(Busqueda: null, ConDesgloseDocumental: false),
            o => o.Excluding(c => c.Pagina).Excluding(c => c.TamanoPagina));
    }

    [Fact]
    public async Task La_descarga_deja_rastro_con_las_filas_y_los_criterios()
    {
        var registro = new RegistroFalso();

        await TrabajadoresEndpoints.ExportarAsync(
            new MediatorFalso([Propio()], DosTrabajadores), new TenantActualFalso(Origen), registro, default,
            q: "01234567Z", estado: "Vencido");

        var rastro = registro.Registros.Should().ContainSingle().Subject;
        rastro.EntidadTipo.Should().Be("Trabajador");
        rastro.Filas.Should().Be(DosTrabajadores.Length);
        // La búsqueda era un DNI: el rastro dice que la hubo, no la copia.
        rastro.Criterios.Should().BeEquivalentTo(new Dictionary<string, string> { ["busqueda"] = "true", ["estado"] = "Vencido" });
    }

    /// <summary>
    /// Un parámetro de consulta es texto libre: quien teclea la URL puede poner un DNI donde la
    /// página pondría un Id. Ese filtro no se aplica (no es un Guid) y tampoco llega al rastro.
    /// </summary>
    [Fact]
    public async Task El_rastro_lleva_el_criterio_aplicado_y_no_el_texto_de_la_peticion()
    {
        var registro = new RegistroFalso();
        var subcontrata = Guid.NewGuid();

        await TrabajadoresEndpoints.ExportarAsync(
            new MediatorFalso([Propio()], DosTrabajadores), new TenantActualFalso(Origen), registro, default,
            estado: "01234567Z", empresa: "01234567Z", subcontrata: subcontrata.ToString(),
            orden: "Apellidos; 01234567Z", desc: true);

        registro.Registros.Should().ContainSingle().Which.Criterios.Should().BeEquivalentTo(
            new Dictionary<string, string> { ["subcontrata"] = subcontrata.ToString(), ["desc"] = "true" });
    }

    [Fact]
    public async Task Si_el_rastro_no_se_puede_guardar_el_fichero_no_sale()
    {
        var exportar = () => TrabajadoresEndpoints.ExportarAsync(
            new MediatorFalso([Propio()], DosTrabajadores), new TenantActualFalso(Origen), new RegistroFalso(falla: true), default);

        await exportar.Should().ThrowAsync<InvalidOperationException>().WithMessage("sin rastro");
    }
}
