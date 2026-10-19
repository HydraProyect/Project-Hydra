using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Features.Documentos.Recursos;

namespace CaeManager.Web.Features.Documentos;

/// <summary>
/// Qué hace «Gestionar» en una fila de documento vista desde un Centro o una plataforma (regla de Chris del 2026-10-19):
/// el estado en TALVEG (vigencia) y el estado en la plataforma (acreditación) son dos cosas distintas.
/// <list type="bullet">
/// <item>Si al documento le falta algo en TALVEG (no hay ninguno o está vencido), la acción es actualizarlo en la ficha 360
/// de su Empresa o Trabajador: desde un Centro no se sube nada.</item>
/// <item>Si no, «Gestionar» abre el panel «Gestionar acreditación en plataforma», que solo cambia el estado allí.</item>
/// <item>El botón va en gris cuando los dos estados están al día.</item>
/// </list>
/// </summary>
public static class GestionarEnPlataforma
{
    /// <summary>Al documento le falta algo en TALVEG: no hay ninguno o está vencido.</summary>
    public static bool FaltaEnTalveg(Guid? documentoId, EstadoDocumento estado) =>
        documentoId is null || estado is EstadoDocumento.Vencido or EstadoDocumento.Faltante;

    /// <summary>La acreditación ya no pide trabajo en esa plataforma.</summary>
    public static bool AcreditacionAlDia(EstadoAcreditacion estado) =>
        estado is EstadoAcreditacion.Aceptada or EstadoAcreditacion.NoRequerida;

    /// <summary>
    /// Los dos estados al día: en TALVEG, lo que ya llevaba el «Gestionar» discreto (Vigente o Sin caducidad; un «Por vencer»
    /// sigue pidiendo trabajo), y validado o no exigido en todas las plataformas del Centro.
    /// </summary>
    public static bool AmbosAlDia(EstadoDocumento estado, IReadOnlyList<AcreditacionResumenDto>? acreditaciones) =>
        EstadoDocumentoFicha360.GestionarEsDiscreto(estado) && (acreditaciones ?? []).All(a => AcreditacionAlDia(a.Estado));

    public static string TextoActualizar(bool esDeEmpresa) =>
        TextosGestionarAcreditacion.Texto(esDeEmpresa ? "ActualizarEnEmpresa" : "ActualizarEnTrabajador");

    /// <summary>Nombre de la ficha 360 del titular, para el aviso y el pie del panel.</summary>
    public static string TextoFicha(bool esDeEmpresa) =>
        TextosGestionarAcreditacion.Texto(esDeEmpresa ? "FichaEmpresa" : "FichaTrabajador");

    public static string RutaFicha(bool esDeEmpresa, Guid propietarioId) =>
        esDeEmpresa ? $"/empresas/{propietarioId}" : $"/trabajadores/{propietarioId}";

    /// <summary>Línea «En la plataforma»: el estado en cada plataforma del Centro, o que no se acredita en ninguna.</summary>
    public static string TextoEnPlataforma(IReadOnlyList<AcreditacionResumenDto>? acreditaciones) =>
        acreditaciones is { Count: > 0 }
            ? string.Join(" · ", acreditaciones.Select(a => a.NombrePlataforma + ": " + EstadoAcreditacionUi.Texto(a.Estado)))
            : TextosGestionarAcreditacion.Texto("SinPlataformaEnCentro");
}
