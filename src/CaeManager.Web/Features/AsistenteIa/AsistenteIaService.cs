namespace CaeManager.Web.Features.AsistenteIa;

/// <summary>Abre/cierra el panel de chat desde cualquier componente (el botón flotante) sin acoplarlos — mismo patrón que BusquedaGlobalService.</summary>
public class AsistenteIaService
{
    public event Action? SolicitudAbrir;

    public void Abrir() => SolicitudAbrir?.Invoke();
}
