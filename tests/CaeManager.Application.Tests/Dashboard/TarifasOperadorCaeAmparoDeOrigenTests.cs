using CaeManager.Application.Dashboard.TarifasOperadorCae;
using CaeManager.Application.Operaciones;
using CaeManager.Application.Tests.Integraciones;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Dashboard;

/// <summary>
/// Media 6 de la ronda 1 de Codex en la PR #2237: el Operador CAE externo solo fija tarifas a un Tenant beneficiario
/// que gestiona hoy por una Asignación de Operación amparada por la DelegacionTenant concreta de la que nació
/// (<see cref="AmparoDeOrigen"/>). Una operación sin origen, de otro origen, o que sobrevivió a la revocación o la
/// caducidad de su delegación sigue figurando Vigente en estado y fechas, y sin el amparo dejaría fijar tarifas.
/// </summary>
public class TarifasOperadorCaeAmparoDeOrigenTests
{
    private static readonly Guid Operador = Guid.NewGuid();
    private static readonly Guid Propietario = Guid.NewGuid();
    private static readonly DateTime Ahora = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    private static AsignacionOperacion Vigente() =>
        AsignacionOperacion.Externa(
            Propietario, Operador, ServicioCae.Outbound, AmbitoAsignacion.Universal, Ahora.AddDays(-30), null, Ahora.AddDays(-30));

    private static Task<bool> GestionaAsync(AsignacionOperacion operacion, params DelegacionTenant[] delegaciones) =>
        AutorizacionTarifasOperadorCae.GestionaAsync(
            new OperacionesEnMemoria([operacion], [], delegaciones), Operador, Propietario, Ahora, CancellationToken.None);

    [Fact]
    public async Task Control_la_operacion_amparada_por_su_origen_activo_gestiona()
    {
        var operacion = Vigente();
        var origen = new DelegacionTenant(Operador, Propietario);
        operacion.FijarOrigen(origen);

        (await GestionaAsync(operacion, origen)).Should().BeTrue("control: vigente y amparada por su delegación de origen");
    }

    [Fact]
    public async Task Sin_origen_no_gestiona_aunque_haya_una_delegacion_activa_del_par()
    {
        var operacion = Vigente();

        (await GestionaAsync(operacion, new DelegacionTenant(Operador, Propietario))).Should().BeFalse(
            "una operación externa sin origen falla cerrada: otra delegación activa del par no la sostiene");
    }

    [Fact]
    public async Task Con_otro_origen_no_gestiona_aunque_haya_otra_delegacion_activa_del_par()
    {
        var operacion = Vigente();
        var origen = new DelegacionTenant(Operador, Propietario);
        operacion.FijarOrigen(origen);
        origen.Desactivar();

        (await GestionaAsync(operacion, origen, new DelegacionTenant(Operador, Propietario))).Should().BeFalse(
            "su delegación de origen está revocada, y otra delegación vigente del mismo par no la ampara");
    }

    [Fact]
    public async Task Una_operacion_que_sobrevivio_a_la_revocacion_no_gestiona_tras_reactivar_la_delegacion()
    {
        var operacion = Vigente();
        var origen = new DelegacionTenant(Operador, Propietario);
        operacion.FijarOrigen(origen);
        origen.Desactivar();
        origen.Reactivar();

        (await GestionaAsync(operacion, origen)).Should().BeFalse(
            "reactivar no repone la operación que sobrevivió a la revocación: el contador de revocaciones ya no coincide");
    }
}
