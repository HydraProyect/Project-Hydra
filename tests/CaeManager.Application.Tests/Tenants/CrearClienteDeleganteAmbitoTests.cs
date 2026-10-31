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
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public async Task Un_fallo_de_un_guardado_posterior_deshace_la_transaccion_y_restaura_el_ambito(int guardadoQueFalla)
    {
        var origen = Guid.NewGuid();
        using var ambito = AmbitoTenantExplicito.Establecer(origen);
        var transaccion = new TransaccionDeComandoFalsa();
        var guardados = new GuardadosConTenant(fallarEn: guardadoQueFalla);
        var handler = new CrearClienteDeleganteCommandHandler(
            new TenantsFalsos(), new DelegacionTenantRepositorioFalso(),
            new AsignacionOperadorDelegadoRepositorioFalso(), new ParametrosFalsos(),
            AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: origen),
            new AsignacionesOperativasWriterFalso(), guardados, transaccion);

        Func<Task> ejecutar = async () => await handler.Handle(new("Alta fallida"), CancellationToken.None);
        await ejecutar.Should().ThrowAsync<InvalidOperationException>().WithMessage("Guardado fallido");

        guardados.Tenants.Should().HaveCount(guardadoQueFalla).And.OnlyContain(t => t != null && t != origen);
        guardados.Tenants.Distinct().Should().ContainSingle();
        transaccion.Ejecutadas.Should().Be(1);
        transaccion.Deshechas.Should().Be(1);
        transaccion.Confirmadas.Should().Be(0);
        AmbitoTenantExplicito.TenantIdActual.Should().Be(origen);
    }

    [Fact]
    public async Task El_alta_guarda_el_vinculo_y_la_operacion_en_el_Tenant_propietario_y_restaura_el_contexto()
    {
        var origen = Guid.NewGuid();
        using var ambito = AmbitoTenantExplicito.Establecer(origen);
        var tenants = new TenantsFalsos();
        var delegaciones = new DelegacionTenantRepositorioFalso();
        var asignaciones = new AsignacionOperadorDelegadoRepositorioFalso();
        var writer = new AsignacionesOperativasWriterFalso();
        var pendientes = new List<(int Delegaciones, int Asignaciones)>();
        var guardados = new GuardadosConTenant(
            alGuardar: () => pendientes.Add((delegaciones.Delegaciones.Count, asignaciones.Asignaciones.Count)));
        var handler = new CrearClienteDeleganteCommandHandler(
            tenants, delegaciones, asignaciones, new ParametrosFalsos(),
            AutorizacionAdminPlataformaFalsa.Global(), new CurrentUserServiceFalso(Guid.NewGuid(), tenantOrigenId: origen),
            writer, guardados, new CaeManager.Application.Tests.Clientes.TransaccionDeComandoFalsa());

        var resultado = await handler.Handle(new CrearClienteDeleganteCommand("Organización nueva"), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        guardados.Tenants.Should().Equal(resultado.Valor, resultado.Valor, resultado.Valor);
        // Sin clave foránea EF no ordena los INSERT de un mismo guardado, y el WITH CHECK de la
        // asignación busca su delegación: el guardado intermedio la deja escrita antes de añadir la fila.
        pendientes.Should().Equal((0, 0), (1, 0), (1, 1));
        writer.OperacionesAbiertas.Should().ContainSingle().Which.Should().Be((resultado.Valor, origen));
        delegaciones.Delegaciones.Should().ContainSingle().Which.TenantClienteId.Should().Be(resultado.Valor);
        AmbitoTenantExplicito.TenantIdActual.Should().Be(origen);
    }

    private sealed class GuardadosConTenant(int? fallarEn = null, Action? alGuardar = null) : IUnitOfWork
    {
        public List<Guid?> Tenants { get; } = [];
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Tenants.Add(AmbitoTenantExplicito.TenantIdActual);
            alGuardar?.Invoke();
            if (Tenants.Count == fallarEn)
                throw new InvalidOperationException("Guardado fallido");
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
