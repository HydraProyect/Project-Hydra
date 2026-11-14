using CaeManager.Application.Tenants;
using CaeManager.Application.Tests.Comercial;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.Application.Tests.Tenants;

/// <summary>
/// Opción A (decisión de Chris del 2026-11-14, PR #2223): la vía de Operación de
/// <see cref="TenantsBeneficiariosAutorizados"/> exige que la DelegacionTenant de Operador CAE externo del par
/// (Tenant propietario, Operador CAE externo) siga activa y sin caducar, el mismo predicado que
/// <c>AlcanceDatosService.ObtenerCarteraAsync</c>. Una Asignación de Operación acotada no lleva la caducidad de la
/// delegación como fin: hasta que el job de expiración o el siguiente arranque la cierran, ella y la Asignación de
/// Cartera del Gestor CAE siguen vigentes. Sin la delegación vigente, el Gestor CAE no elige el Tenant
/// beneficiario en el selector, no lo revalida y no recibe rol en él.
/// </summary>
public class ViaDeOperacionExigeDelegacionVigenteTests
{
    private static readonly Guid Gestor = Guid.NewGuid();
    private static readonly Guid OperadorCaeExterno = Guid.NewGuid();
    private static readonly Guid TenantBeneficiario = Guid.NewGuid();
    private static readonly DateTime Ahora = DateTime.UtcNow;
    private const string RolGestorCae = TenantsBeneficiariosAutorizados.RolAcotadoPorCartera;

    private static T Vigente<T>(T asignacion) where T : class
    {
        typeof(T).GetProperty("Estado")!.SetValue(asignacion, EstadoAsignacion.Vigente);
        return asignacion;
    }

