using CaeManager.Application.Common;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra el escenario de demo de ADR-004-delegacion-consultoras-cae.md: el
/// tenant #1 (<see cref="TenantSeedData.IdPorDefecto"/>) pasa a jugar el
/// papel de Consultora — sin datos operativos propios (ADR-004 § 5.1) — y
/// todos los datos de prueba de CAE (<see cref="DatosPruebaSeeder"/>) se
/// siembran en un tenant Cliente Delegante nuevo, unido al tenant #1 por una
/// <see cref="DelegacionTenant"/> activa y una
/// <see cref="AsignacionOperadorDelegado"/> para el Administrador inicial
/// (<see cref="IdentitySeeder"/>) — así el selector "Cliente activo" tiene
/// algo real que mostrar nada más arrancar en desarrollo.
///
/// Sustituye la siembra anterior, que ponía los 200 clientes/etc. de prueba
/// directamente en el tenant #1: esos datos eran siempre de prueba, nunca
/// datos reales, así que no hace falta preservarlos ni migrarlos — decisión
/// explícita para esta ronda (ver DESIGN_SYSTEM.md/ROADMAP.md). Sigue abierta
/// (ADR-004 § 12.6) la pregunta de qué pasa con el tenant #1 real de
/// producción, que no encaja tal cual en "Consultora sin datos propios" —
/// esta siembra es exclusivamente para desarrollo/demo, no toca ningún
/// entorno desplegado.
///
/// Apagado por defecto, mismo principio "inerte por defecto" que
/// <see cref="SegundoTenantSeeder"/> — corre exactamente cuando
/// <c>DatosPrueba:Activo</c> es true (sustituye la llamada directa a
/// <see cref="DatosPruebaSeeder"/> en Program.cs).
/// </summary>
public static class DelegacionDemoSeeder
{
    // Nombres de ficción (caricaturas), como el resto de la siembra de
    // demo (ver DatosPruebaSeeder) — distintos de los 9 "Cliente" que
    // DatosPruebaSeeder crea dentro de cada tenant, para no repetir marca.
    public const string NombreTenantClienteDemo = "Laboratorios Dexter S.L. (Cliente Delegante demo)";

    /// <summary>
    /// Segundo Cliente Delegante, con datos propios más pequeños y sin
    /// usuarios de prueba: da a la Consultora una cartera de más de un
    /// cliente (Visión de cartera, selector de Cliente activo con opciones
    /// reales) y permite comprobar a simple vista que los datos de un tenant
    /// no se cuelan en el otro.
    /// </summary>
    public const string NombreTenantClienteDemo2 = "Transportes Planet Express S.A. (Cliente Delegante demo 2)";

    /// <summary>
    /// Tercer Cliente Delegante, sin datos ni operadores: existe solo para
    /// que la pantalla /delegaciones tenga una delegación comercial
    /// <b>revocada</b> que reactivar — la única variante de estado de
    /// DelegacionTenant comercial que no puede verse en las otras dos
    /// (revocar y reactivar una activa se prueba sobre cualquiera; una que
    /// ya está revocada hay que sembrarla).
    /// </summary>
    public const string NombreTenantClienteDemo3 = "Hosteleria Krusty Krab S.L. (Cliente Delegante demo 3)";

    public const string RolOperadorDelegadoDemo = "GestorCae";

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        IConfiguration configuration,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
            return;

        var tenantClienteId = await AprovisionarTenantClienteAsync(dbContext, NombreTenantClienteDemo, logger, cancellationToken);

        // Todos los datos operativos de prueba (clientes, empresas, centros,
        // trabajadores, documentos, usuarios prueba.<rol><n>@...) se siembran
        // dentro del tenant Cliente Delegante, nunca en el tenant #1 — ver
        // ADR-004 § 5.1, "la Consultora es un Tenant sin datos operativos
        // propios".
        using (AmbitoTenantExplicito.Establecer(tenantClienteId))
        {
            await DatosPruebaSeeder.SeedAsync(dbContext, userManager, configuration, logger, cancellationToken);
            await ComunicacionesDatosPruebaSeeder.SeedAsync(dbContext, userManager, configuration, logger, cancellationToken);
            await CicloDocumentalDatosPruebaSeeder.SeedAsync(dbContext, userManager, configuration, logger, cancellationToken);
            await SembrarVariantesIdentidadAsync(dbContext, userManager, userStore, logger, cancellationToken);
        }

