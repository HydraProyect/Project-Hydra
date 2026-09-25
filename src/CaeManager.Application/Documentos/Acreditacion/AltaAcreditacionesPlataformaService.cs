using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Trabajadores;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Acreditacion;

/// <summary>
/// Lo que un Command acaba de dar de alta (agregado a su repositorio y todavía
/// sin guardar) y que puede hacer nacer una acreditación de plataforma. Cada
/// colección es opcional: el Command pasa solo lo que ha creado.
/// </summary>
public sealed record AltasConAcreditacion
{
    /// <summary>Documentos nuevos. Solo los de Trabajador y Empresa se acreditan ante una plataforma.</summary>
    public IReadOnlyCollection<Documento> Documentos { get; init; } = [];

    /// <summary>Asignaciones nuevas (Trabajador → Centro). Las cerradas no cuentan.</summary>
    public IReadOnlyCollection<Asignacion> Asignaciones { get; init; } = [];

    /// <summary>
    /// Trabajadores nuevos del mismo alta, solo para conocer su Empresa: la
    /// importación crea el Trabajador y su Asignación en el mismo guardado, así
    /// que la base todavía no sabe a qué Empresa pertenece.
    /// </summary>
    public IReadOnlyCollection<Trabajador> Trabajadores { get; init; } = [];

    /// <summary>Accesos nuevos de un Centro. Solo los de tipo Plataforma se acreditan.</summary>
    public IReadOnlyCollection<CanalGestionDocumental> Canales { get; init; } = [];

    /// <summary>
    /// Posiciones nuevas o editadas de un Centro sobre un TipoDocumento. Las que
    /// quedan en <c>Incluido</c> hacen nacer acreditaciones; todas prevalecen
    /// sobre la fila guardada al resolver qué exige el Centro.
    /// </summary>
    public IReadOnlyCollection<TipoDocumentoCentro> Requisitos { get; init; } = [];

    /// <summary>
    /// Centros que este alta devuelve a <see cref="ModalidadGestionCae.ConGestionCae"/>
    /// (P1-X2). Prevalecen sobre la modalidad guardada, igual que
    /// <see cref="Requisitos"/> sobre la fila guardada: el Command los cambia y
    /// confirma sus acreditaciones en el mismo guardado, así que la base todavía
    /// los tiene como sin gestión CAE.
    /// </summary>
    public IReadOnlyCollection<Guid> CentrosQueVuelvenAGestionCae { get; init; } = [];
}

/// <summary>
/// Regla única de alta de <see cref="AcreditacionDocumentoPlataforma"/> (P0-7).
/// Un Documento se acredita ante un acceso de plataforma
/// (<see cref="CanalGestionDocumental"/> de tipo
/// <see cref="TipoCanalGestion.Plataforma"/>) de un Centro exactamente cuando:
/// <list type="number">
/// <item>su propietario tiene actividad en ese Centro: un Trabajador con una
/// Asignación activa allí, o una Empresa con algún Trabajador suyo asignado
/// allí (Centro cuelga de Cliente, no de Empresa: no hay relación directa);</item>
/// <item>ese Centro exige su tipo, con el mismo criterio que el resto del
/// producto (<see cref="ResolucionTipoDocumentoCentro.Aplica"/>: la fila de
/// <see cref="TipoDocumentoCentro"/> si existe, y si no
/// <see cref="TipoDocumento.CuentaParaCumplimiento"/>).</item>
/// </list>
/// Un Centro sin ningún acceso de tipo Plataforma no recibe acreditaciones.
/// Solo Trabajador y Empresa tienen accesos de plataforma que acreditar
/// (Cliente, Vehículo y Proyecto quedan fuera a propósito).
///
/// Todos los caminos de alta —Documento creado a mano, importado o generado
/// desde plantilla; Asignación individual, en lote o importada; acceso de
/// plataforma nuevo; tipo que un Centro pasa a exigir— llaman aquí. Cada
/// camino evalúa solo lo que su alta pone en juego: un Documento nuevo, ante
/// todos los Centros de su propietario; una Asignación nueva, los Documentos
/// del Trabajador y de su Empresa ante ese Centro; un acceso nuevo, solo ese
/// acceso; un requisito nuevo, solo ese tipo en ese Centro. Nunca repara
/// acreditaciones ajenas al alta que la invoca.
///
/// Idempotente: no agrega una acreditación si el par (Documento, acceso) ya
/// existe (índice único <c>(TenantId, DocumentoId, CanalGestionDocumentalId)</c>).
/// No guarda: agrega al repositorio para que el Command confirme el alta y sus
/// acreditaciones en el mismo <c>SaveChangesAsync</c>, o ninguna. Lee solo con
/// los contextos de consulta del Tenant en curso, bajo RLS: no cruza Tenants.
///
/// No da de baja acreditaciones cuando la obligación desaparece (Asignación
/// cerrada, acceso retirado, tipo que deja de exigirse): eso es otra regla de
/// producto, todavía sin decidir.
/// </summary>
public interface IAltaAcreditacionesPlataformaService
{
    /// <returns>Número de acreditaciones agregadas.</returns>
    Task<int> AgregarPendientesAsync(AltasConAcreditacion altas, CancellationToken cancellationToken = default);
}

