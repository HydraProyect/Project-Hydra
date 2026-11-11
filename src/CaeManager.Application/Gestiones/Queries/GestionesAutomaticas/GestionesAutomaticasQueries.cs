using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Integraciones;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;

/// <summary>
/// Avance de una Gestión automática sobre los pasos que ve quien consulta (§ 4.2, D-4): hechos = subidos
/// pendientes de validar + validados (o «no lo pide»).
/// </summary>
public sealed record AvanceGestionAutomatica(int Pasos, int Hechos, int Validados, int PendientesDeValidar)
{
    public static AvanceGestionAutomatica De(IReadOnlyCollection<EstadoAcreditacion> estados) => new(
        estados.Count,
        estados.Count(GestionAutomatica.PasoHecho),
        estados.Count(e => e is EstadoAcreditacion.Aceptada or EstadoAcreditacion.NoRequerida),
        estados.Count(e => e == EstadoAcreditacion.Subida));
}

public sealed record GestionAutomaticaResumenDto(
    Guid Id, Guid DocumentoId, OrigenGestionAutomatica Origen, string TipoDocumentoNombre, string TitularNombre,
    Guid? EmpresaGestionadaId, string? EmpresaGestionadaNombre, DateTime CreadoEnUtc, EstadoGestionAutomatica Estado,
    AvanceGestionAutomatica Avance, IReadOnlyList<string> Plataformas, bool TieneRechazo);

public sealed record DocumentoDeGestionAutomaticaDto(
    Guid Id, Guid? TrabajadorId, Guid? EmpresaId, string? IdentificadorTitular, DateOnly FechaEmision,
    DateOnly? FechaVencimiento, EstadoVigenciaDocumento EstadoVigencia, bool Operativo, bool VigenteEnTalveg, bool TieneArchivo);

/// <summary>
/// Un paso visible. <paramref name="CentroCodigo"/>, <paramref name="CentroDireccion"/> e <paramref name="InicioAsignacion"/>
/// son la pestaña Centro de «Datos a mano»: el Centro ya lo da a ver el paso (D-7), y el inicio es el de la Asignación
/// del Trabajador titular a ese Centro (null en un Documento de Empresa o sin Asignación).
/// </summary>
public sealed record PasoGestionAutomaticaDto(
    Guid AcreditacionId, EstadoAcreditacion Estado, bool Hecho, DateOnly? FechaVencimientoEnPlataforma,
    Guid CanalId, string Plataforma, string? UrlAcceso, bool TieneUsuario, string? Notas,
    Guid CentroId, string CentroNombre, string? ClienteNombre,
    string? CentroCodigo = null, string? CentroDireccion = null, DateOnly? InicioAsignacion = null);

/// <summary>
/// Pestaña Trabajador de «Datos a mano» (ventana de la Gestión, § 1.1): lo que los portales piden del Trabajador titular,
/// tal como está en su ficha. Un dato null es «Falta en TALVEG», salvo <paramref name="TipoPermisoTrabajo"/> cuando
/// <paramref name="PermisoTrabajoVisible"/> es false: entonces quien consulta no puede leerlo (la misma
/// <see cref="IPoliticaNotaInterna"/> que la ficha del Trabajador) y la pantalla no lo enseña ni ofrece completarlo. El
/// false por defecto hace que quien construya el DTO sin pasar por esa regla oculte, no enseñe.
/// </summary>
public sealed record DatosTrabajadorDeGestionDto(
    Guid Id, string NombreCompleto, string? Dni, DateOnly? FechaNacimiento, string? NumeroAfiliacionSeguridadSocial,
    string? Nacionalidad, string? Puesto, string? TipoVinculo, DateOnly? FechaAltaEmpresa, string? FiguraEnRnt,
    string? TipoPermisoTrabajo, DateOnly? FechaBajaEmpresa, bool PermisoTrabajoVisible = false);

/// <summary>
/// Detalle de una Gestión automática. <paramref name="EmpresaGestionadaCif"/> es el CIF de la Empresa gestionada de la
/// cabecera y sigue su misma regla de cartera: sin <see cref="GestionAutomaticaResumenDto.EmpresaGestionadaId"/>, null.
/// <paramref name="Trabajador"/> solo llega si el Documento es de un Trabajador que quien consulta ve.
/// </summary>
public sealed record GestionAutomaticaDetalleDto(
    GestionAutomaticaResumenDto Resumen, Guid? NacidaPorUsuarioId, DateTime? SustituidaEnUtc, Guid? SustituidaPorGestionId,
    DocumentoDeGestionAutomaticaDto Documento, IReadOnlyList<PasoGestionAutomaticaDto> Pasos,
    string? EmpresaGestionadaCif = null, DatosTrabajadorDeGestionDto? Trabajador = null);

