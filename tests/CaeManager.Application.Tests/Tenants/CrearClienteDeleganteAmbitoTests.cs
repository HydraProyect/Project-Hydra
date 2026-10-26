using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.CrearClienteDelegante;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Application.Tests.Operaciones;
using CaeManager.Application.Tests.Plataforma;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

public class CrearClienteDeleganteAmbitoTests
{
    [Fact]
    public async Task El_alta_guarda_el_vinculo_y_la_operacion_en_el_Tenant_propietario_y_restaura_el_contexto()
    {
        var origen = Guid.NewGuid();
        using var ambito = AmbitoTenantExplicito.Establecer(origen);
        var tenants = new TenantsFalsos();
        var delegaciones = new DelegacionTenantRepositorioFalso();
        var writer = new AsignacionesOperativasWriterFalso();
        var guardados = new GuardadosConTenant();
        var handler = new CrearClienteDeleganteCommandHandler(
            tenants, delegaciones, new AsignacionOperadorDelegadoRepositorioFalso(), new ParametrosFalsos(),
            AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: origen),
            writer, guardados);

        var resultado = await handler.Handle(new CrearClienteDeleganteCommand("Organización nueva"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        guardados.Tenants.Should().Equal(resultado.Valor, resultado.Valor);
        writer.OperacionesAbiertas.Should().ContainSingle().Which.Should().Be((resultado.Valor, origen));
        delegaciones.Delegaciones.Should().ContainSingle().Which.TenantClienteId.Should().Be(resultado.Valor);
        AmbitoTenantExplicito.TenantIdActual.Should().Be(origen);
    }

    private sealed class GuardadosConTenant : IUnitOfWork
    {
        public List<Guid?> Tenants { get; } = [];
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Tenants.Add(AmbitoTenantExplicito.TenantIdActual);
            return Task.FromResult(1);
        }
    }

    private sealed class TenantsFalsos : ITenantRepository
    {
        public Task<bool> ExisteConNombreAsync(string nombre, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<Tenant?> ObtenerPorIdAsync(Guid id, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Agregar(Tenant tenant) { }
    }

    private sealed class ParametrosFalsos : IParametroSistemaRepository
    {
        public Task<ParametroSistema> ObtenerAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public void Agregar(ParametroSistema parametro) { }
    }
}
