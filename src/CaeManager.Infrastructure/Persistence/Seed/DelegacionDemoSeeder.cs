using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Retencion;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra el escenario de demo de ADR-004-delegacion-consultoras-cae.md.
///
/// El tenant #1 (<see cref="TenantSeedData.IdPorDefecto"/>, TALVEG) es
/// puramente el administrador de la plataforma (<c>EsPlataforma = true</c>)
/// — no opera ningún Delegated Workspace y no tiene datos operativos propios
/// de demo. La Consultora de la demo es un tenant aparte, "ArcoSPA
/// Prevención S.L.", con su propio Administrador — decisión del propietario
/// (2026-08-14): mezclar la cuenta de plataforma con la de consultora hacía
/// imposible entender cada rol por separado.
///
/// ArcoSPA gestiona cuatro Clientes Delegantes: "Refrielectric S.L." (datos
/// completos + roster de usuarios propio, la referencia principal de
/// "empresa final"), "Laboratorios Dexter S.L." (datos + usuarios
/// <c>prueba.&lt;rol&gt;</c> — sin cambios respecto a la siembra anterior,
/// solo cambia quién los opera), "Transportes Planet Express S.A." (solo
/// datos, cartera repartida a un único gestor) y "Hosteleria Krusty Krab
/// S.L." (sin datos, solo la delegación comercial revocada de demo).
///
/// Apagado por defecto, mismo principio "inerte por defecto" que
/// <see cref="SegundoTenantSeeder"/> — corre exactamente cuando
/// <c>DatosPrueba:Activo</c> es true (sustituye la llamada directa a
/// <see cref="DatosPruebaSeeder"/> en Program.cs).
/// </summary>
public static class DelegacionDemoSeeder
{
    public const string NombreTenantConsultora = "ArcoSPA Prevención S.L. (Consultora demo)";
    public const string EmailAdministradorConsultora = "admin.arcospa@caemanager.local";

    /// <summary>
    /// Primer Cliente Delegante: la referencia principal de "empresa final"
    /// (perfil Cliente Directo, DDL-072) — datos operativos del mismo
    /// tamaño que Laboratorios Dexter (ver
    /// <see cref="DatosPruebaSeeder.SembrarSoloDatosCompletosAsync"/>) y un
    /// roster de usuarios propio (<c>refri.&lt;rol&gt;</c>), no los
    /// <c>prueba.&lt;rol&gt;</c> compartidos — esos ya pertenecen a
    /// Laboratorios Dexter y repetirlos aquí los movería de cartera.
    /// </summary>
    public const string NombreTenantRefrielectric = "Refrielectric S.L. (Cliente Delegante demo)";

    public const string PrefijoEmailRefrielectric = "refri.";

    // Nombres de ficción (caricaturas), como el resto de la siembra de
    // demo (ver DatosPruebaSeeder) — distintos de los 9 "Cliente" que
    // DatosPruebaSeeder crea dentro de cada tenant, para no repetir marca.
    public const string NombreTenantClienteDemo = "Laboratorios Dexter S.L. (Cliente Delegante demo)";

    /// <summary>
    /// Tercer Cliente Delegante, con datos propios más pequeños y sin
    /// usuarios de prueba: da a la Consultora una cartera de más de un
    /// cliente (Visión de cartera, selector de Cliente activo con opciones
    /// reales) y permite comprobar a simple vista que los datos de un tenant
    /// no se cuelan en el otro.
    /// </summary>
    public const string NombreTenantClienteDemo2 = "Transportes Planet Express S.A. (Cliente Delegante demo 2)";

    /// <summary>
    /// Cuarto Cliente Delegante, sin datos ni operadores: existe solo para
    /// que la pantalla /delegaciones tenga una delegación comercial
    /// <b>revocada</b> que reactivar — la única variante de estado de
    /// DelegacionTenant comercial que no puede verse en las otras (revocar y
    /// reactivar una activa se prueba sobre cualquiera; una que ya está
    /// revocada hay que sembrarla).
    /// </summary>
    public const string NombreTenantClienteDemo3 = "Hosteleria Krusty Krab S.L. (Cliente Delegante demo 3)";

    public const string RolOperadorDelegadoDemo = "GestorCae";

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo"))
            return;

