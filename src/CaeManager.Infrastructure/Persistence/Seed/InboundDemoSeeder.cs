using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Operaciones;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Aprovisionamiento = CaeManager.Infrastructure.Persistence.Seed.DelegacionDemoSeeder;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// Siembra de <b>Development</b> de la línea Inbound, para que las pantallas Inbound ya en Blazor enseñen datos al arrancar
/// la aplicación en local en vez de estados vacíos. Deja un Tenant TCE, el Cliente empresarial que exige documentación
/// (<see cref="NombreTenantTce"/>), y tres Tenants TEC, las Empresas contratistas, cada uno con su Usuario único.
///
/// <list type="bullet">
/// <item><b>Tenant TCE</b>: su Administrador y una Gestora CAE interna con la Asignación de Cartera del Tenant entero; el
/// Cliente empresarial titular de tres Centros; las Empresas contraparte locales que representan a cada Empresa
/// contratista; contratos de acceso en cada estado de B-05 (Activo con condiciones y renovación en el historial, Pendiente de
/// confirmar, Suspendido, Finalizado, Cancelado, Rechazado y uno Activo sin vincular con su Tenant TEC); y dos plantillas
/// de requisitos de B-14 (las de un Centro y las exigencias del Cliente empresarial).</item>
/// <item><b>Tenants TEC</b>: la Empresa propia, Trabajadores con su documentación en varios estados de vigencia y, donde el
/// contrato lo admite, la autorización de lectura al Tenant TCE (una vigente con Trabajadores y una revocada).</item>
/// </list>
///
/// <para>
/// <b>Lo que no siembra, y por qué</b> (solo lo que el backend en main ya modela):
/// <list type="bullet">
/// <item>La línea Inbound del Producto Contratado: solo la escribe <c>cae_app_aprovisionamiento</c> bajo una Sesión
/// Privilegiada de Aprovisionamiento (<c>ContratarLineaInboundCommand</c>, B-04a); cae_app_runtime, la identidad de esta
/// siembra fuera de la base local, solo la lee. Sin ella el gate DN-7 niega la lectura entre Tenants.</item>
/// <item>El papel Inbound de la Asignación de Operación (B-02b): la cartera de la Gestora CAE cuelga de la operación raíz que
/// existe hoy, sin servicio Inbound.</item>
/// <item>El catálogo base de B-13: es global, ningún rol de la aplicación lo escribe y hoy solo tiene requisitos inactivos
/// pendientes de revisión legal. Los requisitos sembrados son propios del Tenant TCE o exigencias del Cliente empresarial,
/// sin requisito base.</item>
/// <item>Las evidencias Inbound (validado, rechazado): solo nacen de leer el documento original del Tenant TEC a través del
/// contrato y la autorización (<c>RegistrarEvidenciaInboundCommand</c>), y su modelo no tiene «por validar», «incompleto» ni
/// «vencido en tolerancia». Los documentos originales del Tenant TEC sí se siembran, en varios estados de vigencia.</item>
/// <item>Confirmar o rechazar un contrato por el Tenant TEC no tiene Command todavía (B-09): el Rechazado usa el método de
/// dominio <see cref="ContratoAcceso.RechazarPorTenantContratista"/>.</item>
/// </list>
/// </para>
///
/// <para>
/// <b>Inerte por defecto.</b> Corre solo con <c>DatosPrueba:Activo</c> y <see cref="ClaveConfiguracion"/>, y únicamente en
/// Development: comparte la contraseña pública de <see cref="CredencialesDemo"/>, así que cualquier otro entorno lanza antes
/// de escribir nada. No envía correo: las cuentas son de un dominio no entregable (<c>@caemanager.local</c>). Idempotente
/// por Tenant.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS ni autorización. Cada fila se escribe dentro del <see cref="AmbitoTenantExplicito"/> del
/// Tenant que la posee: el contrato de acceso, su historial y las plantillas en el Tenant TCE; la autorización de lectura,
/// en el Tenant TEC que la concede. Ninguna escritura cruza de Tenant: el Tenant TEC del contrato y el contrato de la
/// autorización son Ids, como en el producto. La cartera pasa por el mismo <see cref="AsignacionesOperativasWriter"/> que la
/// aplicación. Es Composición en el registro de módulos: compone el aprovisionamiento de la demo con las tablas Inbound.
/// </para>
/// </summary>
public static class InboundDemoSeeder
{
    public const string ClaveConfiguracion = "DatosPrueba:Inbound:Activo";

