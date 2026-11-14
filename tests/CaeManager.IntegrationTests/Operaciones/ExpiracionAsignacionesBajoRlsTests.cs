using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Coordinacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Operaciones;

/// <summary>
/// <b>El job de expiración de asignaciones, tal como corre en producción:</b>
/// conectado como <c>cae_app_runtime</c>, con el interceptor de sesión real y sin
/// usuario. Es la condición que el test anterior
/// (<see cref="CorreccionesRevisionF1Tests"/>) no reproducía: aquel conecta como
/// propietario de la base y sin interceptor, así que la política
/// <c>posicion_en_la_asignacion</c> no le ata y el job "funcionaba".
///
/// <para>
/// Bajo el rol restringido, sin <c>AmbitoTenantExplicito</c>, el interceptor fija
/// <c>app.tenant_id</c> y <c>app.tenant_origen_id</c> a cadena vacía y el job ve
/// cero filas: las Asignaciones vencidas siguen Vigentes (y ocupan los índices
/// únicos parciales, 23505 al dar de alta la sustituta) y las Programadas no se
/// activan nunca.
/// </para>
///
/// <para>
/// Nota sobre la migración <c>20260820230549_RlsCatalogosDeAsignacion</c>: su
/// comentario afirma que el job de expiración "opera como propietario". Era falso
/// desde que el tráfico de la aplicación pasó a <c>cae_app_runtime</c>; la
/// migración ya está aplicada y no se reescribe, así que la corrección queda
/// aquí y en el propio job.
/// </para>
///
/// Todos los casos usan una Asignación de Operación <b>externa</b>: el Tenant
/// operador (un Operador CAE externo) no es el Tenant propietario. Es el caso en
/// que <c>app.tenant_id</c> y <c>app.tenant_origen_id</c> divergen, y el que
/// demuestra que recorrer por Tenant propietario basta.
/// </summary>
public class ExpiracionAsignacionesBajoRlsTests
{
    [Fact]
    public async Task Bajo_el_rol_de_runtime_cierra_las_vencidas_y_activa_las_programadas_de_cada_Tenant_propietario()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var ahora = DateTime.UtcNow;
        var hace3h = ahora.AddHours(-3);

        var propietarioA = new Tenant("Propietario A expiración", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioC = new Tenant("Propietario C expiración", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioSuspendido = new Tenant("Propietario suspendido expiración", PerfilVocabularioTenant.ClienteDirecto);
        propietarioSuspendido.Suspender();
        var operadorExterno = new Tenant("Operador CAE externo expiración", PerfilVocabularioTenant.Consultora);

        // Asignación de Operación Vigente con la vigencia ya pasada: propietario A,
        // operada por el Operador CAE externo.
        var operacionVencida = AsignacionOperacion.Externa(
            propietarioA.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: ahora.AddHours(-1), ahora: hace3h);

        // Asignación de Cartera Programada cuya vigencia ya empezó, sobre una
        // operación vigente del propietario C operada por el mismo Operador CAE externo.
        var operacionDeC = AsignacionOperacion.Externa(
            propietarioC.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: null, ahora: hace3h);
        var carteraProgramada = AsignacionCartera.Externa(
            operacionDeC, usuarioId: Guid.NewGuid(), rol: Roles.GestorCae, AmbitoAsignacion.Universal,
            vigenciaDesde: ahora.AddHours(-1), vigenciaHasta: null, ahora: hace3h);

        // La vigencia de una asignación no deja de correr porque su Tenant
        // propietario esté suspendido: el job anterior (como propietario de la
        // base) no distinguía estados, y este tampoco.
        var operacionVencidaDeSuspendido = AsignacionOperacion.Externa(
            propietarioSuspendido.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: ahora.AddHours(-1), ahora: hace3h);

        operacionVencida.Estado.Should().Be(EstadoAsignacion.Vigente, "precondición de la siembra");
        carteraProgramada.Estado.Should().Be(EstadoAsignacion.Programada, "precondición de la siembra");

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario))
        {
            propietarioBd.Tenants.AddRange(propietarioA, propietarioC, propietarioSuspendido, operadorExterno);
            propietarioBd.AsignacionesOperacion.AddRange(operacionVencida, operacionDeC, operacionVencidaDeSuspendido);
            propietarioBd.AsignacionesCartera.Add(carteraProgramada);
            await propietarioBd.SaveChangesAsync();
        }

        var job = new ExpiracionAsignacionesHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<ExpiracionAsignacionesHostedService>.Instance);

        await job.ProcesarAsync(CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario);

        var vencida = await verificacion.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == operacionVencida.Id);
        vencida.Estado.Should().Be(EstadoAsignacion.Cerrada,
            "la vigencia terminó hace una hora; bajo cae_app_runtime el job tiene que verla y cerrarla");
        vencida.MotivoCierre.Should().Be(MotivoCierreAsignacion.Expirada);

        var programada = await verificacion.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.Id == carteraProgramada.Id);
        programada.Estado.Should().Be(EstadoAsignacion.Vigente,
            "su vigencia empezó hace una hora y su operación está vigente");

        var deSuspendido = await verificacion.AsignacionesOperacion.AsNoTracking()
            .SingleAsync(o => o.Id == operacionVencidaDeSuspendido.Id);
        deSuspendido.Estado.Should().Be(EstadoAsignacion.Cerrada,
            "la suspensión del Tenant propietario no congela la vigencia de sus asignaciones");

        var operacionVigente = await verificacion.AsignacionesOperacion.AsNoTracking().SingleAsync(o => o.Id == operacionDeC.Id);
        operacionVigente.Estado.Should().Be(EstadoAsignacion.Vigente, "sin fecha de fin, no se toca");
    }

    /// <summary>
    /// La fecha de fin de un apoyo (D-5) la cierra este job. Cerrar la cartera no basta: la fila
    /// heredada de Operador Delegado autoriza el Tenant por sí sola
    /// (<c>TenantsBeneficiariosAutorizados.EstaAutorizadoAsync</c>), así que una cartera externa
    /// que caduca tiene que llevársela, igual que la retirada. Bajo <c>cae_app_runtime</c> y con
    /// el recorrido de producción (ámbito = Tenant propietario, sin usuario).
    /// </summary>
    [Fact]
    public async Task Al_caducar_una_cartera_externa_se_borra_la_fila_heredada_de_quien_ya_no_tiene_otra_cartera_vigente()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var ahora = DateTime.UtcNow;
        var hace3h = ahora.AddHours(-3);
        var propietario = new Tenant("Propietario con apoyo que caduca", PerfilVocabularioTenant.ClienteDirecto);
        var operadorExterno = new Tenant("Operador CAE externo del apoyo que caduca", PerfilVocabularioTenant.Consultora);
        var operacion = AsignacionOperacion.Externa(
            propietario.Id, operadorExterno.Id, ServicioCae.Outbound, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: null, ahora: hace3h);
        var vinculo = new DelegacionTenant(operadorExterno.Id, propietario.Id);

        var apoyoQueCaduca = Guid.NewGuid();
        var gestorSinFecha = Guid.NewGuid();
        var carteraQueCaduca = AsignacionCartera.Externa(
            operacion, apoyoQueCaduca, Roles.GestorCae, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: ahora.AddHours(-1), ahora: hace3h);
        var carteraSinFecha = AsignacionCartera.Externa(
            operacion, gestorSinFecha, Roles.GestorCae, AmbitoAsignacion.Universal,
            vigenciaDesde: hace3h, vigenciaHasta: null, ahora: hace3h);

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario))
        {
            propietarioBd.Tenants.AddRange(propietario, operadorExterno);
            propietarioBd.AsignacionesOperacion.Add(operacion);
            propietarioBd.DelegacionesTenant.Add(vinculo);
            propietarioBd.AsignacionesCartera.AddRange(carteraQueCaduca, carteraSinFecha);
            propietarioBd.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                new AsignacionOperadorDelegado(vinculo.Id, apoyoQueCaduca, Roles.GestorCae),
                new AsignacionOperadorDelegado(vinculo.Id, gestorSinFecha, Roles.GestorCae));
            await propietarioBd.SaveChangesAsync();
        }

        var job = new ExpiracionAsignacionesHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<ExpiracionAsignacionesHostedService>.Instance);

        await job.ProcesarAsync(CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario);
        var caducada = await verificacion.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.Id == carteraQueCaduca.Id);
        caducada.Estado.Should().Be(EstadoAsignacion.Cerrada);
        caducada.MotivoCierre.Should().Be(MotivoCierreAsignacion.Expirada);

        var filas = await verificacion.AsignacionesOperadorDelegadoConRevocadas.IgnoreQueryFilters().AsNoTracking()
            .Where(f => f.DelegacionTenantId == vinculo.Id).Select(f => f.UsuarioId).ToListAsync();
        filas.Should().Equal([gestorSinFecha],
            "la fila heredada de quien caducó autoriza el Tenant por sí sola y se va con la cartera; la de quien sigue vigente no se toca");
        (await verificacion.AsignacionesCartera.AsNoTracking().SingleAsync(c => c.Id == carteraSinFecha.Id))
            .Estado.Should().Be(EstadoAsignacion.Vigente, "sin fecha de fin, no caduca");
    }

    /// <summary>
    /// Las dos mitades de «solo si no le queda otra cartera vigente de Outbound del mismo Operador
    /// CAE»: a quien le queda otra cartera Outbound vigente del mismo Operador CAE en ese Tenant
    /// propietario se le conserva la fila heredada; a quien esa otra se le cierra en el mismo pase
    /// (cae con su operación), no. Desde B-02a una cartera de otro servicio ni la sostiene ni la
    /// mueve: a quien solo le queda una Inbound se le quita, y la Inbound que caduca no se lleva la
    /// fila de Outbound de quien no tiene más cartera. Y una cartera de otro Operador CAE sobre el
    /// mismo Tenant propietario tampoco la sostiene: ese estado es inválido (el usuario pertenece
    /// al Tenant del Operador CAE de su cartera) y ante él se falla cerrado.
    ///
    /// <para>
    /// La «otra» Outbound cuelga de una segunda operación externa del mismo Operador CAE, acotada
    /// por un Cliente empresarial: IX_AsignacionesOperacion_DelegacionTotalVigente admite una sola
    /// operación externa del Tenant entero por propietario y servicio, y la cartera es siempre el
    /// Tenant entero (D-7), así que lo acotado vive en la operación. Hasta la tercera revisión de
    /// Codex la «otra» colgaba de la operación raíz del Tenant propietario, cuyo Operador CAE es
    /// el interno (el propio Tenant propietario) y no el externo del usuario: un estado inválido
    /// que la prueba exigía conservar.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Al_caducar_una_cartera_externa_la_fila_heredada_solo_la_sostiene_otra_cartera_Outbound_vigente_del_mismo_Operador_CAE()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);

        var ahora = DateTime.UtcNow;
        var hace3h = ahora.AddHours(-3);
        var haceUnaHora = ahora.AddHours(-1);
        var operadorExterno = new Tenant("Operador CAE externo de las dos carteras", PerfilVocabularioTenant.Consultora);
        var otroOperadorExterno = new Tenant("Otro Operador CAE externo del mismo propietario", PerfilVocabularioTenant.Consultora);
        var propietarioConOtra = new Tenant("Propietario donde queda otra cartera", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioSinOtra = new Tenant("Propietario donde caen las dos", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioSoloInbound = new Tenant("Propietario donde solo queda la Inbound", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioInboundCaduca = new Tenant("Propietario donde caduca la Inbound", PerfilVocabularioTenant.ClienteDirecto);
        var propietarioOtroOperador = new Tenant("Propietario donde queda una cartera de otro Operador CAE", PerfilVocabularioTenant.ClienteDirecto);

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario))
        {
            propietarioBd.Tenants.AddRange(
                operadorExterno, otroOperadorExterno, propietarioConOtra, propietarioSinOtra, propietarioSoloInbound,
                propietarioInboundCaduca, propietarioOtroOperador);
            await propietarioBd.SaveChangesAsync();
        }

        // El Cliente empresarial que acota la segunda operación: la FK del ámbito exige una Empresa del Tenant propietario.
        var clienteConOtra = await SembrarClienteAsync(arnes.CadenaPropietario, propietarioConOtra.Id, "B12345674");
        var clienteSinOtra = await SembrarClienteAsync(arnes.CadenaPropietario, propietarioSinOtra.Id, "B10380186");
        var clienteOtroOperador = await SembrarClienteAsync(arnes.CadenaPropietario, propietarioOtroOperador.Id, "B10380194");

        AsignacionOperacion Operacion(Tenant propietario, ServicioCae servicio) =>
            AsignacionOperacion.Externa(
                propietario.Id, operadorExterno.Id, servicio, AmbitoAsignacion.Universal,
                vigenciaDesde: hace3h, vigenciaHasta: null, ahora: hace3h);
        AsignacionOperacion Acotada(Tenant propietario, Tenant operador, Guid clienteId) =>
            AsignacionOperacion.Externa(
                propietario.Id, operador.Id, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteId),
                vigenciaDesde: hace3h, vigenciaHasta: null, ahora: hace3h);
        AsignacionCartera Cartera(AsignacionOperacion operacion, Guid usuario, DateTime? hasta) =>
            AsignacionCartera.Externa(
                operacion, usuario, Roles.GestorCae, AmbitoAsignacion.Universal,
                vigenciaDesde: hace3h, vigenciaHasta: hasta, ahora: hace3h);

        var conOtra = Guid.NewGuid();
        var outboundConOtra = Operacion(propietarioConOtra, ServicioCae.Outbound);
        var acotadaConOtra = Acotada(propietarioConOtra, operadorExterno, clienteConOtra);
        var vinculoConOtra = new DelegacionTenant(operadorExterno.Id, propietarioConOtra.Id);
        var caducaConOtra = Cartera(outboundConOtra, conOtra, haceUnaHora);
        var laQueQueda = Cartera(acotadaConOtra, conOtra, null);

        var sinOtra = Guid.NewGuid();
        var outboundSinOtra = Operacion(propietarioSinOtra, ServicioCae.Outbound);
        var acotadaQueExpira = Acotada(propietarioSinOtra, operadorExterno, clienteSinOtra);
        var vinculoSinOtra = new DelegacionTenant(operadorExterno.Id, propietarioSinOtra.Id);
        var caducaSinOtra = Cartera(outboundSinOtra, sinOtra, haceUnaHora);
        var laQueCaeConSuOperacion = Cartera(acotadaQueExpira, sinOtra, null);

        // Estado inválido a propósito: la cartera que queda es de otro Operador CAE para un usuario del primero.
        var conOtraDeOtroOperador = Guid.NewGuid();
        var outboundOtroOperador = Operacion(propietarioOtroOperador, ServicioCae.Outbound);
        var deOtroOperador = Acotada(propietarioOtroOperador, otroOperadorExterno, clienteOtroOperador);
        var vinculoOtroOperador = new DelegacionTenant(operadorExterno.Id, propietarioOtroOperador.Id);
        var caducaOtroOperador = Cartera(outboundOtroOperador, conOtraDeOtroOperador, haceUnaHora);
        var laDeOtroOperador = Cartera(deOtroOperador, conOtraDeOtroOperador, null);

        var soloInbound = Guid.NewGuid();
        var outboundSoloInbound = Operacion(propietarioSoloInbound, ServicioCae.Outbound);
        var inboundSoloInbound = Operacion(propietarioSoloInbound, ServicioCae.Inbound);
        var vinculoSoloInbound = new DelegacionTenant(operadorExterno.Id, propietarioSoloInbound.Id);
        var caducaSoloInbound = Cartera(outboundSoloInbound, soloInbound, haceUnaHora);
        var laInboundQueQueda = Cartera(inboundSoloInbound, soloInbound, null);

        var sinCarteraOutbound = Guid.NewGuid();
        var inboundQueCaduca = Operacion(propietarioInboundCaduca, ServicioCae.Inbound);
        var vinculoInboundCaduca = new DelegacionTenant(operadorExterno.Id, propietarioInboundCaduca.Id);
        var caducaLaInbound = Cartera(inboundQueCaduca, sinCarteraOutbound, haceUnaHora);

        await using (var propietarioBd = CrearContextoPropietario(arnes.CadenaPropietario))
        {
            propietarioBd.AsignacionesOperacion.AddRange(
                outboundConOtra, acotadaConOtra, outboundSinOtra, acotadaQueExpira, outboundSoloInbound, inboundSoloInbound,
                inboundQueCaduca, outboundOtroOperador, deOtroOperador);
            // Vence en este pase para que su cartera caiga con ella; se emite sin fecha de fin para que la cartera nazca bajo una vigente.
            propietarioBd.Entry(acotadaQueExpira).Property(o => o.VigenciaHasta).CurrentValue = haceUnaHora;
            propietarioBd.DelegacionesTenant.AddRange(
                vinculoConOtra, vinculoSinOtra, vinculoSoloInbound, vinculoInboundCaduca, vinculoOtroOperador);
            propietarioBd.AsignacionesCartera.AddRange(
                caducaConOtra, laQueQueda, caducaSinOtra, laQueCaeConSuOperacion, caducaSoloInbound, laInboundQueQueda, caducaLaInbound,
                caducaOtroOperador, laDeOtroOperador);
            propietarioBd.AsignacionesOperadorDelegadoConRevocadas.AddRange(
                new AsignacionOperadorDelegado(vinculoConOtra.Id, conOtra, Roles.GestorCae),
                new AsignacionOperadorDelegado(vinculoSinOtra.Id, sinOtra, Roles.GestorCae),
                new AsignacionOperadorDelegado(vinculoSoloInbound.Id, soloInbound, Roles.GestorCae),
                new AsignacionOperadorDelegado(vinculoInboundCaduca.Id, sinCarteraOutbound, Roles.CoordinadorCae),
                new AsignacionOperadorDelegado(vinculoOtroOperador.Id, conOtraDeOtroOperador, Roles.GestorCae));
            await propietarioBd.SaveChangesAsync();
        }

        var job = new ExpiracionAsignacionesHostedService(
            arnes.Servicios.GetRequiredService<IServiceScopeFactory>(),
            new LiderSiempre(),
            NullLogger<ExpiracionAsignacionesHostedService>.Instance);

        await job.ProcesarAsync(CancellationToken.None);

        await using var verificacion = CrearContextoPropietario(arnes.CadenaPropietario);
        var estados = await verificacion.AsignacionesCartera.AsNoTracking()
            .ToDictionaryAsync(c => c.Id, c => c.Estado);
        estados[caducaConOtra.Id].Should().Be(EstadoAsignacion.Cerrada, "control: la que tenía fecha caducó");
        estados[laQueQueda.Id].Should().Be(EstadoAsignacion.Vigente);
        estados[caducaSinOtra.Id].Should().Be(EstadoAsignacion.Cerrada, "control: la que tenía fecha caducó");
        estados[laQueCaeConSuOperacion.Id].Should().Be(EstadoAsignacion.Cerrada, "control: su operación expiró en este pase");
        estados[caducaSoloInbound.Id].Should().Be(EstadoAsignacion.Cerrada, "control: la que tenía fecha caducó");
        estados[laInboundQueQueda.Id].Should().Be(EstadoAsignacion.Vigente);
        estados[caducaLaInbound.Id].Should().Be(EstadoAsignacion.Cerrada, "control: la Inbound con fecha también caduca");
        estados[caducaOtroOperador.Id].Should().Be(EstadoAsignacion.Cerrada, "control: la que tenía fecha caducó");
        estados[laDeOtroOperador.Id].Should().Be(EstadoAsignacion.Vigente);

        var filas = await verificacion.AsignacionesOperadorDelegadoConRevocadas.IgnoreQueryFilters().AsNoTracking()
            .Select(f => f.UsuarioId).ToListAsync();
        filas.Should().Contain(conOtra,
            "le queda otra cartera Outbound vigente del mismo Operador CAE en ese Tenant propietario, que sigue necesitando la fila");
        filas.Should().NotContain(conOtraDeOtroOperador,
            "la cartera que le queda es de otro Operador CAE: estado inválido, no sostiene la fila de esta delegación y se falla cerrado");
        filas.Should().NotContain(sinOtra,
            "su otra cartera se cierra en el mismo pase: aunque todavía figure vigente en la base, no sostiene la fila");
        filas.Should().NotContain(soloInbound,
            "la fila heredada abre Outbound, y una cartera Inbound vigente no la mantiene viva (B-02a)");
        filas.Should().Contain(sinCarteraOutbound,
            "la cartera Inbound que caduca no concedió el acceso de Outbound y no se lo lleva (B-02a)");
    }

    /// <summary>
    /// Una Empresa cliente del Tenant propietario, sembrada como propietario de la base con el sellado de
    /// Tenant, que es quien le pone el <c>TenantId</c>. Devuelve su Id.
    /// </summary>
    private static async Task<Guid> SembrarClienteAsync(string cadenaPropietario, Guid tenantPropietarioId, string cif)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantPropietarioId };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        await using var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
        var cliente = Empresa.CrearComoCliente($"Cliente acotado {cif}", cif, esCritico: false, notas: null, ejecutivoUsuarioId: null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();
        return cliente.Id;
    }

    /// <summary>
    /// Contexto como propietario de la base, sin interceptores: siembra y lectura
    /// de verificación con visión global, fuera de la política.
    /// </summary>
    private static CaeManagerDbContext CrearContextoPropietario(string cadenaPropietario)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = null };
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class LiderSiempre : IEleccionLiderService
    {
        public async Task<bool> IntentarEjecutarComoLiderAsync(
            string clave, Func<CancellationToken, Task> trabajo, CancellationToken cancellationToken)
        {
            await trabajo(cancellationToken);
            return true;
        }
    }
}