        // Antes de aprovisionar el primer tenant: si esto es Producción y no
        // hay credenciales configuradas, lanza. Ver CredencialesDemo.
        var credenciales = CredencialesDemo.Resolver(configuration, entorno);

        var tenantConsultoraId = await AprovisionarTenantAsync(
            dbContext, NombreTenantConsultora, PerfilVocabularioTenant.Consultora, logger, cancellationToken);
        var administradorConsultora = await CrearAdministradorConsultoraAsync(
            dbContext, userManager, userStore, credenciales, logger, tenantConsultoraId, cancellationToken);

        // --- Refrielectric: la referencia principal de "empresa final" ---
        var refrielectricId = await AprovisionarTenantAsync(
            dbContext, NombreTenantRefrielectric, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken);
        using (AmbitoTenantExplicito.Establecer(refrielectricId))
        {
            await DatosPruebaSeeder.SembrarSoloDatosCompletosAsync(dbContext, logger, cancellationToken);
            var gestoresRefrielectric = await SembrarUsuariosRefrielectricAsync(dbContext, userManager, userStore, credenciales, logger, cancellationToken);
            await SembrarEscenariosDashboardRefrielectricAsync(dbContext, gestoresRefrielectric, logger, cancellationToken);
        }
        await CrearDelegacionAsync(
            dbContext, tenantConsultoraId, refrielectricId, administradorConsultora, logger, NombreTenantRefrielectric, cancellationToken);

        // --- Laboratorios Dexter: datos + usuarios prueba.<rol>, sin cambios ---
        var tenantClienteId = await AprovisionarTenantAsync(
            dbContext, NombreTenantClienteDemo, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken);

        // Todos los datos operativos de prueba (clientes, empresas, centros,
        // trabajadores, documentos, usuarios prueba.<rol><n>@...) se siembran
        // dentro del tenant Cliente Delegante, nunca en el tenant #1 — ver
        // ADR-004 § 5.1, "la Consultora es un Tenant sin datos operativos
        // propios".
        using (AmbitoTenantExplicito.Establecer(tenantClienteId))
        {
            await DatosPruebaSeeder.SeedAsync(dbContext, userManager, userStore, configuration, entorno, logger, cancellationToken);
            await ComunicacionesDatosPruebaSeeder.SeedAsync(dbContext, userManager, configuration, logger, cancellationToken);
            await CicloDocumentalDatosPruebaSeeder.SeedAsync(dbContext, userManager, configuration, logger, cancellationToken);
            await SembrarVariantesIdentidadAsync(dbContext, userManager, userStore, credenciales, logger, cancellationToken);
        }
        await CrearDelegacionAsync(
            dbContext, tenantConsultoraId, tenantClienteId, administradorConsultora, logger, NombreTenantClienteDemo, cancellationToken);

        // --- Transportes Planet Express: solo datos + cartera a un único gestor ---
        var tenantCliente2Id = await AprovisionarTenantAsync(
            dbContext, NombreTenantClienteDemo2, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken);

        using (AmbitoTenantExplicito.Establecer(tenantCliente2Id))
        {
            await DatosPruebaSeeder.SembrarSoloDatosAsync(dbContext, logger, cancellationToken);
            await SembrarUsuariosDemo2Async(dbContext, userManager, userStore, credenciales, logger, cancellationToken);
        }
        await CrearDelegacionAsync(
            dbContext, tenantConsultoraId, tenantCliente2Id, administradorConsultora, logger, NombreTenantClienteDemo2, cancellationToken);

        // --- Hosteleria Krusty Krab: sin datos, solo la delegación comercial revocada ---
        var tenantCliente3Id = await AprovisionarTenantAsync(
            dbContext, NombreTenantClienteDemo3, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken);
        await CrearDelegacionRevocadaAsync(dbContext, tenantConsultoraId, tenantCliente3Id, logger, cancellationToken);