    public const string NombreTenantTce = "Logística Delta Sur S.A. (Inbound demo)";
    public const string NombreTenantMoncayo = "Montajes Moncayo S.L. (Inbound demo)";
    public const string NombreTenantRibera = "Electricidad Ribera Baja S.L. (Inbound demo)";
    public const string NombreTenantSomontano = "Limpiezas Somontano S.L. (Inbound demo)";

    /// <summary>Para la allowlist de <see cref="RetiradaTenantDemoService.NombresTenantsDeDemo"/>.</summary>
    public static readonly IReadOnlyList<string> NombresTenants =
        [NombreTenantTce, NombreTenantMoncayo, NombreTenantRibera, NombreTenantSomontano];

    public const string EmailAdministradorTce = "admin.deltasur.inbound@caemanager.local";
    public const string EmailGestoraTce = "gestora.deltasur.inbound@caemanager.local";
    public const string EmailUsuarioMoncayo = "usuario.moncayo.inbound@caemanager.local";
    public const string EmailUsuarioRibera = "usuario.ribera.inbound@caemanager.local";
    public const string EmailUsuarioSomontano = "usuario.somontano.inbound@caemanager.local";

    public const string ClienteEmpresarial = "Logística Delta Sur S.A.";
    public const string Moncayo = "Montajes Moncayo S.L.";
    public const string Ribera = "Electricidad Ribera Baja S.L.";
    public const string Somontano = "Limpiezas Somontano S.L.";

    public const string CentroZaragoza = "Plataforma logística Zaragoza Norte";
    public const string CentroHuesca = "Almacén regulador Huesca";
    public const string CentroTeruel = "Centro de cross-docking Teruel";

    /// <summary>
    /// Las notas de cada contrato sembrado, únicas en el Tenant TCE: con ellas la siembra reencuentra sus contratos en otro
    /// arranque (el Tenant TEC necesita su Id para la autorización) sin una columna propia.
    /// </summary>
    public static class Contratos
    {
        public const string MoncayoActivo = "Montaje y revisión de estanterías de la nave 2.";
        public const string MoncayoFinalizado = "Ampliación del muelle de carga (obra terminada).";
        public const string MoncayoSinVincular = "Mantenimiento de puertas seccionales.";
        public const string RiberaPendiente = "Revisión de la instalación eléctrica de baja tensión.";
        public const string RiberaSuspendido = "Sustitución de luminarias del almacén.";
        public const string SomontanoRechazado = "Limpieza industrial de la zona de cross-docking.";
        public const string SomontanoCancelado = "Limpieza de fin de obra (no llegó a empezar).";

        public static readonly IReadOnlyList<string> Todas =
            [MoncayoActivo, MoncayoFinalizado, MoncayoSinVincular, RiberaPendiente, RiberaSuspendido, SomontanoRechazado, SomontanoCancelado];
    }

    // Tipos del catálogo de serie, copiado a cada Tenant al aprovisionarlo.
    private const string AptitudMedica = "Certificado de aptitud médica";
    private const string EntregaEpi = "Entrega de EPI";
    private const string FormacionArt19 = "Formación PRL del puesto";
    private const string Rnt = "RNT";
    private const string CorrienteSeguridadSocial = "Certificado de estar al corriente con la Seguridad Social";
    private const string CorrienteHacienda = "Certificado de estar al corriente con Hacienda";
    private const string SeguroResponsabilidadCivil = "Seguro de Responsabilidad Civil";

    /// <summary>Días de tolerancia de los requisitos que la admiten (R-6 la niega a salud, formación PRL y seguros).</summary>
    public const int ToleranciaDias = 15;

    public static bool EstaActiva(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveConfiguracion);

    /// <summary>
    /// La guarda, aparte de <see cref="SeedAsync"/> para que el arranque la invoque antes de cualquier escritura. Lanza en
    /// cualquier entorno que no sea Development, Staging incluido. Inerte si la clave no está activa.
    /// </summary>
    public static void RechazarFueraDeDevelopment(IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!EstaActiva(configuration))
            return;

