using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Facturacion;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Tenants;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Domain.Visitas;
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
/// Siembra de <b>cobertura</b> para la revisión visual 1:1 de pantallas: no copia ninguna maqueta, sino que deja, en dos
/// Tenants propietarios nuevos y un Operador CAE externo propio, un ejemplar de cada estado que una pantalla puede enseñar
/// (la matriz declarada está en <see cref="CoberturaPantallasMatriz"/> y la mide <c>CoberturaPantallasTests</c>).
///
/// <list type="bullet">
/// <item><b>Alfa</b> (Tenant propietario con Operador CAE externo): documentos de Trabajador, de Empresa y de Vehículo en
/// los siete estados; Centros Bloqueado por la plataforma CAE, sin requisitos, sin gestión CAE y con requisitos solo para
/// algunos Trabajadores; Empresas con Delegaciones, Oficios y contactos de reclamación (y una sin contactos);
/// Conversaciones en cada estado; Visitas futuras con Trabajadores sin Asignación activa; Trabajadores extranjeros con
/// Visita en el mes; tarifas de Facturación y un usuario por rol.</item>
/// <item><b>Beta</b> (Tenant propietario directo): el caso de volumen, un Centro con cien Trabajadores y una Empresa con
/// trescientos documentos pendientes.</item>
/// </list>
///
/// <para>
/// <b>Inerte por defecto.</b> Corre solo con <c>DatosPrueba:Activo</c> y <see cref="ClaveConfiguracion"/>, y únicamente en
/// Development: comparte la contraseña pública de <see cref="CredencialesDemo"/>, así que cualquier otro entorno (Staging,
/// Producción) lanza antes de escribir nada. No envía correo: los contactos son de un dominio no entregable
/// (<c>@caemanager.local</c>). Idempotente por Tenant.
/// </para>
///
/// <para>
/// <b>Frontera.</b> No toca RLS ni autorización. Cada escritura va dentro del <see cref="AmbitoTenantExplicito"/> de su
/// Tenant; la operación y la cartera pasan por el mismo <see cref="AsignacionesOperativasWriter"/> que usa la aplicación.
/// Aparte de la siembra del piloto Outbound: no comparte ficheros ni Tenants con ella.
/// </para>
/// </summary>
public static class CoberturaPantallasSeeder
{
    public const string ClaveConfiguracion = "DatosPrueba:Cobertura:Activo";

    public const string NombreTenantAlfa = "Cobertura Alfa S.L. (cobertura demo)";
    public const string NombreTenantBeta = "Cobertura Beta S.L. (cobertura volumen demo)";
    public const string NombreTenantOperador = "Cobertura Operadora CAE S.L. (Operador CAE externo, cobertura demo)";

    /// <summary>Para la allowlist de <see cref="RetiradaTenantDemoService.NombresTenantsDeDemo"/>.</summary>
    public static readonly IReadOnlyList<string> NombresTenants = [NombreTenantAlfa, NombreTenantBeta, NombreTenantOperador];

    // Usuarios del Operador CAE externo.
    public const string EmailDireccionCae = "direccion.cobertura@caemanager.local";
    public const string EmailCoordinadorCae = "coordinador.cobertura@caemanager.local";
    public const string EmailGestorConCartera = "gestor.con.cartera.cobertura@caemanager.local";
    public const string EmailGestorSinCartera = "gestor.sin.cartera.cobertura@caemanager.local";

    // Usuarios del Tenant propietario Alfa.
    public const string EmailAdministradorAlfa = "admin.alfa.cobertura@caemanager.local";
    public const string EmailCoordinadorAlfa = "coordinador.alfa.cobertura@caemanager.local";
    public const string EmailGestorInternoAlfa = "gestor.interno.alfa.cobertura@caemanager.local";
    public const string EmailConsultaAlfa = "consulta.alfa.cobertura@caemanager.local";
    public const string EmailClienteAlfa = "cliente.alfa.cobertura@caemanager.local";

    // Usuario del Tenant propietario Beta.
    public const string EmailAdministradorBeta = "admin.beta.cobertura@caemanager.local";

    public const string EmpresaPropiaAlfa = "Alfa Instalaciones S.L.";
    public const string ClienteNorte = "Cliente Norte S.A.";
    public const string ClienteSur = "Cliente Sur S.A.";
    public const string SubcontrataPorEstado = "Montajes Cobertura S.L.";
    public const string SubcontrataSinContactos = "Servicios Sin Contacto S.L.";

    public const string CentroPrincipal = "Centro Principal (cobertura)";
    public const string CentroBloqueadoPorPlataforma = "Centro Bloqueado por plataforma (cobertura)";
    public const string CentroSinRequisitos = "Centro Sin requisitos (cobertura)";
    public const string CentroSinGestionCae = "Centro Sin gestión CAE (cobertura)";
    public const string CentroRequisitosParaAlgunos = "Centro con requisitos para algunos Trabajadores (cobertura)";

    public const string EmpresaPropiaBeta = "Beta Servicios S.L.";
    public const string ClienteMasivo = "Cliente Masivo S.A.";
    public const string CentroMasivo = "Centro Masivo (cien Trabajadores, cobertura)";

