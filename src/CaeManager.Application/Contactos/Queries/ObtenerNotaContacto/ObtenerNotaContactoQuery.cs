using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Contactos.Queries.ObtenerNotaContacto;

/// <summary>
/// Nota fija del interlocutor de una conversación: se busca por correo dentro de la agenda de la entidad a la que está
/// anclado el hilo. Null para quien no lee notas internas, si la entidad no es visible o si esa dirección no está en su agenda; un contacto sin nota
/// devuelve su ficha con <see cref="NotaContactoDto.Notas"/> en null, para poder añadirla.
/// </summary>
public record ObtenerNotaContactoQuery(TipoPropietarioAgenda Tipo, Guid PropietarioId, string Email) : IRequest<NotaContactoDto?>;

public record NotaContactoDto(Guid ContactoId, string Nombre, string? Notas, Guid? AutorUsuarioId, DateTime? ActualizadaEnUtc);

public class ObtenerNotaContactoQueryHandler(
    IContactosAgendaQueryContext contactosContext, IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerNotaContactoQuery, NotaContactoDto?>
{
    public async Task<NotaContactoDto?> Handle(ObtenerNotaContactoQuery request, CancellationToken cancellationToken)
    {
        // La agenda le llega al rol Cliente (portal) por el alcance de lectura; la nota con su autoría no: solo viaja a
        // quien opera desde el lado de gestión, con el mismo criterio que la nota interna de las fichas 360.
        if (!await politicaNotaInterna.PuedeLeerAsync(cancellationToken)) return null;

        var visible = request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => await alcanceDatos.ClienteVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Empresa => await alcanceDatos.EmpresaVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Subcontrata => await alcanceDatos.SubcontrataVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Centro => await alcanceDatos.CentroVisibleAsync(request.PropietarioId, cancellationToken),
            _ => false
        };

        if (!visible) return null;

        var email = request.Email.Trim().ToLowerInvariant();

        return await ObtenerAgendaContactosQueryHandler
            .FiltrarPorPropietario(contactosContext.ContactosAgenda, new ObtenerAgendaContactosQuery(request.Tipo, request.PropietarioId))
            .Where(c => c.Email.ToLower() == email)
            .OrderByDescending(c => c.EsPredeterminado)
            .ThenBy(c => c.Nombre)
            .Select(c => new NotaContactoDto(c.Id, c.Nombre, c.Notas, c.NotasAutorUsuarioId, c.NotasActualizadasEnUtc))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
