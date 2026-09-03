using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Alertas.Queries.ObtenerAlertas;

/// <summary>
/// Se calcula en vivo sobre Documentos en cada petición, en vez de leer de
/// una tabla propia sincronizada por un job en segundo plano — una vista
/// calculada nunca puede desincronizarse del estado real (ver ROADMAP.md).
/// El agregado de dominio y la tabla Alertas que existieron para esto se
/// retiraron (REC-069/DEC-23): nadie los producía ni los consumía, y la
/// notificación real ya va por <c>NotificacionUsuario</c> y correo. <c>EnvioAlertasVencimientoHostedService</c>
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
/// no es "Vencido" (no hay fila que evaluar) ni "SinCaducidad" (ese estado es
/// para Documentos existentes sin vigencia) — el hueco funcional exacto que
/// señala el informe de madurez.
/// </summary>
public record ObtenerAlertasQuery : IRequest<IReadOnlyList<AlertaDto>>;

/// <param name="CentroId">
/// Solo resuelto para alertas de "documento faltante" (exacto, viene del par
/// Trabajador-Centro evaluado) y para alertas de vigencia vía el Centro
/// "principal" del Trabajador (<see cref="IResolverClientePrincipalService"/>)
/// — null si el Trabajador no tiene ninguna asignación activa.
/// </param>
/// <param name="ClienteId">
/// Cliente del <see cref="CentroId"/> resuelto — alimenta la agrupación "por
/// situación" del rediseño de Inicio (hallazgo P-03 de la auditoría de
/// producto 2026-08-16). Ver el comentario de <see cref="IResolverClientePrincipalService"/>
/// sobre por qué una alerta de vigencia (sin Centro propio) no tiene un
/// Cliente canónico cuando el Trabajador tiene varias asignaciones activas.
/// </param>
public record AlertaDto(
    Guid? DocumentoId,
    Guid TrabajadorId,
    string TrabajadorNombre,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    DateOnly? FechaVencimiento,
    EstadoDocumento Estado,
    string? ArchivoUrl,
    string? CentroNombre,
    Guid? CentroId = null,
    Guid? ClienteId = null,
    string? ClienteNombre = null,
    Guid? EmpresaId = null,
    string? EmpresaNombre = null);

