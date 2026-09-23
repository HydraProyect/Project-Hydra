using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Queries.ObtenerRolesNoAsignables;
using CaeManager.Application.Usuarios.Queries.VerificarRolAsignable;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Usuarios;

/// <summary>
/// Decisión del propietario, 2026-09-23: Administrador y Dirección CAE solo se
/// conceden desde /usuarios cuando el Context Workspace activo es el Tenant de
/// origen de quien actúa. Los roles de Operación y de portal no se restringen.
/// </summary>
public class RolesReservadosAlTenantDeOrigenTests
{
    private static readonly Guid TenantOperadorCae = Guid.NewGuid();   // Tenant de origen del actor (ArcosSPA)
    private static readonly Guid TenantPropietario = Guid.NewGuid();   // Context Workspace delegado (Refrielectric)

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Rechaza_los_roles_reservados_en_un_Context_Workspace_cruzado(string rol)
    {
        var resultado = await VerificarAsync(rol, tenantOrigen: TenantOperadorCae, contexto: TenantPropietario);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Usuarios.RolReservadoAlTenantDeOrigen");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    public async Task Admite_los_roles_reservados_en_el_Tenant_de_origen(string rol)
    {
        var resultado = await VerificarAsync(rol, tenantOrigen: TenantOperadorCae, contexto: TenantOperadorCae);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Los_roles_no_reservados_se_admiten_tambien_en_un_Context_Workspace_cruzado(string rol)
    {
        var resultado = await VerificarAsync(rol, tenantOrigen: TenantOperadorCae, contexto: TenantPropietario);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task Sin_Tenant_de_origen_o_sin_Context_Workspace_resueltos_falla_cerrado(bool hayOrigen, bool hayContexto)
    {
        var resultado = await VerificarAsync(
            "Administrador",
            tenantOrigen: hayOrigen ? TenantOperadorCae : null,
            contexto: hayContexto ? TenantOperadorCae : null);

        resultado.EsFallido.Should().BeTrue();
    }

    [Fact]
    public async Task El_selector_no_ofrece_los_roles_reservados_en_un_Context_Workspace_cruzado()
    {
        var handler = new ObtenerRolesNoAsignablesQueryHandler(
            new CurrentUserServiceFalso(tenantOrigenId: TenantOperadorCae), new TenantActualFalso(TenantPropietario));

        var roles = await handler.Handle(new ObtenerRolesNoAsignablesQuery(), CancellationToken.None);

        roles.Should().BeEquivalentTo("Administrador", "DireccionCae");
    }

    [Fact]
    public async Task El_selector_lo_ofrece_todo_en_el_Tenant_de_origen()
    {
        var handler = new ObtenerRolesNoAsignablesQueryHandler(
            new CurrentUserServiceFalso(tenantOrigenId: TenantOperadorCae), new TenantActualFalso(TenantOperadorCae));

        var roles = await handler.Handle(new ObtenerRolesNoAsignablesQuery(), CancellationToken.None);

        roles.Should().BeEmpty();
    }

    [Fact]
    public void Solo_Administrador_y_Direccion_CAE_estan_reservados()
    {
        RolesReservadosAlTenantDeOrigen.Roles.Should().BeEquivalentTo("Administrador", "DireccionCae");
    }

    private static Task<CaeManager.Domain.Common.Result> VerificarAsync(string rol, Guid? tenantOrigen, Guid? contexto) =>
        new VerificarRolAsignableQueryHandler(new CurrentUserServiceFalso(tenantOrigenId: tenantOrigen), new TenantActualFalso(contexto))
            .Handle(new VerificarRolAsignableQuery(rol), CancellationToken.None);

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }
}
