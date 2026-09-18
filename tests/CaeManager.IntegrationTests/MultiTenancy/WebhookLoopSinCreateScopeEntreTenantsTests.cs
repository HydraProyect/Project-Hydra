using System.Data.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// <para>
/// <b>Cómo se observa (2ª corrección tras dos rechazos de Codex, 2026-09-18):
/// ni un comando ADO envuelto en <c>OpenConnectionAsync</c> manual ni
/// <c>Database.SqlQueryRaw</c> después de <c>SaveChangesAsync</c> demuestran
/// nada — cualquier operación POSTERIOR, si necesita abrir conexión, dispara
/// su PROPIA apertura física con el tenant vigente EN ESE MOMENTO (que sigue
/// siendo el correcto porque el <c>using</c> de <c>AmbitoTenantExplicito</c>
/// todavía no se cerró), así que "leo B después de escribir B" no distingue
/// "la escritura usó B" de "la escritura usó un GUC viejo y mi sonda lo
/// arregló por casualidad, un instante después, sin que nadie lo note".</b>
/// La única forma de observar la propiedad de verdad es instrumentar el
/// PROPIO mecanismo bajo prueba, no leerlo por fuera:
/// <see cref="RegistradorAperturasConexion"/> es un segundo
/// <see cref="IDbConnectionInterceptor"/> —registrado JUNTO al
/// <see cref="TenantRlsConnectionInterceptor"/> real, nunca en su lugar—
/// que anota qué <c>TenantId</c> estaba vigente en <see cref="ITenantActual"/>
/// cada vez que EF Core dispara <c>ConnectionOpened</c>. Como los dos
/// interceptores reciben el MISMO evento en la MISMA apertura física, lo que
/// el registrador anota es EXACTAMENTE lo que
/// <c>TenantRlsConnectionInterceptor</c> habría fijado en <c>app.tenant_id</c>
/// para esa apertura — sin ninguna operación añadida después que pueda
/// contaminar la propia medición.
/// </para>
///
/// <see cref="Sin_transaccion_explicita_cada_escritura_reabre_la_conexion_y_refresca_el_guc"/>
/// reproduce el bucle real: sin transacción explícita, EF Core abre y cierra
/// la conexión en cada <c>SaveChangesAsync</c> (recuento de referencias,
/// ninguna otra operación la mantiene abierta entre vueltas), así que la
/// apertura física que ocurre DURANTE la escritura de B queda registrada con
/// B, no con A.
///
/// <see cref="Control_positivo_con_conexion_mantenida_abierta_no_se_registra_ninguna_apertura_para_b"/>
/// es el control de sensibilidad que exige el protocolo (§ 3): si un futuro
/// cambio envolviera el bucle en una transacción explícita o en
/// <c>Database.OpenConnectionAsync()</c> compartido entre vueltas —el único
/// cambio que rompería el mecanismo de arriba—, la escritura de B no
/// dispararía ninguna apertura física nueva y el registrador se quedaría sin
/// anotar nada para B — este mismo instrumento lo detecta en rojo. Sin este
/// control, un verde en el primer test no distinguiría "el mecanismo es
/// seguro" de "el test no podía ver el defecto aunque existiera".
///
/// <para>
/// <b>Verificado también con la transacción REAL, no solo con la conexión
/// mantenida abierta a mano</b> (pregunta de la sesión coordinadora,
/// 2026-09-18: la inmunidad de hoy depende de que nadie envuelva el foreach
/// de <c>WebhookWhatsAppEndpoints.cs</c> en una transacción explícita —
/// ¿es eso un accidente de implementación que el test no vigila, o el
/// invariante que el test protege?). Mutación aplicada y revertida:
/// <c>await dbContext.Database.BeginTransactionAsync()</c> dentro del
/// primer <c>using</c>, viva hasta el final del método (no un
/// <c>using</c> por vuelta) — reproduce exactamente lo que produciría
/// envolver el <c>foreach</c> real en una transacción. Resultado: el primer
/// test se pone en rojo por el motivo previsto (<c>aperturasDuranteB</c>
/// vacío — cero aperturas físicas nuevas durante la escritura de B, la
/// misma causa raíz que mide <see cref="Control_positivo_con_conexion_mantenida_abierta_no_se_registra_ninguna_apertura_para_b"/>).
/// El test SÍ protege el invariante hacia delante, no solo describe el
/// estado de hoy.
/// </para>
///
/// <para>
/// <b>Lo que este fichero NO demuestra</b> (revisión de Codex, 2026-09-18):
/// <see cref="BaseDatosPostgresDePruebas"/> conecta con el rol propietario
/// de la cadena de test (<c>postgres</c>), y RLS no restringe al propietario
/// ni al superusuario (mismo principio que documenta
/// <c>TenantRlsConnectionInterceptor</c>: "mientras la conexión siga usando
/// el rol propietario, esta variable se fija igual pero Postgres no la usa
/// para nada"). Este test prueba que <c>app.tenant_id</c> se ASIGNARÍA
/// correctamente en cada apertura física de conexión — el requisito previo
/// para que RLS pueda hacer algo con ese valor una vez el rol restringido
/// esté activo —, no que RLS lo HAGA CUMPLIR; eso es una propiedad distinta,
/// ya cubierta en <c>AislamientoRlsPostgresTests</c>, que este fichero no
/// toca ni repite.
/// </para>
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
        await using var dbContext = CrearContexto(tenantActualDeSiembra, new RegistradorAperturasConexion(tenantActualDeSiembra));
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

    private CaeManagerDbContext CrearContexto(ITenantActual tenantActual, RegistradorAperturasConexion registrador)
    {
        var interceptorRls = new TenantRlsConnectionInterceptor(
            tenantActual,
            new ClienteActivoSeleccionadoAusente(),
            new CurrentUserServiceFalso());

        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // El registrador va JUNTO al interceptor real de RLS, nunca en su
            // lugar: los dos reciben el mismo evento ConnectionOpened en la
            // misma apertura física, así que lo que el registrador anota es
            // lo que TenantRlsConnectionInterceptor habría fijado.
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual), interceptorRls, registrador)
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
        var registrador = new RegistradorAperturasConexion(tenantActual);
        await using var dbContext = CrearContexto(tenantActual, registrador);

        Guid eventoAId, eventoBId;

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            var eventoA = new EventoWebhook(_conexionA, "{\"tenant\":\"A\"}");
            dbContext.EventosWebhook.Add(eventoA);
            await dbContext.SaveChangesAsync();
            eventoAId = eventoA.Id;
        }

        registrador.Aperturas.Should().NotBeEmpty(
            "guarda del instrumento: si esto sigue vacío, ConnectionOpened nunca disparó y el resto del test " +
            "compararía dos vacíos sin medir nada");
        registrador.Aperturas.Should().OnlyContain(tenantId => tenantId == _tenantA,
            "cada apertura física registrada durante la vuelta de A tiene que llevar el tenant de A");

        var aperturasAntesDeB = registrador.Aperturas.Count;

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var eventoB = new EventoWebhook(_conexionB, "{\"tenant\":\"B\"}");
            dbContext.EventosWebhook.Add(eventoB);
            await dbContext.SaveChangesAsync();
            eventoBId = eventoB.Id;
        }

        // La comprobación que de verdad importa: la escritura de B, sobre la
        // MISMA instancia de DbContext y sin CreateScope entre medias,
        // disparó al menos una apertura física NUEVA, y TODAS las aperturas
        // nuevas (las que no estaban ya contadas antes de este bloque)
        // llevan el tenant de B — no el de A, que sería el síntoma exacto de
        // REC-195 trasladado a la capa de conexión. Esto es lo que
        // TenantRlsConnectionInterceptor habría visto en el mismo instante,
        // no una lectura añadida después que pudiera arreglar el dato por su
        // cuenta.
        var aperturasDuranteB = registrador.Aperturas.Skip(aperturasAntesDeB).ToList();
        aperturasDuranteB.Should().NotBeEmpty(
            "la escritura de B tiene que haber abierto físicamente la conexión al menos una vez — sin transacción " +
            "explícita compartida, SaveChangesAsync de A ya cerró la suya y esta es una apertura nueva");
        aperturasDuranteB.Should().OnlyContain(tenantId => tenantId == _tenantB,
            "toda apertura física ocurrida durante la escritura de B tiene que llevar el tenant de B");

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
    /// UNA vez (para A) y la escritura de B no abre nada nuevo — el defecto
    /// que demuestra que el test de arriba SÍ puede ver el problema si
    /// existiera, y no solo que no lo vio esta vez.
    /// </summary>
    [Fact]
    public async Task Control_positivo_con_conexion_mantenida_abierta_no_se_registra_ninguna_apertura_para_b()
    {
        var tenantActual = new TenantActualPorAmbitoExplicito();
        var registrador = new RegistradorAperturasConexion(tenantActual);
        await using var dbContext = CrearContexto(tenantActual, registrador);

        using (AmbitoTenantExplicito.Establecer(_tenantA))
        {
            // Mantiene la conexión abierta a propósito, DESDE dentro del
            // ámbito de A — simula una transacción explícita compartida
            // entre vueltas, que es la única forma real de que esto ocurra
            // en producción. ConnectionOpened dispara aquí, con el tenant de
            // A ya registrado.
            await dbContext.Database.OpenConnectionAsync();

            var eventoA = new EventoWebhook(_conexionA, "{\"tenant\":\"A\"}");
            dbContext.EventosWebhook.Add(eventoA);
            await dbContext.SaveChangesAsync();
        }

        registrador.Aperturas.Should().NotBeEmpty("guarda del instrumento, igual que en el test de arriba");
        var aperturasAntesDeB = registrador.Aperturas.Count;

        using (AmbitoTenantExplicito.Establecer(_tenantB))
        {
            var eventoB = new EventoWebhook(_conexionB, "{\"tenant\":\"B\"}");
            dbContext.EventosWebhook.Add(eventoB);
            // La conexión sigue abierta desde la vuelta de A (recuento de
            // referencias > 0): este SaveChangesAsync NO dispara una nueva
            // apertura física, así que no se registra nada nuevo para B.
            await dbContext.SaveChangesAsync();
        }

        await dbContext.Database.CloseConnectionAsync();

        registrador.Aperturas.Skip(aperturasAntesDeB).Should().BeEmpty(
            "con la conexión mantenida abierta a mano, la escritura de B no disparó ninguna apertura física " +
            "nueva — esto demuestra que el instrumento SÍ detecta el patrón peligroso cuando existe, no solo " +
            "que no lo encontró en el caso real de hoy");
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

    /// <summary>
    /// Segundo <see cref="IDbConnectionInterceptor"/>, registrado JUNTO al
    /// <see cref="TenantRlsConnectionInterceptor"/> real (ver doc-comment de
    /// la clase): anota qué <see cref="ITenantActual.TenantId"/> estaba
    /// vigente en cada apertura física de conexión — exactamente lo que el
    /// interceptor real habría fijado en <c>app.tenant_id</c> para esa
    /// apertura, sin depender de una lectura posterior que pueda contaminar
    /// la propia medición.
    /// </summary>
    private sealed class RegistradorAperturasConexion(ITenantActual tenantActual) : DbConnectionInterceptor
    {
        public List<Guid?> Aperturas { get; } = [];

        public override async Task ConnectionOpenedAsync(
            DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Aperturas.Add(tenantActual.TenantId);
            await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
        }

        public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
        {
            Aperturas.Add(tenantActual.TenantId);
            base.ConnectionOpened(connection, eventData);
        }
    }
}
