using CaeManager.Domain.Common;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra de verificación E2E del recorrido de un Gestor CAE de un Operador CAE
/// externo: un Operador CAE externo propio, un Gestor CAE suyo con una Asignación
/// de Cartera vigente en dos Tenants beneficiarios —colgada de una Asignación de
/// Operación externa vigente, no de la raíz— y un tercer Tenant beneficiario que
/// ese mismo Operador CAE externo opera pero que queda fuera de la cartera del
/// Gestor CAE (control negativo). En el primer Tenant beneficiario hay además un
/// Cliente empresarial sin Asignación de Cartera: lo que tenga no debe llegar a
/// la cola del Gestor CAE.
///
/// <para>
/// Cada Tenant beneficiario de la cartera recibe lo justo para el recorrido: su
/// Empresa propia (el modelo es egocéntrico por Tenant: la contratista es el propio
/// Tenant beneficiario), un Cliente empresarial con un Centro con canal de
/// plataforma y una Visita próxima, y un Trabajador de la Empresa propia con un
/// documento cuya acreditación está
/// <c>Rechazada</c>, y otro a punto de vencer (<c>Proximo</c>) cuya acreditación
/// está aceptada con una vigencia en plataforma ya pasada (<c>Vencida en
/// plataforma</c>, la fila que se retira confirmando la vigencia a mano).
/// </para>
///
/// <para>
/// El Cliente empresarial sin cartera del primer Tenant beneficiario tiene su Centro
/// atendido por plantilla de una Subcontrata, no de la Empresa propia: por la
/// decisión D-8 (piloto Outbound) un Gestor CAE con cartera en el Tenant ve toda la
/// plantilla propia, así que un Trabajador propio en ese Centro sería visible por
/// derecho y el control negativo no mediría la frontera de la cartera.
/// </para>
///
/// <para>
/// <b>Inerte por defecto.</b> Corre solo con <c>DatosPrueba:Activo</c> y
/// <see cref="ClaveConfiguracion"/> a la vez; la activa únicamente la fixture E2E
/// que la necesita, para que el resto de colecciones no vea tres Tenants más. En
/// Producción lanza aunque las dos claves estén activas: comparte la contraseña
/// pública de <see cref="CredencialesDemo"/>.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS ni autorización: cada escritura va dentro del
/// <see cref="AmbitoTenantExplicito"/> de su Tenant propietario, y la operación y
/// la cartera pasan por el mismo <see cref="AsignacionesOperativasWriter"/> que usa
/// la aplicación. Mismo orden que <see cref="EscenariosDireccionDemoSeeder"/>:
/// delegación y asignación del operador delegado primero (el escritor lee de ahí
/// el rol delegado), después la operación externa y por último la cartera.
/// </para>
/// </summary>
public static class GestorCaeCarteraMultiTenantSeeder
{
    public const string ClaveConfiguracion = "DatosPrueba:GestorCaeCarteraMultiTenant";

    public const string NombreTenantOperador = "Gestoría Levante CAE S.L. (Operador CAE externo E2E)";
    public const string EmailGestorCae = "gestor.cartera.e2e@caemanager.local";

    private const string AptitudMedica = "Certificado de aptitud médica";
    private const string FormacionArt19 = "Formación Art. 19";

    /// <summary>Días hasta el vencimiento del documento «a punto de vencer»: dentro del umbral ámbar (30) y fuera del rojo (15).</summary>
    private const int DiasProximo = 27;

    /// <param name="Empleador">Empresa propia del Tenant, o la Subcontrata si <paramref name="EmpleadorEsSubcontrata"/>.</param>
    private sealed record RamaE2E(
        string NombreTenant, bool EnCartera, string Cliente, string Empleador, string Centro, string CodigoCentro,
        string Nombre, string Apellidos, int Indice, bool EmpleadorEsSubcontrata = false);

