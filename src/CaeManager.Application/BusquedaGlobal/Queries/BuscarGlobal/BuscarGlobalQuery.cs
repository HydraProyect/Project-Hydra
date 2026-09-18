using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Application.Vehiculos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.BusquedaGlobal.Queries.BuscarGlobal;

/// <summary>
/// Alimenta el Command Palette (Ctrl/Cmd+K, Parte XVI PROMPT 05) — el
/// reemplazo directo de la hoja "Filtros" manual del Excel original. Cada
/// resultado enlaza a la ficha de la entidad (Trabajador 360, Centro 360)
/// cuando existe; para Empresa/Documento, que todavía no tienen una página
/// de detalle propia, enlaza al listado del módulo con el texto ya
/// cargado en el filtro (?q=).
/// </summary>
public record BuscarGlobalQuery(string Termino) : IRequest<ResultadoBusquedaGlobalDto>;

public record ItemBusquedaDto(Guid Id, string Titulo, string? Subtitulo, string UrlDestino);

/// <param name="Empresas">
/// Una fila por Empresa, sin importar cuántos papeles contextuales tenga
/// (Cliente empresarial, Subcontrata) — el contrato de lenguaje (ADR-011)
/// los trata como papeles dentro de una Relación Empresarial, nunca como
/// tipos de Empresa, así que no pueden ser categorías separadas del
/// buscador (P41d, 2026-09-18). <see cref="ItemBusquedaDto.Subtitulo"/>
/// lleva aquí los papeles en bruto separados por coma ("Cliente",
/// "Subcontrata"; vacío si no tiene ninguno) — la traducción a etiqueta
/// canónica ("Cliente empresarial · Subcontrata") es cosa de la capa Web,
/// igual que ya hacía para el resto de categorías.
/// </param>
public record ResultadoBusquedaGlobalDto(
    IReadOnlyList<ItemBusquedaDto> Empresas,
    IReadOnlyList<ItemBusquedaDto> Centros,
    IReadOnlyList<ItemBusquedaDto> Trabajadores,
    IReadOnlyList<ItemBusquedaDto> Documentos)
{
    public bool TieneResultados =>
        Empresas.Count > 0 || Centros.Count > 0 || Trabajadores.Count > 0 || Documentos.Count > 0;
}