        await SembrarOperadoresConsultoraAsync(
            dbContext, userManager, credenciales, logger, tenantConsultoraId, refrielectricId, tenantClienteId, cancellationToken);
    }

    /// <summary>
    /// Administrador propio de ArcoSPA — nunca <c>admin@caemanager.local</c>
    /// (ese es el administrador de la plataforma, TALVEG, que no debe operar
    /// ningún Delegated Workspace). Con 2FA activo (P1-13 de
    /// docs/business/MATURITY_REVIEW.md exige 2FA para todo Administrador) y
    /// la misma clave TOTP fija que el resto de la siembra, para que los
    /// tests E2E puedan calcular el código sin acceso a BD.
    /// </summary>
    private static async Task<ApplicationUser> CrearAdministradorConsultoraAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        CredencialesDemo credenciales,
        ILogger logger,
        Guid tenantConsultoraId,
        CancellationToken cancellationToken)
    {
        var existente = await userManager.FindByEmailAsync(EmailAdministradorConsultora);
        if (existente is not null)
            return existente;

        var administrador = new ApplicationUser
        {
            UserName = EmailAdministradorConsultora,
            Email = EmailAdministradorConsultora,
            NombreCompleto = "Administrador ArcoSPA",
            EmailConfirmed = true,
            DebeCambiarContrasena = false,
            TenantId = tenantConsultoraId
        };

        var resultado = await userManager.CreateAsync(administrador, credenciales.Contrasena);
        if (!resultado.Succeeded)
        {
            logger.LogWarning("No se pudo crear el administrador de ArcoSPA: {Errores}",
                string.Join(", ", resultado.Errors.Select(e => e.Description)));
            throw new InvalidOperationException("No se pudo crear el administrador de ArcoSPA.");
        }

        await userManager.AddToRoleAsync(administrador, Roles.Administrador);

        if (userStore is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
        {
            await claveStore.SetAuthenticatorKeyAsync(
                administrador, IdentitySeeder.ClaveTotpAdministradorInicial, cancellationToken);
            await userManager.UpdateAsync(administrador);
        }
        await userManager.SetTwoFactorEnabledAsync(administrador, true);

        using (AmbitoTenantExplicito.Establecer(tenantConsultoraId))
        {
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, administrador.Id, cancellationToken);
        }

        return administrador;
    }

    /// <summary>
    /// Roster de usuarios propio de Refrielectric — no reutiliza los
    /// <c>prueba.&lt;rol&gt;</c> de Laboratorios Dexter (emails únicos, ya
    /// pertenecen a ese tenant) ni la siembra completa de claves API /
    /// filtros guardados / reclamaciones de <see cref="DatosPruebaSeeder"/>
    /// (pensada para un único tenant "principal", no para repetirse). Cubre
    /// los mismos roles con la misma forma de cartera (3 GestorCae, reparto
    /// round-robin de los 9 Clientes) para que Refrielectric sea una
    /// referencia de "empresa final" tan completa como Laboratorios Dexter.
    /// </summary>
    private static async Task<List<ApplicationUser>> SembrarUsuariosRefrielectricAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        CredencialesDemo credenciales,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var tenantId = AmbitoTenantExplicito.TenantIdActual!.Value;

        async Task<ApplicationUser?> CrearAsync(string rol, int indice, string rolVisible)
        {
            var email = $"{PrefijoEmailRefrielectric}{rol.ToLowerInvariant()}{indice}@caemanager.local";
            if (await userManager.FindByEmailAsync(email) is not null)
                return null;

            var usuario = new ApplicationUser
            {
                UserName = email,
                Email = email,
                NombreCompleto = $"Refrielectric {rolVisible} {indice}",
                EmailConfirmed = true,
                DebeCambiarContrasena = false,
                TenantId = tenantId
            };

            var resultado = await userManager.CreateAsync(usuario, credenciales.Contrasena);
            if (!resultado.Succeeded)
            {
                logger.LogWarning("No se pudo crear el usuario de Refrielectric {Email}: {Errores}",
                    email, string.Join(", ", resultado.Errors.Select(e => e.Description)));
                return null;
            }

            await userManager.AddToRoleAsync(usuario, rol);
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, usuario.Id, cancellationToken);
            return usuario;
        }

        var administrador = await CrearAsync(Roles.Administrador, 1, "Administrador");
        if (administrador is not null && userStore is IUserAuthenticatorKeyStore<ApplicationUser> claveStore)
        {
            // P1-13: todo Administrador con 2FA — misma clave fija que el resto de la siembra.
            await claveStore.SetAuthenticatorKeyAsync(administrador, IdentitySeeder.ClaveTotpAdministradorInicial, cancellationToken);
            await userManager.UpdateAsync(administrador);
            await userManager.SetTwoFactorEnabledAsync(administrador, true);
        }

        await CrearAsync(Roles.DireccionCae, 1, "Direccion CAE");
        var coordinador = await CrearAsync(Roles.CoordinadorCae, 1, "Coordinador CAE");

        // El fallback a FindByEmailAsync cubre el caso en que este seeder ya
        // corrió antes: CrearAsync devuelve null si el usuario ya existe, pero
        // el llamador (SeedAsync) necesita la lista de gestores en cualquier
        // caso para sembrar los escenarios de "Pendiente por plataforma" y
        // "Reclamado y sin respuesta" del rediseño de Inicio.
        var gestores = new List<ApplicationUser>();
        for (var i = 1; i <= 3; i++)
        {
            var gestor = await CrearAsync(Roles.GestorCae, i, "Gestor CAE")
                ?? await userManager.FindByEmailAsync($"{PrefijoEmailRefrielectric}gestorcae{i}@caemanager.local");
            if (gestor is not null) gestores.Add(gestor);
        }

        if (coordinador is not null)
        {
            foreach (var gestor in gestores)
            {
                gestor.CoordinadorUsuarioId = coordinador.Id;
                await userManager.UpdateAsync(gestor);
            }
        }

        if (gestores.Count > 0)
        {
            var clientes = await dbContext.Empresas.Where(e => e.EsCritico != null).OrderBy(c => c.CreadoEnUtc).ToListAsync(cancellationToken);
            for (var i = 0; i < clientes.Count; i++)
                clientes[i].AsignarEjecutivo(gestores[i % gestores.Count].Id);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var clientePrueba = await CrearAsync(Roles.Cliente, 1, "Cliente");
        if (clientePrueba is not null)
        {
            var primerCliente = await dbContext.Empresas.Where(e => e.EsCritico != null).OrderBy(c => c.CreadoEnUtc).FirstOrDefaultAsync(cancellationToken);
            if (primerCliente is not null)
            {
                clientePrueba.ClienteId = primerCliente.Id;
                await userManager.UpdateAsync(clientePrueba);
            }
        }

        await CrearAsync(Roles.Consulta, 1, "Consulta");

        logger.LogInformation(
            "Usuarios de Refrielectric sembrados (contraseña «{Contrasena}» para todos, email {Prefijo}<rol><n>@caemanager.local).",
            credenciales.Contrasena, PrefijoEmailRefrielectric);

        return gestores;
    }

    /// <summary>
    /// "Pendiente por plataforma" y "Reclamado y sin respuesta" (rediseño de
    /// Inicio, hallazgos P-04 y el seguimiento de reclamaciones) reutilizan
    /// exactamente la misma lógica de dominio que <see cref="DatosPruebaSeeder"/>
    /// y <see cref="CicloDocumentalDatosPruebaSeeder"/> ya siembran para
    /// Laboratorios Dexter — pero esos dos métodos están pensados para "un
    /// único tenant principal" y nunca se llaman en el bloque de Refrielectric
    /// (ver el comentario de <see cref="SembrarUsuariosRefrielectricAsync"/>).
    /// Con Refrielectric como referencia principal de "empresa final" (y el
    /// tenant real que se usa para verificar el rediseño de Inicio en
    /// vivo), esas dos secciones se quedaban permanentemente vacías —
    /// no por falta de datos base (los CanalGestionDocumental de plataforma y
    /// los Documento sí existen, vía SembrarSoloDatosCompletosAsync), sino
    /// porque nadie derivaba las acreditaciones ni enviaba las reclamaciones
    /// aquí. Guard idempotente propio (no el de los métodos que reutiliza,
    /// pensados para su propio primer-run): sin él, cada reinicio de la app
    /// repetiría el envío de reclamaciones.
    /// </summary>
    private static async Task SembrarEscenariosDashboardRefrielectricAsync(
        CaeManagerDbContext dbContext, List<ApplicationUser> gestores, ILogger logger, CancellationToken cancellationToken)
    {
        if (!await dbContext.ReclamacionesDocumentales.AnyAsync(cancellationToken))
        {
            var clientes = await dbContext.Empresas.Where(e => e.EsCritico != null).OrderBy(c => c.CreadoEnUtc).ToListAsync(cancellationToken);
            await DatosPruebaSeeder.SembrarReclamacionesAsync(dbContext, clientes, gestores.FirstOrDefault()?.Id, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Reclamaciones de demo sembradas para Refrielectric (incluye la variante sin respuesta).");
        }

        // SembrarAcreditacionesAsync elige el primer canal de plataforma por
        // CreadoEnUtc sin mirar de quién es cartera — correcto para
        // Laboratorios Dexter (un único GestorCae de prueba visible). Con el
        // reparto round-robin de Refrielectric entre 3 gestores, ese canal
        // podía caer en la cartera de otro gestor y la sección quedaba vacía
        // para quien la estuviera verificando. Reasignar el ejecutivo del
        // Cliente dueño de ese canal al primer gestor es la misma operación
        // de demo que ya hace el reparto round-robin de
        // SembrarUsuariosRefrielectricAsync — y por eso, igual que él, tiene
        // que repetirse en cada arranque, no solo la primera vez: ese reparto
        // round-robin no tiene guard propio (corre siempre), así que en el
        // siguiente arranque volvía a repartir este Cliente a otro gestor y
        // deshacía la fijación de un guard "solo primera vez" (bug real
        // encontrado en vivo — la sección desaparecía tras reiniciar).
        if (gestores.Count > 0)
        {
            var clienteDelCanal = await (
                from canal in dbContext.CanalesGestionDocumental
                where canal.Tipo == TipoCanalGestion.Plataforma
                orderby canal.CreadoEnUtc, canal.Id
                join centro in dbContext.Centros on canal.CentroId equals centro.Id
                select centro.ClienteId)
                .FirstOrDefaultAsync(cancellationToken);

            if (clienteDelCanal != Guid.Empty)
            {
                var cliente = await dbContext.Empresas.Where(e => e.EsCritico != null).FirstAsync(c => c.Id == clienteDelCanal, cancellationToken);
                if (cliente.EjecutivoUsuarioId != gestores[0].Id)
                {
                    cliente.AsignarEjecutivo(gestores[0].Id);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }
            }
        }

        if (!await dbContext.AcreditacionesDocumentoPlataforma.AnyAsync(cancellationToken))
        {
            await CicloDocumentalDatosPruebaSeeder.SembrarAcreditacionesAsync(dbContext, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Acreditaciones de plataforma de demo sembradas para Refrielectric.");
        }
    }

    /// <summary>
    /// Usuarios de ArcoSPA que operan Delegated Workspaces con roles
    /// distintos de Administrador — sin ellos, el selector de Cliente activo
    /// y "retirar operador" solo podían probarse con el Administrador de
    /// ArcoSPA.
    /// </summary>
    private static async Task SembrarOperadoresConsultoraAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        CredencialesDemo credenciales,
        ILogger logger,
        Guid tenantConsultoraId,
        Guid tenantClienteDemo1Id,
        Guid tenantClienteDemo2Id,
        CancellationToken cancellationToken)
    {
        var operadorGestor = await CrearUsuarioConsultoraAsync(
            dbContext, userManager, credenciales, logger, tenantConsultoraId, "prueba.operador.gestor1@caemanager.local",
            "Operador Consultora Gestor (prueba)", Roles.GestorCae, cancellationToken);
        var operadorConsulta = await CrearUsuarioConsultoraAsync(
            dbContext, userManager, credenciales, logger, tenantConsultoraId, "prueba.operador.consulta1@caemanager.local",
            "Operador Consultora Consulta (prueba)", Roles.Consulta, cancellationToken);

        var delegacionDemo1 = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
            d => d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteDemo1Id
                 && d.Proposito == PropositoDelegacion.Comercial, cancellationToken);
        var delegacionDemo2 = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
            d => d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteDemo2Id
                 && d.Proposito == PropositoDelegacion.Comercial, cancellationToken);

        using (AmbitoTenantExplicito.Establecer(tenantConsultoraId))
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
        CredencialesDemo credenciales,
        ILogger logger,
        Guid tenantConsultoraId,
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
            TenantId = tenantConsultoraId
        };

        var resultado = await userManager.CreateAsync(usuario, credenciales.Contrasena);
        if (!resultado.Succeeded)
        {
            logger.LogWarning("No se pudo crear el operador de consultora {Email}: {Errores}",
                email, string.Join(", ", resultado.Errors.Select(e => e.Description)));
            return null;
        }

        await userManager.AddToRoleAsync(usuario, rol);
        using (AmbitoTenantExplicito.Establecer(tenantConsultoraId))
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
        CredencialesDemo credenciales,
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

            var resultado = await userManager.CreateAsync(usuario, credenciales.Contrasena);
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
        CredencialesDemo credenciales,
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
        var resultado = await userManager.CreateAsync(administrador, credenciales.Contrasena);
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
        resultado = await userManager.CreateAsync(gestor, credenciales.Contrasena);
        if (resultado.Succeeded)
        {
            await userManager.AddToRoleAsync(gestor, Roles.GestorCae);
            await AceptacionTerminosSeedHelper.AceptarParaUsuarioDeSemillaAsync(dbContext, gestor.Id, cancellationToken);

            // Toda la cartera del demo 2 a su único gestor — sin ejecutivo
            // asignado, un GestorCae no vería ningún dato (IAlcanceDatosService).
            var clientes = await dbContext.Empresas.Where(e => e.EsCritico != null).ToListAsync(cancellationToken);
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
        CaeManagerDbContext dbContext, Guid tenantConsultoraId, Guid tenantClienteId, ILogger logger, CancellationToken cancellationToken)
    {
        if (await dbContext.DelegacionesTenant.AnyAsync(
                d => d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteId,
                cancellationToken))
        {
            return;
        }

        var delegacion = new DelegacionTenant(tenantConsultoraId, tenantClienteId);
        delegacion.Desactivar();

        using (AmbitoTenantExplicito.Establecer(tenantConsultoraId))
        {
            dbContext.DelegacionesTenant.Add(delegacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation("Delegación comercial revocada de demo sembrada hacia {TenantCliente}.", tenantClienteId);
    }

    private static async Task<Guid> AprovisionarTenantAsync(
        CaeManagerDbContext dbContext, string nombreTenant, PerfilVocabularioTenant perfil, ILogger logger, CancellationToken cancellationToken)
    {
        var tenantExistente = await dbContext.Tenants
            .FirstOrDefaultAsync(t => t.Nombre == nombreTenant, cancellationToken);
        if (tenantExistente is not null)
            return tenantExistente.Id;

        // DDL-072: el perfil de vocabulario es del tenant que se mira a sí
        // mismo, no de quién lo administra — un Cliente Delegante se ve como
        // Cliente Directo aunque ArcoSPA (Consultora) lo opere en plural.
        var tenant = new Tenant(nombreTenant, perfil);

        // Mismo motivo que SegundoTenantSeeder: hace falta un tenant
        // resuelto ya para este primer guardado (el interceptor de
        // auditoría necesita sellar contra algo), antes incluso de que
        // el propio Tenant exista en la base de datos — el Id ya se
        // conoce porque se genera en el constructor (ver Entity).
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            dbContext.Tenants.Add(tenant);

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

        logger.LogInformation("Tenant de demo sembrado: {Nombre} ({TenantId}).", nombreTenant, tenant.Id);
        return tenant.Id;
    }

    private static async Task CrearDelegacionAsync(
        CaeManagerDbContext dbContext,
        Guid tenantConsultoraId,
        Guid tenantClienteId,
        ApplicationUser administradorConsultora,
        ILogger logger,
        string nombreTenantCliente,
        CancellationToken cancellationToken)
    {
        if (await dbContext.DelegacionesTenant.AnyAsync(
                d => d.TenantConsultoraId == tenantConsultoraId && d.TenantClienteId == tenantClienteId,
                cancellationToken))
        {
            return;
        }

        var delegacion = new DelegacionTenant(tenantConsultoraId, tenantClienteId);

        using (AmbitoTenantExplicito.Establecer(tenantConsultoraId))
        {
            dbContext.DelegacionesTenant.Add(delegacion);
            await dbContext.SaveChangesAsync(cancellationToken);

            var asignacion = new AsignacionOperadorDelegado(delegacion.Id, administradorConsultora.Id, RolOperadorDelegadoDemo);
            dbContext.AsignacionesOperadorDelegado.Add(asignacion);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        logger.LogInformation(
            "Delegated Workspace de demo sembrado: {Administrador} puede operar {TenantCliente} como {Rol}.",
            administradorConsultora.Email, nombreTenantCliente, RolOperadorDelegadoDemo);
    }
}
