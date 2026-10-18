using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerPlataformaCaeCliente;

/// <summary>
/// La parte no secreta de la caja «Plataforma CAE» de Cliente 360: la plataforma del Cliente
/// empresarial, la dirección de su portal y el código de cliente. No lee ninguna credencial:
/// los accesos de cada Empresa van por <c>ObtenerAccesosPlataformaCaeClienteQuery</c>, marcada.
///
/// <para>
/// <c>null</c> = la caja no se pinta: Cliente empresarial fuera de la cartera, o Usuario de
/// Cliente (rol Cliente), que abre la ficha de su propio Cliente empresarial pero para quien
/// la plataforma con la que se le acredita es un artefacto interno de la gestión. El rol
/// Consulta sí la recibe (decisión del 17-10: «Consulta: solo plataforma y Abrir portal»).
/// </para>
/// </summary>
public record ObtenerPlataformaCaeClienteQuery(Guid ClienteEmpresarialId) : IRequest<PlataformaCaeClienteDto?>;

/// <param name="Registrada">False: el Cliente empresarial aún no tiene plataforma registrada.</param>
public record PlataformaCaeClienteDto(bool Registrada, string? Nombre, string? UrlPortal, string? CodigoCliente);

public class ObtenerPlataformaCaeClienteQueryHandler(IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerPlataformaCaeClienteQuery, PlataformaCaeClienteDto?>
{
    public async Task<PlataformaCaeClienteDto?> Handle(ObtenerPlataformaCaeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteEmpresarialId, cancellationToken)
            || !await alcanceDatos.OperaDesdeElLadoDeGestionAsync(cancellationToken))
            return null;

        if (!await dbContext.Empresas.AnyAsync(e => e.Id == request.ClienteEmpresarialId, cancellationToken))
            return null;

        var plataforma = await dbContext.PlataformasCaeClienteEmpresarial
            .Where(p => p.ClienteEmpresarialId == request.ClienteEmpresarialId)
            .Select(p => new PlataformaCaeClienteDto(true, p.Nombre, p.UrlPortal, p.CodigoCliente))
            .FirstOrDefaultAsync(cancellationToken);

        return plataforma ?? new PlataformaCaeClienteDto(false, null, null, null);
    }
}