    // Tipos del catálogo de serie con los que se ejercita cada estado.
    public const string AptitudMedica = "Certificado de aptitud médica";
    public const string EntregaEpi = "Entrega de EPI";
    public const string FormacionArt19 = "Formación Art. 19";
    public const string Rnt = "RNT";
    public const string Rlc = "RLC";
    public const string CorrienteSeguridadSocial = "Certificado de estar al corriente con la Seguridad Social";
    public const string CorrienteHacienda = "Certificado de estar al corriente con Hacienda";
    public const string SeguroResponsabilidadCivil = "Seguro de Responsabilidad Civil + recibo de pago";
    public const string EvaluacionDeRiesgos = "Evaluación de Riesgos Laborales";
    public const string PlanificacionPreventiva = "Planificación de la Actividad Preventiva";
    public const string InspeccionTecnica = "ITC";

    /// <summary>Días de tolerancia de los requisitos bloqueantes del Centro Principal.</summary>
    public const int ToleranciaDias = 15;

    public const string PrefijoTipoDeVolumen = "Documento de volumen ";

    public static bool EstaActiva(IConfiguration configuration) =>
        configuration.GetValue<bool>("DatosPrueba:Activo") && configuration.GetValue<bool>(ClaveConfiguracion);

    /// <summary>
    /// La guarda de esta siembra, aparte de <see cref="SeedAsync"/> para que el arranque la invoque antes de cualquier
    /// escritura. Lanza en cualquier entorno que no sea Development, Staging incluido. Inerte si la clave no está activa.
    /// </summary>
    public static void RechazarFueraDeDevelopment(IConfiguration configuration, IHostEnvironment entorno)
    {
        if (!EstaActiva(configuration))
            return;

        if (!entorno.IsDevelopment())
            throw new InvalidOperationException(
                $"{ClaveConfiguracion} solo puede activarse en Development (entorno actual: {entorno.EnvironmentName}): " +
                "siembra datos de cobertura con la contraseña pública de la demo local (CredencialesDemo).");
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

        RechazarFueraDeDevelopment(configuration, entorno);

        var credenciales = CredencialesDemo.Resolver(configuration, entorno);
        var hoy = DiaDeNegocio.Hoy();

        var operadorId = await Aprovisionamiento.AprovisionarTenantAsync(
            dbContext, NombreTenantOperador, PerfilVocabularioTenant.Consultora, logger, cancellationToken,
            esOperadorCaeExterno: true, marcarComoDemo: true);

        async Task<ApplicationUser> UsuarioAsync(Guid tenantId, string email, string nombre, string rol) =>
            await Aprovisionamiento.CrearUsuarioConsultoraAsync(
                dbContext, userManager, credenciales, logger, tenantId, email, nombre, rol, cancellationToken)
            ?? throw new InvalidOperationException($"No se pudo sembrar el usuario de cobertura {email}.");

        var direccion = await UsuarioAsync(operadorId, EmailDireccionCae, "Dirección CAE (cobertura)", Roles.DireccionCae);
        var coordinador = await UsuarioAsync(operadorId, EmailCoordinadorCae, "Coordinación CAE (cobertura)", Roles.CoordinadorCae);
        var gestorConCartera = await UsuarioAsync(operadorId, EmailGestorConCartera, "Gestora CAE con cartera (cobertura)", Roles.GestorCae);
        var gestorSinCartera = await UsuarioAsync(operadorId, EmailGestorSinCartera, "Gestor CAE sin cartera (cobertura)", Roles.GestorCae);

        // ── Alfa: el Tenant propietario con Operador CAE externo ──────────────────────────────────────────────────
        var alfaId = await Aprovisionamiento.AprovisionarTenantAsync(
            dbContext, NombreTenantAlfa, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken,
            esOperadorCaeExterno: false, marcarComoDemo: true);

        var administradorAlfa = await UsuarioAsync(alfaId, EmailAdministradorAlfa, "Administración Alfa (cobertura)", Roles.Administrador);
        await UsuarioAsync(alfaId, EmailCoordinadorAlfa, "Coordinación interna Alfa (cobertura)", Roles.CoordinadorCae);
        await UsuarioAsync(alfaId, EmailGestorInternoAlfa, "Gestor CAE interno Alfa (cobertura)", Roles.GestorCae);
        await UsuarioAsync(alfaId, EmailConsultaAlfa, "Consulta Alfa (cobertura)", Roles.Consulta);
        var clienteAlfa = await UsuarioAsync(alfaId, EmailClienteAlfa, "Usuario Cliente empresarial Alfa (cobertura)", Roles.Cliente);

        // La operación se abre por el ayudante de la demo, con el Gestor CAE con cartera como primera persona asignada; el
        // resto del equipo del Operador CAE entra en la misma operación. Dirección CAE entra como Consulta: no es un rol
        // delegable (ver EscenariosDireccionDemoSeeder).
        await Aprovisionamiento.CrearDelegacionAsync(
            dbContext, operadorId, alfaId, gestorConCartera, logger, NombreTenantAlfa, cancellationToken);
        await AsignarAlEquipoAsync(dbContext, operadorId, gestorConCartera, direccion, Roles.Consulta, cancellationToken);
        await AsignarAlEquipoAsync(dbContext, operadorId, gestorConCartera, coordinador, Roles.CoordinadorCae, cancellationToken);
        await AsignarAlEquipoAsync(dbContext, operadorId, gestorConCartera, gestorSinCartera, Roles.GestorCae, cancellationToken);

        // Los Gestores CAE del Operador CAE cuelgan de su Coordinador CAE: sin ese vínculo el Coordinador no vería nada.
        using (AmbitoTenantExplicito.Establecer(operadorId))
            foreach (var gestor in new[] { gestorConCartera, gestorSinCartera })
            {
                if (gestor.CoordinadorUsuarioId == coordinador.Id) continue;
                gestor.CoordinadorUsuarioId = coordinador.Id;
                var resultado = await userManager.UpdateAsync(gestor);
                if (!resultado.Succeeded)
                    throw new InvalidOperationException(
                        $"No se pudo vincular a {gestor.Email} con su Coordinador CAE: {string.Join(", ", resultado.Errors.Select(e => e.Description))}");
            }

        using (AmbitoTenantExplicito.Establecer(alfaId))
        {
            var writer = new AsignacionesOperativasWriter(
                dbContext, new TenantActualAmbiental { TenantId = alfaId }, new EscenariosDireccionDemoSeeder.ActorDeSiembra());
            await writer.AbrirOperacionDelegadaAsync(alfaId, operadorId, DateTime.UtcNow.AddDays(-30), vigenciaHasta: null, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            if (!await dbContext.Empresas.AnyAsync(e => e.RazonSocial == EmpresaPropiaAlfa, cancellationToken))
                await new ConstructorAlfa(dbContext, hoy, administradorAlfa.Id).ConstruirAsync(cancellationToken);

            // Solo el Gestor CAE con cartera la recibe: el otro queda con operación abierta y sin cartera.
            await writer.AsegurarCarteraTenantEnteroAsync(alfaId, gestorConCartera.Id, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);

            // La cuenta de Usuario de Cliente queda ligada a su Cliente empresarial fuera de la rama de construcción, para que
            // un arranque interrumpido se repare en el siguiente; y solo se escribe si cambia.
            var clienteNorteId = await dbContext.Empresas
                .Where(e => e.RazonSocial == ClienteNorte).Select(e => e.Id).SingleAsync(cancellationToken);
            if (clienteAlfa.ClienteId != clienteNorteId)
            {
                clienteAlfa.ClienteId = clienteNorteId;
                var resultado = await userManager.UpdateAsync(clienteAlfa);
                if (!resultado.Succeeded)
                    throw new InvalidOperationException(
                        $"No se pudo ligar a {EmailClienteAlfa} con su Cliente empresarial: " +
                        string.Join(", ", resultado.Errors.Select(e => e.Description)));
            }

        }

        // ── Beta: el caso de volumen ──────────────────────────────────────────────────────────────────────────────
        var betaId = await Aprovisionamiento.AprovisionarTenantAsync(
            dbContext, NombreTenantBeta, PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken,
            esOperadorCaeExterno: false, marcarComoDemo: true);
        await UsuarioAsync(betaId, EmailAdministradorBeta, "Administración Beta (cobertura)", Roles.Administrador);

        using (AmbitoTenantExplicito.Establecer(betaId))
        {
            if (!await dbContext.Empresas.AnyAsync(e => e.RazonSocial == EmpresaPropiaBeta, cancellationToken))
                await new ConstructorBeta(dbContext, hoy).ConstruirAsync(cancellationToken);

            dbContext.ChangeTracker.Clear();
        }

        logger.LogInformation("Siembra de cobertura de pantallas lista: {Alfa}, {Beta} y {Operador}.", NombreTenantAlfa, NombreTenantBeta, NombreTenantOperador);
    }

    /// <summary>
    /// El resto del equipo del Operador CAE entra en la misma operación que <paramref name="primero"/> (la persona con la
    /// que se abrió), bajo el ámbito del Operador (le pertenece).
    /// </summary>
    private static async Task AsignarAlEquipoAsync(
        CaeManagerDbContext dbContext, Guid operadorId, ApplicationUser primero, ApplicationUser usuario, string rol,
        CancellationToken cancellationToken)
    {
        using (AmbitoTenantExplicito.Establecer(operadorId))
        {
            if (await dbContext.AsignacionesOperadorDelegado.AnyAsync(a => a.UsuarioId == usuario.Id, cancellationToken))
                return;

            var operacionId = await dbContext.AsignacionesOperadorDelegado
                .Where(a => a.UsuarioId == primero.Id).Select(a => a.DelegacionTenantId).SingleAsync(cancellationToken);
            dbContext.AsignacionesOperadorDelegadoConRevocadas.Add(new AsignacionOperadorDelegado(operacionId, usuario.Id, rol));
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Cómo está un documento: los siete estados de la interfaz (Pendiente es la ausencia del documento).</summary>
    private enum Caso { Vigente, PorVencer, SinConfirmar, Vencido, EnTolerancia, Bloqueado, Pendiente }

    /// <summary>
    /// Emisión y vigencia de un documento en un caso. En tolerancia vence hace menos de <see cref="ToleranciaDias"/> días y
    /// Bloqueado hace más; Vencido es el mismo documento caducado en un requisito que no bloquea.
    /// </summary>
    private static (DateOnly Emision, VigenciaDocumento Vigencia) Vigencia(Caso caso, DateOnly hoy)
    {
        DateOnly Vence(int dias) => hoy.AddDays(dias);
        (DateOnly, VigenciaDocumento) Con(int dias) => (Vence(dias).AddYears(-1) > hoy ? hoy : Vence(dias).AddYears(-1), VigenciaDocumento.VenceEl(Vence(dias)));

        return caso switch
        {
            Caso.Vigente => Con(300),
            Caso.PorVencer => Con(12),
            Caso.SinConfirmar => (hoy.AddDays(-90), VigenciaDocumento.SinConfirmar),
            Caso.Vencido => Con(-40),
            Caso.EnTolerancia => Con(-5),
            Caso.Bloqueado => Con(-40),
            _ => throw new ArgumentOutOfRangeException(nameof(caso), caso, "Un documento ausente no tiene vigencia que sembrar.")
        };
    }

    /// <summary>Lo que comparten los dos constructores: catálogo, proveedor, y alta de Trabajadores con su documentación.</summary>
    private abstract class ConstructorBase(CaeManagerDbContext dbContext, DateOnly hoy)
    {
        protected readonly CaeManagerDbContext Db = dbContext;
        protected readonly DateOnly Hoy = hoy;
        protected Dictionary<string, TipoDocumento> Tipos = [];
        protected ProveedorPlataformaCae Proveedor = null!;
        private int _cif;

        protected async Task CargarCatalogoAsync(int baseCif, CancellationToken cancellationToken)
        {
            Tipos = await Db.TiposDocumento.ToDictionaryAsync(t => t.Nombre, cancellationToken);
            Proveedor = await Db.ProveedoresPlataformaCae.Where(p => p.Activo).OrderBy(p => p.Codigo).FirstOrDefaultAsync(cancellationToken)
                        ?? throw new InvalidOperationException("La siembra de cobertura necesita un proveedor de plataforma CAE activo.");
            _cif = baseCif;
        }

        protected string SiguienteCif() => DatosPruebaSeeder.GenerarCifValido(_cif++);

        protected TipoDocumento Tipo(string nombre) =>
            Tipos.TryGetValue(nombre, out var tipo)
                ? tipo
                : throw new InvalidOperationException($"El catálogo del Tenant no tiene el Tipo de documento «{nombre}».");

        protected CanalGestionDocumental CanalDePlataforma(Centro centro, int indice)
        {
            var canal = CanalGestionDocumental.DePlataforma(
                centro.Id, "Gestión general", Proveedor.Id,
                $"https://portal-cobertura-{Proveedor.Codigo}.local", $"gestion.cobertura{indice}", "sin-credencial-real");
            canal.MarcarComoPrincipal();
            Db.CanalesGestionDocumental.Add(canal);
            return canal;
        }

        /// <summary>El Trabajador con su documentación estándar al día, salvo los tipos de <paramref name="aparte"/>.</summary>
        protected Trabajador NuevoTrabajador(
            Empresa empleador, bool deSubcontrata, string nombre, string apellidos, string identificacion,
            IReadOnlySet<string>? aparte = null)
        {
            var trabajador = deSubcontrata
                ? Trabajador.DeSubcontrata(empleador.Id, nombre, apellidos, identificacion, fechaNacimiento: Hoy.AddYears(-33))
                : Trabajador.DeEmpresa(empleador.Id, nombre, apellidos, identificacion, fechaNacimiento: Hoy.AddYears(-33));
            Db.Trabajadores.Add(trabajador);

            var alDia = Hoy.AddDays(200);
            foreach (var nombreTipo in DatosPruebaSeeder.DocumentacionEstandarTrabajador.Concat(DatosPruebaSeeder.DocumentacionObligatoriaSinVencimientoTrabajador))
            {
                if (aparte?.Contains(nombreTipo) == true)
                    continue;

                var tipo = Tipo(nombreTipo);
                Db.Documentos.Add(tipo.AplicaVencimientoAutomatico
                    ? Documento.DeTrabajador(trabajador.Id, tipo.Id, alDia.AddMonths(-(tipo.VigenciaMeses ?? 12)), VigenciaDocumento.VenceEl(alDia))
                    : Documento.DeTrabajador(trabajador.Id, tipo.Id, Hoy.AddDays(-120), VigenciaDocumento.NoCaduca));
            }

            return trabajador;
        }

        protected Documento? DocumentoDeTrabajador(Trabajador trabajador, string nombreTipo, Caso caso)
        {
            if (caso == Caso.Pendiente)
                return null;

            var (emision, vigencia) = Vigencia(caso, Hoy);
            var documento = Documento.DeTrabajador(trabajador.Id, Tipo(nombreTipo).Id, emision, vigencia);
            Db.Documentos.Add(documento);
            return documento;
        }

        protected void Asignar(Trabajador trabajador, Centro centro, int altaHaceDias = 120) =>
            Db.Asignaciones.Add(new Asignacion(trabajador.Id, centro.Id, Hoy.AddDays(-altaHaceDias)));

        protected Centro NuevoCentro(Empresa cliente, Empresa propia, string nombre, string codigo) =>
            Add(new Centro(
                cliente.Id, propia.Id, nombre: nombre, codigoCentro: codigo, direccion: $"Polígono de cobertura, nave {codigo}",
                contacto: null, contratoVigenteHasta: Hoy.AddDays(300)));

        protected T Add<T>(T entidad) where T : class
        {
            Db.Add(entidad);
            return entidad;
        }
    }

    /// <summary>El Tenant propietario Alfa, con todo lo que no es volumen.</summary>
    private sealed class ConstructorAlfa(CaeManagerDbContext db, DateOnly hoy, Guid administradorId) : ConstructorBase(db, hoy)
    {
        private Empresa _propia = null!;
        private Empresa _norte = null!;
        private Empresa _sur = null!;
        private Empresa _montajes = null!;
        private Empresa _sinContactos = null!;
        private Centro _principal = null!;
        private Centro _plataforma = null!;
        private Centro _sinRequisitos = null!;
        private Centro _sinGestion = null!;
        private Centro _algunos = null!;
        private CanalGestionDocumental _canalPrincipal = null!;
        private readonly List<Trabajador> _delPrincipal = [];
        private int _dni = 62_000_000;

        private string SiguienteDni() => DatosPruebaSeeder.GenerarDniValido(_dni++);

        public async Task<Guid> ConstruirAsync(CancellationToken cancellationToken)
        {
            await CargarCatalogoAsync(8_100_000, cancellationToken);

            Empresas();
            Centros();
            RequisitosDelCentroPrincipal();
            TrabajadoresPorEstado();
            EmpresaSubcontrataPorEstado();
            Vehiculos();
            SedesOficiosYContactos();
            CentroBloqueadoPorLaPlataforma();
            CentrosRestantes();
            Visitas();
            Tarifas();
            Conversaciones();

            await Db.SaveChangesAsync(cancellationToken);
            return _norte.Id;
        }

        private void Empresas()
        {
            _propia = Add(new Empresa(EmpresaPropiaAlfa, SiguienteCif()));
            _norte = Add(Empresa.CrearComoCliente(ClienteNorte, SiguienteCif(), esCritico: false, notas: null, ejecutivoUsuarioId: null));
            _sur = Add(Empresa.CrearComoCliente(ClienteSur, SiguienteCif(), esCritico: true, notas: null, ejecutivoUsuarioId: null));

            var ahora = DateTime.UtcNow;
            var relacionNorte = Add(RelacionEmpresarial.Crear(_propia.Id, _norte.Id, ahora));
            Add(RelacionEmpresarial.Crear(_propia.Id, _sur.Id, ahora));

            _montajes = Add(Empresa.CrearComoSubcontrata(SubcontrataPorEstado, SiguienteCif(), NivelServicioSubcontrata.Gestionada.ToString()));
            _sinContactos = Add(Empresa.CrearComoSubcontrata(SubcontrataSinContactos, SiguienteCif(), NivelServicioSubcontrata.Gestionada.ToString()));
            foreach (var subcontrata in new[] { _montajes, _sinContactos })
            {
                Add(RelacionEmpresarial.Crear(subcontrata.Id, _propia.Id, ahora));
                Add(RelacionEmpresarial.Crear(subcontrata.Id, _norte.Id, ahora, relacionNorte.Id));
            }
        }

        private void Centros()
        {
            _principal = NuevoCentro(_norte, _propia, CentroPrincipal, "CB-01");
            _plataforma = NuevoCentro(_norte, _propia, CentroBloqueadoPorPlataforma, "CB-02");
            _sinRequisitos = NuevoCentro(_norte, _propia, CentroSinRequisitos, "CB-03");
            _sinGestion = NuevoCentro(_sur, _propia, CentroSinGestionCae, "CB-04");
            _algunos = NuevoCentro(_sur, _propia, CentroRequisitosParaAlgunos, "CB-05");
            _sinGestion.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);
            _canalPrincipal = CanalDePlataforma(_principal, 1);
        }

        /// <summary>
        /// Los requisitos del Centro Principal: la aptitud y las dos obligaciones de Empresa sin bloqueo cubren el Vencido;
        /// la Entrega de EPI, el RNT y el RLC bloquean con tolerancia, que es lo que da Bloqueado y En tolerancia.
        /// </summary>
        private void RequisitosDelCentroPrincipal()
        {
            foreach (var (nombre, bloquea) in new[]
                     {
                         (AptitudMedica, false), (EntregaEpi, true), (Rnt, true), (Rlc, true), (CorrienteSeguridadSocial, false),
                         (CorrienteHacienda, false), (SeguroResponsabilidadCivil, false), (EvaluacionDeRiesgos, false),
                         (PlanificacionPreventiva, false), (InspeccionTecnica, false)
                     })
                Db.TiposDocumentoCentros.Add(new TipoDocumentoCentro(
                    Tipo(nombre).Id, _principal.Id, incluido: true, bloqueaAcceso: bloquea, toleranciaDias: bloquea ? ToleranciaDias : null));
        }

        /// <summary>
        /// Siete Trabajadores de la Empresa propia, cada uno el ejemplar de un estado, con la aptitud (sin bloqueo) y la
        /// Entrega de EPI (con bloqueo y tolerancia) en el Centro Principal.
        /// </summary>
        private void TrabajadoresPorEstado()
        {
            HashSet<string> aparte = [AptitudMedica, EntregaEpi];
            (string Nombre, string Apellidos, Caso Aptitud, Caso Epi)[] casos =
            [
                ("Vera", "Vigente Soler", Caso.Vigente, Caso.Vigente),
                ("Pablo", "Porvencer Díaz", Caso.PorVencer, Caso.Vigente),
                ("Sonia", "Sinconfirmar Ruiz", Caso.SinConfirmar, Caso.Vigente),
                ("Vicente", "Vencido Mora", Caso.Vencido, Caso.Vigente),
                ("Paula", "Pendiente Gil", Caso.Pendiente, Caso.Vigente),
                ("Tomás", "Tolerancia Vera", Caso.Vigente, Caso.EnTolerancia),
                ("Berta", "Bloqueada Lara", Caso.Vigente, Caso.Bloqueado)
            ];

            foreach (var (nombre, apellidos, aptitud, epi) in casos)
            {
                var trabajador = NuevoTrabajador(_propia, false, nombre, apellidos, SiguienteDni(), aparte);
                DocumentoDeTrabajador(trabajador, AptitudMedica, aptitud);
                DocumentoDeTrabajador(trabajador, EntregaEpi, epi);
                Asignar(trabajador, _principal);
                _delPrincipal.Add(trabajador);
            }

            // Un documento pendiente en la plataforma del Centro Principal (sin subir) y otro subido sin validar.
            var vigente = _delPrincipal[0];
            foreach (var (nombreTipo, subida) in new[] { (FormacionArt19, false), (AptitudMedica, true) })
            {
                var documento = Db.Documentos.Local.First(d => d.TrabajadorId == vigente.Id && d.TipoDocumentoId == Tipo(nombreTipo).Id);
                var acreditacion = new AcreditacionDocumentoPlataforma(documento.Id, _canalPrincipal.Id);
                if (subida)
                    acreditacion.MarcarSubida();
                Db.AcreditacionesDocumentoPlataforma.Add(acreditacion);
            }
        }

        /// <summary>
        /// La Subcontrata de las Delegaciones: sus documentos de Empresa recorren los siete estados y, como el RNT y el RLC
        /// bloquean en el Centro Principal, sus Trabajadores aparecen bloqueados por un documento que no es suyo.
        /// </summary>
        private void EmpresaSubcontrataPorEstado()
        {
            (string Tipo, Caso Caso)[] casos =
            [
                (Rnt, Caso.Bloqueado), (Rlc, Caso.EnTolerancia), (CorrienteSeguridadSocial, Caso.Vencido),
                (CorrienteHacienda, Caso.PorVencer), (SeguroResponsabilidadCivil, Caso.SinConfirmar),
                (EvaluacionDeRiesgos, Caso.Vigente), (PlanificacionPreventiva, Caso.Pendiente)
            ];
            foreach (var (tipo, caso) in casos)
            {
                if (caso == Caso.Pendiente)
                    continue;
                var (emision, vigencia) = Vigencia(caso, Hoy);
                Db.Documentos.Add(Documento.DeEmpresa(_montajes.Id, Tipo(tipo).Id, emision, vigencia));
            }

            // La Empresa propia lo tiene todo al día: sus Trabajadores solo se bloquean por lo suyo.
            foreach (var tipo in new[] { Rnt, Rlc, CorrienteSeguridadSocial, CorrienteHacienda, SeguroResponsabilidadCivil, EvaluacionDeRiesgos, PlanificacionPreventiva })
            {
                var (emision, vigencia) = Vigencia(Caso.Vigente, Hoy);
                foreach (var empresa in new[] { _propia, _sinContactos })
                    Db.Documentos.Add(Documento.DeEmpresa(empresa.Id, Tipo(tipo).Id, emision, vigencia));
            }
        }

        private void Vehiculos()
        {
            (string Matricula, string Nombre, Caso Caso)[] casos =
            [
                ("5101 BCD", "Furgoneta ITC vigente", Caso.Vigente), ("5102 BCF", "Furgoneta ITC por vencer", Caso.PorVencer),
                ("5103 BCG", "Furgoneta ITC sin confirmar", Caso.SinConfirmar), ("5104 BCH", "Furgoneta ITC vencida", Caso.Vencido),
                ("5105 BCJ", "Furgoneta sin ITC", Caso.Pendiente)
            ];
            foreach (var (matricula, nombre, caso) in casos)
            {
                var vehiculo = Add(Vehiculo.DeEmpresa(_propia.Id, nombre, "Modelo de cobertura", matricula));
                if (caso == Caso.Pendiente)
                    continue;
                var (emision, vigencia) = Vigencia(caso, Hoy);
                Db.Documentos.Add(Documento.DeVehiculo(vehiculo.Id, Tipo(InspeccionTecnica).Id, emision, vigencia));
            }
        }

        /// <summary>
        /// La Empresa propia: tres delegaciones (dos con contacto de reclamación y una sin él, que cae a los contactos de
        /// la Empresa), dos Oficios y Trabajadores repartidos. Servicios Sin Contacto: sin delegaciones ni contactos.
        /// </summary>
        private void SedesOficiosYContactos()
        {
            var madrid = Add(new DelegacionEmpresa(_propia.Id, "Delegación Madrid"));
            madrid.FijarContactoReclamacion("Lucía Reclama (Madrid)", "reclama.madrid@caemanager.local", "910000001");
            var sevilla = Add(new DelegacionEmpresa(_propia.Id, "Delegación Sevilla"));
            sevilla.FijarContactoReclamacion("Raúl Reclama (Sevilla)", "reclama.sevilla@caemanager.local", "950000002");
            var valencia = Add(new DelegacionEmpresa(_propia.Id, "Delegación Valencia"));
            var electricista = Add(new OficioEmpresa(_propia.Id, "Electricista"));
            var soldador = Add(new OficioEmpresa(_propia.Id, "Soldador"));

            var contacto = ContactoAgenda.DeEmpresa(
                _propia.Id, "Marcos Central (Alfa Instalaciones)", "central@alfa-instalaciones.caemanager.local",
                cargo: "Responsable CAE", esPredeterminado: true);
            contacto.EstablecerRoles([RolContacto.ContactoCae]);
            Add(contacto);

            (string Nombre, string Apellidos, DelegacionEmpresa? Sede, OficioEmpresa? Oficio)[] plantilla =
            [
                ("Álvaro", "Madrid Uno", madrid, electricista), ("Bea", "Madrid Dos", madrid, soldador),
                ("Cristian", "Sevilla Uno", sevilla, electricista), ("Dora", "Sevilla Dos", sevilla, soldador),
                ("Emilio", "Valencia Uno", valencia, electricista), ("Flora", "Sin Delegación", null, null)
            ];
            foreach (var (nombre, apellidos, delegacion, oficio) in plantilla)
            {
                var trabajador = NuevoTrabajador(_propia, false, nombre, apellidos, SiguienteDni());
                trabajador.FijarDelegacionEmpresaYOficio(delegacion, oficio);
                Asignar(trabajador, _principal);
            }

            Asignar(NuevoTrabajador(_sinContactos, true, "Gael", "Sin Contacto", SiguienteDni()), _principal);
            Asignar(NuevoTrabajador(_montajes, true, "Hugo", "Montajes Uno", SiguienteDni()), _principal);
        }

        /// <summary>El Centro con un documento rechazado y otro vencido en su plataforma CAE: la causa de «Bloqueado».</summary>
        private void CentroBloqueadoPorLaPlataforma()
        {
            var canal = CanalDePlataforma(_plataforma, 2);
            var trabajador = NuevoTrabajador(_propia, false, "Rosa", "Rechazada Plata", SiguienteDni());
            Asignar(trabajador, _plataforma);

            var documento = Db.Documentos.Local.First(d => d.TrabajadorId == trabajador.Id && d.TipoDocumentoId == Tipo(FormacionArt19).Id);
            var rechazada = new AcreditacionDocumentoPlataforma(documento.Id, canal.Id);
            rechazada.MarcarSubida();
            rechazada.Rechazar(CausaRechazoAcreditacion.Ilegible, "El certificado llega escaneado sin la firma del formador.", DateTime.UtcNow.AddDays(-2));
            Db.AcreditacionesDocumentoPlataforma.Add(rechazada);
        }

        private void CentrosRestantes()
        {
            // Sin requisitos: el Centro excluye todos los tipos del catálogo.
            foreach (var tipo in Tipos.Values)
                Db.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipo.Id, _sinRequisitos.Id, incluido: false));
            Asignar(NuevoTrabajador(_propia, false, "Sergio", "Sinrequisitos Ortiz", SiguienteDni()), _sinRequisitos);

            Asignar(NuevoTrabajador(_propia, false, "Gema", "Singestion Pla", SiguienteDni()), _sinGestion);

            // Requisitos para algunos Trabajadores: solo dos de la plantilla están asignados a este Centro, que exige la
            // Entrega de EPI como bloqueante; los demás Trabajadores de la Empresa no entran en lo que exige.
            Db.TiposDocumentoCentros.Add(new TipoDocumentoCentro(Tipo(EntregaEpi).Id, _algunos.Id, incluido: true, bloqueaAcceso: true, toleranciaDias: 0));
            HashSet<string> aparte = [EntregaEpi];
            var primero = NuevoTrabajador(_propia, false, "Adela", "Algunos Uno", SiguienteDni(), aparte);
            DocumentoDeTrabajador(primero, EntregaEpi, Caso.Vigente);
            var segundo = NuevoTrabajador(_propia, false, "Bruno", "Algunos Dos", SiguienteDni(), aparte);
            DocumentoDeTrabajador(segundo, EntregaEpi, Caso.Bloqueado);
            Asignar(primero, _algunos);
            Asignar(segundo, _algunos);
        }

        private void Visitas()
        {
            // Futuras con quien no está asignado: sin Asignación nunca, y con la Asignación dada de baja.
            var sinAsignacion = NuevoTrabajador(_propia, false, "Nuria", "Sinasignacion Rey", SiguienteDni());
            var dadoDeBaja = NuevoTrabajador(_propia, false, "Bernardo", "Debaja Pons", SiguienteDni());
            var asignacionBaja = new Asignacion(dadoDeBaja.Id, _principal.Id, Hoy.AddDays(-200));
            asignacionBaja.DarDeBaja(Hoy.AddDays(-20));
            Db.Asignaciones.Add(asignacionBaja);

            var futura = Add(new Visita(_principal.Id, Hoy.AddDays(3), Hoy.AddDays(4), notas: null, OrigenVisita.Plataforma));
            Add(new VisitaTrabajador(futura.Id, sinAsignacion));
            Add(new VisitaTrabajador(futura.Id, dadoDeBaja));
            Add(new VisitaTrabajador(futura.Id, _delPrincipal[0]));

            var enSinGestion = Add(new Visita(_sinGestion.Id, Hoy.AddDays(7), Hoy.AddDays(7), notas: null, OrigenVisita.Plataforma));
            Add(new VisitaTrabajador(enSinGestion.Id, sinAsignacion));

            // Extranjeros con Visita en el mes: dos con pasaporte y uno con DNI marcado a mano como cobrable.
            var inicioDeMes = new DateOnly(Hoy.Year, Hoy.Month, 1);
            var delMes = Add(new Visita(_principal.Id, inicioDeMes, inicioDeMes, notas: null, OrigenVisita.Plataforma));
            var pasaporteUno = NuevoTrabajador(_propia, false, "Ivana", "Pasaporte Kovač", "PB1234567");
            var pasaporteDos = NuevoTrabajador(_montajes, true, "Omar", "Pasaporte Haddad", "PC7654321");
            var marcado = NuevoTrabajador(_propia, false, "Ulises", "Marcado Cobrable", SiguienteDni());
            marcado.FijarMarcaExtranjeroCobrable(true);
            foreach (var trabajador in new[] { pasaporteUno, pasaporteDos, marcado })
            {
                Asignar(trabajador, _principal);
                Add(new VisitaTrabajador(delMes.Id, trabajador));
            }
        }

        private void Tarifas()
        {
            foreach (var (concepto, precio) in new[]
                     {
                         (ConceptoFacturable.TrabajadorActivo, 4.50m), (ConceptoFacturable.AltaCentro, 25m),
                         (ConceptoFacturable.VisitaTrabajadorExtranjero, 12m), (ConceptoFacturable.DocumentoGestionado, 1.50m)
                     })
                Add(TarifaCliente.Crear(_norte.Id, concepto, precio, "EUR"));
        }

        /// <summary>
        /// Una conversación en cada estado, con remitente conocido (un usuario del Cliente empresarial) y desconocido (sin
        /// Cliente), una larga de cuarenta mensajes y adjuntos sin contenido: la descarga falla limpiamente, como en la
        /// siembra de comunicaciones del resto de demos.
        /// </summary>
        private void Conversaciones()
        {
            var ahora = DateTime.UtcNow;
            var estados = new[]
            {
                EstadoConversacion.Abierta, EstadoConversacion.Pendiente, EstadoConversacion.Resuelta, EstadoConversacion.Cerrada,
                EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero
            };

            for (var i = 0; i < estados.Length; i++)
            {
                var conocido = i % 2 == 0;
                var remitente = conocido ? "contacto@cliente-norte.caemanager.local" : "desconocido@dominio-sin-identificar.caemanager.local";
                var conversacion = new Conversacion(
                    $"Cobertura · conversación {estados[i]}{(conocido ? "" : " (remitente desconocido)")}", conocido ? _norte.Id : null);

                var mensajes = i == 0 ? CoberturaPantallasMatriz.MensajesDeLaConversacionLarga : 3;
                for (var m = 0; m < mensajes; m++)
                {
                    var entrante = m % 2 == 0;
                    var mensaje = conversacion.AgregarMensaje(
                        entrante ? DireccionMensaje.Entrante : DireccionMensaje.Saliente, conversacion.Canal,
                        entrante ? remitente : "cae@alfa.caemanager.local",
                        $"<p>Mensaje {m + 1} de la conversación de cobertura ({estados[i]}).</p>",
                        ahora.AddDays(-mensajes).AddHours(m * 6));
                    if (m == 1 || m == mensajes - 1)
                        mensaje.AgregarAdjunto($"documento-cobertura-{i}-{m}.pdf", "application/pdf", 48_213, $"adjuntos-demo/cobertura-{i}-{m}.pdf");
                }

                conversacion.AgregarParticipante(
                    remitente, RolParticipante.De, conocido ? TipoParticipanteOrigen.UsuarioCliente : TipoParticipanteOrigen.Desconocido);
                conversacion.AgregarParticipante("cae@alfa.caemanager.local", RolParticipante.Para, TipoParticipanteOrigen.Desconocido);

                if (estados[i] != EstadoConversacion.Abierta)
                    conversacion.CambiarEstado(estados[i]);
                if (conocido)
                    conversacion.Asignar(administradorId);

                Add(conversacion);
            }
        }
    }

