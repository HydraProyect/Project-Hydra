using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Telemetria;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Documentos;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Telemetria;
using FluentAssertions;

namespace CaeManager.Application.Tests.Dashboard;

public class DesempatesDashboardTests
{
    private static Guid Id(int i) => new(i, 0, 0, new byte[8]);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Extranjeros_desempata_por_asignacion_aunque_el_Trabajador_se_repita(bool invertir)
    {
        var dia = new DateOnly(2026, 10, 1);
        ObtenerExtranjerosDelMesQueryHandler.Fila Fila(int i) =>
            new(Id(i), Id(10), "Igual", DateTime.UnixEpoch, null, null, null, null,
                "Igual", dia, dia, "Igual", null, null, Id(20));
        var filas = new[] { Fila(2), Fila(1) };

        ObtenerExtranjerosDelMesQueryHandler.Ordenar(invertir ? filas.Reverse() : filas)
            .Select(f => f.Id).Should().Equal(Id(1), Id(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Pendientes_desempata_por_nombre_e_identificador_del_proveedor_de_plataforma(bool invertir)
    {
        PendientePorPlataformaDto[] filas = [new(Id(2), "Igual", 2, 1), new(Id(1), "Igual", 1, 2), new(Id(3), "Antes", 3, 0)];

        ObtenerPendientePorPlataformaQueryHandler.Ordenar(invertir ? filas.Reverse() : filas)
            .Select(f => f.ProveedorPlataformaCaeId).Should().Equal(Id(3), Id(1), Id(2));
    }

    [Fact]
    public void Pendientes_ordena_el_nombre_en_Ordinal_con_mayusculas_y_tildes()
    {
        PendientePorPlataformaDto[] filas = [new(Id(1), "álamo", 1, 0), new(Id(2), "Zeta", 1, 0), new(Id(3), "alamo", 1, 0)];

        ObtenerPendientePorPlataformaQueryHandler.Ordenar(filas)
            .Select(f => f.ProveedorNombre).Should().Equal("Zeta", "alamo", "álamo");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Facturacion_desempata_la_frecuencia_de_moneda_por_codigo_ordinal(bool invertir)
    {
        var concepto = CaeManager.Domain.Dashboard.ConceptoTarifaOperadorCae.VisitaTrabajadorExtranjero;
        FilaFacturacionOperadorCaeDto Fila(string moneda) => CalculosFacturacionOperadorCae.Fila(
            Id(1), "Igual", 1, 0, 0, 1, [], [new(concepto, "", moneda, 1, 10m, 10m, true)], []);
        var filas = new[] { Fila("USD"), Fila("EUR") };

        CalculosFacturacionOperadorCae.Total(invertir ? filas.Reverse().ToArray() : filas)
            .Moneda.Should().Be("EUR");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Las_horas_por_Cliente_empresarial_desempatan_antes_del_limite(bool invertir)
    {
        var empresas = new EmpresasQueryContextFalso();
        empresas.ListaEmpresas.AddRange(Enumerable.Range(1, 7).Select(i =>
            Empresa.CrearComoCliente(i >= 6 ? "Antes" : "Igual", "B12345674", false, null, null)));
        var ordenadas = empresas.ListaEmpresas.OrderBy(e => e.Id).ToArray();
        var inicio = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var registros = ordenadas.Reverse().Select(e => new RegistroTiempoGestion(
            Id(1), Id(2), e.Id, inicio, inicio.AddMinutes(2), 100, MotivoCierreSesionGestion.AccionCompletada)).ToArray();
        var handler = new ObtenerKpisBpoQueryHandler(
            new Telemetria(invertir ? registros.Reverse().ToArray() : registros), null!, null!, empresas, new AlcanceDatosServiceFalso());

        var resultado = await handler.CalcularHorasPorClienteAsync(inicio, inicio.AddMonths(1), default);

        resultado.Select(h => h.ClienteId).Should().Equal(
            ordenadas.Where(e => e.RazonSocial == "Antes").Select(e => e.Id)
                .Concat(ordenadas.Where(e => e.RazonSocial == "Igual").Take(3).Select(e => e.Id)));
        resultado.Should().OnlyContain(h => h.SegundosActivos == 100);
    }

    private sealed class Telemetria(RegistroTiempoGestion[] registros) : ITelemetriaQueryContext
    {
        public IQueryable<RegistroTiempoGestion> RegistrosTiempoGestion =>
            new TestAsyncQueryable<RegistroTiempoGestion>(registros.AsQueryable());
    }
}
