using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// <b>Un <c>SaveChanges</c> que mezcla Tenants falla entero, y no deja la
/// sesión RLS en el Tenant equivocado.</b>
///
/// <para>
/// <c>TenantSelladoInterceptor</c> fija <c>app.tenant_id</c> al <b>Tenant
/// propietario de la cuenta</b> auditada antes de insertar su fila de
/// <c>RegistrosAuditoria</c> (#704, #726). Si ese Tenant difiere del de sesión
/// y el mismo lote lleva además una escritura de dominio del Tenant de sesión,
/// esa escritura se ejecuta con la variable de sesión del OTRO Tenant. El
/// comentario del interceptor declaraba que eso fallaba «ruidoso»
/// (<c>42501</c> y reversión) — una INFERENCE sin test. Este archivo la
/// convierte en propiedad medida.
/// </para>
///
/// <para>
/// <b>Qué se demuestra</b> (Tenant propietario de la cuenta = X, Tenant de
/// sesión = Y). <b>Grupo A, el cableado real</b>: con una escritura de dominio
/// de Y de cada clase —INSERT, UPDATE y DELETE—, por la vía síncrona, por
/// <c>UserManager</c> y con dos cuentas de Tenants distintos, el
/// <c>SaveChanges</c> lanza <c>42501</c>; no queda escrito nada ni en X ni en
/// Y; y el <c>app.tenant_id</c> de la conexión vuelve a Y. En el cableado real
/// el primer rechazo lo produce la fila de auditoría de Y (todo tipo de
/// dominio se audita, salvo <c>RegistroAccesoDocumentoSensible</c>, y esa fila
/// se ejecuta con <c>app.tenant_id = X</c>).
/// </para>
///
/// <para>
/// <b>Grupo B, la capa de dominio aislada</b>: esa auditoría de Y tapa la
/// pregunta que importa para UPDATE y DELETE, porque bajo
/// <c>app.tenant_id = X</c> la política <c>USING</c> hace invisible la fila de
/// Y y Postgres afecta a 0 filas <i>sin error</i>. Para medir que eso tampoco
/// pasa en silencio se usa un contexto que audita solo la Identidad (una fila
/// sintética de X que provoca el mismo cambio de <c>app.tenant_id</c>) y no el
/// dominio: ahí lo que lo impide es que EF exige 1 fila afectada y lanza
/// <see cref="DbUpdateConcurrencyException"/>, y la transacción revierte
/// también la auditoría de X.
/// </para>
///
/// <para>
/// <b>Capa</b>: <c>cae_app_runtime</c> con RLS real
/// (<see cref="ArnesDeArranqueRuntime"/>). Bajo el propietario de la base nada
/// de esto se ejercitaría: RLS no le aplica y todos los lotes «funcionarían».
/// Los controles positivos (lote solo de Identidad y lote solo de dominio,
/// ambos con éxito) comprueban que el arnés ejercita el camino de
/// propagación: un fallo aquí no puede ser un fallo del arnés.
/// </para>
///
/// <para>
/// <b>Lo que NO prueba</b>: que ningún camino de producción construya el lote.
/// Hasta donde se ha leído no hay ninguno por diseño (el contexto es scoped y
/// compartido por <c>UserManager</c> y el dominio, y ningún handler de
/// Application llama a <c>UserManager</c>), pero un contexto de circuito que
/// conserve entidades de dominio rastreadas tras un fallo previo sí podría
/// arrastrarlas al <c>SaveChanges</c> de un <c>UserManager.UpdateAsync</c>; en
/// ese caso el efecto es este mismo fallo cerrado, no una escritura cruzada.
/// </para>
/// </summary>
public sealed class LoteMixtoIdentidadDominioFallaCerradoTests : IAsyncLifetime
{
    private const int UmbralAmbarInicial = 30;
    private const int UmbralRojoInicial = 7;
    private const int UmbralAmbarRecuperacion = 45;
    private const int UmbralRojoRecuperacion = 9;

