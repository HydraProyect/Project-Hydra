using CaeManager.Application.Common;

namespace CaeManager.Application.Centros.Queries;

/// <summary>
/// Papel con el que la Empresa del Centro entra en él (DECISION_LOG, 2026-11-08, «Papel por Centro»).
/// <see cref="EntraSubcontratada"/> = falso: entra como contratista principal y no se muestra nada.
/// Verdadero solo ante quien ve a X, la contratista principal, entre sus Clientes empresariales visibles; entonces
/// <see cref="ContratistaPrincipalId"/> y <see cref="ContratistaPrincipalRazonSocial"/> la nombran. Ante quien no
/// ve a X el resultado es el mismo que sin ancla: ni el nombre ni la marca de subcontrata, porque la marca sola ya
/// le dice que hay un ancla hacia alguien fuera de su Asignación de Cartera.
/// </summary>
public sealed record PapelDeEntradaDto(
    bool EntraSubcontratada, Guid? ContratistaPrincipalId, string? ContratistaPrincipalRazonSocial)
{
    public static readonly PapelDeEntradaDto ContratistaPrincipal = new(false, null, null);
}

/// <summary>
/// Decide el papel a partir de lo que la consulta de la ficha (<c>ObtenerCentroPorIdQueryHandler</c>) ya leyó del
/// ancla <c>Centro.RelacionEmpresarialEntradaId</c> en la misma sentencia SQL; no hay columna de rol (ADR-011). Esa
/// consulta lee la arista en el Tenant propietario del Centro y vuelve a comprobar lo que fijó
/// <c>Centro.AnclarRelacionDeEntrada</c> (proveedora = Empresa del Centro, cliente ≠ titular): un ancla que ya no
/// cumpla llega aquí como X = null y da contratista principal, que no enseña nada. La vigencia no se vuelve a
/// comprobar: si la arista se cierra después, el papel sigue saliendo hasta que alguien quite o cambie el ancla
/// (pendiente de decisión). Quien llama ya ha comprobado que el Centro es visible.
///
/// <para>
/// El badge es para el lado de gestión CAE (DECISION_LOG: «para que el Gestor CAE sepa…»): al rol Cliente
/// (portal) le llega contratista principal, que no enseña nada, hasta que se decida otra cosa.
/// </para>
/// </summary>
public static class PapelDeEntradaEnCentro
{
    public static async Task<PapelDeEntradaDto> ResolverAsync(
        Guid? contratistaPrincipalId,
        string? contratistaPrincipalRazonSocial,
        IAlcanceDatosService alcanceDatos,
        CancellationToken cancellationToken)
    {
        if (contratistaPrincipalId is not { } xId)
            return PapelDeEntradaDto.ContratistaPrincipal;

        if (!await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return PapelDeEntradaDto.ContratistaPrincipal;

        // X fuera de la cartera de quien mira: igual que sin ancla, para no revelar que la hay.
        if (!await alcanceDatos.ClienteVisibleAsync(xId, cancellationToken))
            return PapelDeEntradaDto.ContratistaPrincipal;

        return contratistaPrincipalRazonSocial is null
            ? new PapelDeEntradaDto(true, null, null)
            : new PapelDeEntradaDto(true, xId, contratistaPrincipalRazonSocial);
    }
}
