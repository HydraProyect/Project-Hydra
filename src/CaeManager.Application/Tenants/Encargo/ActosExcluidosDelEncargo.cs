using System.Collections.Concurrent;
using System.Reflection;
using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Configuracion.Commands.ActualizarPresupuestoIa;
using CaeManager.Application.Configuracion.Commands.ActualizarSeguridadAcceso;
using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Application.Integraciones.Commands.ActualizarLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.FijarAcuseAutomaticoBuzon;
using CaeManager.Application.Integraciones.Commands.FijarResponsableBuzon;
using CaeManager.Application.Integraciones.Commands.GuardarConexionPlataformaCae;
using CaeManager.Application.Integraciones.Commands.RetirarConexionPlataformaCae;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Commands.RepararMarcaHueco;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;
using MediatR;

namespace CaeManager.Application.Tenants.Encargo;

/// <summary>
/// Lista cerrada de lo que el Encargo de administración <b>no pleno</b> no abre
/// (decisión D-8, 2026-10-08), aunque el rol efectivo de quien administra por
/// encargo sea Administrador o Dirección CAE. La aplica
/// <see cref="Common.ExclusionesDelEncargoBehavior{TRequest,TResponse}"/>.
///
/// <para>
/// <b>Dos regímenes.</b> Con encargo <b>no pleno</b> (el Tenant propietario
/// tiene Administrador propio, activo o pendiente de activar) rige esta lista
/// entera. Con encargo <b>pleno</b> (el Tenant propietario no tiene
/// Administrador propio; decisión de Chris, 2026-10-15) no rige: quien
/// administra por encargo es Administrador pleno y el behavior deja pasar todas
/// estas peticiones. La señal es <see cref="Common.IEncargoDeAdministracionActual.EncargoPlenoAsync"/>,
/// y sin ella el behavior falla cerrado (rige la lista).
/// </para>
///
/// <para>
/// <b>Por qué hace falta.</b> Estas peticiones no comprueban el rol en su
/// handler: su única barrera de Administrador era la puerta de la página o del
/// endpoint, que mira el rol ya elevado. Sin esta lista, elevar el rol las
/// abriría todas de golpe.
/// </para>
///
/// <para>
/// <b>Qué se excluye y con qué grano.</b> Un área entera cuando todas sus
/// peticiones eran exclusivas de Administrador o Dirección CAE
/// (<see cref="AreasExcluidas"/>). Petición a petición cuando el área también
/// sirve a pantallas que la cartera ya tenía (<see cref="PeticionesExcluidas"/>):
/// excluir Integraciones entera dejaría al Gestor CAE sin buzón, y Auditoría
/// entera, sin la pestaña de historial de cada ficha. El encargo sube el techo:
/// <b>nunca quita lo que la cartera ya daba.</b>
/// </para>
///
/// <para>
/// Los tres actos excluidos de la decisión no están aquí porque no dependen de
/// actuar por encargo sino de la posición de quien actúa: cuentas con rol de
/// Propiedad (<c>CuentasConRolDePropiedad</c>), Operadores CAE externos
/// (<c>IAutorizacionDelegacionTenant</c>) y el propio encargo
/// (<see cref="AutoridadSobreElEncargo"/>). Con encargo pleno, las cuentas con
/// rol de Propiedad y la renuncia al propio encargo se abren en sus reglas
/// respectivas; los Operadores CAE externos siguen exigiendo ser Administrador
/// miembro del Tenant propietario.
/// </para>
///
/// <para>
/// <see cref="AreasPermitidas"/> decide una sola cosa en ejecución: que el área
/// resuelta exista. Un área que no está en ninguna de las dos listas no es área,
/// y la petición se excluye (<see cref="Excluye"/> falla cerrado). Además existe
/// para que <c>AreasClasificadasParaElEncargoTests</c> pueda exigir que toda área
/// con peticiones esté en una de las dos listas, y que un área nueva no quede sin
/// decidir.
/// </para>
///
/// <para>
/// <b>El área se resuelve por la marca del tipo, nunca por la carpeta</b> (ADR-013, M0c; ver
/// <see cref="AreaDe(Type)"/>): la <see cref="AreaDelEncargoAttribute"/> del propio tipo de la petición y,
/// sin ella, la de su base; una derivada nunca queda menos restringida que su base. Una petición sin
/// ninguna se queda sin área y se excluye. Cambiar el área de una petición exige editar su marca, que vive
/// en su propio fichero de <c>src</c>; la lista congelada de <c>AreaDelEncargoPorTipoTests</c> es un segundo
/// instrumento que debe coincidir con las marcas. Antes se deducía del espacio de nombres, y mover
/// <c>AutorizacionesLectura</c> a <c>CaeManager.Application.Inbound.*</c> la habría abierto bajo encargo
/// no pleno (área «Inbound», que no está excluida). <b>Nunca</b> se clasifica un segmento de módulo
/// («Inbound», «Outbound») como área.
/// </para>
///
/// <para>
/// <b>Por qué una marca en cada petición y no una tabla central.</b> Una tabla <c>typeof(Petición) → área</c>
/// en este proyecto referenciaba tipos de Outbound desde el Núcleo (232 aristas Núcleo → Outbound en el IL) y
/// rompía el ratchet de dependencias entre módulos de ADR-013 (M0b). La marca no añade ninguna arista hacia
/// arriba: cada petición solo depende del atributo, que vive en el Núcleo.
/// </para>
///
/// <para>
/// La aplican dos behaviors con la misma semántica: <see cref="Common.ExclusionesDelEncargoBehavior{TRequest,TResponse}"/>
/// a las peticiones de respuesta y <see cref="Common.ExclusionesDelEncargoStreamBehavior{TRequest,TResponse}"/> a
/// las de flujo (<see cref="IStreamRequest{TResponse}"/>), que MediatR no pasa por los primeros.
/// </para>
/// </summary>
public static class ActosExcluidosDelEncargo
{
    /// <summary>Áreas cuyas peticiones, todas, eran exclusivas de Administrador o Dirección CAE.</summary>
    public static readonly IReadOnlySet<string> AreasExcluidas = new HashSet<string>(StringComparer.Ordinal)
    {
        "ApiKeys",      // claves de API del Tenant propietario
        // Autorizaciones de lectura del Tenant de la Empresa contratista: todas sus peticiones son solo del rol
        // efectivo Administrador (AutorizacionLecturaAutoridad). Con encargo no pleno el Tenant propietario tiene
        // Administrador propio, que es quien las concede; con encargo pleno esta lista no rige.
        "AutorizacionesLectura",
        // Contacto de seguridad del Tenant beneficiario: lo propone su propio Administrador (leído en la base,
        // IAdministradorDelTenantPropietario) y lo valida un Actor de Plataforma TALVEG; ninguno de los dos actos
        // es de quien administra por encargo.
        "ContactosSeguridad",
        // Exportación completa del Tenant propietario en autoservicio (#2008, D-8): la pide y la descarga solo su
        // propio Administrador, nunca quien administra por encargo.
        "Exportacion",
        "Facturacion",  // tarifas, resumen y su exportación
        "Importacion",  // importaciones masivas (Clientes empresariales, Documentos, combinada)
        "Retencion",    // purga y retención
        // Decisión y aviso de un incidente de seguridad de Plataforma: solo un Actor de Plataforma TALVEG con
        // concesión AdminPlataforma global y segundo factor; nunca quien administra un Tenant por encargo.
        "Seguridad",
    };