    /// <summary>Operación Outbound acotada a un Cliente empresarial, con la cartera del Gestor CAE bajo ella.</summary>
    private static (OperacionesEnMemoria Contexto, AsignacionOperacion Operacion) OperacionAcotadaConCartera()
    {
        var operacion = Vigente(AsignacionOperacion.Externa(
            TenantBeneficiario, OperadorCaeExterno, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid()), Ahora.AddDays(-30), null, Ahora));
        var cartera = Vigente(AsignacionCartera.Externa(
            operacion, Gestor, RolGestorCae, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora));
        return (new OperacionesEnMemoria([operacion], [cartera]), operacion);
    }

    private static TenantsQueryContextFalso Con(DelegacionTenant? delegacion)
    {
        var tenants = new TenantsQueryContextFalso();
        if (delegacion is not null) tenants.ListaDelegacionesTenant.Add(delegacion);
        return tenants;
    }

    private static DelegacionTenant DelPar() => new(OperadorCaeExterno, TenantBeneficiario);

    private static DelegacionTenant ConCaducidad(DelegacionTenant delegacion, DateTime expiraEnUtc)
    {
        typeof(DelegacionTenant).GetProperty(nameof(DelegacionTenant.ExpiraEnUtc))!.SetValue(delegacion, expiraEnUtc);
        return delegacion;
    }

    public static TheoryData<string> SinDelegacionVigente() =>
        ["caduca justo ahora", "caducada", "desactivada", "ausente", "de otro Tenant propietario", "de otro Operador CAE",
         "con los extremos invertidos", "de Soporte TALVEG"];

    private static DelegacionTenant? Delegacion(string caso)
    {
        switch (caso)
        {
            case "caduca justo ahora": return ConCaducidad(DelPar(), Ahora);
            case "caducada": return ConCaducidad(DelPar(), Ahora.AddDays(-1));
            case "desactivada":
                var desactivada = DelPar();
                desactivada.Desactivar();
                return desactivada;
            case "ausente": return null;
            case "de otro Tenant propietario": return new DelegacionTenant(OperadorCaeExterno, Guid.NewGuid());
            case "de otro Operador CAE": return new DelegacionTenant(Guid.NewGuid(), TenantBeneficiario);
            case "con los extremos invertidos": return new DelegacionTenant(TenantBeneficiario, OperadorCaeExterno);
            case "de Soporte TALVEG":
                var soporte = DelegacionTenant.ParaSoporte(OperadorCaeExterno, TenantBeneficiario);
                soporte.ActivarParaSoporte("Incidencia de prueba", Ahora.AddDays(1), Ahora);
                return soporte;
            default: throw new ArgumentOutOfRangeException(nameof(caso), caso, null);
        }
    }

    [Theory]
    [MemberData(nameof(SinDelegacionVigente))]
    public async Task Sin_DelegacionTenant_vigente_del_par_la_cartera_bajo_una_operacion_acotada_no_autoriza(string caso)
    {
        var (operaciones, operacion) = OperacionAcotadaConCartera();
        var tenants = Con(Delegacion(caso));

        (await TenantsBeneficiariosAutorizados.CarterasPorOperacionDeLaOrganizacion(operaciones, tenants, OperadorCaeExterno, Ahora)
                .ToListAsync())
            .Should().BeEmpty("sin delegación vigente del par la operación externa no autoriza ({0})", caso);
        (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, RolGestorCae, Ahora, default))
            .Should().BeNull("el selector no ofrece ni acepta el Tenant beneficiario ({0})", caso);
        (await TenantsBeneficiariosAutorizados.SigueAutorizadoPorOperacionAsync(
                operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, operacion.Id, RolGestorCae, Ahora,
                default))
            .Should().BeFalse("la revalidación de una selección anterior cae ({0})", caso);
        (await TenantsBeneficiariosAutorizados.RolEnTenantDelegadoAsync(
                operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, operacion.Id, RolGestorCae,
                RolGestorCae, Ahora, default))
            .Should().BeNull("no hay rol efectivo en el Tenant beneficiario ({0})", caso);
        (await TenantsBeneficiariosAutorizados.RolPorOperacionAsync(
                operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, operacion.Id, Ahora, default))
            .Should().BeNull("tampoco hay rol de cartera ({0})", caso);
    }

    [Fact]
    public async Task Con_la_DelegacionTenant_del_par_activa_y_sin_caducar_la_misma_cartera_autoriza()
    {
        var (operaciones, operacion) = OperacionAcotadaConCartera();

        foreach (var tenants in new[] { Con(DelPar()), Con(ConCaducidad(DelPar(), Ahora.AddDays(1))) })
        {
            (await TenantsBeneficiariosAutorizados.CarterasPorOperacionDeLaOrganizacion(operaciones, tenants, OperadorCaeExterno, Ahora)
                    .Select(v => v.Operacion.Id).ToListAsync())
                .Should().Equal([operacion.Id]);
            (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                    operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, RolGestorCae, Ahora, default))
                .Should().Be(operacion.Id);
            (await TenantsBeneficiariosAutorizados.RolEnTenantDelegadoAsync(
                    operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, operacion.Id, RolGestorCae,
                    RolGestorCae, Ahora, default))
                .Should().Be(RolGestorCae);
        }
    }

    /// <summary>
    /// El buzón aplica la misma vía de Operación desde la posición del Tenant propietario: un Gestor CAE del
    /// Operador CAE externo cuya delegación caducó no puede ser responsable del buzón por su cartera.
    /// </summary>
    [Fact]
    public async Task El_Gestor_CAE_con_la_delegacion_caducada_no_es_responsable_elegible_por_la_via_de_Operacion()
    {
        var (operaciones, _) = OperacionAcotadaConCartera();
        // Sin rol guardado: la vía heredada no lo rescata y solo decide la de Operación.
        var destino = new CaeManager.Application.Clientes.DestinoCartera(
            true, null, null, true, OperadorCaeExterno, RolGestorCae);

        async Task<bool> PuedeAtenderAsync(TenantsQueryContextFalso tenants) =>
            await ResponsableBuzonElegibleDePrueba.Con(destino, operaciones, TenantBeneficiario, tenants: tenants)
                .EvaluarAsync(Gestor, new CaeManager.Domain.Integraciones.ConexionIntegracion(
                    "cae@ejemplo.test", "Buzón CAE", null), default) is null;

        (await PuedeAtenderAsync(Con(DelPar()))).Should().BeTrue("con la delegación vigente atiende el buzón del Tenant entero");
        (await PuedeAtenderAsync(Con(ConCaducidad(DelPar(), Ahora.AddDays(-1))))).Should().BeFalse(
            "con la delegación caducada la vía de Operación no le da rol de gestión");
    }
}
