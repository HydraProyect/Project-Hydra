namespace CaeManager.Web.Features.BusquedaGlobal;

/// <summary>Permite abrir el buscador global desde cualquier componente (p. ej. el botón de la barra superior) sin acoplarlos entre sí.</summary>
public class BusquedaGlobalService
{
    public event Action? SolicitudAbrir;

    public void Abrir() => SolicitudAbrir?.Invoke();
}