        if (!entorno.IsDevelopment())
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} solo puede activarse en Development (entorno actual: {entorno.EnvironmentName}): " +
                "siembra datos Inbound con la contraseña pública de la demo local (CredencialesDemo).");
    }

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IUserStore<ApplicationUser> userStore,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!EstaActiva(configuration))
            return;

        RechazarFueraDeDevelopment(configuration, entorno);

        var credenciales = CredencialesDemo.Resolver(configuration, entorno);
        var hoy = DiaDeNegocio.Hoy();

        async Task<ApplicationUser> UsuarioAsync(Guid tenantId, string email, string nombre, string rol, bool segundoFactor)
        {
            var usuario = await Aprovisionamiento.CrearUsuarioConsultoraAsync(
                              dbContext, userManager, credenciales, logger, tenantId, email, nombre, rol, cancellationToken)
                          ?? throw new InvalidOperationException($"No se pudo sembrar el usuario Inbound {email}.");

            // P1-13: todo Administrador lleva 2FA. Solo si aún no la tiene, para que otro arranque no reescriba la cuenta.
            if (segundoFactor && !usuario.TwoFactorEnabled)
                using (AmbitoTenantExplicito.Establecer(tenantId))
                    await IdentitySeeder.AsignarSegundoFactorDeSiembraAsync(usuario, userManager, userStore, entorno, cancellationToken);

            return usuario;
        }

        async Task<Guid> TenantAsync(string nombre) =>
            await Aprovisionamiento.AprovisionarTenantAsync(
                dbContext, nombre, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken,
                esOperadorCaeExterno: false, marcarComoDemo: true);

        // ── Tenants y cuentas ─────────────────────────────────────────────────────────────────────────────────────
        var tceId = await TenantAsync(NombreTenantTce);
        var administradorTce = await UsuarioAsync(
            tceId, EmailAdministradorTce, "Administración Logística Delta Sur (Inbound demo)", Roles.Administrador, segundoFactor: true);
        var gestoraTce = await UsuarioAsync(
            tceId, EmailGestoraTce, "Gestora CAE Logística Delta Sur (Inbound demo)", Roles.GestorCae, segundoFactor: false);

        var tecs = new List<TenantTec>();
        foreach (var (nombreTenant, razonSocial, email, indice) in new[]
                 {
                     (NombreTenantMoncayo, Moncayo, EmailUsuarioMoncayo, 0),
                     (NombreTenantRibera, Ribera, EmailUsuarioRibera, 1),
                     (NombreTenantSomontano, Somontano, EmailUsuarioSomontano, 2),
                 })
        {
            var tenantId = await TenantAsync(nombreTenant);
            // El Usuario único del Tenant TEC es su Administrador: con 2FA de siembra, como todo Administrador.
            var usuario = await UsuarioAsync(
                tenantId, email, $"Usuario único {razonSocial} (Inbound demo)", Roles.Administrador, segundoFactor: true);
            tecs.Add(new TenantTec(tenantId, indice, razonSocial, DatosPruebaSeeder.GenerarCifValido(8_300_001 + indice), usuario.Id));
        }

        // ── Tenant TCE ───────────────────────────────────────────────────────────────────────────────────────────
        Dictionary<string, Guid> contratos;
        using (AmbitoTenantExplicito.Establecer(tceId))
        {
            var writer = new AsignacionesOperativasWriter(
                dbContext, new TenantActualAmbiental { TenantId = tceId }, new EscenariosDireccionDemoSeeder.ActorDeSiembra());
            await writer.AsegurarOperacionRaizAsync(tceId, DateTime.UtcNow.AddDays(-180), cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            await writer.AsegurarCarteraTenantEnteroAsync(tceId, gestoraTce.Id, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!await dbContext.ContratosAcceso.AnyAsync(cancellationToken))
                await new ConstructorTce(dbContext, hoy, administradorTce.Id, tecs).ConstruirAsync(cancellationToken);

            contratos = await dbContext.ContratosAcceso
                .Where(c => c.Notas != null && Contratos.Todas.Contains(c.Notas))
                .ToDictionaryAsync(c => c.Notas!, c => c.Id, cancellationToken);
            dbContext.ChangeTracker.Clear();
        }

        // ── Tenants TEC ──────────────────────────────────────────────────────────────────────────────────────────
        foreach (var tec in tecs)
            using (AmbitoTenantExplicito.Establecer(tec.Id))
            {
                if (!await dbContext.Trabajadores.AnyAsync(cancellationToken))
                    await new ConstructorTec(dbContext, hoy, tec, tceId, contratos).ConstruirAsync(cancellationToken);

                dbContext.ChangeTracker.Clear();
            }

        logger.LogInformation(
            "Siembra Inbound de Development lista: {Tce} y sus Tenants TEC {Tecs}.", NombreTenantTce, string.Join(", ", tecs.Select(t => t.RazonSocial)));
    }

    private sealed record TenantTec(Guid Id, int Indice, string RazonSocial, string Cif, Guid UsuarioUnicoId);

    private static async Task<Dictionary<string, TipoDocumento>> TiposAsync(CaeManagerDbContext db, CancellationToken cancellationToken) =>
        await db.TiposDocumento.ToDictionaryAsync(t => t.Nombre, cancellationToken);

    private static TipoDocumento Tipo(Dictionary<string, TipoDocumento> tipos, string nombre) =>
        tipos.TryGetValue(nombre, out var tipo)
            ? tipo
            : throw new InvalidOperationException($"El catálogo del Tenant no tiene el Tipo de documento «{nombre}».");

    /// <summary>El Tenant TCE: Cliente empresarial, Centros, contrapartes locales, contratos de acceso y plantillas.</summary>
    private sealed class ConstructorTce(CaeManagerDbContext db, DateOnly hoy, Guid administradorId, IReadOnlyList<TenantTec> tecs)
    {
        private readonly DateTime _ahora = DateTime.UtcNow;

        public async Task ConstruirAsync(CancellationToken cancellationToken)
        {
            var tipos = await TiposAsync(db, cancellationToken);

            var cliente = Empresa.CrearComoCliente(
                ClienteEmpresarial, DatosPruebaSeeder.GenerarCifValido(8_300_000), esCritico: true, notas: null, ejecutivoUsuarioId: null);
            db.Empresas.Add(cliente);

            // Cada Empresa contratista tiene en el Tenant TCE su Empresa contraparte local, con el mismo CIF que su Empresa
            // propia en el Tenant TEC: es la que nombra el contrato (EmpresaContratistaId), no el Tenant TEC.
            var contrapartes = tecs.ToDictionary(
                t => t.RazonSocial,
                t => Empresa.CrearComoSubcontrata(t.RazonSocial, t.Cif, NivelServicioSubcontrata.Gestionada.ToString()));
            db.Empresas.AddRange(contrapartes.Values);

            Centro NuevoCentro(string nombre, string codigo, string direccion) =>
                new(cliente.Id, cliente.Id, nombre: nombre, codigoCentro: codigo, direccion: direccion,
                    contacto: null, contratoVigenteHasta: null);
            var zaragoza = NuevoCentro(CentroZaragoza, "ZGZ-N", "Polígono Plaza, calle B, 50197 Zaragoza");
            var huesca = NuevoCentro(CentroHuesca, "HU-R", "Polígono Sepes, nave 14, 22006 Huesca");
            var teruel = NuevoCentro(CentroTeruel, "TE-X", "Plataforma Platea, parcela 7, 44195 Teruel");
            db.Centros.AddRange(zaragoza, huesca, teruel);
            await db.SaveChangesAsync(cancellationToken);

            Guid TecDe(string razonSocial) => tecs.Single(t => t.RazonSocial == razonSocial).Id;

            ContratoAcceso Contrato(
                string razonSocial, Centro centro, string notas, DateOnly inicio, DateOnly? fin,
                CondicionesContratoAcceso? condiciones = null, bool pendiente = false, bool vincular = true)
            {
                var contrato = ContratoAcceso.Crear(
                    cliente.Id, centro.Id, contrapartes[razonSocial].Id, inicio, fin, notas, condiciones, pendienteDeConfirmar: pendiente);
                if (vincular)
                    contrato.VincularTenantContratista(TecDe(razonSocial));
                db.ContratosAcceso.Add(contrato);
                return contrato;
            }

            // Activo con todas sus condiciones y dos cambios de fin en el historial (una edición y una renovación).
            var activo = Contrato(
                Moncayo, zaragoza, Contratos.MoncayoActivo, hoy.AddDays(-120), hoy.AddDays(30),
                new CondicionesContratoAcceso(
                    TipoContratoAcceso.Recurrente, PropiaActividadContrato.Si, nivelMaxSubcontrata: 1,
                    cupoTrabajadores: 8, cupoSubcontratas: 1, motivoCupo: "Aforo de la nave 2 durante el montaje."));
            activo.Actualizar(hoy.AddDays(60), activo.Notas, administradorId, _ahora.AddDays(-40));
            activo.Renovar(hoy.AddDays(245), administradorId, _ahora.AddDays(-5));

            var finalizado = Contrato(
                Moncayo, huesca, Contratos.MoncayoFinalizado, hoy.AddDays(-300), hoy.AddDays(-20),
                new CondicionesContratoAcceso(TipoContratoAcceso.Obra, PropiaActividadContrato.No, 0, null, null, null));
            finalizado.Finalizar(hoy.AddDays(-20));

            // Activo, indefinido, sin condiciones fijadas y sin vincular con el Tenant TEC.
            Contrato(Moncayo, teruel, Contratos.MoncayoSinVincular, hoy.AddDays(-60), null, vincular: false);

            Contrato(
                Ribera, zaragoza, Contratos.RiberaPendiente, hoy.AddDays(-3), hoy.AddDays(180),
                new CondicionesContratoAcceso(TipoContratoAcceso.Temporal, PropiaActividadContrato.NoLoSe, 2, null, null, null),
                pendiente: true);

            var suspendido = Contrato(
                Ribera, huesca, Contratos.RiberaSuspendido, hoy.AddDays(-45), hoy.AddDays(90),
                new CondicionesContratoAcceso(TipoContratoAcceso.Puntual, PropiaActividadContrato.No, 0, 4, null, "Turno de noche con aforo reducido."));
            suspendido.Suspender();

            var rechazado = Contrato(Somontano, teruel, Contratos.SomontanoRechazado, hoy.AddDays(-10), hoy.AddDays(355), pendiente: true);
            rechazado.RechazarPorTenantContratista();

            var cancelado = Contrato(Somontano, zaragoza, Contratos.SomontanoCancelado, hoy.AddDays(30), hoy.AddDays(45));
            cancelado.Cancelar(hoy, tieneActividad: false);

            Plantillas(tipos, cliente, zaragoza);
            await db.SaveChangesAsync(cancellationToken);
        }

        private void Plantillas(Dictionary<string, TipoDocumento> tipos, Empresa cliente, Centro centro)
        {
            AtributosRequisito Atributos(
                TipoDocumento tipo, OrigenRequisito origen, ModoVigenciaRequisito modo, int? meses, bool bloquea, string? criterio,
                AmbitoDocumentoRequisito ambito) =>
                new(origen, modo, meses,
                    ToleranciaDias: modo != ModoVigenciaRequisito.SinVencimiento && ReglaToleranciaRequisito.AdmiteTolerancia(tipo) ? ToleranciaDias : null,
                    BloqueaAcceso: bloquea, Criterio: criterio, SePideA: DestinatarioRequisito.Todos,
                    TiposContratacion: TiposContratacion.Todos, Equivalencias: [], AmbitoDocumento: ambito);

            void Requisito(PlantillaRequisitos plantilla, string nombreTipo, OrigenRequisito origen, ModoVigenciaRequisito modo,
                int? meses, bool bloquea, string? criterio, AmbitoDocumentoRequisito ambito)
            {
                var tipo = Tipo(tipos, nombreTipo);
                plantilla.AnadirRequisito(tipo, requisitoBase: null, Atributos(tipo, origen, modo, meses, bloquea, criterio, ambito));
            }

            var delCentro = PlantillaRequisitos.ParaCentro(centro.Id);
            Requisito(delCentro, AptitudMedica, OrigenRequisito.PropioTenant, ModoVigenciaRequisito.Fija, 12, true,
                "Apto para trabajos en altura.", AmbitoDocumentoRequisito.GeneralTec);
            Requisito(delCentro, FormacionArt19, OrigenRequisito.PropioTenant, ModoVigenciaRequisito.SinVencimiento, null, true,
                null, AmbitoDocumentoRequisito.GeneralTec);
            Requisito(delCentro, EntregaEpi, OrigenRequisito.PropioTenant, ModoVigenciaRequisito.SinVencimiento, null, false,
                "Calzado de seguridad y chaleco de alta visibilidad.", AmbitoDocumentoRequisito.EspecificoCentro);
            db.PlantillasRequisitos.Add(delCentro);

            var exigencias = PlantillaRequisitos.ParaExigenciasDe(cliente.Id);
            Requisito(exigencias, SeguroResponsabilidadCivil, OrigenRequisito.ExigenciaN3, ModoVigenciaRequisito.LaPoneElDocumento, null, true,
                "Cobertura mínima de 600.000 € por siniestro.", AmbitoDocumentoRequisito.EspecificoTce);
            Requisito(exigencias, CorrienteSeguridadSocial, OrigenRequisito.ExigenciaN3, ModoVigenciaRequisito.Fija, 1, true,
                null, AmbitoDocumentoRequisito.GeneralTec);
            Requisito(exigencias, CorrienteHacienda, OrigenRequisito.ExigenciaN3, ModoVigenciaRequisito.Fija, 12, false,
                null, AmbitoDocumentoRequisito.GeneralTec);
            Requisito(exigencias, Rnt, OrigenRequisito.ExigenciaN3, ModoVigenciaRequisito.Fija, 1, false,
                null, AmbitoDocumentoRequisito.GeneralTec);
            db.PlantillasRequisitos.Add(exigencias);
        }
    }

    /// <summary>
    /// Un Tenant TEC: Empresa propia, Trabajadores con documentación en varios estados y, si tiene contrato vinculado que lo
    /// admita, la autorización de lectura al Tenant TCE.
    /// </summary>
    private sealed class ConstructorTec(
        CaeManagerDbContext db, DateOnly hoy, TenantTec tec, Guid tceId, IReadOnlyDictionary<string, Guid> contratos)
    {
        // Personas inventadas, tres por Tenant TEC en el orden en que se aprovisionan.
        private static readonly (string Nombre, string Apellidos)[,] Personas =
        {
            { ("Laura", "Gimeno Royo"), ("Javier", "Lasheras Pina"), ("Marina", "Bielsa Cortés") },
            { ("Óscar", "Lacambra Sesé"), ("Pilar", "Aznárez Ubieto"), ("Rubén", "Escartín Mur") },
            { ("Nerea", "Salcedo Abad"), ("Iván", "Ferrer Larrosa"), ("Sara", "Molinos Badía") },
        };

        public async Task ConstruirAsync(CancellationToken cancellationToken)
        {
            var tipos = await TiposAsync(db, cancellationToken);
            var propia = await SiembraEmpresaPropia.AsegurarAsync(db, tec.Id, tec.RazonSocial, tec.Cif, cancellationToken);

            // Documentos de Empresa: al día, a punto de vencer y vencido hace pocos días (dentro de la tolerancia del
            // requisito del Tenant TCE que la admite).
            db.Documentos.Add(Documento.DeEmpresa(propia.Id, Tipo(tipos, SeguroResponsabilidadCivil).Id, hoy.AddMonths(-3), VigenciaDocumento.VenceEl(hoy.AddDays(270))));
            db.Documentos.Add(Documento.DeEmpresa(propia.Id, Tipo(tipos, CorrienteSeguridadSocial).Id, hoy.AddDays(-35), VigenciaDocumento.VenceEl(hoy.AddDays(-5))));
            db.Documentos.Add(Documento.DeEmpresa(propia.Id, Tipo(tipos, CorrienteHacienda).Id, hoy.AddMonths(-11), VigenciaDocumento.VenceEl(hoy.AddDays(12))));

            // Trabajadores: el primero al día, el segundo con la aptitud médica vencida y el tercero sin la aptitud y con la
            // fecha de vencimiento de la EPI sin confirmar.
            var trabajadores = new List<Trabajador>();
            for (var i = 0; i < Personas.GetLength(1); i++)
            {
                var (nombre, apellidos) = Personas[tec.Indice, i];
                var trabajador = Trabajador.DeEmpresa(
                    propia.Id, nombre, apellidos, DatosPruebaSeeder.GenerarDniValido(63_100_000 + tec.Indice * 10 + i),
                    fechaNacimiento: hoy.AddYears(-30 - i));
                db.Trabajadores.Add(trabajador);
                trabajadores.Add(trabajador);
            }

            Documento DeTrabajador(Trabajador t, string nombreTipo, DateOnly emision, VigenciaDocumento vigencia)
            {
                var documento = Documento.DeTrabajador(t.Id, Tipo(tipos, nombreTipo).Id, emision, vigencia);
                db.Documentos.Add(documento);
                return documento;
            }

            DeTrabajador(trabajadores[0], AptitudMedica, hoy.AddMonths(-2), VigenciaDocumento.VenceEl(hoy.AddMonths(10)));
            DeTrabajador(trabajadores[0], FormacionArt19, hoy.AddMonths(-8), VigenciaDocumento.NoCaduca);
            DeTrabajador(trabajadores[0], EntregaEpi, hoy.AddMonths(-1), VigenciaDocumento.NoCaduca);
            DeTrabajador(trabajadores[1], AptitudMedica, hoy.AddMonths(-13), VigenciaDocumento.VenceEl(hoy.AddDays(-30)));
            DeTrabajador(trabajadores[1], FormacionArt19, hoy.AddMonths(-20), VigenciaDocumento.NoCaduca);
            DeTrabajador(trabajadores[2], EntregaEpi, hoy.AddDays(-60), VigenciaDocumento.SinConfirmar);
            await db.SaveChangesAsync(cancellationToken);

            await AutorizacionesAsync(trabajadores, cancellationToken);
        }

        private async Task AutorizacionesAsync(IReadOnlyList<Trabajador> trabajadores, CancellationToken cancellationToken)
        {
            if (tec.RazonSocial != Moncayo)
                return;

            // Vigente, con Documentos de Empresa y dos de los tres Trabajadores, ligada al contrato Activo.
            if (contratos.TryGetValue(Contratos.MoncayoActivo, out var activoId))
            {
                var vigente = AutorizacionLecturaDocumental.Conceder(
                    tceId, activoId, incluyeDocumentosEmpresa: true, hoy.AddDays(-120), vigenciaHasta: null,
                    tec.UsuarioUnicoId, DateTime.UtcNow.AddDays(-119));
                db.AutorizacionesLecturaDocumental.Add(vigente);
                db.AutorizacionesLecturaTrabajadores.Add(vigente.AsignarTrabajador(trabajadores[0].Id));
                db.AutorizacionesLecturaTrabajadores.Add(vigente.AsignarTrabajador(trabajadores[1].Id));
            }

            // Revocada al terminar la obra del contrato Finalizado. Se revoca en un segundo guardado: la base solo deja
            // escribir la revocación como UPDATE de sus columnas, y sus Trabajadores ya no se tocan después.
            AutorizacionLecturaDocumental? revocada = null;
            if (contratos.TryGetValue(Contratos.MoncayoFinalizado, out var finalizadoId))
            {
                revocada = AutorizacionLecturaDocumental.Conceder(
                    tceId, finalizadoId, incluyeDocumentosEmpresa: false, hoy.AddDays(-300), hoy.AddDays(-20),
                    tec.UsuarioUnicoId, DateTime.UtcNow.AddDays(-299));
                db.AutorizacionesLecturaDocumental.Add(revocada);
                db.AutorizacionesLecturaTrabajadores.Add(revocada.AsignarTrabajador(trabajadores[2].Id));
            }

            await db.SaveChangesAsync(cancellationToken);

            if (revocada is not null)
            {
                revocada.Revocar(tec.UsuarioUnicoId, DateTime.UtcNow.AddDays(-20));
                await db.SaveChangesAsync(cancellationToken);
            }
        }
    }
}