    /// <summary>Peticiones excluidas de áreas que, por lo demás, quedan permitidas.</summary>
    public static readonly IReadOnlySet<Type> PeticionesExcluidas = new HashSet<Type>
    {
        // Auditoría: el detalle de un registro y el rastro de acceso a documentos sensibles. El
        // listado general se decide por su filtro: ver Excluye.
        typeof(ObtenerRegistroAuditoriaPorIdQuery),
        typeof(ObtenerAccesosDocumentosSensiblesQuery),
        // Auditoría de IA: la única petición del área DocumentosIa hoy; se nombra por tipo para que
        // una petición operativa futura del área no nazca excluida ni permitida por accidente.
        typeof(ObtenerAuditoriaIaQuery),
        // Integraciones: conectar y desconectar buzones de Microsoft 365 y líneas de WhatsApp. Leer el
        // buzón y saber qué conexiones existen sigue permitido: lo usan las pantallas de Comunicaciones.
        typeof(ConectarBuzonMicrosoft365Command),
        typeof(DesconectarBuzonCommand),
        typeof(RepararMarcaHuecoCommand),
        typeof(ReactivarConexionCommand),
        typeof(FijarResponsableBuzonCommand),
        typeof(FijarAcuseAutomaticoBuzonCommand),
        typeof(CrearLineaWhatsAppCommand),
        typeof(ActualizarLineaWhatsAppCommand),
        typeof(GuardarConexionPlataformaCaeCommand),
        typeof(RetirarConexionPlataformaCaeCommand),
        typeof(ObtenerLineasWhatsAppQuery),
        // Configuración global de IA del Tenant propietario y su presupuesto.
        typeof(ActualizarLecturaIaGlobalCommand),
        typeof(ActualizarVerificacionIaGlobalCommand),
        typeof(ActualizarDeteccionTrabajadoresGlobalCommand),
        typeof(ActualizarPerfilDocumentoOficialGlobalCommand),
        typeof(ActualizarPresupuestoIaCommand),
        // Seguridad de acceso del Tenant: solo su Administrador, leído en la base. Leerla sigue permitido.
        typeof(ActualizarSeguridadAccesoCommand),
    };

