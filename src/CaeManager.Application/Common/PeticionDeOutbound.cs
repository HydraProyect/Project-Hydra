namespace CaeManager.Application.Common;

/// <summary>
/// ¿Es <c>TRequest</c> una petición (Command o Query) del módulo Outbound? Es la copia en tiempo de ejecución, solo para
/// <c>CaeManager.Application</c>, de lo que el registro de módulos de ADR-013 (M0a, <c>Modulos-registro.txt</c> de los
/// tests de arquitectura) dice de esas peticiones: las áreas de Outbound, las excepciones de Outbound dentro de áreas del
/// Núcleo (la acreditación frente a plataformas CAE y sus conectores) y las de Facturación dentro de Dashboard.
/// <c>PeticionesDeOutboundTests</c> compara las dos clasificaciones petición a petición, así que una petición nueva o una
/// línea nueva del registro que las haga divergir da rojo.
///
/// <para>
/// A través de <see cref="CerradaAlSoloInbound"/> la usan <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>,
/// para cerrar los comandos de Outbound a un Tenant solo Inbound con el papel de Tenant de origen
/// (<see cref="ICriterioDeOutbound"/>), y <see cref="AmbitoInboundBehavior{TRequest,TResponse}"/>, que fija
/// <see cref="AmbitoPeticionOutbound"/> para que <c>AlcanceDatosService</c> no dé alcance de Outbound a esas peticiones.
/// Los comandos del Núcleo y de Facturación no entran: un Tenant solo Inbound sigue administrando sus usuarios, sus
/// Empresas o su configuración. Comunicaciones, aunque es de Outbound, no se le cierra entera: se le abre acotada a los
/// hilos con sus Empresas contratistas (D6=a del 2026-11-19; <see cref="AcotadaAlSoloInbound"/> y
/// <see cref="AmbitoComunicacionesSoloInbound"/>).
/// </para>
/// </summary>
public static class PeticionDeOutbound
{
    private const string Raiz = "CaeManager.Application.";

    /// <summary>
    /// Áreas (primer segmento bajo <c>CaeManager.Application</c>) que el registro asigna a Outbound. Subcontratas sigue aquí
    /// aunque D5 (2026-11-19) la abra al Tenant TEC: esta clasificación es la del Tenant solo Inbound, al que sigue
    /// cerrada; la apertura al Tenant TEC vive en <see cref="RegistroLimiteTenantTec.AbiertasAlTenantTec"/>.
    /// </summary>
    private static readonly HashSet<string> AreasDeOutbound = new(StringComparer.Ordinal)
    {
        "Contactos", "Contacto", "Marco", "Clientes", "AsistenteIa", "Proyectos", "Subcontratas", "Visitas", "Gestiones",
        "Reclamaciones", "Incidencias", "Blindaje42", "Dashboard", "Reportes", "Bandeja", "Alertas", "VigilanciaNormativa",
        "Importacion", "VistaDemo", "Comunicaciones",
    };

    /// <summary>Excepciones de Outbound dentro de áreas del Núcleo (tipo o subnamespace).</summary>
    private static readonly string[] ExcepcionesDeOutbound =
    [
        "Documentos.Acreditacion",
        "Documentos.Commands.CambiarEstadoAcreditacionEnCentro",
        "Documentos.Commands.ConfirmarVigenciaAcreditacion",
        "Documentos.Commands.MarcarAcreditacionAceptada",
        "Documentos.Commands.MarcarAcreditacionNoRequerida",
        "Documentos.Commands.MarcarAcreditacionPendienteDeSubir",
        "Documentos.Commands.MarcarAcreditacionRechazada",
        "Documentos.Commands.MarcarAcreditacionSubida",
        "Documentos.Commands.RestaurarAnotacionAcreditacion",
        "Documentos.Queries.ObtenerAcreditacionesDelDocumento",
        "Documentos.Queries.ObtenerAcreditacionesPorProveedor",
        "Documentos.Queries.ObtenerPlataformasEnUso",
        "Integraciones.ConectoresPlataformaCae",
        "Integraciones.Commands.CambiarActivoProveedorPlataforma",
        "Integraciones.Commands.CambiarConectorApiProveedorPlataforma",
        "Integraciones.Commands.GuardarConexionPlataformaCae",
        "Integraciones.Commands.RetirarConexionPlataformaCae",
        "Integraciones.Queries.ObtenerProveedoresPlataformaCae",
    ];

    /// <summary>Excepciones de Facturación dentro del área Dashboard, que es de Outbound (tipo o subnamespace).</summary>
    private static readonly string[] ExcepcionesFueraDeOutbound =
    [
        "Dashboard.TarifasOperadorCae",
        "Dashboard.Queries.ObtenerFacturacionOperadorCaeQuery",
        "Dashboard.ReabrirMesCuadroDireccionCommand",
    ];

