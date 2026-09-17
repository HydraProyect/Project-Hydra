using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.MultiTenancy;

/// <summary>
/// REC-212 — el único de los 42 usos de <c>AmbitoTenantExplicito.Establecer(</c>
/// fuera de Application (auditados en la ficha) que reproduce la FORMA exacta
/// del defecto de REC-195 (varios tenants, misma instancia de scope/DbContext,
/// SIN <c>CreateScope()</c> entre vueltas): el bucle de
/// <see cref="CaeManager.Web.Api.Integraciones.WebhookWhatsAppEndpoints"/>
/// sobre los fragmentos de un mismo payload de Meta — cada fragmento puede
/// resolver a un tenant distinto, y <c>eventoRepositorio</c>/<c>unitOfWork</c>
/// (el mismo <see cref="CaeManagerDbContext"/>) se inyectan una sola vez por
/// petición, no por vuelta.
///
/// La diferencia con REC-195 es la CAPA: allí lo envenenado era la caché de
/// <c>IAlcanceDatosService</c> (app), aquí lo que se comprueba es la variable
/// de sesión de PostgreSQL <c>app.tenant_id</c> que fija
/// <see cref="TenantRlsConnectionInterceptor"/> en cada apertura FÍSICA de
/// conexión — esta clase nunca resuelve <c>IAlcanceDatosService</c> (medido
/// por grep, cero apariciones ejecutables en el fichero), así que el
/// mecanismo de REC-195 no aplica; el candidato real es este.
///
/// <see cref="Sin_transaccion_explicita_cada_escritura_reabre_la_conexion_y_refresca_el_guc"/>
/// reproduce el bucle real: sin transacción explícita, EF Core abre y cierra
/// la conexión en cada <c>SaveChangesAsync</c> (recuento de referencias,
/// ninguna otra operación la mantiene abierta entre vueltas), así que
/// <c>ConnectionOpened</c> vuelve a disparar y <c>app.tenant_id</c> se
/// refresca al tenant correcto — CERO filas de un tenant contaminadas con el
/// GUC del anterior.
///
/// <see cref="Control_positivo_con_conexion_mantenida_abierta_el_guc_se_queda_pegado_al_primer_tenant"/>
/// es el control de sensibilidad que exige el protocolo (§ 3): si un futuro
/// cambio envolviera el bucle en una transacción explícita o en
/// <c>Database.OpenConnectionAsync()</c> compartido entre vueltas —el único
/// cambio que rompería el mecanismo de arriba—, este mismo instrumento lo
/// detecta en rojo. Sin este control, un verde en el primer test no
/// distinguiría "el mecanismo es seguro" de "el test no podía ver el
/// defecto aunque existiera".
/// </summary>
public class WebhookLoopSinCreateScopeEntreTenantsTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private Guid _tenantA;
    private Guid _tenantB;
    private Guid _conexionA;
    private Guid _conexionB;

    public async Task InitializeAsync()
    {
        var tenantActualDeSiembra = new TenantActualPorAmbitoExplicito();
        await using var dbContext = CrearContexto(tenantActualDeSiembra);
        await dbContext.Database.MigrateAsync();

        var tenantA = new Domain.Tenants.Tenant("Tenant A (webhook WhatsApp)");
        var tenantB = new Domain.Tenants.Tenant("Tenant B (webhook WhatsApp)");
        dbContext.Tenants.AddRange(tenantA, tenantB);
        await dbContext.SaveChangesAsync();
        _tenantA = tenantA.Id;
        _tenantB = tenantB.Id;

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var conexionA = new ConexionIntegracion("+34600000001", "Línea WhatsApp A", proveedor: ProveedorIntegracion.WhatsApp);
            dbContext.ConexionesIntegracion.Add(conexionA);
            await dbContext.SaveChangesAsync();
            _conexionA = conexionA.Id;
        }

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var conexionB = new ConexionIntegracion("+34600000002", "Línea WhatsApp B", proveedor: ProveedorIntegracion.WhatsApp);
            dbContext.ConexionesIntegracion.Add(conexionB);
            await dbContext.SaveChangesAsync();
            _conexionB = conexionB.Id;
        }
    }

    public async Task DisposeAsync() =>
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private CaeManagerDbContext CrearContexto(ITenantActual tenantActual)
    {
        var interceptorRls = new TenantRlsConnectionInterceptor(
            tenantActual,
            new ClienteActivoSeleccionadoAusente(),
            new CurrentUserServiceFalso());

        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), interceptorRls)
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// Reproduce exactamente el foreach de <c>WebhookWhatsAppEndpoints</c>:
    /// mismo <see cref="CaeManagerDbContext"/>, dos vueltas con
    /// <c>AmbitoTenantExplicito.Establecer</c> de tenants distintos, cada una
    /// seguida de <c>Add</c> + <c>SaveChangesAsync</c> — sin
    /// <c>CreateScope()</c> ni transacción explícita entre ellas.
    /// </summary>
    [Fact]
    public async Task Sin_transaccion_explicita_cada_escritura_reabre_la_conexion_y_refresca_el_guc()
    {
        var tenantActual = new TenantActualPorAmbitoExplicito();
        await using var dbContext = CrearContexto(tenantActual);

        Guid eventoAId, eventoBId;

        // La lectura del GUC ocurre DENTRO de cada using, igual que el propio
        // SaveChangesAsync — es la MISMA vuelta del bucle real, no una
        // comprobación después de que AmbitoTenantExplicito ya se restauró.
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var eventoA = new EventoWebhook(_conexionA, "{\"tenant\":\"A\"}");
            dbContext.EventosWebhook.Add(eventoA);
            await dbContext.SaveChangesAsync();
            eventoAId = eventoA.Id;
        }

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var eventoB = new EventoWebhook(_conexionB, "{\"tenant\":\"B\"}");
            dbContext.EventosWebhook.Add(eventoB);
            await dbContext.SaveChangesAsync();
            eventoBId = eventoB.Id;

            // La comprobación que de verdad importa: tras la vuelta de B,
            // sobre la MISMA instancia de DbContext, sin CreateScope entre
            // medias, el GUC vigente es el de B — no el de A, que sería el
            // síntoma exacto de REC-195 trasladado a la capa de conexión.
            // SaveChangesAsync ya cerró su conexión (sin transacción
            // explícita que la mantenga abierta), así que este comando
            // dispara su propio ConnectionOpened y confirma qué GUC deja esa
            // reapertura.
            await using var comandoB = dbContext.Database.GetDbConnection().CreateCommand();
            comandoB.CommandText = "SELECT current_setting('app.tenant_id', true);";
            await dbContext.Database.OpenConnectionAsync();
            var gucTrasEscribirB = (string?)await comandoB.ExecuteScalarAsync();
            await dbContext.Database.CloseConnectionAsync();

            gucTrasEscribirB.Should().Be(_tenantB.ToString(),
                "cada SaveChangesAsync abre y cierra su propia conexión (sin transacción explícita que las una), " +
                "así que ConnectionOpened vuelve a disparar y a fijar el tenant vigente en ESE momento");
        }

        // Y a nivel de fila, cada EventoWebhook quedó sellado con SU tenant,
        // no con el del otro — TenantSelladoInterceptor lee ITenantActual en
        // vivo (sin caché), igual que TenantActual.cs en producción.
        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var filaA = await dbContext.EventosWebhook.IgnoreQueryFilters()
                .SingleAsync(e => e.Id == eventoAId);
            filaA.TenantId.Should().Be(_tenantA);
        }

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var filaB = await dbContext.EventosWebhook.IgnoreQueryFilters()
                .SingleAsync(e => e.Id == eventoBId);
            filaB.TenantId.Should().Be(_tenantB);
        }
    }

    /// <summary>
    /// Control de sensibilidad (§ 3 del protocolo): el ÚNICO cambio que
    /// rompería el mecanismo de arriba es que algo mantenga la conexión
    /// físicamente abierta ENTRE las dos vueltas del bucle — aquí se fuerza a
    /// mano con <c>Database.OpenConnectionAsync()</c> antes de la primera
    /// vuelta y sin cerrarla hasta el final, exactamente lo que pasaría si un
    /// futuro cambio envolviera el foreach en una transacción explícita
    /// compartida. Bajo esa condición, <c>ConnectionOpened</c> solo dispara
    /// UNA vez (para A) y el GUC se queda pegado a A también para la
    /// escritura de B — el defecto que demuestra que el test de arriba SÍ
    /// puede ver el problema si existiera, y no solo que no lo vio esta vez.
    /// </summary>
    [Fact]
    public async Task Control_positivo_con_conexion_mantenida_abierta_el_guc_se_queda_pegado_al_primer_tenant()
    {
        var tenantActual = new TenantActualPorAmbitoExplicito();
        await using var dbContext = CrearContexto(tenantActual);

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            // Mantiene la conexión abierta a propósito, DESDE dentro del
            // ámbito de A — simula una transacción explícita compartida
            // entre vueltas, que es la única forma real de que esto ocurra
            // en producción. ConnectionOpened dispara aquí, con el GUC de A.
            await dbContext.Database.OpenConnectionAsync();

            var eventoA = new EventoWebhook(_conexionA, "{\"tenant\":\"A\"}");
            dbContext.EventosWebhook.Add(eventoA);
            await dbContext.SaveChangesAsync();
        }

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var eventoB = new EventoWebhook(_conexionB, "{\"tenant\":\"B\"}");
            dbContext.EventosWebhook.Add(eventoB);
            // La conexión sigue abierta desde la vuelta de A (recuento de
            // referencias > 0): este SaveChangesAsync NO dispara una nueva
            // apertura física, así que el GUC nunca se refresca a B.
            await dbContext.SaveChangesAsync();
        }

        await using var comando = dbContext.Database.GetDbConnection().CreateCommand();
        comando.CommandText = "SELECT current_setting('app.tenant_id', true);";
        var gucFinal = (string?)await comando.ExecuteScalarAsync();

        await dbContext.Database.CloseConnectionAsync();

        gucFinal.Should().Be(_tenantA.ToString(),
            "con la conexión mantenida abierta a mano, ConnectionOpened solo disparó una vez (para A) " +
            "y el GUC nunca se refrescó a B — esto demuestra que el instrumento SÍ detecta el patrón " +
            "peligroso cuando existe, no solo que no lo encontró en el caso real de hoy");
    }

    /// <summary>Igual que <c>Web/Services/TenantActual.cs</c>: lee <see cref="AmbitoTenantExplicito"/> en vivo, sin memoizar.</summary>
    private sealed class TenantActualPorAmbitoExplicito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class ClienteActivoSeleccionadoAusente : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }
}
