namespace CaeManager.Application.Common;

/// <summary>
/// Qué Commands cierra D3 al Tenant TEC participante sin línea Outbound vigente (decisiones de Chris del 2026-11-19,
/// DN-4 y DN-7). Lo consume <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/>. Las claves son tipos o
/// subnamespaces relativos a <c>CaeManager.Application</c>, como en el registro de módulos, para que el registro no dependa
/// en compilación de cada área; <c>LimiteTenantTecBehaviorTests.Cada_clave_del_registro_nombra_al_menos_un_Command</c>
/// exige que cada clave case con al menos un Command, de modo que un renombrado da rojo en vez de abrir en silencio. La
/// clasificación de todos los Commands (cerrado aquí, cerrado en su handler, abierto con su motivo o no aplica) está
/// congelada en <c>tests/CaeManager.Architecture.Tests/Datos/LimiteTenantTecClasificacion.txt</c>.
///
/// <para>
/// <b>Cerrados en su handler</b>, porque el Tenant beneficiario no es el Tenant activo o depende de los datos: las altas de
/// Asignaciones de Cartera (solicitar y aceptar una incorporación, proponer y aceptar un apoyo, «Asignar empresas», el alta
/// de un Gestor CAE con cartera y el traspaso al desactivar), que deciden por el Tenant beneficiario de cada cartera; la
/// tarifa del Operador CAE (F-6), que decide por el Tenant de origen del Operador CAE; y la edición y la restauración de una
/// Empresa, que solo se cierran si añaden o devuelven Relaciones Empresariales con Clientes empresariales. Desde la ronda 3
/// (EP-4: sin Producto Contratado P no hay Operador CAE), todas las vías que hacen de un Tenant el Operador CAE de otro
/// deciden también por el Tenant de origen de ese Operador CAE: las altas de cartera de arriba, la delegación, el alta del
/// Tenant propietario de un Operador CAE externo, el alta de un cliente delegante y el Encargo de administración.
/// </para>
///
/// <para>
/// <b>Cerradas sin línea</b> (<see cref="CerradasSinLinea"/>): el alta de Centros, el alta de Clientes empresariales y las
/// delegaciones (DN-4), con sus restauraciones y reactivaciones (DN-7), lo que el Tenant TEC sin línea Outbound no es
/// (Operador CAE: la acreditación frente a plataformas CAE de los canales de un Centro y
/// de un Cliente empresarial) y las importaciones, que crean Centros y Clientes empresariales sin pasar por sus Commands.
/// Las bajas quedan abiertas (DN-7): eliminar un Centro, un Cliente empresarial o un canal, desactivar una delegación,
/// revocar una asignación o retirar un encargo. El alta de una Empresa solo se cierra si la relaciona con Clientes
/// empresariales (<see cref="ILimiteTenantTecSegunDatos"/>; la Empresa propia sin Clientes empresariales sigue abierta), y
/// la edición de una Empresa, si le añade uno (<c>EditarEmpresaCommandHandler</c>).
/// </para>
///
/// <para>
/// <b>Abiertas al Tenant TEC por decisión</b> (<see cref="AbiertasAlTenantTec"/>): áreas de Outbound que el Tenant TEC
/// conserva aunque no tenga línea Outbound. Subcontratas, por D5 (DECISION_LOG del 2026-11-19, «Pantallas de Inbound y
/// Outbound: rutas mixtas, rótulos y capacidades del Tenant TEC»: «se le abre», EP-3 y tarjeta 23): la lista, la ficha y
/// todos sus Commands y Queries. Ninguna clave de <see cref="CerradasSinLinea"/> puede caer dentro de ellas. DN-7 no les
/// aplica: rige dentro de lo que D3 cierra, y DN-4 deja fuera las Subcontratas. Abrirlas al Tenant TEC no las abre al
/// Tenant solo Inbound: <c>PeticionDeOutbound</c> las sigue clasificando como Outbound y la guarda por servicio se las
/// cierra con su propia condición, que es otra. Ni <c>PaginaDeOutbound</c> ni el menú miran el marcador del Tenant TEC, así
/// que la página y su entrada del menú tampoco se le cierran.
/// </para>
///
/// <para>
/// <b>Pendientes de decisión</b> (<see cref="PendientesDeDecision"/>): no se cierran ni se abren; siguen como hoy (abiertas
/// en Application y en Web para el Tenant TEC: <c>PaginaDeOutbound</c> no lo mira). Comunicaciones espera a DN-5.
/// </para>
///
/// <para>
/// <b>Punto de integración con «solo Inbound» (#2379)</b>. DN-4 cierra «todo Outbound». La clasificación de las áreas de
/// Outbound (<c>PeticionDeOutbound</c>) y <c>ICriterioDeOutbound</c> ya están en main desde #2379, pero esta guarda todavía
/// no los consume: este registro solo cierra la lista explícita de arriba, y las demás áreas de Outbound (Visitas,
/// Gestiones, Incidencias, acreditación, Asistente IA salvo los Commands anidados que ya cierra, etc.) y las lecturas de
/// DN-8 quedan sin cerrar en Application para el Tenant TEC marcado sin línea Outbound. <c>ICriterioDeOutbound</c> solo
/// cierra al Tenant solo Inbound, que es otra condición. El incremento que lo integre hace que <see cref="CierraSinLinea"/>
/// añada las peticiones de <c>PeticionDeOutbound.Es</c> menos <see cref="AbiertasAlTenantTec"/>,
/// <see cref="PendientesDeDecision"/> y las bajas, y que <c>ICriterioDeOutbound</c> añada esta misma condición al alcance
/// y a las páginas sin cerrar <see cref="AbiertasAlTenantTec"/>.
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

        // Importaciones (área de Outbound): crean Centros, Clientes empresariales y Empresas sin sus Commands.
        "Importacion.Commands",
    ];

    /// <summary>
    /// Fuera de D3 hasta su decisión: ni se cierran ni se abren. Cada entrada cita la decisión que la resolverá, y se
    /// retira de aquí al cerrarla.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> PendientesDeDecision = new Dictionary<string, string>
    {
        ["Comunicaciones"] = "DN-5 (Comunicaciones del Tenant TEC sin línea Outbound)",
    };

    /// <summary>
    /// Áreas o subnamespaces de Outbound abiertos al Tenant TEC sin línea Outbound vigente por una decisión, con la decisión
    /// que los abre. Nada de <see cref="CerradasSinLinea"/> puede caer dentro.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> AbiertasAlTenantTec = new Dictionary<string, string>
    {
        ["Subcontratas"] = "D5 (Subcontratas abiertas al Tenant TEC; DECISION_LOG 2026-11-19)",
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

    /// <summary>Si <paramref name="tipoPeticion"/> está en <see cref="AbiertasAlTenantTec"/>.</summary>
    public static bool EstaAbiertaAlTenantTec(Type tipoPeticion) =>
        tipoPeticion.FullName is { } nombre && nombre.StartsWith(Raiz, StringComparison.Ordinal)
        && AbiertasAlTenantTec.Keys.Any(clave => Casa(nombre[Raiz.Length..], clave));

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
/// Un Command que actúa sobre una <c>DelegacionTenant</c> (entidad legacy de la vía heredada de la Asignación de Operación:
/// deuda terminológica) cuyo Tenant beneficiario, su <c>TenantClienteId</c>, puede no ser el Tenant activo.
/// <see cref="LimiteTenantTecBehavior{TRequest,TResponse}"/> la lee y aplica el límite también a ese Tenant y al Tenant de
/// origen de su Operador CAE (<c>TenantConsultoraId</c>, EP-4); si no la encuentra, deniega.
/// </summary>
public interface IComandoSobreDelegacionTenant
{
    Guid DelegacionTenantId { get; }
}