public class BuscarGlobalQueryHandler(
    ICentrosQueryContext centrosContext, IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    IVehiculosQueryContext vehiculosContext, IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos) : IRequestHandler<BuscarGlobalQuery, ResultadoBusquedaGlobalDto>
{
    private const int LimitePorCategoria = 5;

    public async Task<ResultadoBusquedaGlobalDto> Handle(BuscarGlobalQuery request, CancellationToken cancellationToken)
    {
        var termino = request.Termino.Trim();

        if (termino.Length < 2)
            return new ResultadoBusquedaGlobalDto([], [], [], []);

        var terminoMayus = termino.ToUpper();

        // Alcance de cartera en las cuatro categorías. El buscador global es una
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

        var empresas = await BuscarEmpresasAsync(
            terminoMayus, clienteIdsVisibles, empresaIdsVisibles, subcontrataIdsVisibles, cancellationToken);

        // Centro y Trabajador SÍ tienen ficha propia (Centro 360, Trabajador
        // 360) — a diferencia de Empresa/Documento, que siguen enlazando al
        // listado con filtro porque no la tienen todavía.
        var centros = await centrosContext.Centros
            .Where(c => centroIdsVisibles == null || centroIdsVisibles.Contains(c.Id))
            .Where(c => c.Nombre.ToUpper().Contains(terminoMayus))
            .OrderBy(c => c.Nombre)
            .Take(LimitePorCategoria)
            .Select(c => new ItemBusquedaDto(c.Id, c.Nombre, "Centro", $"/centros/{c.Id}"))
            .ToListAsync(cancellationToken);

        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(t.Id))
            .Where(t =>
                t.Nombre.ToUpper().Contains(terminoMayus) ||
                t.Apellidos.ToUpper().Contains(terminoMayus) ||
                (t.Dni ?? "").ToUpper().Contains(terminoMayus))
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Take(LimitePorCategoria)
            .Select(t => new ItemBusquedaDto(t.Id, t.Nombre + " " + t.Apellidos, t.Dni, $"/trabajadores/{t.Id}"))
            .ToListAsync(cancellationToken);

        var documentos = await BuscarDocumentosAsync(terminoMayus, cancellationToken);

        return new ResultadoBusquedaGlobalDto(empresas, centros, trabajadores, documentos);
    }

    /// <summary>
    /// Empresa (papel neutro), Cliente empresarial y Subcontrata (F3c: mismos
    /// discriminadores que <c>ObtenerClientesQuery</c>/<c>ObtenerSubcontratasQuery</c>,
    /// EsCritico/NivelServicio != null) son la MISMA tabla Empresas — antes de
    /// P41d (2026-09-18) eran tres categorías del buscador, así que una Empresa
    /// con más de un papel contextual (o simplemente visible sin discriminador)
    /// salía repetida. El contrato de lenguaje (ADR-011) ya lo decía: Cliente y
    /// Subcontrata son papeles dentro de una Relación Empresarial, no tipos de
    /// Empresa — así que aquí se consulta cada alcance por separado, EXACTAMENTE
    /// como antes (misma cartera, mismo discriminador), y se fusiona por
    /// Empresa.Id antes de aplicar el límite de categoría, para no cortar el
    /// top N a mitad de una fusión ni tener que inventar un alcance combinado
    /// nuevo que no está probado.
    /// </summary>
    private async Task<IReadOnlyList<ItemBusquedaDto>> BuscarEmpresasAsync(
        string terminoMayus,
        IReadOnlyList<Guid>? clienteIdsVisibles, IReadOnlyList<Guid>? empresaIdsVisibles, IReadOnlyList<Guid>? subcontrataIdsVisibles,
        CancellationToken cancellationToken)
    {
        var comoClientes = await empresasContext.Empresas.Where(e => e.EsCritico != null)
            .Where(c => clienteIdsVisibles == null || clienteIdsVisibles.Contains(c.Id))
            .Where(c => c.RazonSocial.ToUpper().Contains(terminoMayus))
            .Select(c => new { c.Id, c.RazonSocial })
            .ToListAsync(cancellationToken);

        var comoSubcontratas = await empresasContext.Empresas.Where(e => e.NivelServicio != null)
            .Where(s => subcontrataIdsVisibles == null || subcontrataIdsVisibles.Contains(s.Id))
            .Where(s => s.RazonSocial.ToUpper().Contains(terminoMayus))
            .Select(s => new { s.Id, s.RazonSocial })
            .ToListAsync(cancellationToken);

        var comoEmpresas = await empresasContext.Empresas
            .Where(e => empresaIdsVisibles == null || empresaIdsVisibles.Contains(e.Id))
            .Where(e => e.RazonSocial.ToUpper().Contains(terminoMayus))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToListAsync(cancellationToken);

        var papelesPorEmpresa = new Dictionary<Guid, (string RazonSocial, List<string> Papeles)>();

        void Registrar(IEnumerable<(Guid Id, string RazonSocial)> filas, string? papel)
        {
            foreach (var (id, razonSocial) in filas)
            {
                if (!papelesPorEmpresa.TryGetValue(id, out var entrada))
                {
                    entrada = (razonSocial, []);
                    papelesPorEmpresa[id] = entrada;
                }

                if (papel is not null && !entrada.Papeles.Contains(papel))
                    entrada.Papeles.Add(papel);
            }
        }

        // Orden de registro deliberado: primero los papeles con nombre
        // (Cliente/Subcontrata), luego la visibilidad "sin papel" — así el
        // subtítulo nunca pierde un papel real solo porque la fila también es
        // visible por el alcance general de Empresa.
        Registrar(comoClientes.Select(c => (c.Id, c.RazonSocial)), "Cliente");
        Registrar(comoSubcontratas.Select(s => (s.Id, s.RazonSocial)), "Subcontrata");
        Registrar(comoEmpresas.Select(e => (e.Id, e.RazonSocial)), null);

        return papelesPorEmpresa
            .OrderBy(kv => kv.Value.RazonSocial, StringComparer.Ordinal)
            .Take(LimitePorCategoria)
            .Select(kv => new ItemBusquedaDto(
                kv.Key, kv.Value.RazonSocial,
                kv.Value.Papeles.Count > 0 ? string.Join(",", kv.Value.Papeles) : null,
                $"/empresas?q={Uri.EscapeDataString(kv.Value.RazonSocial)}"))
            .ToList();
    }

    /// <summary>
    /// Búsqueda por Tipo de Documento (ej. "Formación PRL" → todas las
    /// instancias de ese tipo, de cualquier ámbito) — mismo patrón de unión
    /// por ámbito que <c>ObtenerDocumentosQuery</c>, recortado a lo que
    /// necesita el palette (sin Acreditaciones ni paginación completa).
    /// </summary>
    private async Task<IReadOnlyList<ItemBusquedaDto>> BuscarDocumentosAsync(string terminoMayus, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        var vehiculoIdsVisibles = await alcanceDatos.ObtenerVehiculoIdsVisiblesAsync(cancellationToken);

        var deTrabajador =
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.Nombre.ToUpper().Contains(terminoMayus)
            select new { documento.Id, TipoNombre = tipoDocumento.Nombre, PropietarioNombre = trabajador.Nombre + " " + trabajador.Apellidos };

        var deCliente =
            from documento in documentosContext.Documentos
            where documento.ClienteId != null
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(documento.ClienteId!.Value)
            // Documento.ClienteId apunta a Empresas desde el repunteo de FKs de
            // F3b — el ancla sigue siendo la contraparte, no la relación (F4b
            // diferida, ADR-011 § 18.1).
            join cliente in empresasContext.Empresas on documento.ClienteId!.Value equals cliente.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.Nombre.ToUpper().Contains(terminoMayus)
            select new { documento.Id, TipoNombre = tipoDocumento.Nombre, PropietarioNombre = cliente.RazonSocial };

        var deEmpresa =
            from documento in documentosContext.Documentos
            where documento.EmpresaId != null
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join empresa in empresasContext.Empresas on documento.EmpresaId!.Value equals empresa.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.Nombre.ToUpper().Contains(terminoMayus)
            select new { documento.Id, TipoNombre = tipoDocumento.Nombre, PropietarioNombre = empresa.RazonSocial };

        var deVehiculo =
            from documento in documentosContext.Documentos
            where documento.VehiculoId != null
            where vehiculoIdsVisibles == null || vehiculoIdsVisibles.Contains(documento.VehiculoId!.Value)
            join vehiculo in vehiculosContext.Vehiculos on documento.VehiculoId!.Value equals vehiculo.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.Nombre.ToUpper().Contains(terminoMayus)
            select new { documento.Id, TipoNombre = tipoDocumento.Nombre, PropietarioNombre = vehiculo.Nombre + " (" + vehiculo.NumeroPlaca + ")" };

        var deProyecto =
            from documento in documentosContext.Documentos
            where documento.ProyectoId != null
            join proyecto in proyectosContext.Proyectos on documento.ProyectoId!.Value equals proyecto.Id
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(proyecto.ClienteId)
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where tipoDocumento.Nombre.ToUpper().Contains(terminoMayus)
            select new { documento.Id, TipoNombre = tipoDocumento.Nombre, PropietarioNombre = proyecto.Nombre };

        var union = deTrabajador.Concat(deCliente).Concat(deEmpresa).Concat(deVehiculo).Concat(deProyecto);

        var filas = await union
            .OrderBy(x => x.TipoNombre)
            .Take(LimitePorCategoria)
            .ToListAsync(cancellationToken);

        // Sin ficha propia: abre el listado de Documentos filtrado por el
        // propietario, que es lo que de verdad distingue una fila de otra
        // cuando varias comparten el mismo Tipo de Documento.
        return filas
            .Select(f => new ItemBusquedaDto(f.Id, f.TipoNombre, f.PropietarioNombre, $"/documentos?q={Uri.EscapeDataString(f.PropietarioNombre)}"))
            .ToList();
    }
}