public sealed record ListadoGestionesAutomaticasDto(
    IReadOnlyList<GestionAutomaticaResumenDto> Filas, IReadOnlyDictionary<EstadoGestionAutomatica, int> RecuentosPorEstado);

/// <summary>
/// Listado de Gestiones automáticas que ve quien consulta. <paramref name="Estado"/> filtra después de contar:
/// cada cifra de <see cref="ListadoGestionesAutomaticasDto.RecuentosPorEstado"/> dice cuántas quedarían al marcar
/// ese estado, y sale del mismo universo que las filas (<see cref="PasosVisiblesDeGestionesAutomaticas"/>).
/// <paramref name="DocumentoId"/> da la Gestión de un Documento (enlace «se ha abierto la Gestión»).
/// Una Gestión que sigue abierta con su Documento ya en el historial cuenta y filtra como Sustituida, la marcara o
/// no la renovación (<see cref="GestionAutomatica.ResolverEstado"/>): ninguna cifra la da por abierta.
/// </summary>
public record ObtenerGestionesAutomaticasQuery(EstadoGestionAutomatica? Estado = null, Guid? DocumentoId = null)
    : IRequest<ListadoGestionesAutomaticasDto>;

/// <summary>La Gestión automática con sus pasos visibles en el orden de D-5; null si no existe o no se ve.</summary>
public record ObtenerGestionAutomaticaQuery(Guid Id) : IRequest<GestionAutomaticaDetalleDto?>;