    private static readonly RamaE2E[] Ramas =
    [
        new("Conservas Albatros S.L. (Tenant beneficiario E2E)", EnCartera: true,
            "Astilleros Cantábrico S.A.", "Conservas Albatros S.L.", "Nave Albatros Gijón", "GCM-A1", "Nerea", "Castany Olmo", 0),
        new("Talleres Boreal S.A. (Tenant beneficiario E2E)", EnCartera: true,
            "Harinas del Duero S.A.", "Talleres Boreal S.A.", "Planta Boreal Burgos", "GCM-B1", "Bruno", "Ledesma Pardo", 1),
        new("Minería Cierzo S.L. (fuera de cartera E2E)", EnCartera: false,
            "Áridos Moncayo S.A.", "Minería Cierzo S.L.", "Mina Cierzo Teruel", "GCM-C1", "Samuel", "Oria Benet", 2),
    ];

    /// <summary>
    /// Los cuatro Tenants que aprovisiona esta siembra (el del Operador CAE externo y los tres
    /// beneficiarios), para la allowlist de <see cref="RetiradaTenantDemoService.NombresTenantsDeDemo"/>:
    /// si la clave se activa sobre una base que no es efímera, la retirada de demo los puede borrar.
    /// </summary>
    public static readonly IReadOnlyList<string> NombresTenants =
        [NombreTenantOperador, .. Ramas.Select(r => r.NombreTenant)];

    /// <summary>Cliente empresarial del primer Tenant beneficiario sin Asignación de Cartera de nadie.</summary>
    private static readonly RamaE2E FueraDeCarteraEnTenantA = new(
        Ramas[0].NombreTenant, EnCartera: false,
        "Pescados Vilches S.L.", "Frío Industrial Lugo S.L.", "Taller Vilches Lugo", "GCM-A2", "Aitana", "Vilches Roca", 3,
        EmpleadorEsSubcontrata: true);

    public static void RechazarEnProduccion(IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!EstaActiva(configuration))
            return;