    /// <summary>
    /// Áreas con peticiones que el encargo no cierra por entero. Las que tienen
    /// alguna petición en <see cref="PeticionesExcluidas"/> también están aquí.
    /// </summary>
    public static readonly IReadOnlySet<string> AreasPermitidas = new HashSet<string>(StringComparer.Ordinal)
    {
        "Alertas", "Asignaciones", "AsistenteIa", "Auditoria", "Bandeja", "Blindaje42", "BusquedaGlobal",
        "Calendario", "CatalogoBase", "Centros", "Clientes", "Comercial", "Comunicaciones", "Configuracion", "Contactos", "ContratosAcceso",
        "Cumplimiento", "Dashboard", "Documentos", "DocumentosIa", "Empresas", "Gestiones", "HerramientasPdf", "Incidencias",
        "Integraciones", "Marco", "Notificaciones", "Operaciones", "Plantillas", "Plataforma", "Proyectos",
        "Reclamaciones", "Reportes", "Subcontratas", "Telemetria", "Tenants",
        "TiposDocumento", "Trabajadores", "Usuarios", "Vehiculos", "VigilanciaNormativa", "Visitas",
    };

    /// <summary>
    /// La petición está fuera del encargo. Recibe la petición y no solo su tipo
    /// por el registro de auditoría: <see cref="ObtenerAuditoriaQuery"/> acotada
    /// a una entidad es la pestaña de historial de una ficha, que cualquier rol
    /// de cartera ya veía; sin entidad es el registro de auditoría del Tenant
    /// propietario entero, que era solo del Administrador.
    ///
    /// <para>
    /// <b>Falla cerrado</b> (ADR-013, M0c): una petición sin área resuelta
    /// (<see cref="AreaDe(Type)"/> devuelve <c>null</c>) se excluye. Los handlers de
    /// estas áreas no comprueban el rol (D-8), así que dejarla pasar daría a
    /// quien administra por encargo no pleno el poder de un Administrador sobre
    /// algo que nadie ha clasificado; excluirla solo le niega un acto que el
    /// Administrador propio del Tenant propietario sigue pudiendo hacer, y
    /// <c>AreasClasificadasParaElEncargoTests</c> pone el commit en rojo antes de
    /// que llegue a ejecutarse.
    /// </para>
    /// </summary>
    public static bool Excluye(object peticion)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        var tipo = peticion.GetType();

