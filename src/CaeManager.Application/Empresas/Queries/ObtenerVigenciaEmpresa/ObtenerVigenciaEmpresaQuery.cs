using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.DocumentacionBase;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Empresas.Queries.ObtenerVigenciaEmpresa;

/// <summary>
/// Matriz «Vigencia» de Empresa 360: cada Trabajador de la Empresa frente a cada tipo de documento de Ámbito Trabajador, con el
/// estado de vigencia de su documento. No inventa reglas: reutiliza las que ya mandan en Centro 360 y Trabajador 360 —qué exige
/// cada Centro (<see cref="ResolucionTipoDocumentoCentro"/>, con la serie por tipo de contratación del Trabajador), qué copia
/// representa a cada par (<see cref="DocumentoEfectivo"/>) y su estado (<see cref="CalculadoraEstadoDocumento"/> con los umbrales
/// de Parámetros del sistema)—. Es el estado de vigencia propio del Documento, sin contexto de Centro: no aplica tolerancias.
///
/// <para>
/// Alcance como el de sus vecinas: la Empresa debe ser visible (<see cref="AlcanceDatosServiceExtensions.EmpresaVisibleAsync"/>);
/// solo cuentan los Centros y los Trabajadores visibles para quien pregunta, así que quien ve la Empresa pero no todos sus
/// Centros solo ve lo que exigen los suyos. Un Centro sin gestión CAE no exige ningún tipo.
/// </para>
/// </summary>
public record ObtenerVigenciaEmpresaQuery(Guid EmpresaId) : IRequest<VigenciaEmpresaDto>;

/// <summary>Estado de una celda. «Vigente» incluye «Sin caducidad» (confirmado que no caduca).</summary>
public enum EstadoCeldaVigencia
{
    NoAplica = 0,
    Vigente = 1,
    PorVencer = 2,
    Vencido = 3,
    Falta = 4,
    SinConfirmar = 5
}

public enum GrupoColumnaVigencia
{
    Basicos = 0,
    Formacion = 1,
    Opcional = 2
}

/// <param name="UmbralPorVencerDias">Los días de «Por vencer» de Parámetros del sistema (umbral ámbar).</param>
public record VigenciaEmpresaDto(
    IReadOnlyList<VigenciaCentroDto> Centros,
    IReadOnlyList<VigenciaColumnaDto> Columnas,
    IReadOnlyList<VigenciaFilaDto> Filas,
    int UmbralPorVencerDias)
{
    public static VigenciaEmpresaDto Vacia(int umbralPorVencerDias = 30) => new([], [], [], umbralPorVencerDias);
}

public record VigenciaCentroDto(Guid CentroId, string Nombre, string ClienteNombre);

/// <param name="Basico">El tipo básico que es (reconocimiento, EPI, art. 18, art. 19), si lo es: da el nombre corto de la cabecera.</param>
/// <param name="VigenciaMesesDelTipo">La vigencia propia del tipo; cuando ningún Centro lo exige es la única que hay.</param>
/// <param name="ExigidoPor">Los Centros que lo exigen a algún Trabajador de la Empresa y los meses de vigencia que pide cada uno.</param>
public record VigenciaColumnaDto(
    Guid TipoDocumentoId, string Nombre, TipoDocumentoBase? Basico, GrupoColumnaVigencia Grupo,
    int? VigenciaMesesDelTipo, IReadOnlyList<VigenciaExigenciaCentroDto> ExigidoPor);

/// <param name="VigenciaMeses">Los meses de vigencia que rigen en ese Centro (su periodicidad o, si no la define, la del tipo): para rotular.</param>
/// <param name="PeriodicidadEspecialMeses">Solo la periodicidad que el Centro IMPONE; <c>null</c> si no la define (manda el documento).</param>
public record VigenciaExigenciaCentroDto(Guid CentroId, int? VigenciaMeses, int? PeriodicidadEspecialMeses = null);

/// <param name="Celdas">Una por columna, en el mismo orden que <see cref="VigenciaEmpresaDto.Columnas"/>.</param>
public record VigenciaFilaDto(
    Guid TrabajadorId, string Nombre, string? Puesto, Guid? DelegacionEmpresaId, string? DelegacionEmpresaNombre,
    IReadOnlyList<Guid> CentroIds, IReadOnlyList<VigenciaCeldaDto> Celdas);

/// <param name="ExigidaPorCentroIds">Los Centros del Trabajador que exigen este tipo; vacío = no lo exige ninguno (opcional).</param>
public record VigenciaCeldaDto(
    EstadoCeldaVigencia Estado, Guid? DocumentoId, DateOnly? FechaVencimiento, DateOnly? FechaEmision,
    IReadOnlyList<Guid> ExigidaPorCentroIds);

