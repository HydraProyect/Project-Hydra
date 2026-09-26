namespace CaeManager.Domain.Plataforma;

/// <summary>
/// Qué puede hacer un usuario de la plataforma sobre un tenant ajeno —
/// ADR-011 § 8.2.
///
/// <b>Son capacidades, no un rol.</b> La matriz del plano 3 es
/// <i>capability-based</i> a propósito: nunca un
/// <c>PlatformAdmin</c> monolítico del que cuelguen implícitamente todas. Meter
/// "puede consultar documentos de cualquier tenant" dentro de
/// <see cref="AdminPlataforma"/> reintroduciría exactamente el problema que
/// este plano elimina — administrar la infraestructura y leer el contenido de
/// los clientes son dos permisos distintos que se conceden por separado.
/// </summary>
public enum CapacidadPrivilegio
{
    /// <summary>
    /// Inspección de solo lectura de un tenant. Sin escritura operativa, sin
    /// excepción implícita: la escritura excepcional es
    /// <see cref="BreakGlass"/>, y es otra concesión. Admite concesión de
    /// alcance global (Soporte TALVEG universal, ADR-011 § 8.9), pero cada
    /// entrada sigue siendo una <see cref="SesionPrivilegiada"/> sobre un único
    /// Tenant objetivo.
    /// </summary>
    SoporteLectura = 0,

    /// <summary>
    /// Reproducir la sesión de un usuario concreto. La autorización se evalúa
    /// con el contexto del simulado; la identidad del actor real se conserva
    /// siempre (ADR-011 § 8.4). Simular no amplía: quien impersona ve
    /// exactamente lo que vería esa persona, ni un dato más.
    /// </summary>
    Impersonacion = 1,

    /// <summary>
    /// Escritura excepcional para resolver un incidente. Exige motivo,
    /// duración, auditoría íntegra y revisión posterior. No se concede por
    /// defecto ni se deduce de ninguna otra capacidad.
    /// </summary>
    BreakGlass = 2,

    /// <summary>
    /// Gestión de tenants, facturación, configuración global, diagnóstico.
    /// <b>No implica leer el contenido documental de ningún tenant</b>: para
    /// eso hace falta <see cref="SoporteLectura"/>, concedida aparte.
    /// </summary>
    AdminPlataforma = 3,

    /// <summary>
    /// Alta planificada de contenido CAE en un tenant durante su
    /// aprovisionamiento inicial. Mismo régimen que <see cref="BreakGlass"/>
    /// —motivo, ventana, auditoría, revisión, nunca por defecto ni deducida—
    /// pero semántica distinta: esto no es un incidente, es un alta acordada.
    /// Se separan para que las métricas de uso de <see cref="BreakGlass"/>
    /// sigan midiendo excepciones y no altas comerciales. La escritura que
    /// habilita queda acotada al tenant objetivo de la sesión y a los
    /// comandos marcados <c>IComandoDeAprovisionamiento</c> — ver
    /// <c>AutorizacionEscrituraBehavior</c>.
    /// </summary>
    Aprovisionamiento = 4,

    /// <summary>
    /// Soporte TALVEG restablece la verificación en dos pasos del
    /// <b>Administrador único</b> de un Tenant que ha perdido el móvil y los
    /// códigos de recuperación (ADR-011 § 8.7, punto 3, opción B de P0-8). Es una
    /// capacidad acotada a un solo acto, no una vía de escritura general:
    /// <list type="bullet">
    /// <item>concesión por Tenant, nunca global (<c>AdmiteAlcanceGlobal</c>), y
    /// solo por <c>ConcederPrivilegioCommand</c>: la emite un AdminPlataforma a
    /// otro usuario de plataforma, nunca uno a sí mismo;</item>
    /// <item>se ejerce desde una <see cref="SesionPrivilegiada"/> abierta sobre ese
    /// Tenant, que sigue leyendo con el rol de solo lectura;</item>
    /// <item>el único comando que admite es el restablecimiento de P0-8, y la
    /// escritura la hace una función de la base que vuelve a comprobar la
    /// sesión, la concesión y la cuenta (<c>app_restablecer_segundo_factor_por_soporte</c>).</item>
    /// </list>
    /// No convierte a Soporte TALVEG en Administrador, Gestor CAE ni Operador CAE
    /// del Tenant: dentro de la sesión su rol efectivo sigue siendo nulo.
    /// </summary>
    RestablecimientoSegundoFactor = 5
}
