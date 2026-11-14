using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// Decisión de Chris del 2026-11-14 (hallazgo de Codex en la revisión de la PR #2215): el backfill de
/// arranque nunca reabre ni recrea una Asignación de Operación Outbound que ya se cerró o expiró, y una
/// delegación inactiva no deja ninguna autorización Outbound derivada, tampoco acotada. Antes, una
/// operación del Tenant entero expirada antes que su delegación (o revocada con la delegación todavía
/// marcada activa) volvía a autorizar en el arranque siguiente: el backfill solo buscaba una del Tenant
/// entero sin cerrar y, al no encontrarla, creaba otra desde la delegación.
///
/// <para>
/// Como en producción, el backfill corre con la conexión de bootstrap (cross-tenant por diseño), no
/// como <c>cae_app_runtime</c>: lo que se mide aquí es qué filas escribe. Que una operación cerrada no
/// autoriza bajo RLS lo fijan las pruebas de <c>TenantsBeneficiariosAutorizados</c>.
/// </para>
/// </summary>
public class BackfillNoReabreOperacionesCerradasTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _gestorOperador = Guid.NewGuid();
    private Guid _operador;
    private Guid _propietario;
    private Guid _delegacion;
    private Guid _clienteEmpresarial;

    public async Task InitializeAsync()
    {
        await using var contextoInicial = CrearContexto(Guid.NewGuid());
        await contextoInicial.Database.MigrateAsync();

        var operador = new Tenant("Operador CAE externo", PerfilVocabularioTenant.Consultora);
        var propietario = new Tenant("Tenant propietario", PerfilVocabularioTenant.ClienteDirecto);
        contextoInicial.Tenants.AddRange(operador, propietario);
        await contextoInicial.SaveChangesAsync();
        (_operador, _propietario) = (operador.Id, propietario.Id);

        await using var contexto = CrearContexto(_propietario);
        contexto.Users.Add(new ApplicationUser
        {
            Id = _gestorOperador,
            TenantId = _operador,
            UserName = "gestora@operador",
            Email = "gestora@operador"
        });
        await contexto.SaveChangesAsync();

        var delegacion = new DelegacionTenant(_operador, _propietario);
        contexto.DelegacionesTenant.Add(delegacion);
        var cliente = Empresa.CrearComoCliente("Cliente empresarial acotado", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();
        (_delegacion, _clienteEmpresarial) = (delegacion.Id, cliente.Id);
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Theory]
    [InlineData(MotivoCierreAsignacion.Expirada)]
    [InlineData(MotivoCierreAsignacion.Revocada)]
    public async Task Con_la_delegacion_activa_no_recrea_la_operacion_del_Tenant_entero_ya_cerrada(MotivoCierreAsignacion motivo)
    {
        await EjecutarBackfillAsync();
        (await ExternasOutboundAsync()).Should().ContainSingle("control: la primera pasada migra la delegación")
            .Which.Estado.Should().Be(EstadoAsignacion.Vigente);

        await using (var contexto = CrearContexto(_propietario))
        {
            var operacion = await contexto.AsignacionesOperacion.SingleAsync(o => !o.EsRaiz && o.PropietarioTenantId == _propietario);
            operacion.Cerrar(motivo, DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        await EjecutarBackfillAsync();

        var externas = await ExternasOutboundAsync();
        externas.Should().ContainSingle("el arranque no recrea una operación que ya se cerró o expiró")
            .Which.Estado.Should().Be(EstadoAsignacion.Cerrada);
        externas[0].MotivoCierre.Should().Be(motivo, "ni la reabre");
    }

    [Fact]
    public async Task Con_la_delegacion_activa_y_solo_una_operacion_acotada_no_abre_la_del_Tenant_entero()
    {
        // Abrirla ampliaría al Tenant entero lo que alguien acotó a un Cliente empresarial.
        await SembrarAcotadaConCarteraAsync();

        await EjecutarBackfillAsync();

        var externas = await ExternasOutboundAsync();
        externas.Should().ContainSingle().Which.Ambito.RelacionClienteId.Should().Be(_clienteEmpresarial);
    }

    [Fact]
    public async Task Con_la_delegacion_inactiva_cierra_tambien_la_operacion_acotada_y_su_cartera()
    {
        var acotada = await SembrarAcotadaConCarteraAsync();
        Guid inbound;
        await using (var contexto = CrearContexto(_propietario))
        {
            (await contexto.DelegacionesTenant.SingleAsync(d => d.Id == _delegacion)).Desactivar();
            var ahora = DateTime.UtcNow;
            var deInbound = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Inbound,
                AmbitoAsignacion.Universal, ahora.AddDays(-1), null, ahora);
            contexto.AsignacionesOperacion.Add(deInbound);
            await contexto.SaveChangesAsync();
            inbound = deInbound.Id;
        }

        await EjecutarBackfillAsync();

        await using var verificacion = CrearContexto(_propietario);
        var operacion = await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == acotada);
        operacion.Estado.Should().Be(EstadoAsignacion.Cerrada, "una delegación inactiva no deja autorización Outbound acotada");
        operacion.MotivoCierre.Should().Be(MotivoCierreAsignacion.MigradaSinFecha);
        (await verificacion.AsignacionesCartera.IgnoreQueryFilters()
                .Where(c => c.AsignacionOperacionId == acotada)
                .Select(c => c.Estado)
                .ToListAsync())
            .Should().ContainSingle().Which.Should().Be(EstadoAsignacion.Cerrada, "ni su cartera");
        (await verificacion.AsignacionesOperacion.SingleAsync(o => o.Id == inbound)).Estado
            .Should().Be(EstadoAsignacion.Vigente, "Inbound tiene su propio ciclo: el backfill de delegaciones Outbound no la toca");
    }

    private async Task<Guid> SembrarAcotadaConCarteraAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        var ahora = DateTime.UtcNow;
        var acotada = AsignacionOperacion.Externa(_propietario, _operador, ServicioCae.Outbound,
            AmbitoAsignacion.DeRelacionCliente(_clienteEmpresarial), ahora.AddDays(-1), null, ahora);
        contexto.AsignacionesOperacion.Add(acotada);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Externa(
            acotada, _gestorOperador, Roles.GestorCae, AmbitoAsignacion.Universal, ahora, null, ahora));
        await contexto.SaveChangesAsync();
        return acotada.Id;
    }

    private async Task<List<AsignacionOperacion>> ExternasOutboundAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        return await contexto.AsignacionesOperacion
            .Where(o => !o.EsRaiz && o.PropietarioTenantId == _propietario && o.OperadorTenantId == _operador
                        && o.Servicio == ServicioCae.Outbound)
            .ToListAsync();
    }

    private async Task EjecutarBackfillAsync()
    {
        await using var contexto = CrearContexto(_propietario);
        await AsignacionesOperativasBackfillSeeder.SeedAsync(contexto, NullLogger.Instance);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}