    private readonly TenantActualFijo _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    public async Task InitializeAsync() =>
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false, tenantActualPersonalizado: _sesion);

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    // ── Controles positivos: sin ellos, un fallo de abajo podría ser del arnés ──

    [Fact]
    public async Task Control_lote_solo_de_Identidad_de_otro_Tenant_tiene_exito_sella_con_el_propietario_y_restaura_la_sesion()
    {
        var (tenantCuenta, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        cuenta!.PhoneNumber = "600000001";

        await sesion.Contexto.SaveChangesAsync();

        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(),
            "el éxito también tiene que devolver la variable de sesión al Tenant de sesión");
        (await TenantDeLaUltimaAuditoriaAsync(cuentaId)).Should().Be(tenantCuenta,
            "la fila de auditoría es del Tenant propietario de la cuenta, no del de sesión");
        (await FotoAsync(cuentaId, tenantSesion)).TelefonoDeLaCuenta.Should().Be("600000001");
    }

    [Fact]
    public async Task Control_lote_solo_de_dominio_del_Tenant_de_sesion_tiene_exito()
    {
        var (_, tenantSesion, _) = await EscenarioAsync(conParametroDeSesion: true);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);

        await sesion.Contexto.SaveChangesAsync();

        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
        (await FotoAsync(Guid.Empty, tenantSesion)).UmbralAmbar.Should().Be(UmbralAmbarRecuperacion);
    }

    // ── Grupo A. El riesgo, con el cableado real de producción ──────────────────

    [Fact]
    public async Task Cuenta_de_X_mas_INSERT_de_dominio_de_Y_falla_entero()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString(), "control del instrumento");

        sesion.Contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarInicial, UmbralRojoInicial));
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        cuenta!.PhoneNumber = "600000002";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>();
        SqlStateDe(excepcion).Should().Be("42501",
            "el INSERT de Y con app.tenant_id = X lo rechaza WITH CHECK");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    [Fact]
    public async Task Cuenta_de_X_mas_UPDATE_de_dominio_de_Y_falla_entero()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        cuenta!.PhoneNumber = "600000003";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>();
        SqlStateDe(excepcion).Should().Be("42501",
            "la fila de auditoría de Y, con app.tenant_id = X, la rechaza WITH CHECK");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    [Fact]
    public async Task Cuenta_de_X_mas_DELETE_de_dominio_de_Y_falla_entero()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        sesion.Contexto.ParametrosSistema.Remove(parametro);
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        cuenta!.PhoneNumber = "600000004";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>();
        SqlStateDe(excepcion).Should().Be("42501");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    /// <summary>
    /// La versión síncrona del interceptor es un segundo camino de código
    /// (<c>SavingChanges</c>/<c>SaveChangesFailed</c>). Una mutación que la
    /// dejara sin restaurar no la vería ningún test asíncrono.
    /// </summary>
    [Fact]
    public async Task Cuenta_de_X_mas_UPDATE_de_dominio_de_Y_falla_entero_tambien_con_SaveChanges_sincrono()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        cuenta!.PhoneNumber = "600000005";

        var excepcion = CapturarSincrono(() => sesion.Contexto.SaveChanges());

        excepcion.Should().BeOfType<DbUpdateException>();
        SqlStateDe(excepcion).Should().Be("42501");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    /// <summary>
    /// La forma de producción: la entidad de dominio ya está rastreada en el
    /// contexto scoped y es <c>UserManager.UpdateAsync</c> quien llama a
    /// <c>SaveChanges</c>. El contrato es «no tiene éxito y no escribe nada»,
    /// tanto si <c>UserStore</c> convierte el fallo en un
    /// <see cref="IdentityResult"/> como si lo propaga.
    /// </summary>
    [Fact]
    public async Task Cuenta_de_X_mas_UPDATE_de_dominio_de_Y_a_traves_de_UserManager_no_tiene_exito_ni_escribe()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        cuenta!.PhoneNumber = "600000006";

        var exito = false;
        try { exito = (await sesion.Usuarios.UpdateAsync(cuenta)).Succeeded; }
        catch (DbUpdateException) { /* también es un fallo cerrado */ }

        exito.Should().BeFalse("el lote mixto no puede completarse por la vía de UserManager");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    /// <summary>
    /// Sin ninguna escritura de dominio: dos cuentas de Tenants propietarios
    /// distintos en el mismo lote. La variable de sesión solo puede reflejar
    /// una, así que la fila de la otra la rechaza <c>WITH CHECK</c>.
    /// </summary>
    [Fact]
    public async Task Dos_cuentas_de_Tenants_distintos_en_el_mismo_lote_fallan_enteras()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var tenantOtra = await CrearTenantAsync();
        var otraCuentaId = await CrearCuentaAsync(tenantOtra);
        var antes = await FotoAsync(cuentaId, tenantSesion);
        var antesOtra = await FotoAsync(otraCuentaId, tenantSesion);

        await using var sesion = await AbrirSesionAsync(tenantSesion);
        var cuenta = await sesion.Usuarios.FindByIdAsync(cuentaId.ToString());
        var otraCuenta = await sesion.Usuarios.FindByIdAsync(otraCuentaId.ToString());
        cuenta!.PhoneNumber = "600000007";
        otraCuenta!.PhoneNumber = "600000008";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>();
        SqlStateDe(excepcion).Should().Be("42501");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        (await FotoAsync(otraCuentaId, tenantSesion)).Should().Be(antesOtra,
            "tampoco la segunda cuenta ni su auditoría pueden haberse escrito");
    }

    // ── Grupo B. La capa de dominio aislada de la auditoría de dominio ──────────

    [Fact]
    public async Task Control_B_el_contexto_de_auditoria_solo_de_Identidad_completa_un_lote_de_Identidad_y_restaura_la_sesion()
    {
        var (tenantCuenta, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000011";

        await sesion.Contexto.SaveChangesAsync();

        (await LeerTenantDeSesionAsync(sesion.Contexto)).Should().Be(tenantSesion.ToString());
        (await TenantDeLaUltimaAuditoriaAsync(cuentaId)).Should().Be(tenantCuenta,
            "sin este control, un fallo del grupo B podría deberse a que la fila sintética no cambia app.tenant_id");
    }

    [Fact]
    public async Task B_Cuenta_de_X_mas_INSERT_de_dominio_de_Y_lo_rechaza_la_politica_de_la_tabla_de_dominio()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        sesion.Contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarInicial, UmbralRojoInicial));
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000012";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateException>();
        SqlStateDe(excepcion).Should().Be("42501");
        MensajeDe(excepcion).Should().Contain("ParametrosSistema",
            "sin auditoría de dominio, lo que rechaza el lote es la política de la propia tabla de dominio");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    [Fact]
    public async Task B_Cuenta_de_X_mas_UPDATE_de_dominio_de_Y_no_afecta_a_0_filas_en_silencio()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000013";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateConcurrencyException>(
            "con app.tenant_id = X la fila de Y es invisible: 0 filas afectadas, y EF exige 1");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion, restauracionDiferida: true);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    [Fact]
    public async Task B_Cuenta_de_X_mas_DELETE_de_dominio_de_Y_no_afecta_a_0_filas_en_silencio()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);
        var antes = await FotoAsync(cuentaId, tenantSesion);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        sesion.Contexto.ParametrosSistema.Remove(parametro);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000014";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync());

        excepcion.Should().BeOfType<DbUpdateConcurrencyException>();
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion, restauracionDiferida: true);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    /// <summary>
    /// EF no llama a <c>SaveChangesFailed</c> cuando el <c>SaveChanges</c> se
    /// cancela: llama a <c>SaveChangesCanceled</c>. Un lote de Identidad de otro
    /// Tenant que ya movió <c>app.tenant_id</c> a X y se cancela a mitad tiene
    /// que devolver la variable a Y igual que un fallo. La cancelación se
    /// dispara desde un interceptor colocado DESPUÉS del sellado, es decir con
    /// la variable ya movida y antes de ejecutar ningún comando.
    /// </summary>
    [Fact]
    public async Task B_Un_lote_de_Identidad_de_otro_Tenant_cancelado_a_mitad_restaura_la_sesion()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: false);
        var antes = await FotoAsync(cuentaId, tenantSesion);
        using var cancelacion = new CancellationTokenSource();

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(
            tenantSesion, new CancelaTrasElSelladoInterceptor(cancelacion));
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000015";

        var excepcion = await CapturarAsync(() => sesion.Contexto.SaveChangesAsync(cancelacion.Token));

        excepcion.Should().BeAssignableTo<OperationCanceledException>(
            "el control del instrumento: el lote se canceló de verdad, no falló por otro motivo");
        await AfirmarNadaEscritoYSesionRestauradaAsync(sesion, antes, cuentaId, tenantSesion);
        await AfirmarQueLaSesionSigueUsableAsync(sesion, tenantSesion);
    }

    // ── Aserciones compartidas ───────────────────────────────────────────────────

    /// <summary>
    /// Lee como PROPIETARIO (sin RLS): ninguna fila de X ni de Y cambió —ni la
    /// cuenta, ni su auditoría, ni el parámetro de dominio— y la variable de
    /// sesión de ESA MISMA conexión ha vuelto a Y. Se lee en la conexión que el
    /// test mantiene abierta: al cerrarla, Npgsql y el interceptor de sesión
    /// la reinicializarían y ocultarían un <c>app.tenant_id</c> olvidado.
    /// </summary>
    /// <param name="restauracionDiferida">
    /// En un <see cref="DbUpdateConcurrencyException"/> el interceptor no puede
    /// ejecutar nada (el lector del lote sigue abierto cuando EF avisa) y
    /// restaura antes del PRIMER COMANDO EF posterior. Ahí la lectura se hace
    /// por EF —un comando del contexto, que es lo que la aplicación ejecuta—
    /// y no por la conexión cruda, que el interceptor de comandos no ve.
    /// </param>
    private async Task AfirmarNadaEscritoYSesionRestauradaAsync(
        SesionDeTest sesion, FotoDeEstado antes, Guid cuentaId, Guid tenantSesion, bool restauracionDiferida = false)
    {
        (await FotoAsync(cuentaId, tenantSesion)).Should().Be(antes,
            "el lote falló entero: ni la auditoría y la cuenta de X ni el dominio de Y pueden haber cambiado");
        var tenantDeLaConexion = restauracionDiferida
            ? await LeerTenantDeSesionPorEfAsync(sesion.Contexto)
            : await LeerTenantDeSesionAsync(sesion.Contexto);
        tenantDeLaConexion.Should().Be(tenantSesion.ToString(),
            "tras el fallo, app.tenant_id de la conexión vuelve al Tenant de sesión");
    }

    /// <summary>
    /// El siguiente lote tras un conflicto de concurrencia, SIN consulta previa
    /// que dispare la restauración diferida: el lote es de una cuenta del propio
    /// Tenant de sesión, así que no hay fijado de variable que lo arregle, y
    /// correría con el Tenant de la cuenta del lote anterior si el inicio de
    /// <c>SavingChanges</c> no restaurara primero.
    /// </summary>
    [Fact]
    public async Task B_Tras_un_conflicto_de_concurrencia_el_siguiente_lote_sin_consulta_previa_corre_con_el_Tenant_de_sesion()
    {
        var (_, tenantSesion, cuentaId) = await EscenarioAsync(conParametroDeSesion: true);

        await using var sesion = await AbrirSesionSoloAuditoriaDeIdentidadAsync(tenantSesion);
        var parametro = await sesion.Contexto.ParametrosSistema.SingleAsync();
        parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);
        var cuenta = await sesion.Contexto.Users.SingleAsync(u => u.Id == cuentaId);
        cuenta.PhoneNumber = "600000016";
        (await CapturarAsync(() => sesion.Contexto.SaveChangesAsync()))
            .Should().BeOfType<DbUpdateConcurrencyException>("precondición: el lote anterior termina en conflicto");

        sesion.Contexto.ChangeTracker.Clear();
        var sufijo = Guid.NewGuid().ToString("N");
        var nueva = new ApplicationUser
        {
            UserName = $"tras-conflicto-{sufijo}@caemanager.local",
            NormalizedUserName = $"TRAS-CONFLICTO-{sufijo}@CAEMANAGER.LOCAL",
            Email = $"tras-conflicto-{sufijo}@caemanager.local",
            NombreCompleto = "Alta tras conflicto",
            TenantId = tenantSesion,
        };
        sesion.Contexto.Users.Add(nueva);

        // Sin ninguna consulta antes: lo primero que hace este contexto es guardar.
        var act = () => sesion.Contexto.SaveChangesAsync();

        await act.Should().NotThrowAsync(
            "la fila de auditoría de una cuenta del propio Tenant de sesión no debe ejecutarse con el Tenant de la cuenta del lote anterior");
        (await TenantDeLaUltimaAuditoriaAsync(nueva.Id)).Should().Be(tenantSesion);
    }

    private static async Task<string?> LeerTenantDeSesionPorEfAsync(CaeManagerDbContext contexto) =>
        (await contexto.Database
            .SqlQueryRaw<string>("SELECT current_setting('app.tenant_id', true) AS \"Value\"")
            .ToListAsync()).Single();

    /// <summary>
    /// La restauración no basta con leerse: una escritura de dominio de Y,
    /// ahora sola, tiene que poder completarse en el mismo contexto y quedar
    /// con el Tenant de Y.
    /// </summary>
    private async Task AfirmarQueLaSesionSigueUsableAsync(SesionDeTest sesion, Guid tenantSesion)
    {
        sesion.Contexto.ChangeTracker.Clear();

        var parametro = await sesion.Contexto.ParametrosSistema.SingleOrDefaultAsync();
        if (parametro is null)
            sesion.Contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarRecuperacion, UmbralRojoRecuperacion));
        else
            parametro.Actualizar(UmbralAmbarRecuperacion, UmbralRojoRecuperacion);

        await sesion.Contexto.SaveChangesAsync();

        (await FotoAsync(Guid.Empty, tenantSesion)).UmbralAmbar.Should().Be(UmbralAmbarRecuperacion);
    }

    // ── Infraestructura del test ─────────────────────────────────────────────────

    /// <summary>Tenant de la cuenta (X), Tenant de sesión (Y) y una cuenta de X.</summary>
    private async Task<(Guid TenantCuenta, Guid TenantSesion, Guid CuentaId)> EscenarioAsync(bool conParametroDeSesion)
    {
        var tenantCuenta = await CrearTenantAsync();
        var tenantSesion = await CrearTenantAsync();
        var cuentaId = await CrearCuentaAsync(tenantCuenta);
        if (conParametroDeSesion) await SembrarParametroAsync(tenantSesion);
        return (tenantCuenta, tenantSesion, cuentaId);
    }

    private async Task<Guid> CrearTenantAsync()
    {
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;
        await using var contextoPropietario = new CaeManagerDbContext(
            opciones, new EphemeralDataProtectionProvider(), new TenantActualFijo());

        var tenant = new Tenant($"Tenant lote mixto {Guid.NewGuid():N}");
        contextoPropietario.Tenants.Add(tenant);
        await contextoPropietario.SaveChangesAsync();
        return tenant.Id;
    }

    private async Task<Guid> CrearCuentaAsync(Guid tenantPropietario)
    {
        _sesion.TenantId = tenantPropietario;
        using var ambito = _arnes.Servicios.CreateScope();
        var usuarios = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var sufijo = Guid.NewGuid().ToString("N");
        var cuenta = new ApplicationUser
        {
            UserName = $"lote-mixto-{sufijo}@caemanager.local",
            Email = $"lote-mixto-{sufijo}@caemanager.local",
            NombreCompleto = "Cuenta lote mixto",
            EmailConfirmed = true,
            TenantId = tenantPropietario,
        };
        var resultado = await usuarios.CreateAsync(cuenta, "Arnes#2026Seguro");
        resultado.Succeeded.Should().BeTrue(string.Join(", ", resultado.Errors.Select(e => e.Description)));
        return cuenta.Id;
    }

    private async Task SembrarParametroAsync(Guid tenant)
    {
        _sesion.TenantId = tenant;
        using var ambito = _arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        contexto.ParametrosSistema.Add(new ParametroSistema(UmbralAmbarInicial, UmbralRojoInicial));
        await contexto.SaveChangesAsync();
    }

    /// <summary>
    /// Abre un scope con el Tenant de sesión Y y MANTIENE la conexión abierta:
    /// así <c>app.tenant_id</c> se puede leer después en la misma conexión
    /// física, que es lo único que distingue «restaurado» de «reinicializado al
    /// reabrir».
    /// </summary>
    private async Task<SesionDeTest> AbrirSesionAsync(Guid tenantSesion)
    {
        _sesion.TenantId = tenantSesion;
        var ambito = _arnes.Servicios.CreateAsyncScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        await contexto.Database.OpenConnectionAsync();
        return new SesionDeTest(
            ambito, contexto, ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>());
    }

    /// <summary>
    /// Grupo B: el mismo cableado de sellado y de sesión RLS que producción,
    /// pero SIN <c>AuditoriaInterceptor</c>: en su lugar, una fila de auditoría
    /// sintética solo para las cuentas modificadas. Provoca el mismo cambio de
    /// <c>app.tenant_id</c> (lo decide <c>TenantSelladoInterceptor</c> a partir
    /// de esa fila) y deja las escrituras de dominio sin fila de auditoría que
    /// las rechace antes.
    /// </summary>
    private async Task<SesionDeTest> AbrirSesionSoloAuditoriaDeIdentidadAsync(
        Guid tenantSesion, SaveChangesInterceptor? trasElSellado = null)
    {
        _sesion.TenantId = tenantSesion;
        var servicios = _arnes.Servicios;
        var interceptores = new List<IInterceptor>
        {
            new AuditoriaSoloDeIdentidadInterceptor(),
            new TenantSelladoInterceptor(_sesion),
        };
        if (trasElSellado is not null) interceptores.Add(trasElSellado);
        interceptores.Add(new TenantRlsConnectionInterceptor(
            _sesion,
            servicios.GetRequiredService<IClienteActivoSeleccionado>(),
            servicios.GetRequiredService<ICurrentUserService>()));

        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(
                BaseDatosPostgresDePruebas.CadenaComoRuntime(_arnes.CadenaPropietario),
                npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(interceptores)
            .Options;

        var contexto = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), _sesion);
        await contexto.Database.OpenConnectionAsync();
        return new SesionDeTest(null, contexto, null);
    }

    private static async Task<string?> LeerTenantDeSesionAsync(CaeManagerDbContext contexto)
    {
        await using var comando = contexto.Database.GetDbConnection().CreateCommand();
        comando.CommandText = "SELECT current_setting('app.tenant_id', true);";
        return (string?)await comando.ExecuteScalarAsync();
    }

    private async Task<Guid> TenantDeLaUltimaAuditoriaAsync(Guid cuentaId)
    {
        await using var conexion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"SELECT ""TenantId"" FROM ""RegistrosAuditoria""
WHERE ""EntidadId"" = @id ORDER BY ""FechaUtc"" DESC LIMIT 1;";
        comando.Parameters.AddWithValue("id", cuentaId);
        return (Guid)(await comando.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Foto del estado observable, leída como PROPIETARIO (RLS no le aplica y
    /// ve las filas de todos los Tenants). Si el lote escribiera algo en X o en
    /// Y —una fila de auditoría, el teléfono de la cuenta, el parámetro—, la
    /// foto cambiaría. La cuenta <see cref="Guid.Empty"/> significa «sin cuenta».
    /// </summary>
    private async Task<FotoDeEstado> FotoAsync(Guid cuentaId, Guid tenantSesion)
    {
        await using var conexion = new NpgsqlConnection(_arnes.CadenaPropietario);
        await conexion.OpenAsync();

        async Task<T> Escalar<T>(string sql, params (string Nombre, object Valor)[] parametros)
        {
            await using var comando = conexion.CreateCommand();
            comando.CommandText = sql;
            foreach (var (nombre, valor) in parametros) comando.Parameters.AddWithValue(nombre, valor);
            var resultado = await comando.ExecuteScalarAsync();
            return resultado is null or DBNull ? default! : (T)resultado;
        }

        return new FotoDeEstado(
            FilasDeAuditoria: await Escalar<long>(@"SELECT count(*) FROM ""RegistrosAuditoria"";"),
            TelefonoDeLaCuenta: cuentaId == Guid.Empty
                ? null
                : await Escalar<string?>(@"SELECT ""PhoneNumber"" FROM ""AspNetUsers"" WHERE ""Id"" = @id;", ("id", cuentaId)),
            FilasDeParametro: await Escalar<long>(
                @"SELECT count(*) FROM ""ParametrosSistema"" WHERE ""TenantId"" = @t;", ("t", tenantSesion)),
            UmbralAmbar: await Escalar<int>(
                @"SELECT COALESCE(MAX(""UmbralAmbarDias""), 0) FROM ""ParametrosSistema"" WHERE ""TenantId"" = @t;", ("t", tenantSesion)));
    }

    private static async Task<Exception?> CapturarAsync(Func<Task> accion)
    {
        try { await accion(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static Exception? CapturarSincrono(Action accion)
    {
        try { accion(); return null; }
        catch (Exception ex) { return ex; }
    }

    private static string? SqlStateDe(Exception? excepcion) => PostgresDe(excepcion)?.SqlState;

    private static string? MensajeDe(Exception? excepcion) => PostgresDe(excepcion)?.MessageText;

    private static PostgresException? PostgresDe(Exception? excepcion)
    {
        for (var actual = excepcion; actual is not null; actual = actual.InnerException)
            if (actual is PostgresException postgres) return postgres;
        return null;
    }

    private sealed record FotoDeEstado(long FilasDeAuditoria, string? TelefonoDeLaCuenta, long FilasDeParametro, int UmbralAmbar);

    private sealed class SesionDeTest(
        AsyncServiceScope? ambito, CaeManagerDbContext contexto, UserManager<ApplicationUser>? usuarios) : IAsyncDisposable
    {
        public CaeManagerDbContext Contexto { get; } = contexto;
        public UserManager<ApplicationUser> Usuarios { get; } = usuarios!;

        public async ValueTask DisposeAsync()
        {
            await Contexto.Database.CloseConnectionAsync();
            await Contexto.DisposeAsync();
            if (ambito is { } a) await a.DisposeAsync();
        }
    }

    /// <summary>
    /// Sustituye a <c>AuditoriaInterceptor</c> en el grupo B: una fila
    /// «Creado» o «Modificado» por cada <see cref="ApplicationUser"/> nuevo o modificado y nada
    /// para las entidades de dominio. Va PRIMERO en la lista, igual que el
    /// auditor real, para que <c>TenantSelladoInterceptor</c> ya la vea.
    /// </summary>
    private sealed class AuditoriaSoloDeIdentidadInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Auditar(eventData.Context!);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }

        public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
        {
            Auditar(eventData.Context!);
            return base.SavingChanges(eventData, result);
        }

        private static void Auditar(DbContext contexto)
        {
            var filas = contexto.ChangeTracker.Entries<ApplicationUser>()
                .Where(e => e.State is EntityState.Modified or EntityState.Added)
                .Select(e => new RegistroAuditoria(EntidadTipoAuditoria.Usuario, e.Entity.Id, e.State == EntityState.Added ? "Creado" : "Modificado", null, null, null))
                .ToList();
            contexto.Set<RegistroAuditoria>().AddRange(filas);
        }
    }

    /// <summary>Cancela el <c>SaveChanges</c> con el sellado ya hecho y ningún comando ejecutado.</summary>
    private sealed class CancelaTrasElSelladoInterceptor(CancellationTokenSource cancelacion) : SaveChangesInterceptor
    {
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            cancelacion.Cancel();
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private sealed class TenantActualFijo : ITenantActual
    {
        public Guid? TenantId { get; set; }
    }
}
