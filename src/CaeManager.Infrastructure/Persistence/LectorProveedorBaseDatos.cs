using Microsoft.Extensions.Configuration;

namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Lee <c>Database:Proveedor</c>. Por defecto SQLite: un despliegue que no
/// diga nada tiene que seguir arrancando exactamente como hasta ahora.
/// Un valor escrito mal no cae de vuelta en silencio a SQLite — apuntar sin
/// querer a otro motor que el previsto es la clase de error que acaba
/// creando una base de datos vacía en paralelo a la de verdad.
///
/// Compartido entre el registro del DbContext y BackupHostedService, que
/// elige el mecanismo de backup según el motor — dos lecturas distintas de la
/// misma clave podrían divergir en el trato de errores.
/// </summary>
public static class LectorProveedorBaseDatos
{
    public static ProveedorBaseDatos Leer(IConfiguration configuration)
    {
        var valor = configuration["Database:Proveedor"];

        if (string.IsNullOrWhiteSpace(valor))
            return ProveedorBaseDatos.Sqlite;

        if (!Enum.TryParse<ProveedorBaseDatos>(valor, ignoreCase: true, out var proveedor))
            throw new InvalidOperationException(
                $"Database:Proveedor tiene el valor '{valor}', que no es válido. " +
                $"Valores admitidos: {string.Join(", ", Enum.GetNames<ProveedorBaseDatos>())}.");

        return proveedor;
    }
}
