namespace CaeManager.E2ETests;

/// <summary>
/// P0 del piloto Outbound (2026-09-28), caso del Administrador de un Operador CAE
/// externo que alcanza el Tenant beneficiario por delegación: cambia de Tenant con el
/// selector, abre Trabajador 360 desde la lista y el contexto debe seguir en el Tenant
/// beneficiario elegido, sin aviso de fin de acceso. El caso del Gestor CAE con
/// Asignación de Cartera está en <see cref="GestorCaeCarteraMultiTenantTests"/>.
/// </summary>
[Collection("AppCollectionVentanaSoporte")]
public class SeleccionTenantDelegadoFichas360Tests(WebAppFixtureVentanaSoporte fixture)
{
    [Fact]
    public async Task El_Tenant_beneficiario_elegido_sobrevive_a_abrir_las_fichas_360_desde_sus_listas()
    {
        await using var contexto = await fixture.Browser.NewContextAsync();
        var page = await contexto.NewPageAsync();
        await Ayudas.IniciarSesionAsync(page, fixture.BaseUrl, Ayudas.EmailOperadorConsultaConsultora, Ayudas.ContrasenaUsuariosPrueba);

        var tenantBeneficiario = await fixture.LeerValorSqlAsync(
            """SELECT "Id"::text FROM "Tenants" WHERE "Nombre" = @n""", ("n", Ayudas.NombreClienteDelegadoDemo));
        Assert.NotNull(tenantBeneficiario);
        await Ayudas.CambiarClienteActivoAsync(page, fixture, Ayudas.NombreClienteDelegadoDemo);

        await RecorridoFichas360.RecorrerTodasAsync(page, fixture.BaseUrl, tenantBeneficiario);
    }
}
