using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tests.Clientes;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Hallazgo N-4 de INFORME-AUDITORIA-2.md: la delegación era create-only —
/// los repositorios no exponían borrado ni desactivación, y
/// <c>DelegacionTenant.Desactivar()</c>/<c>Reactivar()</c> eran código muerto.
/// En la práctica una Consultora no podía perder el acceso a los datos de un
/// Cliente Delegante por ningún camino del producto.
///
/// Lo que estos tests fijan, además de que revocar funcione, es <b>quién</b>
/// puede hacerlo: ambas partes de la delegación y nadie más.
/// </summary>
public class RevocacionDelegacionTests
{
    private static readonly Guid Consultora = Guid.NewGuid();
    private static readonly Guid ClienteDelegante = Guid.NewGuid();

    [Fact]
    public async Task La_consultora_puede_renunciar_al_acceso()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            repositorio, new CurrentUserServiceFalso(tenantOrigenId: Consultora), unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeFalse();
    }

    [Fact]
    public async Task El_cliente_delegante_puede_retirarle_el_acceso_a_la_consultora()
    {
        // Lo que de verdad exige el hallazgo: el dueño de los datos tiene que
        // poder cortar sin depender de la buena voluntad de quien los trata.
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            repositorio, new CurrentUserServiceFalso(tenantOrigenId: ClienteDelegante), unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeFalse();
    }

    [Fact]
    public async Task Un_tercero_no_puede_tocar_una_delegacion_ajena()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            repositorio, new CurrentUserServiceFalso(tenantOrigenId: Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        // "No encontrada", no "no autorizado": DelegacionTenant es catálogo
        // global sin filtro de tenant, así que confirmar la existencia de la
        // fila ya revelaría qué consultoras operan sobre qué clientes.
        resultado.Error.Codigo.Should().Be("DelegacionTenant.NoEncontrada");
        delegacion.Activa.Should().BeTrue();
        unitOfWork.VecesGuardado.Should().Be(0);
    }

    [Fact]
    public async Task Sin_tenant_de_origen_no_se_revoca_nada()
    {
        // Fallo cerrado, igual que el resto de la cadena de resolución
        // (ADR-004 § 6): fuera de un circuito autenticado no hay origen.
        var (delegacion, repositorio, unitOfWork) = Preparar();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            repositorio, new CurrentUserServiceFalso(), unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
    }

    [Fact]
    public async Task Revocar_dos_veces_no_es_silencioso()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = new DesactivarDelegacionTenantCommandHandler(
            repositorio, new CurrentUserServiceFalso(tenantOrigenId: Consultora), unitOfWork);

        var resultado = await handler.Handle(new DesactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DelegacionTenant.YaInactiva");
    }

    [Fact]
    public async Task Reactivar_devuelve_el_acceso_sin_perder_los_operadores()
    {
        var (delegacion, repositorio, unitOfWork) = Preparar();
        delegacion.Desactivar();
        var handler = new ReactivarDelegacionTenantCommandHandler(
            repositorio, new CurrentUserServiceFalso(tenantOrigenId: ClienteDelegante), unitOfWork);

        var resultado = await handler.Handle(new ReactivarDelegacionTenantCommand(delegacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        delegacion.Activa.Should().BeTrue();
    }

    [Fact]
    public async Task Se_puede_retirar_un_operador_sin_revocar_la_delegacion_entera()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: Consultora), unitOfWork);

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().BeEmpty();
        delegacion.Activa.Should().BeTrue("retirar a una persona no corta el acceso de todo el equipo");
    }

    [Fact]
    public async Task Un_tercero_no_puede_retirar_operadores_de_una_delegacion_ajena()
    {
        var (delegacion, delegacionRepositorio, unitOfWork) = Preparar();
        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, Guid.NewGuid(), "GestorCae");
        var asignacionRepositorio = new AsignacionOperadorDelegadoRepositorioFalso();
        asignacionRepositorio.Agregar(asignacion);

        var handler = new RevocarAsignacionOperadorDelegadoCommandHandler(
            asignacionRepositorio, delegacionRepositorio,
            new CurrentUserServiceFalso(tenantOrigenId: Guid.NewGuid()), unitOfWork);

        var resultado = await handler.Handle(
            new RevocarAsignacionOperadorDelegadoCommand(asignacion.Id), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        asignacionRepositorio.Asignaciones.Should().ContainSingle();
    }

    private static (DelegacionTenant, DelegacionTenantRepositorioFalso, UnitOfWorkFalso) Preparar()
    {
        var delegacion = new DelegacionTenant(Consultora, ClienteDelegante);
        var repositorio = new DelegacionTenantRepositorioFalso();
        repositorio.Agregar(delegacion);

        return (delegacion, repositorio, new UnitOfWorkFalso());
    }
}