public class ObtenerGestionesAutomaticasQueryHandler(
    IGestionesQueryContext gestionesContext, IDocumentosQueryContext documentosContext,
    ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerGestionesAutomaticasQuery, ListadoGestionesAutomaticasDto>
{
    public async Task<ListadoGestionesAutomaticasDto> Handle(ObtenerGestionesAutomaticasQuery request, CancellationToken cancellationToken)
    {
        var pasos = await PasosVisiblesDeGestionesAutomaticas.CargarAsync(
            gestionesContext, documentosContext, centrosContext, proveedoresContext, alcanceDatos,
            gestionId: null, request.DocumentoId, cancellationToken);

        var resumenes = await CabecerasGestionesAutomaticas.ResumirAsync(
            pasos, gestionesContext, documentosContext, trabajadoresContext, empresasContext, tiposDocumentoContext, alcanceDatos,
            cancellationToken);

        var recuentos = Enum.GetValues<EstadoGestionAutomatica>()
            .ToDictionary(e => e, e => resumenes.Count(r => r.Estado == e));

        var filas = resumenes
            .Where(r => request.Estado is null || r.Estado == request.Estado)
            .OrderByDescending(r => r.TieneRechazo)
            .ThenBy(r => r.CreadoEnUtc)
            .ThenBy(r => r.Id)
            .ToList();

        return new ListadoGestionesAutomaticasDto(filas, recuentos);
    }
}

public class ObtenerGestionAutomaticaQueryHandler(
    IGestionesQueryContext gestionesContext, IDocumentosQueryContext documentosContext,
    ICentrosQueryContext centrosContext, IProveedoresPlataformaCaeQueryContext proveedoresContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext, IAsignacionesQueryContext asignacionesContext,
    IAlcanceDatosService alcanceDatos, IPoliticaNotaInterna politicaNotaInterna)
    : IRequestHandler<ObtenerGestionAutomaticaQuery, GestionAutomaticaDetalleDto?>
{
    public async Task<GestionAutomaticaDetalleDto?> Handle(ObtenerGestionAutomaticaQuery request, CancellationToken cancellationToken)
    {
        var pasos = await PasosVisiblesDeGestionesAutomaticas.CargarAsync(
            gestionesContext, documentosContext, centrosContext, proveedoresContext, alcanceDatos,
            request.Id, documentoId: null, cancellationToken);
        if (pasos.Count == 0)
            return null;

        var resumen = (await CabecerasGestionesAutomaticas.ResumirAsync(
            pasos, gestionesContext, documentosContext, trabajadoresContext, empresasContext, tiposDocumentoContext, alcanceDatos,
            cancellationToken))
            .Single();

        var gestion = await gestionesContext.GestionesAutomaticas
            .Where(g => g.Id == request.Id)
            .Select(g => new { g.NacidaPorUsuarioId, g.SustituidaEnUtc, g.SustituidaPorGestionId })
            .SingleAsync(cancellationToken);

        var documento = await documentosContext.Documentos
            .Where(d => d.Id == resumen.DocumentoId)
            .Select(d => new
            {
                d.Id,
                d.TrabajadorId,
                d.EmpresaId,
                d.FechaEmision,
                d.FechaVencimiento,
                d.EstadoVigencia,
                d.ArchivoUrl,
                d.SustituidoEnUtc,
                d.SustituidoPorDocumentoId
            })
            .SingleAsync(cancellationToken);

        // Sustituida sin marca: la lectura la deriva porque su Documento ya está en el historial (ResolverEstado), y
        // entonces el instante y la sustituta salen del Documento: cuándo se sustituyó y la Gestión del que lo sustituyó.
        var sustituidaSinMarca = resumen.Estado == EstadoGestionAutomatica.Sustituida && gestion.SustituidaEnUtc is null;
        var sustituidaEnUtc = gestion.SustituidaEnUtc ?? (sustituidaSinMarca ? documento.SustituidoEnUtc : null);
        var sustitutaId = gestion.SustituidaPorGestionId;
        if (sustitutaId is null && sustituidaSinMarca && documento.SustituidoPorDocumentoId is { } documentoSustitutoId)
            sustitutaId = await gestionesContext.GestionesAutomaticas
                .Where(g => g.DocumentoId == documentoSustitutoId)
                .Select(g => (Guid?)g.Id)
                .SingleOrDefaultAsync(cancellationToken);

        // El enlace a la Gestión que la sustituyó solo sale si quien consulta también ve esa: su Id no puede revelar
        // la existencia de una Gestión con todos sus pasos fuera de su cartera (mismo universo, D-7).
        var sustitutaVisible = sustitutaId is { } idSustituta
                               && (await PasosVisiblesDeGestionesAutomaticas.CargarAsync(
                                   gestionesContext, documentosContext, centrosContext, proveedoresContext, alcanceDatos,
                                   idSustituta, documentoId: null, cancellationToken)).Count > 0;
        var operativo = await documentosContext.Documentos.Operativos().AnyAsync(d => d.Id == resumen.DocumentoId, cancellationToken);

        string? identificador = documento.TrabajadorId is { } trabajadorId
            ? await trabajadoresContext.Trabajadores.Where(t => t.Id == trabajadorId).Select(t => t.Dni).FirstOrDefaultAsync(cancellationToken)
            : await empresasContext.Empresas.Where(e => e.Id == documento.EmpresaId).Select(e => e.Cif).FirstOrDefaultAsync(cancellationToken);

        var vigente = operativo && (documento.EstadoVigencia == EstadoVigenciaDocumento.NoCaduca
                                    || (documento.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha
                                        && documento.FechaVencimiento >= DiaDeNegocio.Hoy()));

        var canalIds = pasos.Select(p => p.CanalId).Distinct().ToArray();
        var canales = await centrosContext.CanalesGestionDocumental
            .Where(c => canalIds.Contains(c.Id))
            .Select(c => new { c.Id, c.UrlAcceso, TieneUsuario = c.Usuario != null, c.Notas })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        var clientesEmpresarialesIds = pasos.Select(p => p.ClienteEmpresarialId).Distinct().ToArray();
        var clientes = await empresasContext.Empresas
            .Where(e => clientesEmpresarialesIds.Contains(e.Id))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        // Pestaña Centro de «Datos a mano»: los Centros ya son los de los pasos visibles (D-7).
        var centroIds = pasos.Select(p => p.CentroId).Distinct().ToArray();
        var centros = await centrosContext.Centros
            .Where(c => centroIds.Contains(c.Id))
            .Select(c => new { c.Id, c.CodigoCentro, c.Direccion })
            .ToDictionaryAsync(c => c.Id, cancellationToken);

        // Inicio de la asignación: la Asignación del Trabajador titular a cada Centro; la activa si la hay, si no la última.
        var inicioPorCentro = documento.TrabajadorId is { } titularId
            ? (await asignacionesContext.Asignaciones
                    .Where(a => a.TrabajadorId == titularId && centroIds.Contains(a.CentroId))
                    .Select(a => new { a.CentroId, a.FechaAlta, Activa = a.FechaBaja == null })
                    .ToListAsync(cancellationToken))
                .GroupBy(a => a.CentroId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.Activa).ThenByDescending(a => a.FechaAlta).First().FechaAlta)
            : new Dictionary<Guid, DateOnly>();

        var pasosDto = PasosVisiblesDeGestionesAutomaticas.Ordenar(pasos)
            .Select(p =>
            {
                var canal = canales.GetValueOrDefault(p.CanalId);
                var centro = centros.GetValueOrDefault(p.CentroId);
                return new PasoGestionAutomaticaDto(
                    p.AcreditacionId, p.Estado, GestionAutomatica.PasoHecho(p.Estado), p.FechaVencimientoEnPlataforma,
                    p.CanalId, p.Plataforma, canal?.UrlAcceso, canal?.TieneUsuario ?? false, canal?.Notas,
                    p.CentroId, p.CentroNombre, clientes.GetValueOrDefault(p.ClienteEmpresarialId),
                    centro?.CodigoCentro, centro?.Direccion,
                    inicioPorCentro.TryGetValue(p.CentroId, out var inicio) ? inicio : null);
            })
            .ToList();

        // El CIF va con el nombre de la cabecera: si la regla de cartera no deja nombrar a la Empresa gestionada
        // (CabecerasGestionesAutomaticas), tampoco su CIF.
        var cif = resumen.EmpresaGestionadaId is { } gestionadaId
            ? await empresasContext.Empresas.Where(e => e.Id == gestionadaId).Select(e => e.Cif).FirstOrDefaultAsync(cancellationToken)
            : null;

        return new GestionAutomaticaDetalleDto(
            resumen, gestion.NacidaPorUsuarioId, sustituidaEnUtc, sustitutaVisible ? sustitutaId : null,
            new DocumentoDeGestionAutomaticaDto(
                documento.Id, documento.TrabajadorId, documento.EmpresaId, identificador, documento.FechaEmision,
                documento.FechaVencimiento, documento.EstadoVigencia, operativo, vigente,
                !string.IsNullOrWhiteSpace(documento.ArchivoUrl)),
            pasosDto,
            string.IsNullOrWhiteSpace(cif) ? null : cif,
            await DatosDelTrabajadorAsync(documento.TrabajadorId, cancellationToken));
    }

    /// <summary>
    /// La pestaña Trabajador: los mismos datos que su ficha. No necesita otra comprobación de alcance porque el universo de
    /// la Gestión ya la hace: <see cref="PasosVisiblesDeGestionesAutomaticas.CargarAsync"/> solo admite Documentos de
    /// Trabajador cuyo Trabajador está en <see cref="IAlcanceDatosService.ObtenerTrabajadorIdsVisiblesAsync"/>, y sin pasos
    /// visibles el detalle es null. El tipo de permiso de trabajo sí lleva la regla propia de la ficha
    /// (<c>ObtenerTrabajadorPorIdQueryHandler</c>, decisión de Chris del 2026-10-15): solo sale del servidor si quien
    /// consulta lee la nota interna (<see cref="IPoliticaNotaInterna"/>).
    /// </summary>
    private async Task<DatosTrabajadorDeGestionDto?> DatosDelTrabajadorAsync(Guid? trabajadorId, CancellationToken cancellationToken)
    {
        if (trabajadorId is not { } id)
            return null;

        var datos = await trabajadoresContext.Trabajadores
            .Where(t => t.Id == id)
            .Select(t => new DatosTrabajadorDeGestionDto(
                t.Id, t.Nombre + " " + t.Apellidos, t.Dni, t.FechaNacimiento, t.NumeroAfiliacionSeguridadSocial,
                t.Nacionalidad, t.Puesto, t.TipoVinculo, t.FechaAltaEmpresa, t.FiguraEnRnt, t.TipoPermisoTrabajo,
                t.FechaBajaEmpresa, false))
            .FirstOrDefaultAsync(cancellationToken);
        if (datos is null)
            return null;

        return await politicaNotaInterna.PuedeLeerAsync(cancellationToken)
            ? datos with { PermisoTrabajoVisible = true }
            : datos with { TipoPermisoTrabajo = null, PermisoTrabajoVisible = false };
    }
}

