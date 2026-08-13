using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Clientes.Queries.ObtenerClientes;

public record ObtenerClientesQuery(
    string? Busqueda, bool? SoloCriticos, int Pagina = 1, int TamanoPagina = 20,
    string? OrdenarPor = null, bool Descendente = false)
    : IRequest<ResultadoPaginado<ClienteListaDto>>;

/// <param name="SinContactoEnAgenda">
/// Perfil incompleto: no hay nadie a quien reclamarle documentación. La
/// reclamación de este cliente fallará hasta que la agenda tenga al menos un
/// contacto (decisión del usuario, 2026-08-13) — se expone en el listado para
/// que el hueco se pueda cerrar en bloque, no de uno en uno al fallar el envío.
/// </param>
public record ClienteListaDto(
    Guid Id, string RazonSocial, string Cif, bool EsCritico, DateTime CreadoEnUtc, bool SinContactoEnAgenda = false);

public class ObtenerClientesQueryHandler(
    IClientesQueryContext dbContext, IContactosAgendaQueryContext contactosContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerClientesQuery, ResultadoPaginado<ClienteListaDto>>
{
    public async Task<ResultadoPaginado<ClienteListaDto>> Handle(ObtenerClientesQuery request, CancellationToken cancellationToken)
    {
        var consulta = dbContext.Clientes.AsQueryable();

        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        if (clienteIdsVisibles is not null)
            consulta = consulta.Where(c => clienteIdsVisibles.Contains(c.Id));

        if (!string.IsNullOrWhiteSpace(request.Busqueda))
        {
            var busqueda = request.Busqueda.ToUpper();
            consulta = consulta.Where(c => c.RazonSocial.ToUpper().Contains(busqueda));
        }

        if (request.SoloCriticos == true)
            consulta = consulta.Where(c => c.EsCritico);

        var total = await consulta.CountAsync(cancellationToken);

        // Lista blanca de columnas ordenables (nombres de propiedad de
        // ClienteListaDto, que es lo que envía QuickGrid): un OrdenarPor
        // desconocido cae al orden por defecto, nunca se interpola en SQL.
        var ordenada = (request.OrdenarPor, request.Descendente) switch
        {
            (nameof(ClienteListaDto.RazonSocial), true) => consulta.OrderByDescending(c => c.RazonSocial),
            (nameof(ClienteListaDto.Cif), false) => consulta.OrderBy(c => c.Cif),
            (nameof(ClienteListaDto.Cif), true) => consulta.OrderByDescending(c => c.Cif),
            (nameof(ClienteListaDto.EsCritico), false) => consulta.OrderBy(c => c.EsCritico).ThenBy(c => c.RazonSocial),
            (nameof(ClienteListaDto.EsCritico), true) => consulta.OrderByDescending(c => c.EsCritico).ThenBy(c => c.RazonSocial),
            (nameof(ClienteListaDto.CreadoEnUtc), false) => consulta.OrderBy(c => c.CreadoEnUtc),
            (nameof(ClienteListaDto.CreadoEnUtc), true) => consulta.OrderByDescending(c => c.CreadoEnUtc),
            _ => consulta.OrderBy(c => c.RazonSocial)
        };
        // Desempate estable: sin un criterio total, PostgreSQL puede devolver
        // las filas empatadas en distinto orden entre una página y otra, y al
        // paginar en SQL eso hace que una fila aparezca dos veces o no
        // aparezca nunca. El Id no se ordena nunca por sí solo — solo cierra
        // el orden que haya elegido el usuario.
        ordenada = ordenada.ThenBy(c => c.Id);

        var elementos = await ordenada
            .Skip((request.Pagina - 1) * request.TamanoPagina)
            .Take(request.TamanoPagina)
            .Select(c => new ClienteListaDto(c.Id, c.RazonSocial, c.Cif, c.EsCritico, c.CreadoEnUtc))
            .ToListAsync(cancellationToken);

        // Una sola consulta para la página entera, no una por fila.
        var idsPagina = elementos.Select(c => c.Id).ToList();
        var clientesConAgenda = await contactosContext.ContactosAgenda
            .Where(contacto => contacto.ClienteId != null && idsPagina.Contains(contacto.ClienteId.Value))
            .Select(contacto => contacto.ClienteId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        var conAgenda = clientesConAgenda.ToHashSet();
        var conAviso = elementos
            .Select(c => c with { SinContactoEnAgenda = !conAgenda.Contains(c.Id) })
            .ToList();

        return new ResultadoPaginado<ClienteListaDto>(conAviso, total, request.Pagina, request.TamanoPagina);
    }
}