public class ObtenerAlertasQueryHandler(
    IConfiguracionQueryContext configuracionContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    IResolverClientePrincipalService resolverClientePrincipal,
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

        // Un documento con vencimiento más allá del umbral ámbar es SIEMPRE
        // Vigente (ver CalculadoraEstadoDocumento) y el filtro de más abajo lo
        // descarta igual — pero antes se traía de PostgreSQL primero y se
        // descartaba en memoria. Este límite superior acota la consulta al
        // universo que puede alertar (Vencido no tiene límite inferior: un
        // documento caducado hace años debe seguir alertando, por eso el
        // filtro es solo por arriba).
        var fechaLimiteAlerta = hoy.AddDays(parametros.UmbralAmbarDias);

        var vigenciaFilas = await (
            from documento in documentosContext.Documentos
            where documento.TrabajadorId != null
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            join trabajador in trabajadoresContext.Trabajadores on documento.TrabajadorId!.Value equals trabajador.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            where documento.FechaVencimiento != null && documento.FechaVencimiento <= fechaLimiteAlerta
            select new
            {
                DocumentoId = documento.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                TipoDocumentoId = tipoDocumento.Id,
                TipoDocumentoNombre = tipoDocumento.Nombre,
                documento.FechaVencimiento,
                documento.ArchivoUrl,
                trabajador.EmpresaId
            })
            .ToListAsync(cancellationToken);

        var alertasVigenciaSinCliente = vigenciaFilas
            .Select(f => new AlertaDto(
                f.DocumentoId, f.TrabajadorId, f.TrabajadorNombre, f.TipoDocumentoId, f.TipoDocumentoNombre,
                f.FechaVencimiento,
                CalculadoraEstadoDocumento.Calcular(
                    f.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                f.ArchivoUrl, CentroNombre: null, EmpresaId: f.EmpresaId))
            .Where(a => a.Estado is EstadoDocumento.Proximo or EstadoDocumento.Urgente or EstadoDocumento.Vencido)
            .ToList();

        var clientePrincipalPorTrabajador = await resolverClientePrincipal.ResolverPorTrabajadorAsync(
            alertasVigenciaSinCliente.Select(a => a.TrabajadorId).Distinct().ToList(), cancellationToken);

        var alertasVigencia = alertasVigenciaSinCliente.Select(a =>
            clientePrincipalPorTrabajador.TryGetValue(a.TrabajadorId, out var principal)
                ? a with { CentroId = principal.CentroId, ClienteId = principal.ClienteId, ClienteNombre = principal.ClienteNombre }
                : a);

        var alertasFaltantes = await ObtenerFaltantesAsync(trabajadorIdsVisibles, centroIdsVisibles, cancellationToken);

        var combinadas = alertasVigencia
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

        return await ConEmpresaNombreAsync(combinadas, cancellationToken);
    }

    /// <summary>
    /// Nombre de Empresa resuelto en un único lote al final (no por fila):
    /// alimenta la agrupación Empresa→Trabajador de "Requiere atención" en
    /// vocabulario Consultora (GrupoCola, ver también
    /// ObtenerDocumentacionBloqueantePendienteQuery, mismo patrón).
    /// </summary>
    private async Task<List<AlertaDto>> ConEmpresaNombreAsync(List<AlertaDto> alertas, CancellationToken cancellationToken)
    {
        var empresaIds = alertas.Where(a => a.EmpresaId is not null).Select(a => a.EmpresaId!.Value).Distinct().ToList();
        if (empresaIds.Count == 0) return alertas;

        var nombresPorEmpresa = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        return alertas
            .Select(a => a.EmpresaId is { } empresaId && nombresPorEmpresa.TryGetValue(empresaId, out var nombre)
                ? a with { EmpresaNombre = nombre }
                : a)
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
                centro.Nombre,
                trabajador.EmpresaId
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return [];

        var empresaIdPorTrabajador = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.First().EmpresaId);

        var parejas = asignacionesActivas
            .Select(a => new ParejaTrabajadorCentro(a.TrabajadorId, a.TrabajadorNombre, a.CentroId, a.Nombre))
            .ToList();

        var faltantes = await documentosFaltantesService.CalcularAsync(parejas, cancellationToken);
        if (faltantes.Count == 0)
            return [];

        // El par (Trabajador, Centro) ya viene exacto de documentosFaltantesService —
        // a diferencia de las alertas de vigencia, aquí no hace falta "elegir uno
        // entre varios", solo resolver el Cliente del Centro exacto que faltó.
        var centroIds = faltantes.Select(f => f.CentroId).Distinct().ToList();
        var clientePorCentro = await (
            from centro in centrosContext.Centros
            where centroIds.Contains(centro.Id)
            // F3b — ClienteId ahora repunta contra Empresas.
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new { centro.Id, ClienteId = cliente.Id, cliente.RazonSocial })
            .ToDictionaryAsync(x => x.Id, x => (x.ClienteId, x.RazonSocial), cancellationToken);

        return faltantes
            .Select(f =>
            {
                var (clienteId, clienteNombre) = clientePorCentro.TryGetValue(f.CentroId, out var cliente)
                    ? (cliente.ClienteId, cliente.RazonSocial)
                    : ((Guid?)null, (string?)null);

                return new AlertaDto(
                    DocumentoId: null,
                    f.TrabajadorId,
                    f.TrabajadorNombre,
                    f.TipoDocumentoId,
                    f.TipoDocumentoNombre,
                    FechaVencimiento: null,
                    EstadoDocumento.Faltante,
                    ArchivoUrl: null,
                    f.CentroNombre,
                    CentroId: f.CentroId,
                    ClienteId: clienteId,
                    ClienteNombre: clienteNombre,
                    EmpresaId: empresaIdPorTrabajador.GetValueOrDefault(f.TrabajadorId));
            })
            .ToList();
    }
}