/// <summary>
/// Cabecera (título, Empresa gestionada, estado, avance) de las Gestiones que tienen pasos visibles. La Empresa
/// gestionada de un Documento de Trabajador es su Empresa empleadora y solo se informa si está en algún alcance de
/// cartera de quien consulta (<see cref="AlcanceDatosServiceExtensions.EmpresaEnAlgunAlcanceDeCarteraAsync"/>); si
/// no, llega null, id y nombre a la vez.
/// </summary>
internal static class CabecerasGestionesAutomaticas
{
    public static async Task<List<GestionAutomaticaResumenDto>> ResumirAsync(
        IReadOnlyCollection<PasoVisibleGestionAutomatica> pasos, IGestionesQueryContext gestionesContext,
        IDocumentosQueryContext documentosContext, ITrabajadoresQueryContext trabajadoresContext,
        IEmpresasQueryContext empresasContext, ITiposDocumentoQueryContext tiposDocumentoContext,
        IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        if (pasos.Count == 0)
            return [];

        var gestionIds = pasos.Select(p => p.GestionId).Distinct().ToArray();
        var cabeceras = await (
                from gestion in gestionesContext.GestionesAutomaticas
                join documento in documentosContext.Documentos on gestion.DocumentoId equals documento.Id
                join tipo in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
                where gestionIds.Contains(gestion.Id)
                select new
                {
                    gestion.Id,
                    gestion.DocumentoId,
                    gestion.Origen,
                    gestion.CreadoEnUtc,
                    gestion.SustituidaEnUtc,
                    DocumentoSustituido = documento.SustituidoEnUtc != null,
                    TipoNombre = tipo.Nombre,
                    documento.TrabajadorId,
                    documento.EmpresaId
                })
            .ToListAsync(cancellationToken);

        var trabajadorIds = cabeceras.Where(c => c.TrabajadorId != null).Select(c => c.TrabajadorId!.Value).Distinct().ToArray();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id))
            // La Empresa empleadora: EmpresaId ?? SubcontrataId, como RequisitosObligatoriosIncumplidos y
            // CalculoEstadoCentroService (CK_Trabajadores_EmpresaXorSubcontrata: solo una de las dos). SubcontrataId es
            // deuda terminológica legacy: aquí identifica la Empresa empleadora contraparte.
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos, EmpresaEmpleadoraId = t.EmpresaId ?? t.SubcontrataId })
            .ToDictionaryAsync(t => t.Id, cancellationToken);

        // Ver al Trabajador no da a ver a quién emplea: la Empresa empleadora solo se nombra si está en algún alcance de
        // cartera de quien consulta (la regla de la Semana por Empresa y del Mes). Si no, la Gestión sigue viéndose
        // —su universo es el del Trabajador y el Centro— pero sin Empresa gestionada, como el Mes deja el vencimiento
        // sin Empresa. La Empresa de un Documento de Empresa ya la filtró el universo (alcance de gestión).
        var empleadorasEnAlcance = new HashSet<Guid>();
        foreach (var empleadora in trabajadores.Values.Where(t => t.EmpresaEmpleadoraId != null)
                     .Select(t => t.EmpresaEmpleadoraId!.Value).Distinct())
        {
            if (await alcanceDatos.EmpresaEnAlgunAlcanceDeCarteraAsync(empleadora, cancellationToken))
                empleadorasEnAlcance.Add(empleadora);
        }

        var empresaIds = cabeceras.Where(c => c.EmpresaId != null).Select(c => c.EmpresaId!.Value)
            .Concat(empleadorasEnAlcance)
            .Distinct().ToArray();
        var empresas = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var pasosPorGestion = pasos.ToLookup(p => p.GestionId);
        return cabeceras.Select(c =>
            {
                var trabajador = c.TrabajadorId is { } tId ? trabajadores.GetValueOrDefault(tId) : null;
                var empleadoraVisible = trabajador?.EmpresaEmpleadoraId is { } empleadoraId && empleadorasEnAlcance.Contains(empleadoraId)
                    ? empleadoraId
                    : (Guid?)null;
                // Id y nombre van juntos, como en el Mes: una Empresa que la consulta no encuentra (dada de baja) no deja
                // un id sin rótulo.
                var empresaGestionadaId = (c.EmpresaId ?? empleadoraVisible) is { } gestionada && empresas.ContainsKey(gestionada)
                    ? gestionada
                    : (Guid?)null;
                var titular = trabajador?.Nombre
                              ?? (c.EmpresaId is { } eId ? empresas.GetValueOrDefault(eId) : null)
                              ?? string.Empty;
                var deLaGestion = pasosPorGestion[c.Id].ToList();
                var estados = deLaGestion.Select(p => p.Estado).ToList();
                return new GestionAutomaticaResumenDto(
                    c.Id, c.DocumentoId, c.Origen, c.TipoNombre, titular,
                    empresaGestionadaId, empresaGestionadaId is { } id ? empresas.GetValueOrDefault(id) : null,
                    c.CreadoEnUtc, GestionAutomatica.ResolverEstado(c.SustituidaEnUtc is not null, c.DocumentoSustituido, estados),
                    AvanceGestionAutomatica.De(estados),
                    PasosVisiblesDeGestionesAutomaticas.Ordenar(deLaGestion).Select(p => p.Plataforma).Distinct().ToList(),
                    estados.Contains(EstadoAcreditacion.Rechazada));
            })
            .ToList();
    }
}
