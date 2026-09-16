using CaeManager.Application.Common;
using CaeManager.Application.Cumplimiento;
using CaeManager.Application.Cumplimiento.Queries.ObtenerEstadoTratamientoIaActual;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

public class ObtenerEstadoTratamientoIaActualQueryHandlerTests
{
    private sealed class InstruccionTratamientoIaFalsa(bool habilitada) : IInstruccionTratamientoIaService
    {
        public Guid? TenantConsultado { get; private set; }

        public Task<bool> EstaHabilitadaAsync(Guid tenantId, CancellationToken cancellationToken = default)
        {
            TenantConsultado = tenantId;
            return Task.FromResult(habilitada);
        }
    }

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    [Fact]
    public async Task Expone_la_instruccion_vigente_del_tenant_actual()
    {
        var tenantId = Guid.NewGuid();
        var instruccion = new InstruccionTratamientoIaFalsa(habilitada: true);
        var handler = new ObtenerEstadoTratamientoIaActualQueryHandler(instruccion, new TenantActualFalso(tenantId));

        var resultado = await handler.Handle(new ObtenerEstadoTratamientoIaActualQuery(), CancellationToken.None);

        resultado.InstruccionVigente.Should().BeTrue();
        instruccion.TenantConsultado.Should().Be(tenantId, "el Nivel 0 se consulta dentro del tenant actual");
    }

    [Fact]
    public async Task Sin_tenant_actual_cierra_el_estado_sin_consultar_el_servicio()
    {
        var instruccion = new InstruccionTratamientoIaFalsa(habilitada: true);
        var handler = new ObtenerEstadoTratamientoIaActualQueryHandler(instruccion, new TenantActualFalso(null));

        var resultado = await handler.Handle(new ObtenerEstadoTratamientoIaActualQuery(), CancellationToken.None);

        resultado.InstruccionVigente.Should().BeFalse();
        instruccion.TenantConsultado.Should().BeNull("sin tenant no hay autorización implícita para consultar el Nivel 0");
    }
}
