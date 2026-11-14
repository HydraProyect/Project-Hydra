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
/// Procedencia de la Asignación de Operación externa (hallazgos de Codex en la PR #2223, opción A de Chris del
/// 2026-11-14): la vía de Operación de <see cref="TenantsBeneficiariosAutorizados"/>, con cartera y sin ella,
/// exige la DelegacionTenant concreta de la que nació la operación, activa, sin caducar y sin revocaciones desde
/// entonces (<c>AmparoDeOrigen</c>), el mismo predicado que <c>AlcanceDatosService.ObtenerCarteraAsync</c>.
/// Otra delegación vigente del par no la sostiene; una operación que sobrevivió a una revocación no vuelve a
/// autorizar aunque la delegación se reactive; y una operación externa sin origen falla cerrada. Sin amparo, el
/// Gestor CAE no elige el Tenant beneficiario, no lo revalida y no recibe rol en él, y el perfil que entra sin
/// cartera tampoco.
/// </summary>
public class ViaDeOperacionExigeDelegacionVigenteTests
{
    private static readonly Guid Gestor = Guid.NewGuid();
    private static readonly Guid OperadorCaeExterno = Guid.NewGuid();
    private static readonly Guid TenantBeneficiario = Guid.NewGuid();
    private static readonly DateTime Ahora = DateTime.UtcNow;
    private const string RolGestorCae = TenantsBeneficiariosAutorizados.RolAcotadoPorCartera;
    private const string RolSinCartera = "CoordinadorCae";

    private static T Vigente<T>(T asignacion) where T : class
    {
        typeof(T).GetProperty("Estado")!.SetValue(asignacion, EstadoAsignacion.Vigente);
        return asignacion;
    }

    private static DelegacionTenant DelPar() => new(OperadorCaeExterno, TenantBeneficiario);

    private static DelegacionTenant ConCaducidad(DelegacionTenant delegacion, DateTime expiraEnUtc)
    {
        typeof(DelegacionTenant).GetProperty(nameof(DelegacionTenant.ExpiraEnUtc))!.SetValue(delegacion, expiraEnUtc);
        return delegacion;
    }

    /// <summary>
    /// Una operación Outbound (acotada o del Tenant entero) con la cartera del Gestor CAE bajo ella, y las
    /// delegaciones que ve el lector. <paramref name="preparar"/> fija el origen y deja las delegaciones en el
    /// estado del caso; se pasan siempre explícitas, así que el contexto en memoria no ampara por defecto.
    /// </summary>
    private static (OperacionesEnMemoria Contexto, AsignacionOperacion Operacion) Escenario(
        bool delTenantEntero, Func<AsignacionOperacion, IEnumerable<DelegacionTenant>> preparar)
    {
        var ambito = delTenantEntero ? AmbitoAsignacion.Universal : AmbitoAsignacion.DeRelacionCliente(Guid.NewGuid());
        var operacion = Vigente(AsignacionOperacion.Externa(
            TenantBeneficiario, OperadorCaeExterno, ServicioCae.Outbound, ambito, Ahora.AddDays(-30), null, Ahora));
        var cartera = Vigente(AsignacionCartera.Externa(
            operacion, Gestor, RolGestorCae, AmbitoAsignacion.Universal, Ahora.AddDays(-1), null, Ahora));
        var delegaciones = preparar(operacion).ToList();
        return (new OperacionesEnMemoria([operacion], [cartera], delegaciones), operacion);
    }

    public static TheoryData<string> SinAmparo() =>
        ["caduca justo ahora", "caducada", "desactivada", "revocada y reactivada", "otra vigente del par",
         "sin origen y con una vigente del par", "sin origen ni delegación", "de otro Tenant propietario",
         "de otro Operador CAE", "con los extremos invertidos", "de Soporte TALVEG"];

    private static IEnumerable<DelegacionTenant> Preparar(string caso, AsignacionOperacion operacion)
    {
        DelegacionTenant Origen(DelegacionTenant delegacion)
        {
            operacion.FijarOrigen(delegacion);
            return delegacion;
        }

        switch (caso)
        {
            case "caduca justo ahora": return [Origen(ConCaducidad(DelPar(), Ahora))];
            case "caducada": return [Origen(ConCaducidad(DelPar(), Ahora.AddDays(-1)))];
            case "desactivada":
                var desactivada = Origen(DelPar());
                desactivada.Desactivar();
                return [desactivada];
            case "revocada y reactivada":
                // La operación sobrevivió a la revocación (la ranura anterior no la cerró): reactivar no la repone.
                var reactivada = Origen(DelPar());
                reactivada.Desactivar();
                reactivada.Reactivar();
                return [reactivada];
            case "otra vigente del par":
                var revocada = Origen(DelPar());
                revocada.Desactivar();
                return [revocada, DelPar()];
            case "sin origen y con una vigente del par": return [DelPar()];
            case "sin origen ni delegación": return [];
            case "de otro Tenant propietario": return [new DelegacionTenant(OperadorCaeExterno, Guid.NewGuid())];
            case "de otro Operador CAE": return [new DelegacionTenant(Guid.NewGuid(), TenantBeneficiario)];
            case "con los extremos invertidos": return [new DelegacionTenant(TenantBeneficiario, OperadorCaeExterno)];
            case "de Soporte TALVEG":
                var soporte = DelegacionTenant.ParaSoporte(OperadorCaeExterno, TenantBeneficiario);
                soporte.ActivarParaSoporte("Incidencia de prueba", Ahora.AddDays(1), Ahora);
                return [soporte];
            default: throw new ArgumentOutOfRangeException(nameof(caso), caso, null);
        }
    }

