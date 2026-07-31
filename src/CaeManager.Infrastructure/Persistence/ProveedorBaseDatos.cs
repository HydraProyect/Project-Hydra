namespace CaeManager.Infrastructure.Persistence;

/// <summary>
/// Motor de base de datos con el que arranca la aplicación.
///
/// Existe porque la migración a PostgreSQL (`ADR-003`, condición de salida a
/// producción) no puede ser un cambio de un solo golpe: mientras se construye
/// y se valida el camino nuevo, la rama principal tiene que seguir
/// desplegándose sobre la base SQLite que hoy está en producción. Con el
/// motor elegido por configuración, ambos caminos conviven sin ramas largas.
///
/// Es transitorio a propósito. Cuando el corte a PostgreSQL esté hecho y
/// verificado, esta enumeración y la rama de SQLite se retiran — mantener dos
/// motores indefinidamente duplicaría el coste de cada consulta nueva y
/// contradice YAGNI (`PROJECT.md`).
/// </summary>
public enum ProveedorBaseDatos
{
    /// <summary>Archivo local. Lo que hay hoy en producción.</summary>
    Sqlite,

    /// <summary>Destino de la migración.</summary>
    PostgreSql
}
