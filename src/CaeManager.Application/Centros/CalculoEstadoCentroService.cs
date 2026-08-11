using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros;

/// <summary>
/// Causa concreta que empuja el EstadoCentro por debajo de Vigente — un
/// Documento (de la Empresa o de un Trabajador) que no está Vigente, o un
/// hueco de <c>TipoDocumentoCentro.BloqueaAcceso</c> sin Documento Vigente.
/// Solo se generan causas para lo que efectivamente aporta al peor caso —
/// nada Vigente aparece aquí, igual que ObtenerAlertasQuery no lista
/// Documentos al día.
/// </summary>
/// <summary>
/// A quién pertenece la incidencia. Hasta ahora el ámbito solo se podía
/// deducir del sufijo de <see cref="CausaEstadoCentro.Descripcion"/>
/// ("… — Empresa" frente a "… — Nombre del trabajador"), que no es un dato:
/// es una cadena de presentación. El blueprint del Centro 360 § 3.2 exige que
/// el desglose de un recuento declare cuántas incidencias son de cada ámbito
/// (DDL-031, DDL-047), y eso no se puede sostener sobre un sufijo.
/// </summary>
public enum AmbitoCausa
{
    Empresa,
    Trabajador
}

/// <param name="DocumentoId">
/// <c>null</c> salvo en causas de Empresa, donde siempre hay un Documento real
/// detrás (§ AgregarCausasDeEmpresaAsync: sin detección de falta total ahí,
/// solo vigencia) — junto con <paramref name="TipoDocumentoId"/> y
/// <paramref name="FechaVencimiento"/> es lo que necesita la tabla de
/// documentos del bloque Empresa del Centro 360 (Documento · Estado ·
/// Vigencia · Acción, mismas columnas que la tabla de Trabajador) para
/// enlazar "Gestionar" sin una consulta aparte — reutiliza las mismas causas
/// que ya decidieron el EstadoCentro.
/// </param>
public record CausaEstadoCentro(
    string Descripcion, EstadoDocumento? Estado, bool Bloqueante, AmbitoCausa Ambito,
    Guid? DocumentoId, Guid? TipoDocumentoId, DateOnly? FechaVencimiento);

public record ResultadoEstadoCentro(EstadoCentro Estado, IReadOnlyList<CausaEstadoCentro> Causas);

/// <summary>
/// % de cumplimiento documental de un Centro (Centro 360, PLAN-EJECUCION-UX.md
/// § 0.5/0.8) — <c>Requeridos</c> es el número de pares Trabajador×TipoDocumento
/// aplicables a ese Centro (ver <see cref="Documentos.ResolucionTipoDocumentoCentro"/>),
/// <c>AlDia</c> cuántos de esos pares tienen hoy un Documento Vigente o NoAplica.
/// <see cref="Porcentaje"/> es <c>null</c> cuando el centro no tiene ningún par
/// aplicable — un 0% o 100% ahí sería engañoso, "sin requisitos" es la lectura
/// correcta.
/// </summary>
public record FraccionCumplimiento(int AlDia, int Requeridos)
{
    public int? Porcentaje => Requeridos == 0 ? null : (int)Math.Round(AlDia * 100.0 / Requeridos);
}

/// <summary>
/// Cálculo compartido entre ObtenerCentrosQuery (badge de la tabla) y
/// ObtenerEstadoCentroQuery (desglose del Workspace) — agrega de una sola
/// vez los Documentos de Empresa, los Documentos y huecos obligatorios de
/// cada Trabajador con Asignación activa, y los RequisitosDocumentales
/// bloqueantes de uno o varios Centros, para no lanzar N consultas al pintar
/// una página de la tabla. La lógica de "documento faltante" replica la de
/// ObtenerAlertasQuery.ObtenerFaltantesAsync (Trabajador únicamente — los
/// Documentos de Empresa aquí solo aportan su vigencia, sin detección de
/// falta total, mismo alcance que esa Query).
/// </summary>
public interface ICalculoEstadoCentroService
{
    Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);

    /// <summary>
    /// Método aparte de <see cref="CalcularAsync"/> a propósito (PLAN-EJECUCION-UX.md
    /// § 0.5): mismas fuentes de datos (asignaciones activas, tipos
    /// obligatorios, allow-list de <c>TipoDocumentoCentro</c>) pero una
    /// pregunta distinta ("qué fracción" en vez de "cuál es el peor caso") —
    /// separarlo evita arriesgar la lógica de <c>CalcularAsync</c>, ya en
    /// producción y compartida por dos pantallas, al añadirle un cálculo
    /// nuevo dentro del mismo método.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken);
}

