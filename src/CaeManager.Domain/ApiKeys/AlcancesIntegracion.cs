namespace CaeManager.Domain.ApiKeys;

/// <summary>
/// Alcances por recurso de una <see cref="ClaveIntegracionTenant"/> (OWASP API5:2023, ASVS 4.0.3
/// V13.1.4). Estrechan lo que la clave lee, nunca lo amplían, y ninguno se concede solo.
/// <see cref="TrabajadoresDatosPersonalesLeer"/> añade campos a <see cref="TrabajadoresLeer"/> y no
/// vale sin él. No hay ningún alcance de datos de salud (decisión D-4 de la vía de integración).
/// </summary>
public static class AlcancesIntegracion
{
    public const string ClientesLeer = "clientes.leer";
    public const string CentrosLeer = "centros.leer";
    public const string AsignacionesLeer = "asignaciones.leer";
    public const string TrabajadoresLeer = "trabajadores.leer";
    public const string TrabajadoresDatosPersonalesLeer = "trabajadores.datos-personales.leer";
    public const string DocumentosLeer = "documentos.leer";

    public static readonly IReadOnlySet<string> Todos = new HashSet<string>(StringComparer.Ordinal)
    {
        ClientesLeer, CentrosLeer, AsignacionesLeer, TrabajadoresLeer, TrabajadoresDatosPersonalesLeer, DocumentosLeer,
    };
}
