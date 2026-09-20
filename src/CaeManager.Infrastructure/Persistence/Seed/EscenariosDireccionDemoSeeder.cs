using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Incidencias;
using CaeManager.Domain.Integraciones;
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
/// Siembra la matriz de estados de la demo a dirección: el Operador CAE
/// externo ArcoSPA opera seis Tenants propietarios, con un Gestor CAE que
/// lleva una cartera en los seis, un segundo Gestor CAE con cartera en tres y
/// un Coordinador CAE que ve las de ambos. Cada Tenant recibe su rama —
/// Cliente empresarial, contratista, subcontrata, centros, plantilla con
/// extranjeros, documentos, acreditaciones, visitas e incidencias — y las
/// vigencias son <b>relativas a hoy</b>, así que la demo enseña los mismos
/// estados el día que se siembre y unos días después (ver
/// <see cref="VigenciaDemo"/>).
///
/// <para>
/// <b>Inerte por defecto.</b> Corre solo con <c>DatosPrueba:Activo</c> y
/// <c>DatosPrueba:EscenariosDireccion</c> a la vez: es un flag propio y no
/// una ampliación de <c>DatosPrueba:Activo</c> porque esa siembra alimenta los
/// E2E, que cuentan filas exactas y no deben ver seis Tenants más. Y en
/// Producción <b>lanza</b>, aunque las dos claves estén activas: comparte con
/// el resto de la siembra local la contraseña de <see cref="CredencialesDemo"/>
/// y ese es exactamente el defecto del incidente de 2026-08-27. El camino a
/// producción es otro (ver la fase 2 del encargo), y reutiliza
/// <see cref="SembrarRamaAsync"/> sin esta guarda.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS ni autorización. Cada escritura de una rama
/// va dentro del <see cref="AmbitoTenantExplicito"/> de su Tenant propietario
/// (igual que el resto de seeders); la cartera pasa por el mismo
/// <see cref="AsignacionesOperativasWriter"/> que usa la aplicación, no por
/// SQL; y las filas de la delegación, que pertenecen al Tenant del Operador
/// CAE, van bajo el ámbito de ese Tenant, como en
/// <see cref="DelegacionDemoSeeder.CrearDelegacionAsync"/>.
/// </para>
/// </summary>
public static class EscenariosDireccionDemoSeeder
{
    public const string ClaveConfiguracion = "DatosPrueba:EscenariosDireccion";

    public const string EmailGestorPrimero = "gestor1.arcospa@caemanager.local";
    public const string EmailGestorSegundo = "gestor2.arcospa@caemanager.local";
    public const string EmailCoordinador = "coordinador1.arcospa@caemanager.local";

