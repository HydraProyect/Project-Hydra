using CaeManager.Domain.Documentos;

namespace CaeManager.Application.Documentos;

/// <summary>
/// Vocabulario del filtro de "estado documental" que usan los listados de
/// Trabajador, Empresa y Vehículo — entidades sin estado propio en el modelo,
/// cuyo estado se deriva de sus Documentos (ver
/// <see cref="ICalculoEstadoDocumentalService"/>).
///
/// Vive aquí y no en cada Query para que las tres pantallas admitan
/// exactamente los mismos valores: son la misma pregunta hecha sobre tres
/// tablas distintas.
/// </summary>
public static class EstadoDocumentalFiltro
{
    /// <summary>No es un estado de vigencia: el propietario no tiene ningún Documento.</summary>
    public const string SinDocumentos = "SinDocumentos";

    /// <summary>
    /// <paramref name="estado"/> es null cuando el propietario no tiene
    /// Documentos. Un filtro vacío o desconocido no descarta nada, igual que
    /// un <c>OrdenarPor</c> desconocido cae al orden por defecto.
    /// </summary>
    public static bool Coincide(EstadoDocumento? estado, string? filtro)
    {
        if (string.IsNullOrWhiteSpace(filtro))
            return true;

        if (filtro == SinDocumentos)
            return estado is null;

        return Enum.TryParse<EstadoDocumento>(filtro, out var esperado)
            ? estado == esperado
            : true;
    }

    /// <summary>
    /// Clave de orden: primero lo que más urge. Sin documentos va al final —
    /// no es peor que "vencido", solo desconocido.
    /// </summary>
    public static int ClaveOrden(EstadoDocumento? estado) => estado switch
    {
        EstadoDocumento.Vencido => 0,
        EstadoDocumento.Urgente => 1,
        EstadoDocumento.Proximo => 2,
        EstadoDocumento.Vigente => 3,
        EstadoDocumento.SinCaducidad => 4,
        _ => 5
    };
}
