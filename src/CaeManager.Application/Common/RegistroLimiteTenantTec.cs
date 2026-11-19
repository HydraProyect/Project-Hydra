namespace CaeManager.Application.Common;

/// <summary>
/// Qué Commands cierra D3 al Tenant TEC participante sin línea Outbound vigente (decisiones de Chris del 2026-11-19,
/// DN-4 y DN-7). Lo consume <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/>. Las claves son tipos o
/// subnamespaces relativos a <c>CaeManager.Application</c>, como en el registro de módulos, para que el registro no dependa
/// en compilación de cada área; <c>RegistroLimiteTenantTecTests</c> exige que cada clave case con al menos un Command, de
/// modo que un renombrado da rojo en vez de abrir en silencio.
///
/// <para>
/// <b>Cerradas sin línea</b> (<see cref="CerradasSinLinea"/>): el alta de Centros, el alta de Clientes empresariales y las
/// delegaciones (DN-4), con sus restauraciones y reactivaciones (DN-7), lo que el Tenant TEC sin línea Outbound no es
/// (Operador CAE: la tarifa del Operador CAE, F-6; la acreditación frente a plataformas CAE de los canales de un Centro y
/// de un Cliente empresarial) y las importaciones, que crean Centros y Clientes empresariales sin pasar por sus Commands.
/// Las bajas quedan abiertas (DN-7): eliminar un Centro, un Cliente empresarial o un canal, desactivar una delegación,
/// revocar una asignación o retirar un encargo. El alta de una Empresa solo se cierra si la relaciona con Clientes
/// empresariales (<see cref="ILimiteTenantTecSegunDatos"/>; la Empresa propia sin Clientes empresariales sigue abierta), y
/// la edición de una Empresa, si le añade uno (<c>EditarEmpresaCommandHandler</c>).
/// </para>
///
/// <para>
/// <b>Pendientes de decisión</b> (<see cref="PendientesDeDecision"/>): no se cierran ni se abren; siguen como hoy (abiertas
/// en Application y, las de Outbound, cerradas en Web por <c>PaginaDeOutbound</c>). Subcontratas espera a D5, que es el
/// punto de extensión: D5 decide sobre esta entrada y no toca la guarda. Comunicaciones espera a DN-5.
/// </para>
///
/// <para>
/// <b>Punto de integración con <c>inbound/solo-inbound-sin-outbound</c></b>. DN-4 cierra «todo Outbound». La clasificación
/// de las áreas de Outbound (<c>PeticionDeOutbound</c>) llega con esa rama, que no está en main; hasta entonces este registro
/// solo cierra la lista explícita de arriba, y las demás áreas de Outbound (Visitas, Gestiones, Incidencias, acreditación,
/// Asistente IA salvo los Commands anidados que ya cierra, etc.) y las lecturas de DN-8 quedan sin cerrar en Application. Al
/// fusionarla, <see cref="CierraSinLinea"/> añade las peticiones de <c>PeticionDeOutbound.Es</c> menos
/// <see cref="PendientesDeDecision"/> y las bajas, y <c>ICriterioDeOutbound</c> añade esta misma condición al alcance.
/// </para>
/// </summary>
public static class RegistroLimiteTenantTec
{
    private const string Raiz = "CaeManager.Application.";