    /// <summary>
    /// La guarda de Producción de esta siembra, aparte de <see cref="SeedAsync"/> para que
    /// el arranque pueda invocarla <b>antes de cualquier otra siembra</b>: con el flag
    /// activo en Producción, el resto de seeders de demo ya habrían escrito cuando esta
    /// llegara a lanzar, y un rechazo tiene que ser previo a toda escritura. Inerte si
    /// el flag no está activo.
    /// </summary>
    public static void RechazarEnProduccion(IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo") || !configuration.GetValue<bool>(ClaveConfiguracion))
            return;

        if (entorno.IsProduction())
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} no puede activarse en Producción: esta siembra usa la contraseña compartida " +
                "de la demo local (CredencialesDemo). El camino a producción es una siembra propia con " +
                "contraseñas únicas entregadas fuera de banda.");
    }

    public static async Task SeedAsync(
        CaeManagerDbContext dbContext,
        UserManager<ApplicationUser> userManager,
        IConfiguration configuration,
        IHostEnvironment entorno,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        if (!configuration.GetValue<bool>("DatosPrueba:Activo") || !configuration.GetValue<bool>(ClaveConfiguracion))
            return;

        RechazarEnProduccion(configuration, entorno);

        var credenciales = CredencialesDemo.Resolver(configuration, entorno);

        var tenantOperadorId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
            dbContext, DelegacionDemoSeeder.NombreTenantConsultora, PerfilVocabularioTenant.Consultora, logger, cancellationToken);

        var administrador = await userManager.FindByEmailAsync(DelegacionDemoSeeder.EmailAdministradorConsultora)
            ?? throw new InvalidOperationException(
                "Falta el administrador del Operador CAE de la demo: esta siembra debe correr después de DelegacionDemoSeeder.");

        var equipo = await SembrarEquipoAsync(
            dbContext, userManager, _ => credenciales, EmailsEquipo.Local, logger, tenantOperadorId, cancellationToken);

        foreach (var rama in CatalogoEscenariosDireccionDemo.Ramas)
        {
            var tenantPropietarioId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
                dbContext, rama.NombreTenant, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken);
            // Duff y Pizza Planet llevan un nombre sin sufijo «demo»: la retirada exige además este marcador.
            await SiembraDemoDireccionAdministrativa.MarcarComoDemoAsync(dbContext, tenantPropietarioId, cancellationToken);

            await DelegacionDemoSeeder.CrearDelegacionAsync(
                dbContext, tenantOperadorId, tenantPropietarioId, administrador, logger, rama.NombreTenant, cancellationToken);

            await SembrarRamaAsync(
                dbContext, rama, tenantPropietarioId, tenantOperadorId, equipo, hoy: DateOnly.FromDateTime(DateTime.UtcNow),
                indiceRama: CatalogoEscenariosDireccionDemo.Ramas.ToList().IndexOf(rama), logger, cancellationToken);
        }
    }

    /// <summary>
    /// El Gestor CAE que lleva más carteras, el segundo y el Coordinador CAE del Operador CAE externo; y, solo en la
    /// siembra administrativa, la persona de Dirección CAE (no delegable como tal: ver <see cref="AbrirOperacionExternaDeLaRamaAsync"/>).
    /// </summary>
    internal sealed record EquipoOperador(
        ApplicationUser GestorPrimero, ApplicationUser GestorSegundo, ApplicationUser Coordinador,
        ApplicationUser? Direccion = null)
    {
        public ApplicationUser De(GestorDemo gestor) => gestor == GestorDemo.Primero ? GestorPrimero : GestorSegundo;
    }

    /// <summary>Las direcciones del equipo: las de la demo local, o las de un dominio propio en la siembra administrativa (que añade la de Dirección CAE).</summary>
    internal sealed record EmailsEquipo(string Coordinador, string GestorPrimero, string GestorSegundo, string? Direccion = null)
    {
        public static EmailsEquipo Local { get; } = new(EmailCoordinador, EmailGestorPrimero, EmailGestorSegundo);
    }

    /// <param name="credencialesDe">Contraseña de cada cuenta por su email: la misma para todas en la demo local (contraseña pública por diseño), una distinta y aleatoria por cuenta en la siembra administrativa.</param>
    internal static async Task<EquipoOperador> SembrarEquipoAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, Func<string, CredencialesDemo> credencialesDe,
        EmailsEquipo emails, ILogger logger, Guid tenantOperadorId, CancellationToken cancellationToken)
    {
        async Task<ApplicationUser> CrearAsync(string email, string nombre, string rol) =>
            await DelegacionDemoSeeder.CrearUsuarioConsultoraAsync(
                dbContext, userManager, credencialesDe(email), logger, tenantOperadorId, email, nombre, rol, cancellationToken)
            ?? throw new InvalidOperationException($"No se pudo sembrar el usuario {email} del Operador CAE de la demo.");

        var coordinador = await CrearAsync(emails.Coordinador, "Elena Robles (Coordinadora CAE)", Roles.CoordinadorCae);
        var primero = await CrearAsync(emails.GestorPrimero, "Marta Villalba (Gestora CAE)", Roles.GestorCae);
        var segundo = await CrearAsync(emails.GestorSegundo, "Iván Cortés (Gestor CAE)", Roles.GestorCae);
        var direccion = emails.Direccion is null
            ? null
            : await CrearAsync(emails.Direccion, "Carmen Ibáñez (Dirección CAE)", Roles.DireccionCae);

        // El alcance del Coordinador CAE sale de quién le tiene como
        // coordinador (AlcanceDatosService.ObtenerClienteIdsParaCoordinadorAsync),
        // no de un rol ni de una cartera propia: sin este vínculo no vería nada.
        using (AmbitoTenantExplicito.Establecer(tenantOperadorId))
        {
            foreach (var gestor in new[] { primero, segundo })
            {
                if (gestor.CoordinadorUsuarioId == coordinador.Id) continue;
                gestor.CoordinadorUsuarioId = coordinador.Id;
                var resultado = await userManager.UpdateAsync(gestor);
                if (!resultado.Succeeded)
                    throw new InvalidOperationException(
                        $"No se pudo vincular a {gestor.Email} con su Coordinador CAE: " +
                        string.Join(", ", resultado.Errors.Select(e => e.Description)));
            }
        }

        return new EquipoOperador(primero, segundo, coordinador, direccion);
    }

    /// <summary>
    /// La rama de un Tenant propietario: su delegación al Operador CAE (abierta
    /// y con el equipo asignado), sus Clientes empresariales con todos los
    /// datos, y las carteras. Idempotente; sin la guarda de entorno de
    /// <see cref="SeedAsync"/>, que es de la siembra local.
    /// </summary>
    internal static async Task SembrarRamaAsync(
        CaeManagerDbContext dbContext, RamaEscenariosDemo rama, Guid tenantPropietarioId, Guid tenantOperadorId,
        EquipoOperador equipo, DateOnly hoy, int indiceRama, ILogger logger, CancellationToken cancellationToken)
    {
        var delegacion = await AbrirOperacionExternaDeLaRamaAsync(
            dbContext, rama, tenantPropietarioId, tenantOperadorId, equipo, cancellationToken);

        using (AmbitoTenantExplicito.Establecer(tenantPropietarioId))
        {
            var writer = new AsignacionesOperativasWriter(
                dbContext, new TenantActualAmbiental { TenantId = tenantPropietarioId }, new ActorDeSiembra());

            await writer.AbrirOperacionDelegadaAsync(
                tenantPropietarioId, tenantOperadorId, DateTime.UtcNow.AddDays(-30), vigenciaHasta: null, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            var tipos = await dbContext.TiposDocumento.ToListAsync(cancellationToken);
            var proveedores = await dbContext.ProveedoresPlataformaCae
                .Where(p => p.Activo).OrderBy(p => p.Codigo).ToListAsync(cancellationToken);

            for (var i = 0; i < rama.Clientes.Count; i++)
            {
                var spec = rama.Clientes[i];
                var gestor = equipo.De(spec.Gestor);

                var clienteId = await dbContext.Empresas
                    .Where(e => e.EsCritico != null && e.RazonSocial == spec.RazonSocial)
                    .Select(e => (Guid?)e.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (clienteId is null)
                {
                    var constructor = new ConstructorDeCliente(
                        dbContext, spec, gestor.Id, tipos, proveedores, hoy, indiceGlobal: indiceRama * 10 + i);
                    clienteId = constructor.Construir();
                    await dbContext.SaveChangesAsync(cancellationToken);
                }

                await writer.ReasignarCarteraClienteAsync(clienteId.Value, gestor.Id, cancellationToken);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        logger.LogInformation(
            "Rama de escenarios de dirección sembrada: {Tenant} ({Clientes} Clientes empresariales, delegación {Delegacion}).",
            rama.NombreTenant, rama.Clientes.Count, delegacion.Id);
    }

    /// <summary>
    /// Deja la delegación activa y con su equipo asignado. La de Krusty Krab
    /// nace revocada en <see cref="DelegacionDemoSeeder"/> (es el caso de
    /// prueba de «delegación revocada»); bajo este flag, y solo bajo este, se
    /// reactiva, porque una rama sin delegación vigente no tendría cartera que
    /// enseñar.
    /// </summary>
    private static async Task<DelegacionTenant> AbrirOperacionExternaDeLaRamaAsync(
        CaeManagerDbContext dbContext, RamaEscenariosDemo rama, Guid tenantPropietarioId, Guid tenantOperadorId,
        EquipoOperador equipo, CancellationToken cancellationToken)
    {
        using (AmbitoTenantExplicito.Establecer(tenantOperadorId))
        {
            var delegacion = await dbContext.DelegacionesTenant.FirstAsync(
                d => d.TenantConsultoraId == tenantOperadorId && d.TenantClienteId == tenantPropietarioId, cancellationToken);

            if (!delegacion.Activa)
                delegacion.Reactivar();

            var asignaciones = new List<(ApplicationUser Usuario, string Rol)> { (equipo.Coordinador, Roles.CoordinadorCae) };
            if (rama.Clientes.Any(c => c.Gestor == GestorDemo.Primero)) asignaciones.Add((equipo.GestorPrimero, Roles.GestorCae));
            if (rama.Clientes.Any(c => c.Gestor == GestorDemo.Segundo)) asignaciones.Add((equipo.GestorSegundo, Roles.GestorCae));

            // Dirección CAE no es un rol delegable (CrearAsignacionOperadorDelegadoCommand: un operador delegado nunca
            // lleva privilegios de administración del Tenant propietario). Entra en cada Tenant como Consulta, que ve
            // todo el Tenant sin poder escribir: es lo que la autorización vigente permite y lo que se mide en las
            // pruebas; no se ha tocado el alcance para darle más.
            if (equipo.Direccion is not null) asignaciones.Add((equipo.Direccion, Roles.Consulta));

            foreach (var (usuario, rol) in asignaciones)
            {
                if (await dbContext.AsignacionesOperadorDelegado.AnyAsync(
                        a => a.DelegacionTenantId == delegacion.Id && a.UsuarioId == usuario.Id, cancellationToken))
                    continue;

                dbContext.AsignacionesOperadorDelegado.Add(new AsignacionOperadorDelegado(delegacion.Id, usuario.Id, rol));
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return delegacion;
        }
    }

    /// <summary>
    /// Sin usuario: la siembra es un proceso de arranque, no una sesión. El
    /// escritor de asignaciones lo trata como «actor desconocido» (mismo
    /// resultado que el backfill de asignaciones operativas).
    /// </summary>
    private sealed class ActorDeSiembra : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(null);
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult<string?>(null);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(null);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(false);
    }

    private enum Extranjeria { No, FueraDeLaUe, DeLaUe }

    /// <param name="Clave">c0..c5 son de la contratista; s0..s1, de la subcontrata.</param>
    private sealed record PuestoDePlantilla(string Clave, bool DeSubcontrata, int Centro, Extranjeria Extranjeria);

    /// <summary>
    /// Ocho trabajadores por Cliente empresarial repartidos en tres centros,
    /// con tres extranjeros: uno de fuera de la UE con permiso de residencia
    /// (c1), otro de la UE con certificado de registro (c5), y uno de la
    /// subcontrata (s1). El reparto es el mismo en todos los escenarios: lo
    /// que cambia entre ellos son las vigencias, no quién trabaja dónde.
    /// </summary>
    private static readonly PuestoDePlantilla[] Plantilla =
    [
        new("c0", false, 0, Extranjeria.No),
        new("c1", false, 0, Extranjeria.FueraDeLaUe),
        new("c2", false, 1, Extranjeria.No),
        new("c3", false, 1, Extranjeria.No),
        new("c4", false, 2, Extranjeria.No),
        new("c5", false, 2, Extranjeria.DeLaUe),
        new("s0", true, 1, Extranjeria.No),
        new("s1", true, 2, Extranjeria.FueraDeLaUe)
    ];

    private const string AptitudMedica = "Certificado de aptitud médica";
    private const string EntregaEpi = "Entrega de EPI";
    private const string FormacionArt19 = "Formación Art. 19";
    private const string DocumentoIdentidad = "Documento de identidad";
    private const string PermisoResidencia = "Permiso de residencia";
    private const string RegistroUe = "Certificado de Registro de Ciudadano de la UE";

    /// <summary>
    /// La única fuente de qué documento está en qué estado en cada escenario.
    /// Solo lista las excepciones; todo lo demás está <see cref="VigenciaDemo.AlDia"/>.
    /// El estado de cada centro que de aquí sale (Faltante &gt; Vencido &gt;
    /// Urgente &gt; Próximo &gt; Vigente, y Bloqueado si falta un requisito
    /// bloqueante) lo mide <c>EscenariosDireccionDemoTests</c> con el servicio
    /// real, no con esta tabla.
    /// </summary>
    private static VigenciaDemo? VigenciaEspecial(EscenarioClienteDemo escenario, string puesto, string tipo) =>
        (escenario, puesto, tipo) switch
        {
            (EscenarioClienteDemo.CasiCompleto, "c4", AptitudMedica) => VigenciaDemo.APuntoDeVencer,
            (EscenarioClienteDemo.CasiCompleto, "c5", RegistroUe) => VigenciaDemo.APuntoDeVencer,

            (EscenarioClienteDemo.ConAccesoConDocumentacionPendiente, "c0", AptitudMedica) => VigenciaDemo.Vencida,
            (EscenarioClienteDemo.ConAccesoConDocumentacionPendiente, "c1", PermisoResidencia) => VigenciaDemo.Vencida,
            (EscenarioClienteDemo.ConAccesoConDocumentacionPendiente, "c2", FormacionArt19) => VigenciaDemo.Urgente,

            (EscenarioClienteDemo.AccesoBloqueado, "c2", AptitudMedica) => VigenciaDemo.Vencida,
            _ => null
        };

    /// <summary>Documentos que, en ese escenario, ese trabajador no ha presentado nunca.</summary>
    private static bool NoPresentado(EscenarioClienteDemo escenario, string puesto, string tipo) =>
        (escenario, puesto, tipo) is
            (EscenarioClienteDemo.ConAccesoConDocumentacionPendiente, "c4", EntregaEpi)
            or (EscenarioClienteDemo.AccesoBloqueado, "c1", DocumentoIdentidad);

    /// <summary>
    /// Construye, sin guardar, todo lo de un Cliente empresarial. Una clase
    /// aparte y no un método suelto para que el estado (contadores de
    /// identificadores, listas) no viaje por veinte parámetros; y un único
    /// guardado al final —atómico— para que un fallo a medias no deje un
    /// Cliente sin plantilla que la idempotencia (que mira solo el Cliente)
    /// daría por sembrado.
    /// </summary>
    private sealed class ConstructorDeCliente(
        CaeManagerDbContext dbContext, ClienteEscenarioDemo spec, Guid gestorId,
        IReadOnlyList<TipoDocumento> tipos, IReadOnlyList<ProveedorPlataformaCae> proveedores,
        DateOnly hoy, int indiceGlobal)
    {
        private static readonly string[] Nombres =
            ["Lucía", "Andrés", "Noelia", "Rubén", "Paula", "Héctor", "Irene", "Óscar", "Carla", "Mateo", "Sonia", "Adrián"];

        private static readonly string[] Apellidos =
            ["Navarro Gil", "Ferrer Pons", "Molina Ríos", "Soler Vidal", "Cano Prieto", "Ibarra Sanz",
             "Lozano Marín", "Vega Ortiz", "Campos Lara", "Pastor Rey", "Salas Duque", "Bravo Nieto"];

        private static readonly string[] NombresCentro = ["Sede", "Planta", "Almacén"];

        private static readonly string[] Ciudades = ["Zaragoza", "Valencia", "Sevilla", "Bilbao", "Murcia", "Vigo"];

        public Guid Construir()
        {
            var ahora = DateTime.UtcNow;
            var baseCif = 7_000_000 + indiceGlobal * 10;

            var cliente = Empresa.CrearComoCliente(
                spec.RazonSocial, DatosPruebaSeeder.GenerarCifValido(baseCif),
                esCritico: spec.Escenario is EscenarioClienteDemo.AccesoBloqueado, notas: null, ejecutivoUsuarioId: gestorId);
            var contratista = new Empresa(spec.Contratista, DatosPruebaSeeder.GenerarCifValido(baseCif + 1));
            var subcontrata = Empresa.CrearComoSubcontrata(
                spec.Subcontrata, DatosPruebaSeeder.GenerarCifValido(baseCif + 2), NivelServicioSubcontrata.Gestionada.ToString());
            dbContext.Empresas.AddRange(cliente, contratista, subcontrata);

            var contratistaParaCliente = RelacionEmpresarial.Crear(contratista.Id, cliente.Id, ahora);
            dbContext.RelacionesEmpresariales.AddRange(
                contratistaParaCliente,
                RelacionEmpresarial.Crear(subcontrata.Id, contratista.Id, ahora),
                RelacionEmpresarial.Crear(subcontrata.Id, cliente.Id, ahora, contratistaParaCliente.Id));

            var centros = CrearCentros(cliente, contratista);
            var canales = CrearCanales(centros);

            var trabajadores = new Dictionary<string, Trabajador>();
            for (var puesto = 0; puesto < Plantilla.Length; puesto++)
            {
                var plaza = Plantilla[puesto];
                var (nombre, apellidos) = (
                    Nombres[(indiceGlobal + puesto) % Nombres.Length],
                    Apellidos[(indiceGlobal * 3 + puesto) % Apellidos.Length]);
                var identificacion = Identificacion(plaza.Extranjeria, indiceGlobal * 100 + puesto);
                var nacimiento = hoy.AddYears(-(24 + (indiceGlobal + puesto * 3) % 30)).AddDays(-puesto * 17);

                var trabajador = plaza.DeSubcontrata
                    ? Trabajador.DeSubcontrata(subcontrata.Id, nombre, apellidos, identificacion, fechaNacimiento: nacimiento)
                    : Trabajador.DeEmpresa(contratista.Id, nombre, apellidos, identificacion, fechaNacimiento: nacimiento);
                trabajadores[plaza.Clave] = trabajador;

                dbContext.Trabajadores.Add(trabajador);
                dbContext.Asignaciones.Add(new Asignacion(trabajador.Id, centros[plaza.Centro].Id, hoy.AddDays(-200 - puesto * 9)));
            }

            var documentos = new List<(string Puesto, string Tipo, Documento Documento)>();
            foreach (var plaza in Plantilla)
                foreach (var (tipo, documento) in DocumentosDe(plaza, trabajadores[plaza.Clave]))
                {
                    dbContext.Documentos.Add(documento);
                    documentos.Add((plaza.Clave, tipo, documento));
                }

            if (spec.Escenario is EscenarioClienteDemo.AccesoBloqueado)
            {
                // El requisito que bloquea el acceso: sin documento de identidad
                // presentado (ver NoPresentado), el centro de c1 no lo admite.
                var tipoIdentidad = tipos.Single(t => t.Nombre == DocumentoIdentidad);
                dbContext.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipoIdentidad.Id, centros[0].Id, bloqueaAcceso: true));
            }

            CrearAcreditaciones(documentos, canales);
            CrearActividad(centros, trabajadores);

            return cliente.Id;
        }

        private List<Centro> CrearCentros(Empresa cliente, Empresa contratista)
        {
            var fichaCompleta = spec.Escenario is not EscenarioClienteDemo.ConAccesoSinDocumentacionPendiente;
            var centros = new List<Centro>();
            for (var i = 0; i < NombresCentro.Length; i++)
            {
                var ciudad = Ciudades[(indiceGlobal + i) % Ciudades.Length];
                centros.Add(new Centro(
                    cliente.Id, contratista.Id,
                    nombre: $"{NombresCentro[i]} {ciudad}",
                    codigoCentro: $"DIR-{indiceGlobal:D2}{i + 1}",
                    direccion: $"Polígono {ciudad} Norte, nave {i + 1}",
                    contacto: fichaCompleta ? $"Coordinación de {NombresCentro[i].ToLowerInvariant()} — 900 000 {indiceGlobal:D2}{i}" : null,
                    contratoVigenteHasta: fichaCompleta ? hoy.AddDays(300 + i * 30) : null));
            }

            dbContext.Centros.AddRange(centros);
            return centros;
        }

        /// <summary>
        /// Un canal de plataforma principal por centro. La contraseña es un
        /// texto fijo que no abre nada: es el dato del portal de un Cliente
        /// empresarial ficticio, no una credencial de esta plataforma.
        /// </summary>
        private List<CanalGestionDocumental?> CrearCanales(IReadOnlyList<Centro> centros)
        {
            var canales = new List<CanalGestionDocumental?>();
            for (var i = 0; i < centros.Count; i++)
            {
                if (proveedores.Count == 0)
                {
                    canales.Add(null);
                    continue;
                }

                var proveedor = proveedores[(indiceGlobal + i) % proveedores.Count];
                var canal = CanalGestionDocumental.DePlataforma(
                    centros[i].Id, "Gestión general", proveedor.Id,
                    $"https://portal-demo-{proveedor.Codigo}.local", $"gestion.demo{indiceGlobal}{i}", "sin-credencial-real");
                canal.MarcarComoPrincipal();
                dbContext.CanalesGestionDocumental.Add(canal);
                canales.Add(canal);
            }

            return canales;
        }

        private string Identificacion(Extranjeria extranjeria, int numero)
        {
            if (extranjeria == Extranjeria.No)
                return DatosPruebaSeeder.GenerarDniValido(60_000_000 + numero);

            // NIE con prefijo X: la letra de control se calcula sobre el número
            // de siete cifras tal cual (X equivale a 0).
            const string letrasControl = "TRWAGMYFPDXBNJZSQVHLCKE";
            var cuerpo = 1_000_000 + numero;
            return $"X{cuerpo:D7}{letrasControl[cuerpo % 23]}";
        }

        private IEnumerable<(string Tipo, Documento Documento)> DocumentosDe(PuestoDePlantilla plaza, Trabajador trabajador)
        {
            var estandar = DatosPruebaSeeder.DocumentacionEstandarTrabajador
                .Concat(DatosPruebaSeeder.DocumentacionObligatoriaSinVencimientoTrabajador);

            foreach (var nombre in estandar)
            {
                if (NoPresentado(spec.Escenario, plaza.Clave, nombre)) continue;

                var tipo = tipos.Single(t => t.Nombre == nombre);
                var especial = VigenciaEspecial(spec.Escenario, plaza.Clave, nombre);

                if (tipo.AplicaVencimientoAutomatico)
                {
                    var vencimiento = hoy.AddDays((especial ?? VigenciaDemo.AlDia).DiasHastaVencimiento());
                    yield return (nombre, Documento.DeTrabajador(
                        trabajador.Id, tipo.Id, vencimiento.AddMonths(-(tipo.VigenciaMeses ?? 12)), vencimiento));
                }
                else if (nombre == DocumentoIdentidad && plaza.Extranjeria != Extranjeria.No)
                {
                    // El documento de un extranjero sí caduca (NIE/TIE); el
                    // de un nacional se siembra sin vencimiento como en el resto.
                    var vencimiento = hoy.AddDays(400);
                    yield return (nombre, Documento.DeTrabajador(trabajador.Id, tipo.Id, vencimiento.AddYears(-5), vencimiento));
                }
                else
                {
                    yield return (nombre, Documento.DeTrabajador(trabajador.Id, tipo.Id, hoy.AddDays(-120), fechaVencimiento: null));
                }
            }

            var autorizacion = plaza.Extranjeria switch
            {
                Extranjeria.FueraDeLaUe => PermisoResidencia,
                Extranjeria.DeLaUe => RegistroUe,
                _ => null
            };
            if (autorizacion is null) yield break;

            var tipoAutorizacion = tipos.Single(t => t.Nombre == autorizacion);
            var vigencia = VigenciaEspecial(spec.Escenario, plaza.Clave, autorizacion) ?? VigenciaDemo.AlDia;
            var venceEl = hoy.AddDays(vigencia.DiasHastaVencimiento());
            yield return (autorizacion, Documento.DeTrabajador(trabajador.Id, tipoAutorizacion.Id, venceEl.AddYears(-1), venceEl));
        }

        /// <summary>
        /// El estado de acreditación ante la plataforma del Cliente empresarial
        /// sale del escenario: aceptadas donde hay acceso confirmado, subidas
        /// donde se espera su confirmación, sin subir donde hay pendiente. Y un
        /// rechazo con causa en el escenario bloqueado, para que se vea el
        /// historial.
        /// </summary>
        private void CrearAcreditaciones(
            List<(string Puesto, string Tipo, Documento Documento)> documentos, IReadOnlyList<CanalGestionDocumental?> canales)
        {
            foreach (var (puesto, tipo, documento) in documentos)
            {
                var canal = canales[Plantilla.Single(p => p.Clave == puesto).Centro];
                if (canal is null) continue;

                var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
                switch (spec.Escenario)
                {
                    case EscenarioClienteDemo.Completo:
                    case EscenarioClienteDemo.CasiCompleto:
                    case EscenarioClienteDemo.ConAccesoSinDocumentacionPendiente:
                        acreditacion.MarcarSubida();
                        acreditacion.MarcarAceptada();
                        break;

                    case EscenarioClienteDemo.AccesoPendienteDeConfirmacion:
                        acreditacion.MarcarSubida();
                        break;

                    case EscenarioClienteDemo.AccesoBloqueado when puesto == "c0" && tipo == AptitudMedica:
                        acreditacion.MarcarSubida();
                        acreditacion.Rechazar(
                            CausaRechazoAcreditacion.Ilegible,
                            "El PDF llega escaneado torcido y no se distingue el sello.", DateTime.UtcNow.AddDays(-3));
                        break;
                }

                dbContext.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            }
        }

        /// <summary>
        /// Una visita por centro en la última semana y, donde el escenario lo
        /// pide, una incidencia: lo justo para que el dashboard ejecutivo no
        /// salga con las series vacías.
        /// </summary>
        private void CrearActividad(IReadOnlyList<Centro> centros, IReadOnlyDictionary<string, Trabajador> trabajadores)
        {
            for (var i = 0; i < centros.Count; i++)
            {
                var visita = new Visita(centros[i].Id, hoy.AddDays(-7 + i * 2), hoy.AddDays(-7 + i * 2), notas: null, OrigenVisita.Plataforma);
                dbContext.Visitas.Add(visita);
                foreach (var plaza in Plantilla.Where(p => p.Centro == i).Take(2))
                    dbContext.VisitasTrabajadores.Add(new VisitaTrabajador(visita.Id, trabajadores[plaza.Clave].Id));
            }

            var gravedad = spec.Escenario switch
            {
                EscenarioClienteDemo.ConAccesoConDocumentacionPendiente or EscenarioClienteDemo.AccesoBloqueado => GravedadIncidencia.Grave,
                EscenarioClienteDemo.CasiCompleto => GravedadIncidencia.Leve,
                _ => (GravedadIncidencia?)null
            };
            if (gravedad is { } g)
                dbContext.Incidencias.Add(new Incidencia(
                    centros[0].Id, trabajadores["c0"].Id, TipoIncidencia.Incumplimiento, g,
                    hoy.AddDays(-5), "Acceso al centro sin la documentación de cumplimiento completa."));
        }
    }
}