    /// <summary>Si <paramref name="tipoPeticion"/> es una petición del módulo Outbound de <c>CaeManager.Application</c>.</summary>
    public static bool Es(Type tipoPeticion)
    {
        var raiz = tipoPeticion;
        while (raiz.DeclaringType is { } contenedor)
            raiz = contenedor;

        if (raiz.FullName is not { } nombre || !nombre.StartsWith(Raiz, StringComparison.Ordinal))
            return false;

        var relativo = nombre[Raiz.Length..];
        if (ExcepcionesFueraDeOutbound.Any(clave => Casa(relativo, clave)))
            return false;
        if (ExcepcionesDeOutbound.Any(clave => Casa(relativo, clave)))
            return true;

        var punto = relativo.IndexOf('.');
        return punto > 0 && AreasDeOutbound.Contains(relativo[..punto]);
    }

    /// <summary>
    /// Áreas de Outbound que siguen abiertas, acotadas, a un Tenant solo Inbound. Comunicaciones: un Tenant TCE con solo
    /// Inbound conserva los hilos con sus Empresas contratistas (decisión de Chris del 2026-11-19, D6, opción a: «abrir un
    /// subconjunto»), así que la guarda no la cierra entera. El subconjunto lo acotan
    /// <see cref="CerradasDentroDeLasAreasAbiertas"/> (las peticiones que tocan Clientes empresariales, Centros de Cliente,
    /// Visitas, Gestiones o Incidencias, cerradas enteras) y <see cref="AmbitoComunicacionesSoloInbound"/> (el resto solo
    /// alcanza los hilos anclados a una Empresa). El registro de módulos la sigue asignando a Outbound; <see cref="Es"/> no
    /// cambia.
    /// </summary>
    private static readonly HashSet<string> AreasAbiertasAlSoloInbound = new(StringComparer.Ordinal) { "Comunicaciones" };

    /// <summary>
    /// Peticiones (tipo o subnamespace) de las áreas abiertas que siguen cerradas al Tenant solo Inbound, porque su objeto
    /// es de Outbound aunque vivan en Comunicaciones: una Incidencia (resolver o deshacer la derivación de una alerta
    /// crítica), una Visita (sugerencias de visita del correo), una Gestión (sugerencias de gestión), un Cliente empresarial
    /// (anclar un hilo a un Cliente) o un Centro de Cliente (pedir prioridad de validación, sus formatos requeridos).
    /// </summary>
    private static readonly string[] CerradasDentroDeLasAreasAbiertas =
    [
        "Comunicaciones.AlertasCriticas.ResolverAlertaCriticaCommand",
        "Comunicaciones.AlertasCriticas.DeshacerDerivacionAlertaCriticaCommand",
        "Comunicaciones.Commands.AsignarClienteConversacion",
        "Comunicaciones.Commands.DescartarSugerenciaGestion",
        "Comunicaciones.Commands.DescartarSugerenciaVisita",
        "Comunicaciones.Commands.PedirPrioridadValidacion",
        "Comunicaciones.Queries.ObtenerBorradorPedirPrioridad",
        "Comunicaciones.Queries.ObtenerFormatosRequeridosCentro",
        "Comunicaciones.Queries.ObtenerSugerenciaVisitaCorreo",
        "Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes",
    ];

    /// <summary>
    /// Si <paramref name="tipoPeticion"/> se cierra a un Tenant solo Inbound con el papel de Tenant de origen: una petición
    /// de Outbound (<see cref="Es"/>) fuera de <see cref="AreasAbiertasAlSoloInbound"/>, o dentro de ellas y en
    /// <see cref="CerradasDentroDeLasAreasAbiertas"/>. Lo usan la guarda de comandos y el ámbito de petición de Outbound que
    /// lee el alcance.
    /// </summary>
    public static bool CerradaAlSoloInbound(Type tipoPeticion)
    {
        if (!Es(tipoPeticion))
            return false;

        var relativo = Relativo(tipoPeticion);
        var punto = relativo.IndexOf('.');
        if (!(punto > 0 && AreasAbiertasAlSoloInbound.Contains(relativo[..punto])))
            return true;
        return CerradasDentroDeLasAreasAbiertas.Any(clave => Casa(relativo, clave));
    }

    /// <summary>
    /// Si <paramref name="tipoPeticion"/> es una petición de Outbound que queda abierta, acotada, al Tenant solo Inbound:
    /// la guarda fija para ella <see cref="AmbitoComunicacionesSoloInbound"/>, que la limita a los hilos anclados a una
    /// Empresa.
    /// </summary>
    public static bool AcotadaAlSoloInbound(Type tipoPeticion) => Es(tipoPeticion) && !CerradaAlSoloInbound(tipoPeticion);

    private static string Relativo(Type tipoPeticion)
    {
        var raiz = tipoPeticion;
        while (raiz.DeclaringType is { } contenedor)
            raiz = contenedor;
        return raiz.FullName![Raiz.Length..];
    }

