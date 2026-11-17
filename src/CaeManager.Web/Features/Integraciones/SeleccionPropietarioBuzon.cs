namespace CaeManager.Web.Features.Integraciones;

/// <summary>Un Gestor CAE que se puede elegir como propietario de un buzón personal.</summary>
public sealed record GestorParaElegir(Guid Id, string NombreCompleto);

/// <summary>
/// Quién será el propietario del buzón de Microsoft 365 que se va a conectar y a qué URL del flujo OAuth se navega. Es el
/// estado y las reglas que Conexiones tenía en la propia página, sacados para que Conexiones y el cajón «Añadir buzón» de
/// Buzones sean exactamente lo mismo: Empresa sin rol CAE (legado terminológico, E29), buzón personal de un Gestor CAE y buzón
/// compartido son excluyentes entre sí, y la URL no cambia ni sus parámetros
/// (<c>ConectarMicrosoft365Endpoints</c> sigue siendo quien autoriza y valida).
/// </summary>
public sealed class SeleccionPropietarioBuzon
{
    public const string RutaConectar = "/integraciones/conectar-microsoft365";

    public Guid? EmpresaSeleccionadaId { get; private set; }

    public Guid? GestorPropietarioId { get; private set; }

    public string BuzonCompartido { get; private set; } = string.Empty;

    public bool HayBuzonCompartido => !string.IsNullOrWhiteSpace(BuzonCompartido);

    public bool HayEleccion => EmpresaSeleccionadaId is not null || GestorPropietarioId is not null || HayBuzonCompartido;

    /// <summary>Un buzón compartido nunca es el buzón personal de un Gestor CAE: escribir uno limpia esa elección.</summary>
    public void FijarBuzonCompartido(string valor)
    {
        BuzonCompartido = valor;
        if (!string.IsNullOrWhiteSpace(valor)) GestorPropietarioId = null;
    }

    /// <summary>Empresa sin rol CAE y buzón personal son mutuamente excluyentes — elegir uno limpia el otro.</summary>
    public void SeleccionarCliente(string valor)
    {
        EmpresaSeleccionadaId = Guid.TryParse(valor, out var id) ? id : null;
        if (EmpresaSeleccionadaId is not null) GestorPropietarioId = null;
    }

    public void SeleccionarGestorPropietario(string valor)
    {
        GestorPropietarioId = Guid.TryParse(valor, out var id) ? id : null;
        if (GestorPropietarioId is not null) EmpresaSeleccionadaId = null;
    }

    public void Limpiar()
    {
        EmpresaSeleccionadaId = null;
        GestorPropietarioId = null;
        BuzonCompartido = string.Empty;
    }

    public string UrlConectar
    {
        get
        {
            if (GestorPropietarioId is { } gestorId)
                return $"{RutaConectar}?gestorPropietarioId={gestorId}";

            var parametros = new List<string>();
            if (EmpresaSeleccionadaId is { } clienteId)
                parametros.Add($"clienteId={clienteId}");
            if (HayBuzonCompartido)
                parametros.Add($"buzonCompartido={Uri.EscapeDataString(BuzonCompartido.Trim())}");

            return parametros.Count == 0 ? RutaConectar : $"{RutaConectar}?{string.Join('&', parametros)}";
        }
    }
}
