using CaeManager.Application.Auditoria.Queries;
using CaeManager.Application.Configuracion.Commands.ActualizarPresupuestoIa;
using CaeManager.Application.DocumentosIa.Queries;
using CaeManager.Application.Integraciones.Commands.ActualizarLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.ConectarBuzonMicrosoft365;
using CaeManager.Application.Integraciones.Commands.CrearLineaWhatsApp;
using CaeManager.Application.Integraciones.Commands.DesconectarBuzon;
using CaeManager.Application.Integraciones.Commands.ReactivarConexion;
using CaeManager.Application.Integraciones.Queries.ObtenerLineasWhatsApp;
using CaeManager.Application.TiposDocumento.Commands.ActualizarDeteccionTrabajadoresGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarLecturaIaGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarPerfilDocumentoOficialGlobal;
using CaeManager.Application.TiposDocumento.Commands.ActualizarVerificacionIaGlobal;

namespace CaeManager.Application.Tenants.Encargo;

/// <summary>
/// Lista cerrada de lo que el Encargo de administración <b>no</b> abre
/// (decisión D-8, 2026-10-08), aunque el rol efectivo de quien administra por
/// encargo sea Administrador o Dirección CAE. La aplica
/// <see cref="Common.ExclusionesDelEncargoBehavior{TRequest,TResponse}"/>.
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
/// (<see cref="AutoridadSobreElEncargo"/>).
/// </para>
///
/// <para>
/// <see cref="AreasPermitidas"/> no decide nada en ejecución: existe para que
/// <c>AreasClasificadasParaElEncargoTests</c> pueda exigir que toda área con
/// peticiones esté en una de las dos listas, y que un área nueva no quede
/// abierta bajo encargo sin que alguien lo haya decidido.
/// </para>
/// </summary>
public static class ActosExcluidosDelEncargo
{
    private const string RaizDeAreas = "CaeManager.Application.";

    /// <summary>Áreas cuyas peticiones, todas, eran exclusivas de Administrador o Dirección CAE.</summary>
    public static readonly IReadOnlySet<string> AreasExcluidas = new HashSet<string>(StringComparer.Ordinal)
    {
        "ApiKeys",      // claves de API del Tenant propietario
        "Facturacion",  // tarifas, resumen y su exportación
        "Importacion",  // importaciones masivas (Clientes empresariales, Documentos, combinada)
        "Retencion",    // purga y retención
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
        typeof(ReactivarConexionCommand),
        typeof(CrearLineaWhatsAppCommand),
        typeof(ActualizarLineaWhatsAppCommand),
        typeof(ObtenerLineasWhatsAppQuery),
        // Configuración global de IA del Tenant propietario y su presupuesto.
        typeof(ActualizarLecturaIaGlobalCommand),
        typeof(ActualizarVerificacionIaGlobalCommand),
        typeof(ActualizarDeteccionTrabajadoresGlobalCommand),
        typeof(ActualizarPerfilDocumentoOficialGlobalCommand),
        typeof(ActualizarPresupuestoIaCommand),
    };

    /// <summary>
    /// Áreas con peticiones que el encargo no cierra por entero. Las que tienen
    /// alguna petición en <see cref="PeticionesExcluidas"/> también están aquí.
    /// </summary>
    public static readonly IReadOnlySet<string> AreasPermitidas = new HashSet<string>(StringComparer.Ordinal)
    {
        "Alertas", "Asignaciones", "AsistenteIa", "Auditoria", "Bandeja", "Blindaje42", "BusquedaGlobal",
        "Calendario", "Centros", "Clientes", "Comercial", "Comunicaciones", "Configuracion", "Contactos",
        "Cumplimiento", "Dashboard", "Documentos", "DocumentosIa", "Empresas", "Gestiones", "Incidencias",
        "Integraciones", "Notificaciones", "Operaciones", "Plantillas", "Plataforma", "Proyectos",
        "Reclamaciones", "Reportes", "Subcontratas", "Telemetria", "Tenants",
        "TiposDocumento", "Trabajadores", "Usuarios", "Vehiculos", "VigilanciaNormativa", "Visitas",
    };

    /// <summary>
    /// La petición está fuera del encargo. Recibe la petición y no solo su tipo
    /// por el registro de auditoría: <see cref="ObtenerAuditoriaQuery"/> acotada
    /// a una entidad es la pestaña de historial de una ficha, que cualquier rol
    /// de cartera ya veía; sin entidad es el registro de auditoría del Tenant
    /// propietario entero, que era solo del Administrador.
    /// </summary>
    public static bool Excluye(object peticion)
    {
        ArgumentNullException.ThrowIfNull(peticion);
        var tipo = peticion.GetType();

        return (AreaDe(tipo) is { } area && AreasExcluidas.Contains(area))
               || PeticionesExcluidas.Contains(tipo)
               || peticion is ObtenerAuditoriaQuery { EntidadId: null };
    }

    /// <summary>
    /// El área de una petición: el segmento de su espacio de nombres que sigue a
    /// <c>CaeManager.Application.</c>, o <c>null</c> si no vive bajo esa raíz.
    /// </summary>
    public static string? AreaDe(Type tipoPeticion)
    {
        ArgumentNullException.ThrowIfNull(tipoPeticion);
        if (tipoPeticion.Namespace is not { } espacio || !espacio.StartsWith(RaizDeAreas, StringComparison.Ordinal))
            return null;

        var resto = espacio[RaizDeAreas.Length..];
        var fin = resto.IndexOf('.', StringComparison.Ordinal);
        return fin < 0 ? resto : resto[..fin];
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