    /// <summary>El Tenant propietario Beta: un Centro con cien Trabajadores y una Empresa con trescientos documentos pendientes.</summary>
    private sealed class ConstructorBeta(CaeManagerDbContext db, DateOnly hoy) : ConstructorBase(db, hoy)
    {
        public async Task ConstruirAsync(CancellationToken cancellationToken)
        {
            await CargarCatalogoAsync(8_200_000, cancellationToken);

            var propia = Add(new Empresa(EmpresaPropiaBeta, SiguienteCif()));
            var cliente = Add(Empresa.CrearComoCliente(ClienteMasivo, SiguienteCif(), esCritico: false, notas: null, ejecutivoUsuarioId: null));
            Add(RelacionEmpresarial.Crear(propia.Id, cliente.Id, DateTime.UtcNow));

            var centro = NuevoCentro(cliente, propia, CentroMasivo, "CB-M1");
            CanalDePlataforma(centro, 3);

            for (var i = 0; i < CoberturaPantallasMatriz.TrabajadoresDelCentroMasivo; i++)
            {
                var trabajador = NuevoTrabajador(
                    propia, false, $"Masivo{i + 1:000}", $"Trabajador Volumen {i + 1:000}", DatosPruebaSeeder.GenerarDniValido(63_000_000 + i));
                Asignar(trabajador, centro);
            }

            // Trescientos tipos de Empresa que el Centro exige y la Empresa no ha entregado: trescientos pendientes.
            for (var i = 0; i < CoberturaPantallasMatriz.DocumentosPendientesDeLaEmpresaMasiva; i++)
            {
                var tipo = Add(new TipoDocumento(
                    $"{PrefijoTipoDeVolumen}{i + 1:000}", vigenciaMeses: 12, aplicaVencimientoAutomatico: true, orden: 2000 + i,
                    AmbitoAplicacion.Empresa, sensibilidad: SensibilidadDocumental.SinDatosPersonales));
                Db.TiposDocumentoCentros.Add(new TipoDocumentoCentro(tipo.Id, centro.Id, incluido: true));
            }

            await Db.SaveChangesAsync(cancellationToken);
        }
    }
}
