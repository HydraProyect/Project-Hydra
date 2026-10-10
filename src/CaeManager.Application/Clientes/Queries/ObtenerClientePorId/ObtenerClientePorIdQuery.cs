using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientePorId;

public record ObtenerClientePorIdQuery(Guid Id) : IRequest<ClienteDetalleDto?>;

/// <summary>
/// <paramref name="Version"/> viaja al formulario para volver en el Command
/// de edición: es lo que permite detectar que otra persona guardó mientras
/// tanto. Sin ella, el handler recarga la fila justo antes de escribir y el
/// token de concurrencia compara el valor consigo mismo — la columna existe,
/// pero nunca choca (ver EditarClienteCommandHandler).
///
/// <paramref name="Notas"/> es la «Nota interna» del equipo de gestión sobre el
/// Cliente empresarial: solo viaja a quien lo tiene en su alcance de GESTIÓN
/// (<see cref="IAlcanceDatosService.ObtenerClienteIdsParaGestionAsync"/>). Al
/// Usuario de Cliente, que abre su propia ficha por alcance de lectura, le
/// llega <c>null</c>. Quien reenvía este campo a <c>EditarClienteCommand</c>
/// necesita un rol con escritura, y todos ellos son de gestión: ningún
/// llamador que pueda guardar recibe la nota vaciada.
/// </summary>
public record ClienteDetalleDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, string? Notas, DateTime CreadoEnUtc,
    Guid? EjecutivoUsuarioId, Guid Version);

/// <summary>
/// F3b — reemplaza <c>IClientesQueryContext</c> por <c>IEmpresasQueryContext</c>:
/// lee por Id ya conocido, sin necesitar saber si la fila "es" Cliente
/// (lector de categoría B, ver f3b-clasificacion-lectores).
/// </summary>
public class ObtenerClientePorIdQueryHandler(IEmpresasQueryContext dbContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientePorIdQuery, ClienteDetalleDto?>
{
    public async Task<ClienteDetalleDto?> Handle(ObtenerClientePorIdQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.Id, cancellationToken)) return null;

        // Alcance de GESTIÓN para la nota, no de lectura: la ficha le llega al
        // usuario de portal porque es la de su Cliente empresarial; la nota no.
        var conNotaInterna = await alcanceDatos.ClienteParaGestionVisibleAsync(request.Id, cancellationToken);

        return await dbContext.Empresas
            .Where(c => c.Id == request.Id)
            .Select(c => new ClienteDetalleDto(
                c.Id, c.RazonSocial, c.Cif!, c.EsCritico ?? false, conNotaInterna ? c.Notas : null, c.CreadoEnUtc,
                c.EjecutivoUsuarioId, c.Version))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