    /// <summary>Tipos o subnamespaces de Commands cerrados al Tenant TEC participante sin línea Outbound vigente.</summary>
    public static readonly IReadOnlyList<string> CerradasSinLinea =
    [
        // Centros (EP-4 «sin Centros propios»): alta y restauración; los canales de acceso a plataformas CAE, salvo la baja.
        "Centros.Commands.CrearCentro",
        "Centros.Commands.RestaurarCentro",
        "Centros.Commands.CrearCanalGestion",
        "Centros.Commands.EditarCanalGestion",
        "Centros.Commands.MarcarCanalGestionPrincipal",

        // Clientes empresariales (EP-4; el área es de Outbound): todo salvo las bajas.
        "Clientes.Commands.CrearCliente",
        "Clientes.Commands.RestaurarCliente",
        "Clientes.Commands.EditarCliente",
        "Clientes.Commands.ReasignarEjecutivoCliente",
        "Clientes.Commands.GuardarPlataformaCaeCliente",
        "Clientes.Commands.GuardarAccesoPlataformaCae",

        // Delegaciones, Asignación de Operación y Operador CAE externo (EP-4 «sin gestión de la CAE de terceros»).
        "Tenants.Commands.CrearDelegacionTenant",
        "Tenants.Commands.ReactivarDelegacionTenant",
        "Tenants.Commands.CrearAsignacionOperadorDelegado",
        "Tenants.Commands.RegistrarEncargoAdministracion",

        // Asignaciones de Cartera nuevas dentro de una Asignación de Operación (altas, DN-7).
        "Operaciones.ApoyoCartera.Commands.ProponerApoyoCarteraCommand",
        "Operaciones.ApoyoCartera.Commands.AceptarPropuestaApoyoCarteraCommand",
        "Operaciones.IncorporacionCartera.Commands.SolicitarIncorporacionCarteraCommand",
        "Operaciones.IncorporacionCartera.Commands.AceptarSolicitudIncorporacionCarteraCommand",

        // Importaciones (área de Outbound): crean Centros, Clientes empresariales y Empresas sin sus Commands.
        "Importacion.Commands",

        // Tarifa del Operador CAE (F-6): sin línea Outbound, el Tenant TEC no es Operador CAE.
        "Dashboard.TarifasOperadorCae.FijarTarifaOperadorCaeCommand",
    ];

    /// <summary>
    /// Fuera de D3 hasta su decisión: ni se cierran ni se abren. Cada entrada cita la decisión que la resolverá, y se
    /// retira de aquí al cerrarla.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PendientesDeDecision = new Dictionary<string, string>
    {
        ["Subcontratas"] = "D5 (Subcontratas del Tenant TEC)",
        ["Comunicaciones"] = "DN-5 (Comunicaciones del Tenant TEC sin línea Outbound)",
    };

    /// <summary>Si <paramref name="tipoComando"/> está en <see cref="CerradasSinLinea"/>.</summary>
    public static bool CierraSinLinea(Type tipoComando)
    {
        var raiz = tipoComando;
        while (raiz.DeclaringType is { } contenedor)
            raiz = contenedor;

        if (raiz.FullName is not { } nombre || !nombre.StartsWith(Raiz, StringComparison.Ordinal))
            return false;

        var relativo = nombre[Raiz.Length..];
        return CerradasSinLinea.Any(clave => Casa(relativo, clave));
    }

    /// <summary>Si <paramref name="tipoComando"/> está en <see cref="PendientesDeDecision"/>.</summary>
    public static bool EstaPendiente(Type tipoComando) =>
        tipoComando.FullName is { } nombre && nombre.StartsWith(Raiz, StringComparison.Ordinal)
        && PendientesDeDecision.Keys.Any(clave => Casa(nombre[Raiz.Length..], clave));

    /// <summary>Si el nombre <paramref name="relativo"/> es la clave o está debajo de ella.</summary>
    public static bool Casa(string relativo, string clave) =>
        relativo == clave || relativo.StartsWith(clave + ".", StringComparison.Ordinal);
}

/// <summary>
/// Un Command que D3 solo cierra según sus datos: <see cref="AbreLoCerradoAlTenantTec"/> dice si esta petición concreta
/// hace algo de <see cref="RegistroLimiteTenantTec.CerradasSinLinea"/> (p. ej. una Empresa nueva relacionada con
/// Clientes empresariales). Lo lee <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/>.
/// </summary>
public interface ILimiteTenantTecSegunDatos
{
    bool AbreLoCerradoAlTenantTec { get; }
}

/// <summary>
/// Un Command que actúa sobre una Asignación de Operación (en código, la entidad legacy <c>DelegacionTenant</c>: deuda
/// terminológica) cuyo Tenant propietario, el Tenant beneficiario, puede no ser el Tenant activo.
/// <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/> aplica el límite también a ese Tenant.
/// </summary>
public interface IComandoSobreAsignacionDeOperacion
{
    Guid AsignacionDeOperacionId { get; }
}
