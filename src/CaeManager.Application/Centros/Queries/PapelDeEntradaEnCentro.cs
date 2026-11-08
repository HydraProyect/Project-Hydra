using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Queries;

/// <summary>
/// Papel con el que la Empresa del Centro entra en él (DECISION_LOG, 2026-11-08, «Papel por Centro»).
/// <see cref="EntraSubcontratada"/> = falso: entra como contratista principal y no se muestra nada.
/// <see cref="ContratistaPrincipalId"/> y <see cref="ContratistaPrincipalRazonSocial"/> nombran a X, la
/// contratista principal, solo si X está entre los Clientes empresariales visibles de quien mira; si no, van a
/// null aunque el papel sea subcontrata (análisis del 2026-11-08: nombrar a X ante quien no lo ve le enseña una
/// contraparte).
/// </summary>
public sealed record PapelDeEntradaDto(
    bool EntraSubcontratada, Guid? ContratistaPrincipalId, string? ContratistaPrincipalRazonSocial)
{
    public static readonly PapelDeEntradaDto ContratistaPrincipal = new(false, null, null);
}

/// <summary>
/// Calcula el papel a partir del ancla <c>Centro.RelacionEmpresarialEntradaId</c>; no hay columna de rol
/// (ADR-011). La arista se lee en el Tenant propietario del Centro, y se vuelve a comprobar lo que fijó
/// <c>Centro.AnclarRelacionDeEntrada</c> (proveedora = Empresa del Centro, cliente ≠ titular): un ancla que
/// ya no cumpla da contratista principal, que no enseña nada. Quien llama ya ha comprobado que el Centro es
/// visible.
/// </summary>
public static class PapelDeEntradaEnCentro
{
    public static async Task<PapelDeEntradaDto> CalcularAsync(
        Guid centroId,
        ICentrosQueryContext centrosContext,
        IEmpresasQueryContext empresasContext,
        IAlcanceDatosService alcanceDatos,
        CancellationToken cancellationToken)
    {
        var contratistaPrincipalId = await (
            from centro in centrosContext.Centros
            where centro.Id == centroId && centro.RelacionEmpresarialEntradaId != null
            from relacion in empresasContext.RelacionesEmpresariales
            where relacion.Id == centro.RelacionEmpresarialEntradaId
                && relacion.TenantId == centro.TenantId
                && relacion.ProveedoraId == centro.EmpresaId
                && relacion.ClienteId != centro.ClienteId
            select (Guid?)relacion.ClienteId)
            .FirstOrDefaultAsync(cancellationToken);

        if (contratistaPrincipalId is not { } xId)
            return PapelDeEntradaDto.ContratistaPrincipal;

        var clientesVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clientesVisibles is not null && !clientesVisibles.Contains(xId))
            return new PapelDeEntradaDto(true, null, null);

        var razonSocial = await empresasContext.Empresas
            .Where(e => e.Id == xId)
            .Select(e => e.RazonSocial)
            .FirstOrDefaultAsync(cancellationToken);

        return razonSocial is null
            ? new PapelDeEntradaDto(true, null, null)
            : new PapelDeEntradaDto(true, xId, razonSocial);
    }
}