    private static bool Casa(string relativo, string clave) =>
        relativo == clave || relativo.StartsWith(clave + ".", StringComparison.Ordinal);
}

/// <summary>
/// El subconjunto de Comunicaciones que conserva un Tenant solo Inbound con el papel de Tenant de origen (D6=a del
/// 2026-11-19, «hilos con sus Empresas contratistas»). Lo fija <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>
/// alrededor de cada petición acotada (<see cref="PeticionDeOutbound.AcotadaAlSoloInbound"/>) cuando
/// <see cref="ICriterioDeOutbound.CierraPorSoloInboundAsync"/> lo dice; no lo decide el cliente. Mientras está activo:
/// <list type="bullet">
/// <item><see cref="VisibilidadConversacion"/> solo deja pasar los hilos anclados a una Empresa (sin Cliente empresarial):
/// fuera los anclados a un Cliente y los que no tienen ancla (triaje y buzón personal);</item>
/// <item>los Clientes empresariales y los Centros no son visibles (<see cref="AlcanceDatosServiceExtensions"/>): macros
/// de un Cliente, mensajes nuevos a un Cliente, Centros de una Visita o de una intención, y la búsqueda por Centro de la
/// bandeja, que no liga ningún hilo;</item>
/// <item>el detalle del hilo no proyecta Visitas ni sus Centros, ni las sugerencias de Visita o de Gestión pendientes.</item>
/// </list>
/// Los handlers acotados piden la cartera de Clientes y de Centros por <see cref="AlcanceDatosServiceExtensions"/>, nunca a
/// <see cref="IAlcanceDatosService"/> directamente (<c>LecturasDeCarteraEnComunicacionesSoloInboundTests</c>).
/// Solo estrecha, nunca abre: no toca RLS ni <c>AlcanceDatosService</c>. Una petición anidada que no es acotada hereda el
/// marco (también estrecha); una acotada fija el suyo. Mismo patrón de marco que <see cref="AmbitoPeticionInbound"/>.
/// </summary>
public static class AmbitoComunicacionesSoloInbound
{
    private static readonly AsyncLocal<Marco?> Actual = new();

    /// <summary>Si el flujo actual está dentro de una petición acotada al subconjunto de D6, todavía en curso.</summary>
    public static bool Activo => Actual.Value is { Acotada: true, Vigente: true };

    /// <summary>Fija el marco de una petición acotada: activo si se acota, inactivo si no. Al liberar restaura el anterior.</summary>
    public static IDisposable Fijar(bool acotada)
    {
        var anterior = Actual.Value;
        var marco = new Marco(acotada);
        Actual.Value = marco;
        return new Cierre(marco, anterior);
    }

    private sealed class Marco(bool acotada)
    {
        public bool Acotada { get; } = acotada;
        public bool Vigente { get; set; } = true;
    }

    private sealed class Cierre(Marco marco, Marco? anterior) : IDisposable
    {
        public void Dispose()
        {
            marco.Vigente = false;
            Actual.Value = anterior;
        }
    }
}

/// <summary>
/// Dice si el flujo actual está dentro de una petición del módulo Outbound (<see cref="PeticionDeOutbound"/>). Mismo marco
/// compartido que <see cref="AmbitoPeticionInbound"/>: cada petición fija el suyo, también cerrado (una petición del Núcleo
/// dentro de una de Outbound no lo hereda), y al terminar lo invalida y restaura el anterior. Lo fija
/// <see cref="AmbitoInboundBehavior{TRequest,TResponse}"/>; no lo decide el cliente. Lo lee <c>AlcanceDatosService</c>
/// para no dar alcance de Outbound a un Tenant solo Inbound con el papel de Tenant de origen (<see cref="ICriterioDeOutbound"/>).
/// </summary>
public static class AmbitoPeticionOutbound
{
    private static readonly AsyncLocal<Marco?> Actual = new();

    /// <summary>Si el flujo actual está dentro de una petición de Outbound todavía en curso.</summary>
    public static bool Activo => Actual.Value is { Outbound: true, Vigente: true };

    /// <summary>Fija el ámbito de una petición: abierto si es de Outbound, cerrado si no. Al liberar restaura el anterior.</summary>
    public static IDisposable Fijar(bool outbound)
    {
        var anterior = Actual.Value;
        var marco = new Marco(outbound);
        Actual.Value = marco;
        return new Cierre(marco, anterior);
    }

    private sealed class Marco(bool outbound)
    {
        public bool Outbound { get; } = outbound;
        public bool Vigente { get; set; } = true;
    }

    private sealed class Cierre(Marco marco, Marco? anterior) : IDisposable
    {
        public void Dispose()
        {
            marco.Vigente = false;
            Actual.Value = anterior;
        }
    }
}