public class AltaAcreditacionesPlataformaService(
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio)
    : IAltaAcreditacionesPlataformaService
{
    private sealed record DocumentoCandidato(Guid Id, Guid TipoDocumentoId, Guid? TrabajadorId, Guid? EmpresaId);

    public async Task<int> AgregarPendientesAsync(AltasConAcreditacion altas, CancellationToken cancellationToken = default)
    {
        var documentosNuevos = altas.Documentos
            .Where(d => d.TrabajadorId is not null || d.EmpresaId is not null)
            .Select(d => new DocumentoCandidato(d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId))
            .ToList();
        var asignacionesNuevas = altas.Asignaciones.Where(a => a.FechaBaja is null).ToList();
        var canalesNuevos = altas.Canales.Where(c => c.Tipo == TipoCanalGestion.Plataforma).ToList();
        var requisitosIncluidos = altas.Requisitos.Where(r => r.Incluido).ToList();

        if (documentosNuevos.Count == 0 && asignacionesNuevas.Count == 0
            && canalesNuevos.Count == 0 && requisitosIncluidos.Count == 0)
            return 0;

        // Centros donde el alta pone en juego a todos los propietarios con actividad.
        var centrosPorCompleto = canalesNuevos.Select(c => c.CentroId)
            .Concat(requisitosIncluidos.Select(r => r.CentroId))
            .ToHashSet();

        // Empresa de cada Trabajador: la base, más los Trabajadores del propio alta.
        var empresasDeDocumentos = documentosNuevos.Where(d => d.EmpresaId is not null).Select(d => d.EmpresaId!.Value).ToHashSet();
        var trabajadoresDeInteres = documentosNuevos.Where(d => d.TrabajadorId is not null).Select(d => d.TrabajadorId!.Value)
            .Concat(asignacionesNuevas.Select(a => a.TrabajadorId))
            .ToHashSet();
        var empresaDeTrabajador = await CargarEmpresaDeTrabajadorAsync(
            altas.Trabajadores, trabajadoresDeInteres, empresasDeDocumentos, cancellationToken);

        // Pares (Trabajador, Centro) activos: la base, más las Asignaciones del propio alta.
        var trabajadoresConsultados = empresaDeTrabajador.Keys.Concat(trabajadoresDeInteres).Distinct().ToArray();
        var centrosPorCompletoConsulta = centrosPorCompleto.ToArray();
        var paresActivos = (await asignacionesContext.Asignaciones
                .Where(a => a.FechaBaja == null
                    && (trabajadoresConsultados.Contains(a.TrabajadorId) || centrosPorCompletoConsulta.Contains(a.CentroId)))
                .Select(a => new { a.TrabajadorId, a.CentroId })
                .ToListAsync(cancellationToken))
            .Select(a => (a.TrabajadorId, a.CentroId))
            .Concat(asignacionesNuevas.Select(a => (a.TrabajadorId, a.CentroId)))
            .ToHashSet();

        // Los Trabajadores asignados a un Centro puesto en juego por completo también
        // necesitan su Empresa, para acreditar los Documentos de esa Empresa.
        var trabajadoresSinEmpresaConocida = paresActivos
            .Where(p => centrosPorCompleto.Contains(p.CentroId) && !empresaDeTrabajador.ContainsKey(p.TrabajadorId))
            .Select(p => p.TrabajadorId)
            .ToHashSet();
        if (trabajadoresSinEmpresaConocida.Count > 0)
            foreach (var (trabajadorId, empresaId) in await CargarEmpresaDeTrabajadorAsync(
                         altas.Trabajadores, trabajadoresSinEmpresaConocida, new HashSet<Guid>(), cancellationToken))
                empresaDeTrabajador[trabajadorId] = empresaId;

        // Candidatos (Documento, Centro, acceso concreto o null = cualquiera del Centro).
        var candidatos = new List<(DocumentoCandidato Documento, Guid CentroId, Guid? CanalId)>();

        foreach (var documento in documentosNuevos)
            foreach (var centroId in CentrosDelPropietario(documento, paresActivos, empresaDeTrabajador))
                candidatos.Add((documento, centroId, null));

        var documentosExistentes = await CargarDocumentosDePropietariosAsync(
            asignacionesNuevas, centrosPorCompleto, paresActivos, empresaDeTrabajador, cancellationToken);
        var documentosDisponibles = documentosExistentes
            .Concat(documentosNuevos)
            .DistinctBy(d => d.Id)
            .ToList();

        foreach (var asignacion in asignacionesNuevas)
        {
            empresaDeTrabajador.TryGetValue(asignacion.TrabajadorId, out var empresaId);
            foreach (var documento in documentosDisponibles.Where(d =>
                         d.TrabajadorId == asignacion.TrabajadorId || (empresaId is not null && d.EmpresaId == empresaId)))
                candidatos.Add((documento, asignacion.CentroId, null));
        }

        foreach (var canal in canalesNuevos)
            foreach (var documento in DocumentosConActividadEn(canal.CentroId, documentosDisponibles, paresActivos, empresaDeTrabajador))
                candidatos.Add((documento, canal.CentroId, canal.Id));

        foreach (var requisito in requisitosIncluidos)
            foreach (var documento in DocumentosConActividadEn(requisito.CentroId, documentosDisponibles, paresActivos, empresaDeTrabajador)
                         .Where(d => d.TipoDocumentoId == requisito.TipoDocumentoId))
                candidatos.Add((documento, requisito.CentroId, null));

        if (candidatos.Count == 0)
            return 0;

        // P1-X2: un Centro sin gestión CAE no exige nada, así que no acredita
        // ante sus accesos de plataforma aunque los conserve (CentrosSinGestionCae).
        // Salvo que este mismo alta lo devuelva a gestión CAE.
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(
            centrosContext, candidatos.Select(c => c.CentroId), cancellationToken);
        if (sinGestionCae.Count > 0)
            candidatos.RemoveAll(c => sinGestionCae.Contains(c.CentroId)
                && !altas.CentrosQueVuelvenAGestionCae.Contains(c.CentroId));

        // ¿Exige el Centro el tipo? Misma regla que el cumplimiento del Centro.
        var centroIds = candidatos.Select(c => c.CentroId).Distinct().ToArray();
        var tipoIds = candidatos.Select(c => c.Documento.TipoDocumentoId).Distinct().ToArray();
        var obligatorioPorDefecto = await tiposDocumentoContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .Select(t => new { t.Id, t.Requerido })
            .ToDictionaryAsync(t => t.Id, t => t.Requerido == RequisitoDocumental.Si, cancellationToken);
        var filaPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => centroIds.Contains(tc.CentroId) && tipoIds.Contains(tc.TipoDocumentoId))
                .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));
        foreach (var requisito in altas.Requisitos)
            filaPorPar[(requisito.TipoDocumentoId, requisito.CentroId)] = requisito;

        // Accesos de plataforma de cada Centro: la base, más los del propio alta.
        var canalesPorCentro = (await centrosContext.CanalesGestionDocumental
                .Where(c => centroIds.Contains(c.CentroId) && c.Tipo == TipoCanalGestion.Plataforma)
                .Select(c => new { c.Id, c.CentroId })
                .ToListAsync(cancellationToken))
            .Select(c => (c.Id, c.CentroId))
            .Concat(canalesNuevos.Select(c => (c.Id, c.CentroId)))
            .Distinct()
            .ToLookup(c => c.CentroId, c => c.Id);

        var documentoIds = candidatos.Select(c => c.Documento.Id).Distinct().ToArray();
        var yaAcreditados = (await documentosContext.AcreditacionesDocumentoPlataforma
                .Where(a => documentoIds.Contains(a.DocumentoId))
                .Select(a => new { a.DocumentoId, a.CanalGestionDocumentalId })
                .ToListAsync(cancellationToken))
            .Select(a => (a.DocumentoId, a.CanalGestionDocumentalId))
            .ToHashSet();

        var agregadas = 0;
        foreach (var (documento, centroId, canalId) in candidatos)
        {
            if (!ResolucionTipoDocumentoCentro.Aplica(
                    filaPorPar, documento.TipoDocumentoId, centroId,
                    obligatorioPorDefecto.GetValueOrDefault(documento.TipoDocumentoId)))
                continue;

            foreach (var canal in canalesPorCentro[centroId])
            {
                if (canalId is not null && canal != canalId) continue;
                if (!yaAcreditados.Add((documento.Id, canal))) continue;

                acreditacionRepositorio.Agregar(new AcreditacionDocumentoPlataforma(documento.Id, canal));
                agregadas++;
            }
        }

        return agregadas;
    }

    private async Task<Dictionary<Guid, Guid?>> CargarEmpresaDeTrabajadorAsync(
        IReadOnlyCollection<Trabajador> trabajadoresNuevos, IReadOnlySet<Guid> trabajadorIds, IReadOnlySet<Guid> empresaIds,
        CancellationToken cancellationToken)
    {
        var resultado = new Dictionary<Guid, Guid?>();
        var trabajadorIdsConsulta = trabajadorIds.ToArray();
        var empresaIdsConsulta = empresaIds.ToArray();
        if (trabajadorIds.Count > 0 || empresaIds.Count > 0)
            foreach (var t in await trabajadoresContext.Trabajadores
                         .Where(t => trabajadorIdsConsulta.Contains(t.Id) || (t.EmpresaId != null && empresaIdsConsulta.Contains(t.EmpresaId.Value)))
                         .Select(t => new { t.Id, t.EmpresaId })
                         .ToListAsync(cancellationToken))
                resultado[t.Id] = t.EmpresaId;

        foreach (var t in trabajadoresNuevos.Where(t =>
                     trabajadorIds.Contains(t.Id) || (t.EmpresaId is not null && empresaIds.Contains(t.EmpresaId.Value))))
            resultado[t.Id] = t.EmpresaId;

        return resultado;
    }

    /// <summary>Documentos ya guardados de los propietarios que las Asignaciones nuevas y los Centros puestos en juego por completo afectan.</summary>
    private async Task<List<DocumentoCandidato>> CargarDocumentosDePropietariosAsync(
        IReadOnlyCollection<Asignacion> asignacionesNuevas, IReadOnlySet<Guid> centrosPorCompleto,
        IReadOnlySet<(Guid TrabajadorId, Guid CentroId)> paresActivos, IReadOnlyDictionary<Guid, Guid?> empresaDeTrabajador,
        CancellationToken cancellationToken)
    {
        var trabajadorIds = asignacionesNuevas.Select(a => a.TrabajadorId)
            .Concat(paresActivos.Where(p => centrosPorCompleto.Contains(p.CentroId)).Select(p => p.TrabajadorId))
            .Distinct().ToArray();
        if (trabajadorIds.Length == 0) return [];

        var empresaIds = trabajadorIds
            .Select(id => empresaDeTrabajador.GetValueOrDefault(id))
            .Where(e => e is not null)
            .Select(e => e!.Value)
            .ToArray();

        return (await documentosContext.Documentos
                .Where(d => (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))
                    || (d.EmpresaId != null && empresaIds.Contains(d.EmpresaId.Value)))
                .Select(d => new { d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId })
                .ToListAsync(cancellationToken))
            .Select(d => new DocumentoCandidato(d.Id, d.TipoDocumentoId, d.TrabajadorId, d.EmpresaId))
            .ToList();
    }

    private static IEnumerable<Guid> CentrosDelPropietario(
        DocumentoCandidato documento, IReadOnlySet<(Guid TrabajadorId, Guid CentroId)> paresActivos,
        IReadOnlyDictionary<Guid, Guid?> empresaDeTrabajador) =>
        paresActivos
            .Where(p => documento.TrabajadorId is not null
                ? p.TrabajadorId == documento.TrabajadorId
                : empresaDeTrabajador.GetValueOrDefault(p.TrabajadorId) == documento.EmpresaId)
            .Select(p => p.CentroId)
            .Distinct();

    private static IEnumerable<DocumentoCandidato> DocumentosConActividadEn(
        Guid centroId, IReadOnlyCollection<DocumentoCandidato> documentos,
        IReadOnlySet<(Guid TrabajadorId, Guid CentroId)> paresActivos, IReadOnlyDictionary<Guid, Guid?> empresaDeTrabajador)
    {
        var trabajadores = paresActivos.Where(p => p.CentroId == centroId).Select(p => p.TrabajadorId).ToHashSet();
        var empresas = trabajadores
            .Select(t => empresaDeTrabajador.GetValueOrDefault(t))
            .Where(e => e is not null)
            .ToHashSet();

        return documentos.Where(d =>
            (d.TrabajadorId is not null && trabajadores.Contains(d.TrabajadorId.Value))
            || (d.EmpresaId is not null && empresas.Contains(d.EmpresaId)));
    }
}
