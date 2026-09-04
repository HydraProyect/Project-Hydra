using CaeManager.Application.Cumplimiento.Commands.RevocarInstruccionTratamientoIaTenantPropietario;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Cumplimiento;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Cumplimiento;

public class RevocarInstruccionTratamientoIaTenantPropietarioCommandHandlerTests
{
    [Fact]
    public async Task Sin_capacidad_AdminPlataforma_sobre_ese_tenant_falla_y_no_toca_la_fila()
    {
        var tenantId = Guid.NewGuid();
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var fila = new InstruccionTratamientoIaTenantPropietario(
            "v1", "v1", DateTime.UtcNow.AddDays(-5), OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());
        repositorio.Agregar(fila);

        var handler = new RevocarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            AutorizacionAdminPlataformaFalsa.SinNada(), new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RevocarInstruccionTratamientoIaTenantPropietarioCommand(tenantId, "DPA renegociado"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("InstruccionTratamientoIa.SinAutoridad");
        fila.EstaVigente.Should().BeTrue();
    }

    [Fact]
    public async Task Sin_instruccion_vigente_para_ese_tenant_falla()
    {
        var tenantId = Guid.NewGuid();
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();

        var handler = new RevocarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, new UnitOfWorkFalso());

        var resultado = await handler.Handle(new RevocarInstruccionTratamientoIaTenantPropietarioCommand(tenantId, "DPA renegociado"), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("InstruccionTratamientoIa.NoEncontrada");
    }

    [Fact]
    public async Task Con_autoridad_y_fila_vigente_la_cierra_sin_borrarla()
    {
        var repositorio = new InstruccionTratamientoIaTenantPropietarioRepositoryFalso();
        var fila = new InstruccionTratamientoIaTenantPropietario(
            "v1", "v1", DateTime.UtcNow.AddDays(-5), OrigenInstruccionTratamientoIa.AltaManualPlataforma, Guid.NewGuid());
        repositorio.Agregar(fila);
        var unitOfWork = new UnitOfWorkFalso();

        // El fake no sella TenantId (ver su comentario) — se lee del propio
        // objeto para simular "el tenant al que pertenece esta fila".
        var handler = new RevocarInstruccionTratamientoIaTenantPropietarioCommandHandler(
            AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid()), repositorio, unitOfWork);

        var resultado = await handler.Handle(new RevocarInstruccionTratamientoIaTenantPropietarioCommand(fila.TenantId, "DPA renegociado"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        fila.EstaVigente.Should().BeFalse();
        fila.MotivoRevocacion.Should().Be("DPA renegociado");
        repositorio.Filas.Should().ContainSingle("revocar cierra la fila, nunca la borra");
        unitOfWork.VecesGuardado.Should().Be(1);
    }
}
