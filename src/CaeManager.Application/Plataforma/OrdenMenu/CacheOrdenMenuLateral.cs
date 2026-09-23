namespace CaeManager.Application.Plataforma.OrdenMenu;

/// <summary>
/// Caché global (singleton) del orden del menú lateral: el menú se pinta en cada página de cada
/// usuario, y el orden cambia muy de vez en cuando. Se invalida al guardar
/// (<see cref="GuardarOrdenMenuLateralCommandHandler"/>); quien tenga una página abierta ve el
/// cambio al recargar, y eso es aceptable según la decisión del 2026-09-23.
///
/// <para>
/// La generación evita la única carrera que importa: una lectura que empezó antes de un guardado
/// y termina después no puede volver a dejar en la caché el orden viejo. <see cref="Guardar"/>
/// solo acepta el valor si nadie invalidó desde que se leyó la generación.
/// </para>
///
/// <para>
/// Es de un proceso. Con más de una instancia de la aplicación, las demás verían el cambio al
/// reiniciarse; hoy se despliega una sola instancia.
/// </para>
/// </summary>
public sealed class CacheOrdenMenuLateral
{
    private sealed record Entrada(OrdenMenuLateralDto? Valor);

    private readonly Lock _cerrojo = new();
    private Entrada? _entrada;
    private long _generacion;

    public bool IntentarObtener(out OrdenMenuLateralDto? valor, out long generacion)
    {
        lock (_cerrojo)
        {
            generacion = _generacion;
            valor = _entrada?.Valor;
            return _entrada is not null;
        }
    }

    public void Guardar(OrdenMenuLateralDto? valor, long generacionLeida)
    {
        lock (_cerrojo)
        {
            if (generacionLeida == _generacion)
                _entrada = new Entrada(valor);
        }
    }

    public void Invalidar()
    {
        lock (_cerrojo)
        {
            _generacion++;
            _entrada = null;
        }
    }
}
