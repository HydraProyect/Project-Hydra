using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Queries.ObtenerEstadoTipoDocumento;

/// <summary>
/// La página «Tipo de documento 360» (<c>/documentos/tipos/{id}</c>): el estado de UN tipo de documento en todos los
/// Trabajadores a los que algún Centro visible se lo exige.
///
/// <para>
/// <b>Quién la lee</b> (decisión de Chris, 2026-10-08: «todos los perfiles excepto consulta» y «que no la vea cliente»):
/// Administrador, Dirección CAE, Coordinador CAE y Gestor CAE. La página cruza varios Clientes empresariales a la vez, así
/// que al rol Cliente (usuario de un Cliente empresarial) y al rol Consulta se les responde <c>null</c>, igual que a un
/// tipo que no existe: la respuesta no delata si el registro existe.
/// </para>
///
/// <para>
/// <b>Qué cuenta</b>: los pares exigidos Centro × Trabajador de <see cref="ICalculoEstadoCentroService.ObtenerParesExigidosAsync"/>
/// —el mismo universo que mide el cumplimiento del resto de pantallas—, filtrados a este tipo y a los Centros del alcance
/// de quien pregunta. Un Trabajador cuenta una vez por cada Centro que se lo exige; quien no tiene ninguno no aparece.
/// Solo tipos de ámbito Trabajador: para los demás la respuesta es <c>null</c>.
/// </para>
/// </summary>
/// <param name="Estados">Grupos de estado marcados; vacío o <c>null</c>, todos. Filtra las filas, no el anillo ni los recuentos.</param>
public record ObtenerEstadoTipoDocumentoQuery(
    Guid TipoDocumentoId,
    IReadOnlyCollection<GrupoEstadoTipoDocumento>? Estados = null,
    int Pagina = 1,
    int TamanoPagina = ObtenerEstadoTipoDocumentoQuery.TamanoPaginaPorDefecto) : IRequest<EstadoTipoDocumentoDto?>
{
    public const int TamanoPaginaPorDefecto = 20;
    public const int TamanoPaginaMaximo = 100;
}

/// <param name="Cumplimiento">Pares al día sobre pares exigidos. 0 de 0 (porcentaje <c>null</c>) si ningún Centro lo exige a nadie.</param>
/// <param name="Centros">Centros distintos que lo exigen a algún Trabajador.</param>
/// <param name="Trabajadores">Trabajadores distintos a los que se exige: el total de filas sin filtrar.</param>
/// <param name="Recuentos">Filas por grupo de su peor estado, sobre todas las filas.</param>
/// <param name="Filas">La página pedida de las filas que casan con <c>Estados</c>.</param>
/// <param name="TotalFiltradas">Filas que casan con <c>Estados</c>, en todas las páginas.</param>
/// <param name="Notas">
/// La «Nota interna» del lateral (<c>TipoDocumento.Notas</c>). No necesita corte propio: la consulta entera ya responde
/// <c>null</c> a quien no es de <see cref="ObtenerEstadoTipoDocumentoQueryHandler.RolesQueVenLaPagina"/>.
/// </param>
public record EstadoTipoDocumentoDto(
    Guid Id,
    string Nombre,
    int? VigenciaMeses,
    bool AplicaVencimientoAutomatico,
    RequisitoDocumental Requerido,
    NaturalezaJuridica Naturaleza,
    string? SeSolicitaA,
    IReadOnlyList<string> Aliases,
    FraccionCumplimiento Cumplimiento,
    int Centros,
    int Trabajadores,
    IReadOnlyList<RecuentoGrupoEstadoDto> Recuentos,
    IReadOnlyList<FilaTrabajadorTipoDocumentoDto> Filas,
    int TotalFiltradas,
    int Pagina,
    int TamanoPagina,
    string? Notas);

public record RecuentoGrupoEstadoDto(GrupoEstadoTipoDocumento Grupo, int Filas);

/// <param name="DocumentoId">El documento efectivo del Trabajador para este tipo; <c>null</c> si no tiene ninguno.</param>
/// <param name="PeorEstado">El peor estado entre sus Centros: el de la pastilla de la fila.</param>
/// <param name="Centros">Su estado en cada Centro que se lo exige, del peor al mejor.</param>
public record FilaTrabajadorTipoDocumentoDto(
    Guid TrabajadorId,
    string Nombre,
    string? Dni,
    Guid? EmpresaId,
    string? EmpresaNombre,
    Guid? DocumentoId,
    DateOnly? FechaEmision,
    DateOnly? FechaVencimiento,
    EstadoDocumento PeorEstado,
    int CentrosAlDia,
    IReadOnlyList<EstadoEnCentroDto> Centros);