        await CrearDelegacionConAdministradorAsync(
            dbContext, userManager, configuration, logger, tenantClienteId, NombreTenantClienteDemo, cancellationToken);

        // Segundo Cliente Delegante: solo datos (los usuarios de prueba son
        // únicos por email y ya pertenecen al primero).
        var tenantCliente2Id = await AprovisionarTenantClienteAsync(dbContext, NombreTenantClienteDemo2, logger, cancellationToken);

        using (AmbitoTenantExplicito.Establecer(tenantCliente2Id))
        {
            await DatosPruebaSeeder.SembrarSoloDatosAsync(dbContext, logger, cancellationToken);
            await SembrarUsuariosDemo2Async(dbContext, userManager, userStore, logger, cancellationToken);
        }

        await CrearDelegacionConAdministradorAsync(
            dbContext, userManager, configuration, logger, tenantCliente2Id, NombreTenantClienteDemo2, cancellationToken);

        // Tercer tenant, sin datos: solo aporta la delegación comercial revocada.
        var tenantCliente3Id = await AprovisionarTenantClienteAsync(dbContext, NombreTenantClienteDemo3, logger, cancellationToken);
        await CrearDelegacionRevocadaAsync(dbContext, tenantCliente3Id, logger, cancellationToken);

        await SembrarOperadoresConsultoraAsync(
            dbContext, userManager, logger, tenantClienteId, tenantCliente2Id, cancellationToken);
    }

    /// <summary>
    /// Usuarios de la Consultora (tenant #1) que operan Delegated Workspaces
    /// con roles distintos de Administrador — sin ellos, el selector de
    /// Cliente activo y "retirar operador" solo podían probarse con el
    /// Administrador de plataforma.
    /// </summary>
    private static async Task SembrarOperadoresConsultoraAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ILogger logger,
        Guid tenantClienteDemo1Id,
        Guid tenantClienteDemo2Id,
        CancellationToken cancellationToken)
    {
        var operadorGestor = await CrearUsuarioConsultoraAsync(
            dbContext, userManager, logger, "prueba.operador.gestor1@caemanager.local",
            "Operador Consultora Gestor (prueba)", Roles.GestorCae, cancellationToken);
        var operadorConsulta = await CrearUsuarioConsultoraAsync(
            dbContext, userManager, logger, "prueba.operador.consulta1@caemanager.local",
            "Operador Consultora Consulta (prueba)", Roles.Consulta, cancellationToken);

        var delegacionDemo1 = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
            d => d.TenantConsultoraId == TenantSeedData.IdPorDefecto && d.TenantClienteId == tenantClienteDemo1Id
                 && d.Proposito == PropositoDelegacion.Comercial, cancellationToken);
        var delegacionDemo2 = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
            d => d.TenantConsultoraId == TenantSeedData.IdPorDefecto && d.TenantClienteId == tenantClienteDemo2Id
                 && d.Proposito == PropositoDelegacion.Comercial, cancellationToken);

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            if (operadorGestor is not null && delegacionDemo1 is not null &&
                !await dbContext.AsignacionesOperadorDelegado.AnyAsync(
                    a => a.DelegacionTenantId == delegacionDemo1.Id && a.UsuarioId == operadorGestor.Id, cancellationToken))
            {
                dbContext.AsignacionesOperadorDelegado.Add(
                    new AsignacionOperadorDelegado(delegacionDemo1.Id, operadorGestor.Id, Roles.GestorCae));
            }

            if (operadorConsulta is not null && delegacionDemo2 is not null &&
                !await dbContext.AsignacionesOperadorDelegado.AnyAsync(
                    a => a.DelegacionTenantId == delegacionDemo2.Id && a.UsuarioId == operadorConsulta.Id, cancellationToken))
            {
                dbContext.AsignacionesOperadorDelegado.Add(
                    new AsignacionOperadorDelegado(delegacionDemo2.Id, operadorConsulta.Id, Roles.Consulta));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<ApplicationUser?> CrearUsuarioConsultoraAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        ILogger logger,
        string email,
        string nombreCompleto,
        string rol,
        CancellationToken cancellationToken)
    {
        var existente = await userManager.FindByEmailAsync(email);
        if (existente is not null)
            return existente;

        var usuario = new ApplicationUser
        {
            UserName = email,
            Email = email,
            NombreCompleto = nombreCompleto,
            EmailConfirmed = true,
            DebeCambiarContrasena = false,
            TenantId = TenantSeedData.IdPorDefecto
        };

        var resultado = await userManager.CreateAsync(usuario, DatosPruebaSeeder.ContrasenaUsuariosPrueba);
        if (!resultado.Succeeded)
        {
            logger.LogWarning("No se pudo crear el operador de consultora {Email}: {Errores}",
                email, string.Join(", ", resultado.Errors.Select(e => e.Description)));
            return null;
        }

        await userManager.AddToRoleAsync(usuario, rol);
        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, usuario.Id, cancellationToken);
        }

        return usuario;
    }

    /// <summary>
    /// Variantes de identidad y de aceptación de términos que el resto de la
    /// siembra evita a propósito (todos los usuarios prueba.* nacen listos
    /// para entrar sin fricción): sin rol asignado, con cambio de contraseña
    /// forzado, con términos de una versión antigua pendientes de re-aceptar,
    /// y con 2FA activa (clave TOTP fija de IdentitySeeder, la que los tests
    /// E2E saben calcular).
    /// </summary>
    private static async Task SembrarVariantesIdentidadAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tenantId = AmbitoTenantExplicito.TenantIdActual ?? TenantSeedData.IdPorDefecto;

        async Task<ApplicationUser?> CrearAsync(string email, string nombreCompleto, bool debeCambiarContrasena)
        {
            if (await userManager.FindByEmailAsync(email) is not null)
                return null;

            var usuario = new ApplicationUser
            {
                UserName = email,
                Email = email,
                NombreCompleto = nombreCompleto,
                EmailConfirmed = true,
                DebeCambiarContrasena = debeCambiarContrasena,
                TenantId = tenantId
            };

            var resultado = await userManager.CreateAsync(usuario, DatosPruebaSeeder.ContrasenaUsuariosPrueba);
            if (!resultado.Succeeded)
            {
                logger.LogWarning("No se pudo crear el usuario de variante de identidad {Email}: {Errores}",
                    email, string.Join(", ", resultado.Errors.Select(e => e.Description)));
                return null;
            }

            return usuario;
        }

        // Sin rol: aterriza en /cuenta/pendiente-de-rol.
        var sinRol = await CrearAsync("prueba.sinrol1@caemanager.local", "Prueba Sin Rol 1", debeCambiarContrasena: false);
        if (sinRol is not null)
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, sinRol.Id, cancellationToken);

        // Cambio de contraseña forzado en el primer inicio de sesión.
        var cambioContrasena = await CrearAsync(
            "prueba.cambiocontrasena1@caemanager.local", "Prueba Cambio Contrasena 1", debeCambiarContrasena: true);
        if (cambioContrasena is not null)
        {
            await userManager.AddToRoleAsync(cambioContrasena, Roles.Consulta);
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, cambioContrasena.Id, cancellationToken);
        }

        // Aceptación de una versión antigua: el gate de términos vuelve a salir.
        var terminosAntiguos = await CrearAsync(
            "prueba.terminosantiguos1@caemanager.local", "Prueba Terminos Antiguos 1", debeCambiarContrasena: false);
        if (terminosAntiguos is not null)
        {
            await userManager.AddToRoleAsync(terminosAntiguos, Roles.Consulta);
            dbContext.AceptacionesTerminos.Add(new Domain.Cumplimiento.AceptacionTerminos(
                terminosAntiguos.Id, "2026-01-01", DateTime.UtcNow.AddMonths(-7)));
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // 2FA activa con la clave fija de IdentitySeeder.
        var conDobleFactor = await CrearAsync("prueba.con2fa1@caemanager.local", "Prueba Con 2FA 1", debeCambiarContrasena: false);
        if (conDobleFactor is not null)
        {
            await userManager.AddToRoleAsync(conDobleFactor, Roles.Consulta);
            if (userStore is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
            {
                await claveStore.SetAuthenticatorKeyAsync(
                    conDobleFactor, IdentitySeeder.ClaveTotpAdministradorInicial, cancellationToken);
                await userManager.UpdateAsync(conDobleFactor);
            }
            await userManager.SetTwoFactorEnabledAsync(conDobleFactor, true);
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, conDobleFactor.Id, cancellationToken);
        }
    }

    /// <summary>
    /// El tenant demo 2 no tenía ningún usuario propio: un Administrador
    /// (con 2FA, P1-13) y un GestorCae con toda la cartera asignada bastan
    /// para entrar directamente y ejercitar sus flujos sin pasar por el
    /// Delegated Workspace de la Consultora.
    /// </summary>
    private static async Task SembrarUsuariosDemo2Async(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tenantId = AmbitoTenantExplicito.TenantIdActual!.Value;

        if (await userManager.FindByEmailAsync("prueba2.administrador1@caemanager.local") is not null)
            return;

        var administrador = new ApplicationUser
        {
            UserName = "prueba2.administrador1@caemanager.local",
            Email = "prueba2.administrador1@caemanager.local",
            NombreCompleto = "Prueba2 Administrador 1",
            EmailConfirmed = true,
            DebeCambiarContrasena = false,
            TenantId = tenantId
        };
        var resultado = await userManager.CreateAsync(administrador, DatosPruebaSeeder.ContrasenaUsuariosPrueba);
        if (resultado.Succeeded)
        {
            await userManager.AddToRoleAsync(administrador, Roles.Administrador);
            // P1-13: todo Administrador con 2FA — misma clave fija que
            // IdentitySeeder para que los E2E puedan calcular el código.
            if (userStore is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
            {
                await claveStore.SetAuthenticatorKeyAsync(
                    administrador, IdentitySeeder.ClaveTotpAdministradorInicial, cancellationToken);
                await userManager.UpdateAsync(administrador);
            }
            await userManager.SetTwoFactorEnabledAsync(administrador, true);
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, administrador.Id, cancellationToken);

            await SembrarHistorialRetencionAsync(dbContext, administrador.Id, cancellationToken);
        }
        else
        {
            logger.LogWarning("No se pudo crear el administrador del tenant demo 2: {Errores}",
                string.Join(", ", resultado.Errors.Select(e => e.Description)));
        }

        var gestor = new ApplicationUser
        {
            UserName = "prueba2.gestorcae1@caemanager.local",
            Email = "prueba2.gestorcae1@caemanager.local",
            NombreCompleto = "Prueba2 GestorCae 1",
            EmailConfirmed = true,
            DebeCambiarContrasena = false,
            TenantId = tenantId
        };
        resultado = await userManager.CreateAsync(gestor, DatosPruebaSeeder.ContrasenaUsuariosPrueba);
        if (resultado.Succeeded)
        {
            await userManager.AddToRoleAsync(gestor, Roles.GestorCae);
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, gestor.Id, cancellationToken);

            // Toda la cartera del demo 2 a su único gestor — sin ejecutivo
            // asignado, un GestorCae no vería ningún dato (IAlcanceDatosService).
            var clientes = await dbContext.Clientes.ToListAsync(cancellationToken);
            foreach (var cliente in clientes)
                cliente.AsignarEjecutivo(gestor.Id);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        else
        {
            logger.LogWarning("No se pudo crear el gestor del tenant demo 2: {Errores}",
                string.Join(", ", resultado.Errors.Select(e => e.Description)));
        }
    }

    /// <summary>
    /// Historial sintético del ciclo de retención en los 5 estados —
    /// SOLO en el tenant demo 2 a propósito: FlujoRetencionTests (E2E)
    /// ejecuta el ciclo real sobre el demo 1 y espera que el barrido
    /// proponga exactamente dos solicitudes nuevas allí. La Ejecutada
    /// recorre el camino real del dominio (avisar → programar con usuario
    /// autorizante y fecha → ejecutar) — la invariante "no hay 'ejecutada'
    /// sin autorización expresa con fecha" se respeta también en la siembra.
    /// Los veteranos del demo 2 quedan intactos como candidatos vivos del
    /// barrido.
    /// </summary>
    private static async Task SembrarHistorialRetencionAsync(
        CaeManagerDbContext dbContext, Guid autorizadaPorUsuarioId, CancellationToken cancellationToken)
    {
        if (await dbContext.SolicitudesPurga.AnyAsync(cancellationToken))
            return;

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var fechaCorte = hoy.AddYears(-5);

        dbContext.SolicitudesPurga.Add(new SolicitudPurga(TipoDatoPurgable.Documentos, 18, fechaCorte));

        var avisada = new SolicitudPurga(TipoDatoPurgable.TrabajadoresDadosDeBaja, 6, fechaCorte);
        avisada.MarcarTenantAvisado();
        dbContext.SolicitudesPurga.Add(avisada);

        var programada = new SolicitudPurga(TipoDatoPurgable.Documentos, 12, fechaCorte.AddMonths(-2));
        programada.MarcarTenantAvisado();
        programada.Programar(hoy.AddDays(14), autorizadaPorUsuarioId, hoy);
        dbContext.SolicitudesPurga.Add(programada);

        var ejecutada = new SolicitudPurga(TipoDatoPurgable.TrabajadoresDadosDeBaja, 4, fechaCorte.AddMonths(-6));
        ejecutada.MarcarTenantAvisado();
        ejecutada.Programar(hoy, autorizadaPorUsuarioId, hoy);
        ejecutada.Ejecutar(hoy);
        dbContext.SolicitudesPurga.Add(ejecutada);

        var cancelada = new SolicitudPurga(TipoDatoPurgable.Documentos, 9, fechaCorte.AddMonths(-4));
        cancelada.MarcarTenantAvisado();
        cancelada.Cancelar("El tenant pidió más plazo para revisar los expedientes afectados.");
        dbContext.SolicitudesPurga.Add(cancelada);

        await dbContext.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Delegación comercial en estado revocado — Desactivar() nada más
    /// crearla. Cualquiera de las dos partes podría reactivarla desde
    /// /delegaciones, que es exactamente lo que esta variante permite probar.
    /// </summary>
    private static async Task CrearDelegacionRevocadaAsync(
        CaeManagerDbContext dbContext, Guid tenantClienteId, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.DelegacionesTenant.AnyAsync(
                d => d.TenantConsultoraId == TenantSeedData.IdPorDefecto && d.TenantClienteId == tenantClienteId,
                cancellationToken))
        {
            return;
        }

        var delegacion = new DelegacionTenant(TenantSeedData.IdPorDefecto, tenantClienteId);
        delegacion.Desactivar();

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            dbContext.DelegacionesTenant.Add(delegacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Delegación comercial revocada de demo sembrada hacia {TenantCliente}.", tenantClienteId);
    }

    private static async Task<Guid> AprovisionarTenantClienteAsync(
        CaeManagerDbContext dbContext, string nombreTenant, ILogger logger, CancellationToken cancellationToken)
    {
        var tenantExistente = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Nombre == nombreTenant, cancellationToken);
        if (tenantExistente is not null)
            return tenantExistente.Id;

        var tenantCliente = new Tenant(nombreTenant);

        // Mismo motivo que SegundoTenantSeeder: hace falta un tenant
        // resuelto ya para este primer guardado (el interceptor de
        // auditoría necesita sellar contra algo), antes incluso de que
        // el propio Tenant exista en la base de datos — el Id ya se
        // conoce porque se genera en el constructor (ver Entity).
        using (AmbitoTenantExplicito.Establecer(tenantCliente.Id))
        {
            dbContext.Tenants.Add(tenantCliente);

            // Todo tenant necesita su propia fila de ParametroSistema —
            // ObtenerKpisDashboardQuery/ObtenerDesgloseDashboardQuery la
            // leen con SingleAsync() y fallan si no existe ninguna. Al
            // tenant #1 se la da un HasData de migración; un tenant
            // creado en tiempo de ejecución (como este) tiene que
            // sembrarla explícitamente — mismos umbrales por defecto que
            // ParametroSistemaSeedData.
            dbContext.ParametrosSistema.Add(new ParametroSistema(
                ParametroSistemaSeedData.UmbralAmbarDias, ParametroSistemaSeedData.UmbralRojoDias));

            // Mismo motivo: el catálogo de TipoDocumento también tiene
            // HasData solo para el tenant #1 (ver TipoDocumentoConfiguration
            // y el comentario de TipoDocumentoSeedData.ComoFilasParaMigracion,
            // "un tenant nuevo recibirá su propia copia editable al
            // aprovisionarse", docs/MULTITENANCY.md § 7) — sin esto, los
            // Documento que DatosPruebaSeeder genera más abajo referencian
            // un TipoDocumentoId que solo existe bajo el tenant #1, y el
            // filtro global de tenant lo esconde: la lista de Documentos
            // de este tenant aparecería vacía pese a que el Dashboard sí
            // cuenta filas (ese conteo no necesita el join a TiposDocumento).
            // Ids nuevos a propósito — el Id de TipoDocumentoSeedData es
            // fijo para las filas del tenant #1, no reutilizable aquí.
            dbContext.TiposDocumento.AddRange(TipoDocumentoSeedData.CrearCopiasParaTenant());

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Tenant Cliente Delegante de demo sembrado: {Nombre} ({TenantId}).", nombreTenant, tenantCliente.Id);
        return tenantCliente.Id;
    }

    private static async Task CrearDelegacionConAdministradorAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        ILogger logger,
        Guid tenantClienteId,
        string nombreTenantCliente,
        CancellationToken cancellationToken)
    {
        if (await dbContext.DelegacionesTenant.AnyAsync(
                d => d.TenantConsultoraId == TenantSeedData.IdPorDefecto && d.TenantClienteId == tenantClienteId,
                cancellationToken))
        {
            return;
        }

        var delegacion = new DelegacionTenant(TenantSeedData.IdPorDefecto, tenantClienteId);

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            dbContext.DelegacionesTenant.Add(delegacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var emailAdministradorConsultora = configuration["AdministradorInicial:Email"] ?? IdentitySeeder.EmailAdministradorInicial;
        var administradorConsultora = await userManager.FindByEmailAsync(emailAdministradorConsultora);

        if (administradorConsultora is null)
        {
            logger.LogWarning(
                "No se encontró el Administrador inicial ({Email}) para asignarlo como Operador Delegado de demo.",
                emailAdministradorConsultora);
            return;
        }

        var asignacion = new AsignacionOperadorDelegado(delegacion.Id, administradorConsultora.Id, RolOperadorDelegadoDemo);

        using (AmbitoTenantExplicito.Establecer(TenantSeedData.IdPorDefecto))
        {
            dbContext.AsignacionesOperadorDelegado.Add(asignacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Delegated Workspace de demo sembrado: {Administrador} puede operar {TenantCliente} como {Rol}.",
            administradorConsultora.Email, nombreTenantCliente, RolOperadorDelegadoDemo);
    }
}
