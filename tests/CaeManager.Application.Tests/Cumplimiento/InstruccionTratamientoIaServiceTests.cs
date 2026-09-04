using CaeManager.Application.Cumplimiento;
using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

/// <summary>
/// Nivel 0 (DEC-33, REC-035): el único punto de consulta de si un Tenant
/// propietario tiene una instrucción vigente. El aislamiento por tenant en
/// sí (que este servicio nunca vea la fila de OTRO tenant) lo prueba la
/// suite de integración contra Postgres real — aquí solo la lógica pura de
/// "vigente ⇔ existe fila no revocada", con su control positivo y negativo.
/// </summary>
public class InstruccionTratamientoIaServiceTests
{
    [Fact]
    public async Task Sin_ninguna_fila_para_el_tenant_no_esta_habilitada()
    {
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var servicio = new InstruccionTratamientoIaService(repositorio);

        (await servicio.EstaHabilitadaAsync(Guid.NewGuid())).Should().BeFalse();
    }

    [Fact]
    public async Task Con_una_fila_vigente_para_el_tenant_esta_habilitada()
    {
        var tenantId = Guid.NewGuid();
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var fila = new InstruccionTratamientoIaTenantPropietario(
            "v1", "v1", DateTime.UtcNow, OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());
        repositorio.Agregar(fila);
        var servicio = new InstruccionTratamientoIaService(repositorio);

        (await servicio.EstaHabilitadaAsync(fila.TenantId)).Should().BeTrue();
    }

    [Fact]
    public async Task Con_una_fila_revocada_para_el_tenant_deja_de_estar_habilitada()
    {
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var fila = new InstruccionTratamientoIaTenantPropietario(
            "v1", "v1", DateTime.UtcNow.AddDays(-30), OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());
        fila.Revocar("Fin de contrato", DateTime.UtcNow);
        repositorio.Agregar(fila);
        var servicio = new InstruccionTratamientoIaService(repositorio);

        (await servicio.EstaHabilitadaAsync(fila.TenantId)).Should().BeFalse();
    }
}
