namespace CaeManager.Application.EstadoMaterializado.Frescura;

/// <summary>
/// Los avisos anotados en una transacción todavía abierta, que solo salen si se confirma (diseño § 5.3), con los puntos de
/// guardado que hay entre medias: el recálculo en línea escribe dentro de un <c>SAVEPOINT</c> y, si falla, vuelve a él
/// (diseño § 4.5), y lo que se anotó después de abrirlo no llegó a la base.
///
/// <para>
/// El interceptor de transacciones de EF no recibe el nombre del punto de guardado, así que el buzón no sabe a cuál se
/// vuelve. Por eso, al volver a un punto, descarta todo lo anotado desde el más antiguo abierto: descartar de más es
/// seguro (la caché ve una versión sin aviso y relee entero) y descartar de menos no (anunciaría una versión que no se
/// confirmó con unas entidades que no son las suyas).
/// </para>
/// </summary>
public sealed class BuzonAvisosEstado
{
    private readonly object _cerrojo = new();
    private readonly List<AvisoCambioEstado> _pendientes = [];

    // Por cada punto de guardado abierto, cuántos avisos había al abrirlo.
    private readonly Stack<int> _puntos = new();

    /// <summary>Avisos anotados que saldrían con el <c>COMMIT</c>.</summary>
    public int Pendientes
    {
        get { lock (_cerrojo) return _pendientes.Count; }
    }

    public void Anotar(AvisoCambioEstado aviso)
    {
        ArgumentNullException.ThrowIfNull(aviso);
        lock (_cerrojo) _pendientes.Add(aviso);
    }

    /// <summary>Se abrió un punto de guardado.</summary>
    public void AbrirPunto()
    {
        lock (_cerrojo) _puntos.Push(_pendientes.Count);
    }

    /// <summary>Se volvió a un punto de guardado: fuera lo anotado desde el más antiguo abierto (ver la clase).</summary>
    public void VolverAPunto()
    {
        lock (_cerrojo)
        {
            if (_puntos.Count == 0)
            {
                // Un punto que el buzón no vio abrir (se abrió antes de existir el buzón): no se sabe qué sobrevive.
                _pendientes.Clear();
                return;
            }

            var desde = _puntos.Min();
            _pendientes.RemoveRange(desde, _pendientes.Count - desde);
        }
    }

    /// <summary>Se liberó un punto de guardado: lo anotado dentro sigue en la transacción.</summary>
    public void LiberarPunto()
    {
        lock (_cerrojo)
        {
            if (_puntos.Count > 0)
                _puntos.Pop();
        }
    }

    /// <summary>
    /// Deja solo los avisos de <paramref name="tenantConexion"/>, el Tenant de la conexión en que se va a confirmar; sin
    /// Tenant en la conexión, ninguno. Devuelve cuántos descartó.
    /// </summary>
    public int QuedarseConTenant(Guid? tenantConexion)
    {
        lock (_cerrojo)
        {
            var antes = _pendientes.Count;
            _pendientes.RemoveAll(a => a.TenantId != tenantConexion);
            if (_pendientes.Count != antes)
                _puntos.Clear(); // las marcas ya no apuntan a lo mismo; un VolverAPunto posterior descarta todo, que es seguro
            return antes - _pendientes.Count;
        }
    }

    /// <summary>La transacción se confirmó: devuelve lo pendiente, en orden, y vacía el buzón.</summary>
    public IReadOnlyList<AvisoCambioEstado> Tomar()
    {
        lock (_cerrojo)
        {
            var avisos = _pendientes.ToArray();
            _pendientes.Clear();
            _puntos.Clear();
            return avisos;
        }
    }

    /// <summary>La transacción se deshizo, falló o empieza otra: nada de lo anotado salió.</summary>
    public void Descartar()
    {
        lock (_cerrojo)
        {
            _pendientes.Clear();
            _puntos.Clear();
        }
    }
}