    [Theory]
    [MemberData(nameof(SinAmparo))]
    public async Task Sin_el_amparo_de_su_DelegacionTenant_de_origen_la_cartera_bajo_la_operacion_no_autoriza(string caso)
    {
        var (operaciones, operacion) = Escenario(delTenantEntero: false, o => Preparar(caso, o));
        var tenants = new TenantsQueryContextFalso();

        (await TenantsBeneficiariosAutorizados.CarterasPorOperacionDeLaOrganizacion(operaciones, tenants, OperadorCaeExterno, Ahora)
                .ToListAsync())
            .Should().BeEmpty("sin el amparo de su delegación de origen la operación externa no autoriza ({0})", caso);
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
        (await TenantsBeneficiariosAutorizados.AmbitosEfectivosDeCarteraAsync(
                operaciones, [Gestor], TenantBeneficiario, OperadorCaeExterno, Ahora, default))
            .Should().BeEmpty("sin amparo la cartera no da ámbito efectivo ({0})", caso);
    }

    /// <summary>
    /// La vía sin cartera (Administrador, Dirección CAE, Coordinador CAE y Consulta sobre la operación del Tenant
    /// entero) antes no miraba ninguna delegación: una operación superviviente volvía a autorizar al activarse.
    /// </summary>
    [Theory]
    [MemberData(nameof(SinAmparo))]
    public async Task Sin_el_amparo_de_su_DelegacionTenant_de_origen_la_operacion_del_Tenant_entero_no_abre_sin_cartera(string caso)
    {
        var (operaciones, _) = Escenario(delTenantEntero: true, o => Preparar(caso, o));
        var tenants = new TenantsQueryContextFalso();

        (await TenantsBeneficiariosAutorizados.OperacionesDelTenantEntero(operaciones, Ahora).ToListAsync())
            .Should().BeEmpty("la vía sin cartera exige también la delegación de origen ({0})", caso);
        (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, RolSinCartera, Ahora, default))
            .Should().BeNull("el perfil sin cartera no abre el Tenant beneficiario ({0})", caso);
        (await TenantsBeneficiariosAutorizados.OperaElTenantEnteroAsync(
                operaciones, OperadorCaeExterno, TenantBeneficiario, Ahora, default))
            .Should().BeFalse("vista desde el propietario tampoco lo opera ({0})", caso);
    }

    [Fact]
    public async Task Con_su_DelegacionTenant_de_origen_activa_y_sin_caducar_la_misma_operacion_autoriza()
    {
        foreach (var delTenantEntero in new[] { false, true })
            foreach (var origen in new Func<DelegacionTenant>[] { DelPar, () => ConCaducidad(DelPar(), Ahora.AddDays(1)) })
            {
                var (operaciones, operacion) = Escenario(delTenantEntero, o =>
                {
                    var delegacion = origen();
                    o.FijarOrigen(delegacion);
                    return [delegacion];
                });
                var tenants = new TenantsQueryContextFalso();

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

                if (delTenantEntero)
                    (await TenantsBeneficiariosAutorizados.OperacionQueAutorizaAsync(
                            operaciones, tenants, Gestor, OperadorCaeExterno, TenantBeneficiario, RolSinCartera, Ahora, default))
                        .Should().Be(operacion.Id, "la vía sin cartera con su delegación de origen vigente sí abre");
            }
    }

    /// <summary>
    /// El buzón aplica la misma vía de Operación desde la posición del Tenant propietario: un Gestor CAE del
    /// Operador CAE externo cuya delegación de origen caducó no puede ser responsable del buzón por su cartera.
    /// </summary>
    [Fact]
    public async Task El_Gestor_CAE_con_la_delegacion_de_origen_caducada_no_es_responsable_elegible_por_la_via_de_Operacion()
    {
        // Sin rol guardado: la vía heredada no lo rescata y solo decide la de Operación.
        var destino = new CaeManager.Application.Clientes.DestinoCartera(
            true, null, null, true, OperadorCaeExterno, RolGestorCae);

        async Task<bool> PuedeAtenderAsync(DelegacionTenant origen)
        {
            var (operaciones, _) = Escenario(delTenantEntero: false, o =>
            {
                o.FijarOrigen(origen);
                return [origen];
            });
            return await ResponsableBuzonElegibleDePrueba.Con(destino, operaciones, TenantBeneficiario)
                .EvaluarAsync(Gestor, new CaeManager.Domain.Integraciones.ConexionIntegracion(
                    "cae@ejemplo.test", "Buzón CAE", null), default) is null;
        }

        (await PuedeAtenderAsync(DelPar())).Should().BeTrue("con la delegación de origen vigente atiende el buzón");
        (await PuedeAtenderAsync(ConCaducidad(DelPar(), Ahora.AddDays(-1)))).Should().BeFalse(
            "con la delegación de origen caducada la vía de Operación no le da rol de gestión");
    }
}