/// <param name="EnToleranciaHasta">Solo con <see cref="EstadoDocumento.EnTolerancia"/>: último día en que el documento vencido aún vale en ese Centro.</param>
public record EstadoEnCentroDto(
    Guid CentroId, string CentroNombre, Guid ClienteEmpresarialId, EstadoDocumento Estado, DateOnly? EnToleranciaHasta);

public class ObtenerEstadoTipoDocumentoQueryHandler(
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ICentrosQueryContext centrosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ICalculoEstadoCentroService calculoEstadoCentro,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService)
    : IRequestHandler<ObtenerEstadoTipoDocumentoQuery, EstadoTipoDocumentoDto?>
{
    // Mismos literales que AutorizacionEscrituraBehavior, mismo motivo: Application no puede referenciar
    // Infrastructure.Identity.Roles. Es la lista «todos salvo Consulta y Cliente»; que hoy coincida con los roles con
    // escritura es casualidad, no la regla, y por eso no se reutiliza aquella. La ruta repite la lista
    // (TipoDocumentoDetalle.razor) y TipoDocumento360SoloRolesDeGestionTests vigila que no diverjan.
    public static readonly IReadOnlyList<string> RolesQueVenLaPagina =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<EstadoTipoDocumentoDto?> Handle(ObtenerEstadoTipoDocumentoQuery request, CancellationToken cancellationToken)
    {
        var rol = await currentUserService.ObtenerRolEfectivoAsync();
        if (rol is null || !RolesQueVenLaPagina.Contains(rol))
            return null;

        var tipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == request.TipoDocumentoId)
            .Select(t => new
            {
                t.Id,
                t.Nombre,
                t.VigenciaMeses,
                t.AplicaVencimientoAutomatico,
                t.AmbitoAplicacion,
                t.Requerido,
                t.Naturaleza,
                t.SeSolicitaA,
                t.Notas
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (tipo is null || tipo.AmbitoAplicacion != AmbitoAplicacion.Trabajador)
            return null;

        var aliases = await tiposDocumentoContext.TiposDocumentoAlias
            .Where(a => a.TipoDocumentoId == tipo.Id)
            .Select(a => a.Texto)
            .ToListAsync(cancellationToken);

        var pares = await CargarParesAsync(tipo.Id, cancellationToken);
        var filas = EstadoTipoDocumentoCalculo.Agrupar(pares);

        var estados = request.Estados is { Count: > 0 } marcados ? marcados.ToHashSet() : null;
        var filtradas = estados is null
            ? filas
            : filas.Where(f => estados.Contains(EstadoTipoDocumentoCalculo.Grupo(f.PeorEstado))).ToList();

        var tamano = Math.Clamp(request.TamanoPagina, 1, ObtenerEstadoTipoDocumentoQuery.TamanoPaginaMaximo);
        var totalPaginas = Math.Max(1, (int)Math.Ceiling(filtradas.Count / (double)tamano));
        var pagina = Math.Clamp(request.Pagina, 1, totalPaginas);

        return new EstadoTipoDocumentoDto(
            tipo.Id, tipo.Nombre, tipo.VigenciaMeses, tipo.AplicaVencimientoAutomatico, tipo.Requerido, tipo.Naturaleza,
            tipo.SeSolicitaA, aliases,
            Cumplimiento: CumplimientoDocumental.Evaluar(pares.Select(p => p.Estado)),
            Centros: pares.Select(p => p.CentroId).Distinct().Count(),
            Trabajadores: filas.Count,
            Recuentos: EstadoTipoDocumentoCalculo.Recuentos(filas),
            Filas: filtradas.Skip((pagina - 1) * tamano).Take(tamano).ToList(),
            TotalFiltradas: filtradas.Count,
            Pagina: pagina,
            TamanoPagina: tamano,
            Notas: tipo.Notas);
    }

    private async Task<IReadOnlyList<ParDeTipoDocumento>> CargarParesAsync(Guid tipoDocumentoId, CancellationToken cancellationToken)
    {
        // Alcance de cartera: null = sin restricción (todos los Centros del Tenant, que acota RLS).
        var centrosVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var centrosDelAlcance = centrosVisibles is null
            ? centrosContext.Centros
            : centrosContext.Centros.Where(c => centrosVisibles.Contains(c.Id));
        var centros = await centrosDelAlcance
            .Select(c => new { c.Id, c.Nombre, ClienteEmpresarialId = c.ClienteId })
            .ToListAsync(cancellationToken);

        if (centros.Count == 0)
            return [];

        var exigidos = (await calculoEstadoCentro.ObtenerParesExigidosAsync(centros.Select(c => c.Id).ToList(), cancellationToken))
            .Where(p => p.TipoDocumentoId == tipoDocumentoId)
            .ToList();

        if (exigidos.Count == 0)
            return [];

        var trabajadorIds = exigidos.Select(p => p.TrabajadorId).Distinct().ToList();
        var trabajadores = (await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Nombre, t.Apellidos, t.Dni, t.EmpresaId })
            .ToListAsync(cancellationToken))
            .ToDictionary(t => t.Id);

        var empresaIds = trabajadores.Values.Where(t => t.EmpresaId != null).Select(t => t.EmpresaId!.Value).Distinct().ToList();
        var empresas = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        // El documento efectivo de cada Trabajador para el tipo: la misma elección (DocumentoEfectivo) con la que
        // ObtenerParesExigidosAsync calculó el estado del par, para que la fecha pintada sea la del estado pintado.
        var hoy = DiaDeNegocio.Hoy();
        var documentos = DocumentoEfectivo.UnoPorClave(
            await documentosContext.Documentos.Operativos()
                .Where(d => d.TipoDocumentoId == tipoDocumentoId && d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId!.Value))
                .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.Id, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
                .ToListAsync(cancellationToken),
            d => d.TrabajadorId, d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);

        // Tolerancia tras vencer, vista desde cada Centro (VigenciaEnCentro): solo distingue «En tolerancia» de «Vencido»;
        // ninguno de los dos cuenta como al día, así que el anillo no cambia.
        var centroIds = centros.Select(c => c.Id).ToList();
        var filasDeCentro = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tc.TipoDocumentoId == tipoDocumentoId && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => tc.CentroId);
        var toleranciasDeClientes = await VigenciaEnCentro.CargarToleranciasDeClientesAsync(
            tiposDocumentoContext, centros.Select(c => c.ClienteEmpresarialId).Distinct().ToList(), [tipoDocumentoId], cancellationToken);

        var centrosPorId = centros.ToDictionary(c => c.Id);
        var pares = new List<ParDeTipoDocumento>(exigidos.Count);
        foreach (var par in exigidos)
        {
            if (!trabajadores.TryGetValue(par.TrabajadorId, out var trabajador) || !centrosPorId.TryGetValue(par.CentroId, out var centro))
                continue;

            var tieneDocumento = documentos.TryGetValue(par.TrabajadorId, out var documento);
            var estado = par.Estado;
            DateOnly? enToleranciaHasta = null;
            if (tieneDocumento)
            {
                var condiciones = VigenciaEnCentro.Condiciones(
                    filasDeCentro.GetValueOrDefault(par.CentroId),
                    toleranciasDeClientes.TryGetValue((centro.ClienteEmpresarialId, tipoDocumentoId), out var dias) ? dias : null);
                (estado, enToleranciaHasta) = VigenciaEnCentro.Aplicar(
                    par.Estado, documento!.EstadoVigencia, documento.FechaVencimiento, documento.FechaEmision, condiciones, hoy);
            }

            pares.Add(new ParDeTipoDocumento(
                par.TrabajadorId,
                $"{trabajador.Nombre} {trabajador.Apellidos}".Trim(),
                trabajador.Dni,
                trabajador.EmpresaId,
                trabajador.EmpresaId is { } empresaId ? empresas.GetValueOrDefault(empresaId) : null,
                par.CentroId, centro.Nombre, centro.ClienteEmpresarialId,
                estado, enToleranciaHasta,
                tieneDocumento ? documento!.Id : null,
                tieneDocumento ? documento!.FechaEmision : null,
                tieneDocumento ? documento!.FechaVencimiento : null));
        }

        return pares;
    }
}
