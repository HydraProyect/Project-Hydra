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
/// La usan <see cref="GuardaPorServicioBehavior{TRequest,TResponse}"/>, para cerrar los comandos de Outbound a un Tenant
/// solo Inbound con el papel de Tenant de origen (<see cref="ICriterioDeOutbound"/>), y
/// <see cref="AmbitoInboundBehavior{TRequest,TResponse}"/>, que fija <see cref="AmbitoPeticionOutbound"/> para que
/// <c>AlcanceDatosService</c> no dé alcance de Outbound a esas peticiones. Los comandos del Núcleo y de Facturación no
/// entran: un Tenant solo Inbound sigue administrando sus usuarios, sus Empresas o su configuración.
/// </para>
/// </summary>
public static class PeticionDeOutbound
{
    private const string Raiz = "CaeManager.Application.";

    /// <summary>Áreas (primer segmento bajo <c>CaeManager.Application</c>) que el registro asigna a Outbound.</summary>
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

    private static bool Casa(string relativo, string clave) =>
        relativo == clave || relativo.StartsWith(clave + ".", StringComparison.Ordinal);
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