        if (entorno.IsProduction())
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} no puede activarse en Producción: es una siembra de verificación E2E con la " +
                "contraseña compartida de la demo local (CredencialesDemo).");
    }

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!EstaActiva(configuration))
            return;

        RechazarEnProduccion(configuration, entorno);

        var credenciales = CredencialesDemo.Resolver(configuration, entorno);

        var tenantOperadorId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
            dbContext, NombreTenantOperador, PerfilVocabularioTenant.Consultora, logger, cancellationToken,
            esOperadorCaeExterno: true);

        var gestor = await DelegacionDemoSeeder.CrearUsuarioConsultoraAsync(
                         dbContext, userManager, credenciales, logger, tenantOperadorId, EmailGestorCae,
                         "Olga Serrano (Gestora CAE, E2E)", Roles.GestorCae, cancellationToken)
                     ?? throw new InvalidOperationException($"No se pudo sembrar el Gestor CAE {EmailGestorCae}.");

        var hoy = DiaDeNegocio.Hoy();

        foreach (var rama in Ramas)
        {
            var tenantBeneficiarioId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
                dbContext, rama.NombreTenant, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken,
                esOperadorCaeExterno: false);

            await AbrirOperacionHeredadaAsync(dbContext, tenantOperadorId, tenantBeneficiarioId, rama.EnCartera ? gestor : null, cancellationToken);

            using (AmbitoTenantExplicito.Establecer(tenantBeneficiarioId))
            {
                var writer = new AsignacionesOperativasWriter(
                    dbContext, new TenantActualAmbiental { TenantId = tenantBeneficiarioId }, new EscenariosDireccionDemoSeeder.ActorDeSiembra());

                await writer.AbrirOperacionDelegadaAsync(
                    tenantBeneficiarioId, tenantOperadorId, DateTime.UtcNow.AddDays(-30), vigenciaHasta: null, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);

                var clienteId = await SembrarClienteAsync(dbContext, rama, rama.EnCartera ? gestor.Id : null, hoy, cancellationToken);
                if (rama.EnCartera)
                {
                    await writer.ReasignarCarteraClienteAsync(clienteId, gestor.Id, cancellationToken);
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                if (rama == Ramas[0])
                    await SembrarClienteAsync(dbContext, FueraDeCarteraEnTenantA, ejecutivoId: null, hoy, cancellationToken);
            }

            logger.LogInformation(
                "Rama E2E de Gestor CAE con cartera multi-Tenant sembrada: {Tenant} (en cartera: {EnCartera}).",
                rama.NombreTenant, rama.EnCartera);
        }
    }

    private static bool EstaActiva(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveConfiguracion);

    /// <summary>
    /// La delegación del Tenant beneficiario al Operador CAE externo, bajo el ámbito
    /// del Operador (le pertenece). Con Gestor CAE, además su asignación de operador
    /// delegado; sin él (Tenant fuera de cartera), la delegación queda sin nadie del
    /// Operador que la opere como Gestor CAE.
    /// </summary>
    private static async Task AbrirOperacionHeredadaAsync(
        CaeManagerDbContext dbContext, Guid tenantOperadorId, Guid tenantBeneficiarioId, ApplicationUser? gestor,
        CancellationToken cancellationToken)
    {
        using (AmbitoTenantExplicito.Establecer(tenantOperadorId))
        {
            var delegacion = await dbContext.DelegacionesTenant.FirstOrDefaultAsync(
                d => d.TenantConsultoraId == tenantOperadorId && d.TenantClienteId == tenantBeneficiarioId, cancellationToken);
            if (delegacion is null)
            {
                delegacion = new DelegacionTenant(tenantOperadorId, tenantBeneficiarioId);
                dbContext.DelegacionesTenant.Add(delegacion);
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (gestor is null
                || await dbContext.AsignacionesOperadorDelegado.AnyAsync(
                    a => a.DelegacionTenantId == delegacion.Id && a.UsuarioId == gestor.Id, cancellationToken))
                return;

            dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(
                new AsignacionOperadorDelegado(delegacion.Id, gestor.Id, Roles.GestorCae));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Un Cliente empresarial con todo lo del recorrido, en un único guardado. Idempotente
    /// por razón social del Cliente empresarial, como <see cref="EscenariosDireccionDemoSeeder"/>.
    /// </summary>
    private static async Task<Guid> SembrarClienteAsync(
        CaeManagerDbContext dbContext, RamaE2E rama, Guid? ejecutivoId, DateOnly hoy, CancellationToken cancellationToken)
    {
        var existente = await dbContext.Empresas
            .Where(e => e.EsCritico != null && e.RazonSocial == rama.Cliente)
            .Select(e => (Guid?)e.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (existente is { } id)
            return id;

        var tipos = await dbContext.TiposDocumento.ToListAsync(cancellationToken);
        var proveedor = await dbContext.ProveedoresPlataformaCae
                            .Where(p => p.Activo).OrderBy(p => p.Codigo).FirstOrDefaultAsync(cancellationToken)
                        ?? throw new InvalidOperationException(
                            "La siembra E2E del Gestor CAE necesita un proveedor de plataforma CAE activo para las acreditaciones.");

        var ahora = DateTime.UtcNow;
        var baseCif = 7_500_000 + rama.Indice * 10;

        var cliente = Empresa.CrearComoCliente(
            rama.Cliente, DatosPruebaSeeder.GenerarCifValido(baseCif), esCritico: false, notas: null, ejecutivoUsuarioId: ejecutivoId);
        dbContext.Empresas.Add(cliente);

        // Una sola Empresa propia por Tenant: la del primer Cliente empresarial la reutiliza el segundo.
        var propia = await dbContext.Empresas.FirstOrDefaultAsync(e => e.EsPropia, cancellationToken);
        if (propia is null)
        {
            propia = new Empresa(rama.Empleador, DatosPruebaSeeder.GenerarCifValido(baseCif + 1));
            dbContext.Empresas.Add(propia);
        }

        var relacionPropia = RelacionEmpresarial.Crear(propia.Id, cliente.Id, ahora);
        dbContext.RelacionesEmpresariales.Add(relacionPropia);

        Empresa? subcontrata = null;
        if (rama.EmpleadorEsSubcontrata)
        {
            subcontrata = Empresa.CrearComoSubcontrata(
                rama.Empleador, DatosPruebaSeeder.GenerarCifValido(baseCif + 2), NivelServicioSubcontrata.Gestionada.ToString());
            dbContext.Empresas.Add(subcontrata);
            dbContext.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Crear(subcontrata.Id, propia.Id, ahora),
                RelacionEmpresarial.Crear(subcontrata.Id, cliente.Id, ahora, relacionPropia.Id));
        }

        var centro = new Centro(
            cliente.Id, propia.Id, nombre: rama.Centro, codigoCentro: rama.CodigoCentro,
            direccion: $"Polígono {rama.Centro}, nave 1", contacto: null, contratoVigenteHasta: hoy.AddDays(300));
        dbContext.Centros.Add(centro);

        var canal = CanalGestionDocumental.DePlataforma(
            centro.Id, "Gestión general", proveedor.Id,
            $"https://portal-e2e-{proveedor.Codigo}.local", $"gestion.e2e{rama.Indice}", "sin-credencial-real");
        canal.MarcarComoPrincipal();
        dbContext.CanalesGestionDocumental.Add(canal);

        var dni = DatosPruebaSeeder.GenerarDniValido(61_000_000 + rama.Indice);
        var trabajador = subcontrata is null
            ? Trabajador.DeEmpresa(propia.Id, rama.Nombre, rama.Apellidos, dni, fechaNacimiento: hoy.AddYears(-35))
            : Trabajador.DeSubcontrata(subcontrata.Id, rama.Nombre, rama.Apellidos, dni, fechaNacimiento: hoy.AddYears(-35));
        dbContext.Trabajadores.Add(trabajador);
        dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, hoy.AddDays(-120)));

        // Rechazada: la plataforma devolvió la formación y hay que subir una versión corregida.
        var tipoFormacion = tipos.Single(t => t.Nombre == FormacionArt19);
        var vencimientoFormacion = hoy.AddDays(200);
        var formacion = Documento.DeTrabajador(
            trabajador.Id, tipoFormacion.Id, vencimientoFormacion.AddMonths(-(tipoFormacion.VigenciaMeses ?? 12)),
            VigenciaDocumento.VenceEl(vencimientoFormacion));
        var acreditacionRechazada = new AcreditacionDocumentoPlataforma(formacion.Id, canal.Id);
        acreditacionRechazada.MarcarSubida();
        acreditacionRechazada.Rechazar(
            CausaRechazoAcreditacion.Ilegible, "El certificado llega escaneado sin la firma del formador.", ahora.AddDays(-2));

        // A punto de vencer en TALVEG, y aceptado en plataforma con una vigencia allí ya pasada.
        var tipoAptitud = tipos.Single(t => t.Nombre == AptitudMedica);
        var vencimientoAptitud = hoy.AddDays(DiasProximo);
        var aptitud = Documento.DeTrabajador(
            trabajador.Id, tipoAptitud.Id, vencimientoAptitud.AddMonths(-(tipoAptitud.VigenciaMeses ?? 12)),
            VigenciaDocumento.VenceEl(vencimientoAptitud));
        var acreditacionVencida = new AcreditacionDocumentoPlataforma(aptitud.Id, canal.Id);
        acreditacionVencida.MarcarSubida();
        acreditacionVencida.MarcarAceptada(VigenciaEnPlataforma.VenceEl(hoy.AddDays(-5)));

        dbContext.Documentos.AddRange(formacion, aptitud);
        dbContext.AcreditacionesDocumentoPlataforma.AddRange(acreditacionRechazada, acreditacionVencida);

        // Mañana: dentro de las 48 h de aviso por defecto, así que entra en la cola como «Visita próxima».
        var visita = new Visita(centro.Id, hoy.AddDays(1), hoy.AddDays(1), notas: null, OrigenVisita.Plataforma);
        dbContext.Visitas.Add(visita);
        dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajador.Id));

        await dbContext.SaveChangesAsync(cancellationToken);
        return cliente.Id;
    }
}