public class CalculoEstadoCentroService(
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IConfiguracionQueryContext configuracionContext)
    : ICalculoEstadoCentroService
{
    public async Task<IReadOnlyDictionary<Guid, ResultadoEstadoCentro>> CalcularAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0)
            return new Dictionary<Guid, ResultadoEstadoCentro>();

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var causasPorCentro = centroIds.Distinct().ToDictionary(id => id, _ => new List<CausaEstadoCentro>());

        await AgregarCausasDeEmpresaAsync(centroIds, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);
        await AgregarCausasDeTrabajadorAsync(centroIds, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, causasPorCentro, cancellationToken);

        return causasPorCentro.ToDictionary(
            par => par.Key,
            par => new ResultadoEstadoCentro(
                CalculadoraEstadoCentro.Calcular(
                    par.Value.Where(c => c.Estado is not null).Select(c => c.Estado!.Value).ToList(),
                    par.Value.Any(c => c.Bloqueante)),
                par.Value));
    }

    private async Task AgregarCausasDeEmpresaAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var centros = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.EmpresaId })
            .ToListAsync(cancellationToken);

        var centroIdsPorEmpresa = centros
            .GroupBy(c => c.EmpresaId)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var empresaIds = centroIdsPorEmpresa.Keys.ToList();

        var documentosEmpresa = await (
            from documento in documentosContext.Documentos
            where documento.EmpresaId != null && empresaIds.Contains(documento.EmpresaId!.Value)
            where documento.FechaVencimiento != null
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new
            {
                documento.Id,
                EmpresaId = documento.EmpresaId!.Value,
                documento.TipoDocumentoId,
                documento.FechaVencimiento,
                tipoDocumento.Nombre
            })
            .ToListAsync(cancellationToken);

        foreach (var documento in documentosEmpresa)
        {
            var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
            if (estado is EstadoDocumento.NoAplica or EstadoDocumento.Vigente) continue;
            if (!centroIdsPorEmpresa.TryGetValue(documento.EmpresaId, out var centrosDeEmpresa)) continue;

            var causa = new CausaEstadoCentro(
                $"{documento.Nombre} — Empresa", estado, Bloqueante: false, AmbitoCausa.Empresa,
                documento.Id, documento.TipoDocumentoId, documento.FechaVencimiento);
            foreach (var centroId in centrosDeEmpresa)
                causasPorCentro[centroId].Add(causa);
        }
    }

    private async Task AgregarCausasDeTrabajadorAsync(
        IReadOnlyList<Guid> centroIds, DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        Dictionary<Guid, List<CausaEstadoCentro>> causasPorCentro, CancellationToken cancellationToken)
    {
        var asignacionesActivas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null && centroIds.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            select new
            {
                asignacion.CentroId,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos
            })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0) return;

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();

        // Vigencia de los Documentos de Trabajador que ya existen — igual que
        // el bloque "alertasVigencia" de ObtenerAlertasQuery, sin filtrar por
        // EsObligatorio: un Documento vencido cuenta para el Centro exista o
        // no exista una fila de obligatoriedad para su TipoDocumento.
        var documentosTrabajador = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null && trabajadorIds.Contains(documento.TrabajadorId!.Value)
            where documento.FechaVencimiento != null
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            select new { TrabajadorId = documento.TrabajadorId!.Value, documento.FechaVencimiento, tipoDocumento.Nombre })
            .ToListAsync(cancellationToken);

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var documento in documentosTrabajador.Where(d => d.TrabajadorId == asignacion.TrabajadorId))
            {
                var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
                if (estado is EstadoDocumento.NoAplica or EstadoDocumento.Vigente) continue;

                causasPorCentro[asignacion.CentroId].Add(
                    new CausaEstadoCentro(
                        $"{documento.Nombre} — {asignacion.TrabajadorNombre}", estado, Bloqueante: false, AmbitoCausa.Trabajador,
                        DocumentoId: null, TipoDocumentoId: null, FechaVencimiento: null));
            }
        }

        // Huecos requeridos — misma lógica que ObtenerAlertasQuery.ObtenerFaltantesAsync,
        // reacotada a estos Centros. Candidatos = todo el catálogo de Trabajador, no solo
        // EsObligatorio=true: un Centro puede exigir explícitamente un tipo no obligatorio
        // globalmente (PLAN-EJECUCION-UX.md § 0.4, TipoDocumentoCentro.Incluido).
        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0) return;

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var parejasConDocumento = (await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId })
            .ToListAsync(cancellationToken))
            .Select(d => (d.TrabajadorId, d.TipoDocumentoId))
            .ToHashSet();

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, asignacion.CentroId, tipo.EsObligatorio))
                    continue;

                if (parejasConDocumento.Contains((asignacion.TrabajadorId, tipo.Id)))
                    continue;

                // BloqueaAcceso de la fila explícita (si hay) fuerza EstadoCentro.Bloqueado
                // aquí mismo — sustituye a RequisitoDocumental.BloqueaAcceso/Cumplido
                // (retirado): antes era un check manual a nivel de Centro, ahora es
                // automático por trabajador, igual que el resto de este servicio.
                var bloquea = filasPorPar.TryGetValue((tipo.Id, asignacion.CentroId), out var fila) && fila.BloqueaAcceso;

                causasPorCentro[asignacion.CentroId].Add(new CausaEstadoCentro(
                    $"{tipo.Nombre} — {asignacion.TrabajadorNombre}", EstadoDocumento.Faltante, Bloqueante: bloquea, AmbitoCausa.Trabajador,
                    DocumentoId: null, TipoDocumentoId: null, FechaVencimiento: null));
            }
        }
    }

    /// <summary>
    /// Alcance igual al de "huecos obligatorios" de <see cref="AgregarCausasDeTrabajadorAsync"/>
    /// (Trabajador únicamente, sin Documentos de Empresa — así lo pide
    /// PLAN-EJECUCION-UX.md § 0.5: "por trabajador dentro de un centro"),
    /// pero contando el universo completo de pares aplicables en vez de solo
    /// los que fallan.
    /// </summary>
    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> centroIds, CancellationToken cancellationToken)
    {
        var acumulado = centroIds.Distinct().ToDictionary(id => id, _ => (AlDia: 0, Requeridos: 0));
        if (centroIds.Count == 0)
            return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && centroIds.Contains(a.CentroId))
            .Select(a => new { a.CentroId, a.TrabajadorId })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var trabajadorIds = asignacionesActivas.Select(a => a.TrabajadorId).Distinct().ToList();
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var estadosPorPareja = (await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsCandidatos.Contains(d.TipoDocumentoId))
            .Select(d => new { TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken))
            .ToDictionary(
                d => (d.TrabajadorId, d.TipoDocumentoId),
                d => CalculadoraEstadoDocumento.Calcular(d.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias));

        foreach (var asignacion in asignacionesActivas)
        {
            foreach (var tipo in tiposCandidatos)
            {
                if (!ResolucionTipoDocumentoCentro.Aplica(filasPorPar, tipo.Id, asignacion.CentroId, tipo.EsObligatorio))
                    continue;

                var actual = acumulado[asignacion.CentroId];
                var alDia = actual.AlDia;
                if (estadosPorPareja.TryGetValue((asignacion.TrabajadorId, tipo.Id), out var estado)
                    && estado is EstadoDocumento.Vigente or EstadoDocumento.NoAplica)
                    alDia++;

                acumulado[asignacion.CentroId] = (alDia, actual.Requeridos + 1);
            }
        }

        return acumulado.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));
    }
}
