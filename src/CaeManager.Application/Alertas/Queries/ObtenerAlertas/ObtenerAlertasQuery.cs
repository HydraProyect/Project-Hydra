using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Alertas.Queries.ObtenerAlertas;

/// <summary>
/// Se calcula en vivo sobre Documentos en cada petición, en vez de leer de
/// una tabla Alerta sincronizada por un job en segundo plano — una vista
/// calculada nunca puede desincronizarse del estado real (ver ROADMAP.md).
/// La entidad Alerta del dominio queda preparada para cuando se necesite
/// marcar alertas como leídas por usuario. <c>EnvioAlertasVencimientoHostedService</c>
/// (Infrastructure, Issue #2) sí llama a esto periódicamente para el resumen
/// por correo, pero sigue siendo cálculo en vivo en cada ejecución — no una
/// tabla propia. Solo cubre Documentos de Trabajador — los de Cliente/Empresa
/// (ver Documento.Ambito) no generan alerta todavía; ampliar esta vista queda
/// fuera de alcance por ahora.
///
/// P1-15 de docs/business/MATURITY_REVIEW.md añade el segundo bloque:
/// "documento faltante" — un Trabajador con Asignación activa a un Centro
/// que exige (vía TipoDocumentoCentro, o globalmente si un TipoDocumento no
/// tiene ninguna fila ahí — ver ese comentario) un TipoDocumento marcado
/// EsObligatorio, y ningún Documento de ese tipo para ese Trabajador. Antes
/// de esto, un requisito sin ningún Documento no aparecía en ningún sitio:
/// no es "Vencido" (no hay fila que evaluar) ni "NoAplica" (ese estado es
/// para Documentos existentes sin vigencia) — el hueco funcional exacto que
/// señala el informe de madurez.
/// </summary>
public record ObtenerAlertasQuery : IRequest<IReadOnlyList<AlertaDto>>;

public record AlertaDto(
    Guid? DocumentoId,
    Guid TrabajadorId,
    string TrabajadorNombre,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl,
    string? CentroNombre);

public class ObtenerAlertasQueryHandler(
    IConfiguracionQueryContext configuracionContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    IDocumentosFaltantesService documentosFaltantesService)
    : IRequestHandler<ObtenerAlertasQuery, IReadOnlyList<AlertaDto>>
{
    public async Task<IReadOnlyList<AlertaDto>> Handle(ObtenerAlertasQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        return await CalcularAsync(trabajadorIdsVisibles, centroIdsVisibles, cancellationToken);
    }

    /// <summary>
    /// Punto de entrada explícito (sin pasar por MediatR ni por
    /// <see cref="IAlcanceDatosService"/>, que depende del usuario de la
    /// sesión actual y no existe fuera de una petición HTTP/circuito) para
    /// quien ya sabe qué alcance quiere aplicar — <c>null</c> en cualquiera
    /// de los dos significa "sin restricción". Usado por
    /// <c>EnvioAlertasVencimientoHostedService</c> (Infrastructure) para un
    /// resumen diario por correo sin cartera, dentro de un
    /// <c>AmbitoTenantExplicito</c> ya establecido por tenant.
    /// </summary>
    public async Task<IReadOnlyList<AlertaDto>> CalcularAsync(
        IReadOnlyList<Guid>? trabajadorIdsVisibles, IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var vigenciaFilas = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where documento.FechaVencimiento != null
            select new
            {
                DocumentoId = documento.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoId = tipoDocumento.Id,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento,
                documento.ArchivoUrl
            })
            .ToListAsync(cancellationToken);

        var alertasVigencia = vigenciaFilas
            .Select(f => new AlertaDto(
                f.DocumentoId, f.TrabajadorId, f.TrabajadorNombre, f.TipoDocumentoId, f.TipoDocumentoNombre,
                f.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                f.ArchivoUrl, CentroNombre: null))
            .Where(a => a.Estado is EstadoDocumento.Proximo or EstadoDocumento.Urgente or EstadoDocumento.Vencido);

        var alertasFaltantes = await ObtenerFaltantesAsync(trabajadorIdsVisibles, centroIdsVisibles, cancellationToken);

        return alertasVigencia
            .Concat(alertasFaltantes)
            .OrderBy(a => a.Estado switch
            {
                EstadoDocumento.Faltante => 0,
                EstadoDocumento.Vencido => 1,
                EstadoDocumento.Urgente => 2,
                _ => 3
            })
            .ThenBy(a => a.FechaVencimiento)
            .ToList();
    }

    /// <summary>
    /// Delegado en <see cref="IDocumentosFaltantesService"/> (Fase B5): la
    /// misma regla de "qué le falta a un Trabajador en un Centro" que ahora
    /// también usa el preflight de asignación en lote — antes vivía
    /// duplicada en un método privado de esta clase.
    /// </summary>
    private async Task<List<AlertaDto>> ObtenerFaltantesAsync(
        IReadOnlyList<Guid>? trabajadorIdsVisibles, IReadOnlyList<Guid>? centroIdsVisibles, CancellationToken cancellationToken)
    {
        var asignacionesActivas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(asignacion.TrabajadorId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            select new
            {
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                CentroId = centro.Id,
                centro.Nombre
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return [];

        var parejas = asignacionesActivas
            .Select(a => new ParejaTrabajadorCentro(a.TrabajadorId, a.TrabajadorNombre, a.CentroId, a.Nombre))
            .ToList();

        var faltantes = await documentosFaltantesService.CalcularAsync(parejas, cancellationToken);

        return faltantes
            .Select(f => new AlertaDto(
                DocumentoId: null,
                f.TrabajadorId,
                f.TrabajadorNombre,
                f.TipoDocumentoId,
                f.TipoDocumentoNombre,
                FechaVencimiento: null,
                EstadoDocumento.Faltante,
                ArchivoUrl: null,
                f.CentroNombre))
            .ToList();
    }
}
