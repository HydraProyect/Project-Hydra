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
    /// «Al corriente» — <b>no es un estado documental, es una ausencia</b>: el
    /// Cliente no tiene ninguna alerta abierta.
    ///
    /// <para>
    /// Viaja por el hilo como <c>"Vigente"</c> porque
    /// <see cref="ObtenerClientes.ObtenerClientesQuery"/> **secuestra** ese
    /// valor como centinela: <c>Vigente</c> nunca aparece en los estados
    /// presentes de un Cliente (las alertas no se emiten para lo que está en
    /// regla), así que servía para pedir «sin ninguna alerta» sin añadir un
    /// parámetro más. La constante existe para que ese secuestro esté
    /// <b>nombrado en los dos extremos</b> en vez de deducirse leyendo la
    /// consulta.
    /// </para>
    ///
    /// <para>
    /// ⚠️ <b>Deuda declarada</b>: sigue siendo un centinela. El día que las
    /// alertas emitan <c>Vigente</c>, este filtro dejará de significar lo que
    /// dice y no habrá nada que avise. El arreglo de verdad es un valor de
    /// filtro propio —como <see cref="SinDocumentos"/>—, y cuesta romper los
    /// filtros guardados y los enlaces que hoy llevan <c>?estado=Vigente</c>.
    /// </para>
    /// </summary>
    public const string AlCorriente = nameof(Domain.Documentos.EstadoDocumento.Vigente);

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