public class ObtenerVigenciaEmpresaQueryHandler(
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerVigenciaEmpresaQuery, VigenciaEmpresaDto>
{
    public async Task<VigenciaEmpresaDto> Handle(ObtenerVigenciaEmpresaQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaVisibleAsync(request.EmpresaId, cancellationToken))
            return VigenciaEmpresaDto.Vacia();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        var trabajadoresQuery = trabajadoresContext.Trabajadores
            .Where(t => t.EmpresaId == request.EmpresaId);
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            trabajadoresQuery = trabajadoresQuery.Where(t => trabajadorIdsVisibles.Contains(t.Id));

        var trabajadores = await trabajadoresQuery
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new
            {
                t.Id,
                t.Nombre,
                t.Apellidos,
                t.Puesto,
                t.DelegacionEmpresaId,
                t.TipoContratacion
            })
            .ToListAsync(cancellationToken);
        if (trabajadores.Count == 0)
            return VigenciaEmpresaDto.Vacia(parametros.UmbralAmbarDias);

        var trabajadorIds = trabajadores.Select(t => t.Id).ToList();

        var delegaciones = await empresasContext.DelegacionesEmpresa
            .Where(d => d.EmpresaId == request.EmpresaId)
            .ToDictionaryAsync(d => d.Id, d => d.Nombre, cancellationToken);

        // Centros donde trabajan hoy, solo los visibles para quien pregunta.
        var centrosVisibles = centrosContext.Centros;
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        if (centroIdsVisibles is not null)
            centrosVisibles = centrosVisibles.Where(c => centroIdsVisibles.Contains(c.Id));

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && trabajadorIds.Contains(asignacion.TrabajadorId)
            join centro in centrosVisibles on asignacion.CentroId equals centro.Id
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new { asignacion.TrabajadorId, CentroId = centro.Id, CentroNombre = centro.Nombre, ClienteNombre = cliente.RazonSocial })
            .ToListAsync(cancellationToken);

        var centros = asignaciones
            .GroupBy(a => a.CentroId)
            .Select(g => new VigenciaCentroDto(g.Key, g.First().CentroNombre, g.First().ClienteNombre))
            .OrderBy(c => c.ClienteNombre).ThenBy(c => c.Nombre)
            .ToList();
        var centroIds = centros.Select(c => c.CentroId).ToList();

        // P1-X2: un Centro sin gestión CAE no exige ningún tipo (sus requisitos se conservan, pero no cuentan).
        var sinGestionCae = centroIds.Count == 0
            ? new HashSet<Guid>()
            : (await CentrosSinGestionCae.FiltrarAsync(centrosContext, centroIds, cancellationToken)).ToHashSet();

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new
            {
                t.Id,
                t.Nombre,
                t.Orden,
                t.VigenciaMeses,
                CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si,
                t.TiposContratacionDeSerie
            })
            .ToListAsync(cancellationToken);
        var tipoIds = tipos.Select(t => t.Id).ToHashSet();

        var filasDeLosCentros = centroIds.Count == 0
            ? new Dictionary<(Guid TipoDocumentoId, Guid CentroId), CaeManager.Domain.Documentos.TipoDocumentoCentro>()
            : (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => tipoIds.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
                .ToListAsync(cancellationToken))
                .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var documentos = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value) && tipoIds.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc, d.EsRenuncia })
            .ToListAsync(cancellationToken);

        // El mismo documento que representa a cada par Trabajador×Tipo en el resto de la aplicación.
        var efectivos = DocumentoEfectivo.UnoPorClave(
            documentos, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento,
            d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, d => d.EsRenuncia, hoy);

        var centrosDeCadaTrabajador = asignaciones
            .Where(a => !sinGestionCae.Contains(a.CentroId))
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CentroId).Distinct().ToList());
        var todosLosCentrosDeCadaTrabajador = asignaciones
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CentroId).Distinct().ToList());

        // Qué Centros exigen cada tipo a cada Trabajador.
        List<Guid> ExigidoPor(Guid trabajadorId, TipoContratacion contratacion, Guid tipoId, bool porDefecto, TiposContratacion serie) =>
            centrosDeCadaTrabajador.GetValueOrDefault(trabajadorId, [])
                .Where(centroId => ResolucionTipoDocumentoCentro.Aplica(filasDeLosCentros, tipoId, centroId, porDefecto, serie, contratacion))
                .ToList();

        var exigencias = trabajadores
            .SelectMany(t => tipos.Select(tipo => (Trabajador: t.Id, Tipo: tipo.Id,
                Centros: ExigidoPor(t.Id, t.TipoContratacion, tipo.Id, tipo.CuentaParaCumplimiento, tipo.TiposContratacionDeSerie))))
            .ToDictionary(x => (x.Trabajador, x.Tipo), x => x.Centros);

        var tiposConDocumento = efectivos.Keys.Select(k => k.TipoDocumentoId).ToHashSet();

        var columnas = tipos
            .Select(tipo =>
            {
                var exigidoPor = exigencias
                    .Where(e => e.Key.Tipo == tipo.Id)
                    .SelectMany(e => e.Value)
                    .Distinct()
                    .ToList();
                var basico = DocumentacionBaseTrabajador.Clasificar(tipo.Nombre);
                // Sale una columna si algún Centro lo exige o si algún Trabajador tiene un documento de ese tipo (opcional).
                return (Tipo: tipo, ExigidoPor: exigidoPor, Basico: basico,
                    Visible: exigidoPor.Count > 0 || tiposConDocumento.Contains(tipo.Id));
            })
            .Where(c => c.Visible)
            .Select(c => new
            {
                c.Tipo,
                Dto = new VigenciaColumnaDto(
                    c.Tipo.Id, c.Tipo.Nombre, c.Basico,
                    c.Basico is not null ? GrupoColumnaVigencia.Basicos : c.ExigidoPor.Count > 0 ? GrupoColumnaVigencia.Formacion : GrupoColumnaVigencia.Opcional,
                    c.Tipo.VigenciaMeses,
                    [.. c.ExigidoPor.Select(centroId =>
                    {
                        var periodicidad = filasDeLosCentros.TryGetValue((c.Tipo.Id, centroId), out var fila) && fila.Incluido
                            ? fila.PeriodicidadEspecialMeses
                            : null;
                        return new VigenciaExigenciaCentroDto(centroId, periodicidad ?? c.Tipo.VigenciaMeses, periodicidad);
                    })])
            })
            .OrderBy(c => c.Dto.Grupo)
            .ThenBy(c => c.Dto.Grupo == GrupoColumnaVigencia.Basicos ? OrdenBasico(c.Dto.Basico) : 0)
            .ThenBy(c => c.Tipo.Orden)
            .ThenBy(c => c.Tipo.Nombre, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var filas = trabajadores
            .Select(t =>
            {
                var celdas = columnas.Select(col =>
                {
                    var exigidaPor = exigencias[(t.Id, col.Tipo.Id)];
                    // Un documento en renuncia no acredita (DocumentoEfectivo.ValidoHoy): la celda queda como sin documento.
                    if (!efectivos.TryGetValue((t.Id, col.Tipo.Id), out var documento) || documento.EsRenuncia)
                        return exigidaPor.Count > 0
                            ? new VigenciaCeldaDto(EstadoCeldaVigencia.Falta, null, null, null, exigidaPor)
                            : new VigenciaCeldaDto(EstadoCeldaVigencia.NoAplica, null, null, null, exigidaPor);

                    var estado = CalculadoraEstadoDocumento.Calcular(
                        documento.EstadoVigencia, documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                    return new VigenciaCeldaDto(Traducir(estado), documento.Id, documento.FechaVencimiento, documento.FechaEmision, exigidaPor);
                }).ToList();

                var delegacionId = t.DelegacionEmpresaId;
                return new VigenciaFilaDto(
                    t.Id, $"{t.Nombre} {t.Apellidos}".Trim(), t.Puesto, delegacionId,
                    delegacionId is { } id && delegaciones.TryGetValue(id, out var nombre) ? nombre : null,
                    todosLosCentrosDeCadaTrabajador.GetValueOrDefault(t.Id, []), celdas);
            })
            .ToList();

        return new VigenciaEmpresaDto(centros, [.. columnas.Select(c => c.Dto)], filas, parametros.UmbralAmbarDias);
    }

    private static int OrdenBasico(TipoDocumentoBase? basico) => basico switch
    {
        TipoDocumentoBase.AptitudMedica => 0,
        TipoDocumentoBase.EntregaEpi => 1,
        TipoDocumentoBase.InformacionArt18 => 2,
        TipoDocumentoBase.FormacionArt19 => 3,
        _ => 4
    };

    private static EstadoCeldaVigencia Traducir(EstadoDocumento estado) => estado switch
    {
        EstadoDocumento.Proximo or EstadoDocumento.Urgente => EstadoCeldaVigencia.PorVencer,
        EstadoDocumento.Vencido or EstadoDocumento.EnTolerancia => EstadoCeldaVigencia.Vencido,
        EstadoDocumento.SinConfirmar => EstadoCeldaVigencia.SinConfirmar,
        EstadoDocumento.Faltante => EstadoCeldaVigencia.Falta,
        _ => EstadoCeldaVigencia.Vigente
    };
}
