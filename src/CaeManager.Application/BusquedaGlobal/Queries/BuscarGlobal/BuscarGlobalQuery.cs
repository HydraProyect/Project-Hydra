using CaeManager.Application.Centros;
using CaeManager.Application.Clientes;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;

/// <summary>
/// Alimenta el buscador global (Ctrl/Cmd+K, ver UX_PATTERNS.md, "Buscar") —
/// el reemplazo directo de la hoja "Filtros" manual del Excel original.
/// Cada resultado enlaza al listado del módulo correspondiente con el texto
/// ya cargado en el filtro (?q=), no a una página de detalle — todavía no
/// existen páginas de detalle por entidad.
/// </summary>
public record BuscarGlobalQuery(string Termino) : IRequest<ResultadoBusquedaGlobalDto>;

public record ItemBusquedaDto(Guid Id, string Titulo, string? Subtitulo, string UrlDestino);

public record ResultadoBusquedaGlobalDto(
    IReadOnlyList<ItemBusquedaDto> Clientes,
    IReadOnlyList<ItemBusquedaDto> Empresas,
    IReadOnlyList<ItemBusquedaDto> Subcontratas,
    IReadOnlyList<ItemBusquedaDto> Centros,
    IReadOnlyList<ItemBusquedaDto> Trabajadores)
{
    public bool TieneResultados =>
        Clientes.Count > 0 || Empresas.Count > 0 || Subcontratas.Count > 0 || Centros.Count > 0 || Trabajadores.Count > 0;
}

public class BuscarGlobalQueryHandler(
    ICentrosQueryContext centrosContext, IClientesQueryContext clientesContext, IEmpresasQueryContext empresasContext,
    ISubcontratasQueryContext subcontratasContext, ITrabajadoresQueryContext trabajadoresContext,
    IAlcanceDatosService alcanceDatos) : IRequestHandler<BuscarGlobalQuery, ResultadoBusquedaGlobalDto>
{
    private const int LimitePorCategoria = 5;

    public async Task<ResultadoBusquedaGlobalDto> Handle(BuscarGlobalQuery request, CancellationToken cancellationToken)
    {
        var termino = request.Termino.Trim();

        if (termino.Length < 2)
            return new ResultadoBusquedaGlobalDto([], [], [], [], []);

        var terminoMayus = termino.ToUpper();

        // Alcance de cartera en las cinco categorías. El buscador global es una
        // superficie de LISTADO —cada resultado enlaza al listado del módulo con
        // el término ya cargado (?q=)—, no un selector de "elige de la base
        // general": la excepción documentada en IAlcanceDatosService para
        // Trabajador/Vehículo no aplica aquí. Sin esto, un Gestor CAE veía por
        // Ctrl+K razones sociales, nombres y DNI de toda la organización, aunque
        // el listado al que aterrizaba después sí estuviera acotado.
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var subcontrataIdsVisibles = await alcanceDatos.ObtenerSubcontrataIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        var clientes = await clientesContext.Clientes
            .Where(c => clienteIdsVisibles == null || clienteIdsVisibles.Contains(c.Id))
            .Where(c => c.RazonSocial.ToUpper().Contains(terminoMayus))
            .OrderBy(c => c.RazonSocial)
            .Take(LimitePorCategoria)
            .Select(c => new ItemBusquedaDto(c.Id, c.RazonSocial, "Cliente", $"/clientes?q={Uri.EscapeDataString(c.RazonSocial)}"))
            .ToListAsync(cancellationToken);

        var empresas = await empresasContext.Empresas
            .Where(e => empresaIdsVisibles == null || empresaIdsVisibles.Contains(e.Id))
            .Where(e => e.RazonSocial.ToUpper().Contains(terminoMayus))
            .OrderBy(e => e.RazonSocial)
            .Take(LimitePorCategoria)
            .Select(e => new ItemBusquedaDto(e.Id, e.RazonSocial, "Empresa", $"/empresas?q={Uri.EscapeDataString(e.RazonSocial)}"))
            .ToListAsync(cancellationToken);

        var subcontratas = await subcontratasContext.Subcontratas
            .Where(s => subcontrataIdsVisibles == null || subcontrataIdsVisibles.Contains(s.Id))
            .Where(s => s.RazonSocial.ToUpper().Contains(terminoMayus))
            .OrderBy(s => s.RazonSocial)
            .Take(LimitePorCategoria)
            .Select(s => new ItemBusquedaDto(s.Id, s.RazonSocial, "Subcontrata", $"/subcontratas?q={Uri.EscapeDataString(s.RazonSocial)}"))
            .ToListAsync(cancellationToken);

        var centros = await centrosContext.Centros
            .Where(c => centroIdsVisibles == null || centroIdsVisibles.Contains(c.Id))
            .Where(c => c.Nombre.ToUpper().Contains(terminoMayus))
            .OrderBy(c => c.Nombre)
            .Take(LimitePorCategoria)
            .Select(c => new ItemBusquedaDto(c.Id, c.Nombre, "Centro", $"/centros?q={Uri.EscapeDataString(c.Nombre)}"))
            .ToListAsync(cancellationToken);

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(t.Id))
            .Where(t =>
                t.Nombre.ToUpper().Contains(terminoMayus) ||
                t.Apellidos.ToUpper().Contains(terminoMayus) ||
                t.Dni.ToUpper().Contains(terminoMayus))
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Take(LimitePorCategoria)
            .Select(t => new ItemBusquedaDto(
                t.Id, t.Nombre + " " + t.Apellidos, t.Dni, $"/trabajadores?q={Uri.EscapeDataString(t.Dni)}"))
            .ToListAsync(cancellationToken);

        return new ResultadoBusquedaGlobalDto(clientes, empresas, subcontratas, centros, trabajadores);
    }
}