        return AreaDe(tipo) is not { } area
               || AreasExcluidas.Contains(area)
               || DerivaDeUnaPeticionExcluida(tipo)
               || peticion is ObtenerAuditoriaQuery { EntidadId: null };
    }

    /// <summary>
    /// La petición o alguno de sus tipos base está en <see cref="PeticionesExcluidas"/>. La lista va por
    /// tipo, y comparar solo el exacto abriría una petición que heredase de una excluida: una derivada
    /// nunca queda menos restringida que su base.
    /// </summary>
    private static bool DerivaDeUnaPeticionExcluida(Type tipo)
    {
        for (var t = tipo; t is not null; t = t.BaseType)
        {
            if (PeticionesExcluidas.Contains(t))
                return true;
        }

        return false;
    }

    private static readonly ConcurrentDictionary<Type, string?> AreasResueltas = new();

    /// <summary>
    /// El área de una petición, resuelta por la marca de su tipo y nunca por su carpeta (ADR-013, M0c; Codex r2, #2241):
    /// <list type="number">
    ///   <item>la de su <see cref="AreaDelEncargoAttribute"/>, si la lleva;</item>
    ///   <item>si no la lleva, la de su tipo base si es una petición y, si no, la marca más cercana de su cadena
    ///   de tipos base (la marca no se hereda sola: <c>Inherited = false</c>);</item>
    ///   <item>si tampoco, ninguna: <c>null</c>.</item>
    /// </list>
    /// Ninguno depende del espacio de nombres: mover una petición de carpeta no le cambia el área, y cambiársela
    /// exige editar su marca, código de <c>src</c>. Solo vale un área clasificada (<see cref="AreasExcluidas"/> o
    /// <see cref="AreasPermitidas"/>); cualquier otra cosa —una petición nueva sin marca, una marca con un área
    /// inexistente— da <c>null</c>, y <see cref="Excluye"/> la excluye.
    ///
    /// <para>
    /// <b>Una derivada nunca queda menos restringida que su base</b>: si un tipo base lleva la marca
    /// de un área excluida, o es una petición excluida por área o sin área, la derivada toma esa área
    /// (o <c>null</c>) aunque declare otra marca con un área permitida.
    /// </para>
    /// </summary>
    public static string? AreaDe(Type tipoPeticion)
    {
        ArgumentNullException.ThrowIfNull(tipoPeticion);
        return AreasResueltas.GetOrAdd(tipoPeticion, ResolverArea);
    }

    private static string? ResolverArea(Type tipoPeticion)
    {
        // Una marca de área excluida en cualquier tipo base, sea o no una petición, prevalece.
        for (var t = tipoPeticion.BaseType; t is not null; t = t.BaseType)
        {
            if (Marca(t) is { } marcaDeLaBase && AreasExcluidas.Contains(marcaDeLaBase))
                return marcaDeLaBase;
        }

        string? areaDeLaBase = null;
        if (tipoPeticion.BaseType is { } tipoBase && EsPeticion(tipoBase))
        {
            areaDeLaBase = ResolverArea(tipoBase);
            if (areaDeLaBase is null || AreasExcluidas.Contains(areaDeLaBase))
                return areaDeLaBase;
        }

        // Marca propia; si no, la de la base petición o la marca más cercana de la cadena: una derivada sin
        // marca toma el área de su base, nunca una menos restringida.
        var area = Marca(tipoPeticion)
                   ?? areaDeLaBase
                   ?? MarcaHeredadaMasCercana(tipoPeticion);
        return area is not null && EsClasificada(area) ? area : null;
    }

    /// <summary>
    /// Una petición de MediatR: de respuesta (<see cref="IBaseRequest"/>) o de flujo
    /// (<see cref="IStreamRequest{TResponse}"/>, que no deriva de <see cref="IBaseRequest"/>).
    /// </summary>
    private static bool EsPeticion(Type tipo) =>
        typeof(IBaseRequest).IsAssignableFrom(tipo)
        || tipo.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStreamRequest<>));

    private static bool EsClasificada(string area) => AreasExcluidas.Contains(area) || AreasPermitidas.Contains(area);

    private static string? Marca(Type tipo) => tipo.GetCustomAttribute<AreaDelEncargoAttribute>(inherit: false)?.Area;

    private static string? MarcaHeredadaMasCercana(Type tipoPeticion)
    {
        for (var t = tipoPeticion.BaseType; t is not null; t = t.BaseType)
        {
            if (Marca(t) is { } marca)
                return marca;
        }

        return null;
    }
}

/// <summary>
/// Una consulta excluida del Encargo de administración no devuelve
/// <c>Result</c>, así que no hay fallo que construir: se interrumpe. No debería
/// verse en pantalla —la página y el endpoint ya se niegan antes—; si llega, es
/// que una pantalla permitida envía una petición excluida.
/// </summary>
public sealed class ActoExcluidoDelEncargoException(string nombrePeticion)
    : InvalidOperationException($"{ErroresEncargoAdministracion.ActoExcluido.Mensaje} ({nombrePeticion})");
