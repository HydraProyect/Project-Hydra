using CaeManager.Application.Alertas;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Reclamaciones;

/// <summary>Por qué se pide algo que no tiene fecha de vencimiento.</summary>
public enum MotivoPendienteDeReclamacion
{
    /// <summary>El documento nunca se subió: no hay Documento, solo un requisito sin cubrir.</summary>
    Ausente = 1,

    /// <summary>El Documento existe pero su vigencia está «Sin confirmar» y sin fecha (<see cref="VentanaReclamacion.EsSinConfirmarSinFecha"/>).</summary>
    SinConfirmar = 2,
}

/// <summary>
/// Lo que el Gestor CAE pide en una reclamación y no se ancla a un vencimiento. Dos formas, nunca las dos a la vez:
/// <list type="bullet">
/// <item><b>Sin confirmar</b>: <paramref name="DocumentoId"/> informado — el Documento que ya existe.</item>
/// <item><b>Ausente</b>: <paramref name="DocumentoId"/> nulo y <paramref name="TipoDocumentoId"/> informado — el documento que
/// falta, con <paramref name="TrabajadorId"/> cuando es de Trabajador y sin él cuando es de la Empresa titular.</item>
/// </list>
/// Lo que llega de la pantalla se revalida contra la base (<see cref="IPendientesDeReclamacionService.ResolverParaClienteAsync"/>):
/// nada de esto se acepta por venir en la petición.
/// </summary>
public sealed record PendienteSinFecha(Guid? DocumentoId, Guid? TrabajadorId = null, Guid? TipoDocumentoId = null)
{
    public bool EsAusente => DocumentoId is null;

    public static PendienteSinFecha SinConfirmar(Guid documentoId) => new(documentoId);

    public static PendienteSinFecha Ausente(Guid? trabajadorId, Guid tipoDocumentoId) => new(null, trabajadorId, tipoDocumentoId);
}

/// <summary>
/// Una fila de lo pendiente sin fecha, ya resuelta contra la base: es lo que el lote enseña, lo que el envío acepta y lo
/// que el correo dice.
/// </summary>
/// <param name="DocumentoId">El Documento «Sin confirmar»; nulo si el documento falta.</param>
/// <param name="TrabajadorId">A quién le falta o de quién es; nulo en un documento de Empresa.</param>
public sealed record DocumentoPendienteDto(
    Guid? DocumentoId,
    Guid? TrabajadorId,
    string? TrabajadorNombre,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    MotivoPendienteDeReclamacion Motivo)
{
    /// <summary>Identidad estable de la fila (para la selección y el <c>@key</c> de la pantalla): el Documento si lo hay; si no, el par (a quién, qué).</summary>
    public string Clave => DocumentoId is { } id ? $"doc:{id}" : $"falta:{TrabajadorId}:{TipoDocumentoId}";

    /// <summary>La misma fila como petición de envío.</summary>
    public PendienteSinFecha ComoPedido() =>
        Motivo == MotivoPendienteDeReclamacion.SinConfirmar
            ? PendienteSinFecha.SinConfirmar(DocumentoId!.Value)
            : PendienteSinFecha.Ausente(TrabajadorId, TipoDocumentoId);
}

/// <summary>Lo pendiente sin fecha de un titular de reclamación (Cliente empresarial o Empresa), con su nombre.</summary>
public sealed record PendientesDeUnTitular(Guid TitularId, string TitularNombre, IReadOnlyList<DocumentoPendienteDto> Pendientes);

/// <summary>
/// El ÚNICO sitio que decide qué se puede <b>pedir</b> sin que haya un vencimiento al que anclarlo: un documento que nunca se
/// subió o un «Sin confirmar» sin fecha. Es la tercera pata del flujo de reclamación, junto a la ventana de
/// <see cref="VentanaReclamacion"/>: la vista previa del lote, el envío y la vista previa de «Reclamar de nuevo» leen de
/// aquí, de modo que lo que se ofrece es exactamente lo que se acepta.
///
/// <para>
/// No crea un flujo paralelo: no envía, no resuelve destinatarios ni registra nada. Entrega filas y los comandos de envío
/// existentes (<c>EnviarReclamacionCommand</c>, <c>EnviarReclamacionEmpresaCommand</c>) las incorporan a su mismo correo,
/// su mismo registro y su misma autorización.
/// </para>
///
/// <para>
/// De dónde sale que un documento «falta»: de Trabajador, el mismo cálculo que las Alertas
/// (<see cref="IDocumentosFaltantesService"/>: el Tipo aplica al Centro y no hay ningún Documento operativo de ese Tipo);
/// de Empresa, la regla única de acceso por Centro (<see cref="IEvaluacionDeAccesoPorCentroService"/>, situación
/// <see cref="SituacionDeRequisitoBloqueante.Ausente"/>) — es el único lugar que sabe qué documentos de Empresa exige un Centro,
/// así que solo se piden los que bloquean el acceso. Alcance: el mismo que el lote por vencimiento (Trabajadores, Centros y
/// Clientes visibles; Empresas de gestión), y Centros sin gestión CAE excluidos.
/// </para>
/// </summary>
public interface IPendientesDeReclamacionService
{
    /// <summary>Lo pendiente de documentos de Trabajador, agrupado por el Cliente empresarial titular de cada Centro (mismo reparto que el lote por vencimiento).</summary>
    Task<IReadOnlyList<PendientesDeUnTitular>> ListarParaClientesAsync(
        Guid? centroId, Guid? clienteId, IReadOnlyCollection<Guid>? trabajadorIds, IReadOnlyList<Guid>? tipoDocumentoIds,
        CancellationToken cancellationToken);

    /// <summary>Lo pendiente de documentos de Empresa, agrupado por Empresa titular.</summary>
    Task<IReadOnlyList<PendientesDeUnTitular>> ListarParaEmpresasAsync(
        Guid? empresaId, IReadOnlyList<Guid>? tipoDocumentoIds, CancellationToken cancellationToken);

    /// <summary>
    /// Revalida lo pedido contra la base para un Cliente empresarial: todo o nada. Un pedido que ya no es pendiente (se subió el
    /// documento, se anotó la vigencia, el Trabajador salió del Centro) hace fallar el conjunto, igual que un documento fuera
    /// de la ventana hace fallar el envío por vencimiento.
    /// </summary>
    Task<Result<IReadOnlyList<DocumentoPendienteDto>>> ResolverParaClienteAsync(
        Guid clienteId, IReadOnlyList<PendienteSinFecha> pedidos, CancellationToken cancellationToken);

    /// <summary>Lo mismo para una Empresa titular (documentos de Empresa).</summary>
    Task<Result<IReadOnlyList<DocumentoPendienteDto>>> ResolverParaEmpresaAsync(
        Guid empresaId, IReadOnlyList<PendienteSinFecha> pedidos, CancellationToken cancellationToken);

    /// <summary>
    /// De los Documentos dados, los que hoy siguen «Sin confirmar» y sin fecha (<see cref="VentanaReclamacion.SinConfirmarSinFecha"/>).
    /// Lo usa el historial para saber qué línea de una reclamación enviada se reenvía como pendiente y no por vencimiento: así la
    /// pregunta «¿sigue sin confirmar?» tiene un solo sitio, el que también decide qué se ofrece y qué se acepta.
    /// </summary>
    Task<IReadOnlySet<Guid>> DocumentoIdsAunSinConfirmarSinFechaAsync(
        IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken);
}

public class PendientesDeReclamacionService(
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    IDocumentosFaltantesService documentosFaltantes,
    IEvaluacionDeAccesoPorCentroService evaluacionDeAcceso,
    IAlcanceDatosService alcanceDatos)
    : IPendientesDeReclamacionService
{
    private sealed record Par(Guid TrabajadorId, string TrabajadorNombre, Guid CentroId, string CentroNombre, Guid ClienteId, string ClienteNombre);

    public async Task<IReadOnlyList<PendientesDeUnTitular>> ListarParaClientesAsync(
        Guid? centroId, Guid? clienteId, IReadOnlyCollection<Guid>? trabajadorIds, IReadOnlyList<Guid>? tipoDocumentoIds,
        CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);
        var clienteIdsVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);

        // Los pares Trabajador × Centro donde el lote por vencimiento ofrecería algo: Asignación activa, Centro con gestión
        // CAE (P1-X2) y todo dentro del alcance visible. Mismo join que ObtenerLoteReclamacionQuery.
        var pares = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            where trabajadorIds == null || trabajadorIds.Contains(asignacion.TrabajadorId)
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(asignacion.TrabajadorId)
            where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            where centro.GestionCae != ModalidadGestionCae.SinGestionCae
            where centroId == null || centro.Id == centroId
            where clienteId == null || centro.ClienteId == clienteId
            where clienteIdsVisibles == null || clienteIdsVisibles.Contains(centro.ClienteId)
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            select new Par(
                trabajador.Id, trabajador.Nombre + " " + trabajador.Apellidos, centro.Id, centro.Nombre, cliente.Id, cliente.RazonSocial))
            .Distinct()
            .ToListAsync(cancellationToken);

        if (pares.Count == 0)
            return [];

        var filas = new List<(Guid ClienteId, string ClienteNombre, DocumentoPendienteDto Pendiente)>();

        // «Sin confirmar» sin fecha: el Documento existe.
        var trabajadorIdsConPar = pares.Select(p => p.TrabajadorId).Distinct().ToList();
        var sinConfirmar = await (
            from documento in documentosContext.Documentos.SinConfirmarSinFecha()
            where documento.TrabajadorId != null && trabajadorIdsConPar.Contains(documento.TrabajadorId!.Value)
            join tipo in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
            where tipoDocumentoIds == null || tipoDocumentoIds.Contains(tipo.Id)
            select new { DocumentoId = documento.Id, TrabajadorId = documento.TrabajadorId!.Value, TipoId = tipo.Id, TipoNombre = tipo.Nombre })
            .ToListAsync(cancellationToken);

        foreach (var documento in sinConfirmar)
        {
            foreach (var par in pares.Where(p => p.TrabajadorId == documento.TrabajadorId).DistinctBy(p => (p.TrabajadorId, p.ClienteId)))
            {
                filas.Add((par.ClienteId, par.ClienteNombre, new DocumentoPendienteDto(
                    documento.DocumentoId, par.TrabajadorId, par.TrabajadorNombre, documento.TipoId, documento.TipoNombre,
                    MotivoPendienteDeReclamacion.SinConfirmar)));
            }
        }

        // Ausentes: el requisito del Centro sin ningún Documento operativo del Tipo.
        var faltantes = await documentosFaltantes.CalcularAsync(
            [.. pares.Select(p => new ParejaTrabajadorCentro(p.TrabajadorId, p.TrabajadorNombre, p.CentroId, p.CentroNombre))],
            cancellationToken);

        var clientePorCentro = pares.GroupBy(p => p.CentroId).ToDictionary(g => g.Key, g => g.First());
        foreach (var faltante in faltantes)
        {
            if (tipoDocumentoIds is not null && !tipoDocumentoIds.Contains(faltante.TipoDocumentoId))
                continue;

            var par = clientePorCentro[faltante.CentroId];
            filas.Add((par.ClienteId, par.ClienteNombre, new DocumentoPendienteDto(
                null, faltante.TrabajadorId, faltante.TrabajadorNombre, faltante.TipoDocumentoId, faltante.TipoDocumentoNombre,
                MotivoPendienteDeReclamacion.Ausente)));
        }

        return [.. filas
            .GroupBy(f => (f.ClienteId, f.ClienteNombre))
            .Select(g => new PendientesDeUnTitular(
                g.Key.ClienteId, g.Key.ClienteNombre,
                [.. g.Select(f => f.Pendiente).DistinctBy(p => p.Clave).OrderBy(Orden)]))
            .OrderBy(t => t.TitularNombre)];
    }

    public async Task<IReadOnlyList<PendientesDeUnTitular>> ListarParaEmpresasAsync(
        Guid? empresaId, IReadOnlyList<Guid>? tipoDocumentoIds, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN de Empresas, no de lectura: mismo criterio que ObtenerLoteReclamacionEmpresaQuery.
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

        var filas = new List<(Guid EmpresaId, DocumentoPendienteDto Pendiente)>();

        var sinConfirmar = await (
            from documento in documentosContext.Documentos.SinConfirmarSinFecha()
            where documento.EmpresaId != null
            where empresaId == null || documento.EmpresaId == empresaId
            where empresaIdsVisibles == null || empresaIdsVisibles.Contains(documento.EmpresaId!.Value)
            join tipo in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipo.Id
            where tipo.AmbitoAplicacion == AmbitoAplicacion.Empresa
            where tipoDocumentoIds == null || tipoDocumentoIds.Contains(tipo.Id)
            select new { DocumentoId = documento.Id, EmpresaId = documento.EmpresaId!.Value, TipoId = tipo.Id, TipoNombre = tipo.Nombre })
            .ToListAsync(cancellationToken);

        filas.AddRange(sinConfirmar.Select(d => (d.EmpresaId, new DocumentoPendienteDto(
            d.DocumentoId, null, null, d.TipoId, d.TipoNombre, MotivoPendienteDeReclamacion.SinConfirmar))));

        // Ausentes: lo que algún Centro visible exige a la Empresa de sus Trabajadores asignados y no existe.
        var evaluacion = await evaluacionDeAcceso.EvaluarAsync(null, cancellationToken);
        var ausentes = evaluacion.Requisitos
            .Where(r => r.Ambito == AmbitoAplicacion.Empresa
                && r.Resultado.Situacion == SituacionDeRequisitoBloqueante.Ausente
                && r.EmpresaId is not null)
            .Select(r => (EmpresaId: r.EmpresaId!.Value, r.TipoDocumentoId))
            .Where(a => (empresaId is null || a.EmpresaId == empresaId)
                && (empresaIdsVisibles is null || empresaIdsVisibles.Contains(a.EmpresaId))
                && (tipoDocumentoIds is null || tipoDocumentoIds.Contains(a.TipoDocumentoId)))
            .Distinct()
            .ToList();

        if (ausentes.Count > 0)
        {
            var tipoIds = ausentes.Select(a => a.TipoDocumentoId).Distinct().ToList();
            var nombresTipo = await tiposDocumentoContext.TiposDocumento
                .Where(t => tipoIds.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

            filas.AddRange(ausentes
                .Where(a => nombresTipo.ContainsKey(a.TipoDocumentoId))
                .Select(a => (a.EmpresaId, new DocumentoPendienteDto(
                    null, null, null, a.TipoDocumentoId, nombresTipo[a.TipoDocumentoId], MotivoPendienteDeReclamacion.Ausente))));
        }

        if (filas.Count == 0)
            return [];

        var empresaIds = filas.Select(f => f.EmpresaId).Distinct().ToList();
        var nombresEmpresa = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        return [.. filas
            .Where(f => nombresEmpresa.ContainsKey(f.EmpresaId))
            .GroupBy(f => f.EmpresaId)
            .Select(g => new PendientesDeUnTitular(
                g.Key, nombresEmpresa[g.Key],
                [.. g.Select(f => f.Pendiente).DistinctBy(p => p.Clave).OrderBy(Orden)]))
            .OrderBy(t => t.TitularNombre)];
    }

    public async Task<Result<IReadOnlyList<DocumentoPendienteDto>>> ResolverParaClienteAsync(
        Guid clienteId, IReadOnlyList<PendienteSinFecha> pedidos, CancellationToken cancellationToken)
    {
        if (!PedidosBienFormados(pedidos, conTrabajador: true))
            return Result.Fallo<IReadOnlyList<DocumentoPendienteDto>>(ErrorDesactualizados());

        // Acota el cálculo a los Trabajadores pedidos cuando todos los pedidos lo dicen (los ausentes); un «Sin confirmar»
        // se identifica por su Documento y no trae Trabajador, así que con alguno el cálculo no se acota.
        IReadOnlyCollection<Guid>? trabajadorIds = pedidos.All(p => p.TrabajadorId is not null)
            ? [.. pedidos.Select(p => p.TrabajadorId!.Value).Distinct()]
            : null;

        var lotes = await ListarParaClientesAsync(
            centroId: null, clienteId, trabajadorIds, tipoDocumentoIds: null, cancellationToken);

        return Emparejar(pedidos, lotes.SelectMany(l => l.Pendientes));
    }

    public async Task<Result<IReadOnlyList<DocumentoPendienteDto>>> ResolverParaEmpresaAsync(
        Guid empresaId, IReadOnlyList<PendienteSinFecha> pedidos, CancellationToken cancellationToken)
    {
        if (!PedidosBienFormados(pedidos, conTrabajador: false))
            return Result.Fallo<IReadOnlyList<DocumentoPendienteDto>>(ErrorDesactualizados());

        var lotes = await ListarParaEmpresasAsync(empresaId, tipoDocumentoIds: null, cancellationToken);
        return Emparejar(pedidos, lotes.SelectMany(l => l.Pendientes));
    }

    public async Task<IReadOnlySet<Guid>> DocumentoIdsAunSinConfirmarSinFechaAsync(
        IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken)
    {
        if (documentoIds.Count == 0)
            return new HashSet<Guid>();

        return (await documentosContext.Documentos.SinConfirmarSinFecha()
            .Where(d => documentoIds.Contains(d.Id))
            .Select(d => d.Id)
            .ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>
    /// Cada pedido tiene exactamente una forma: «Sin confirmar» (solo <c>DocumentoId</c>) o «Ausente» (sin <c>DocumentoId</c>,
    /// con Tipo y, para un Cliente, con Trabajador; para una Empresa, sin él). Una forma mixta no se interpreta: falla.
    /// </summary>
    private static bool PedidosBienFormados(IReadOnlyList<PendienteSinFecha> pedidos, bool conTrabajador) =>
        pedidos.All(p => p.DocumentoId is { } documentoId
            ? documentoId != Guid.Empty && p.TipoDocumentoId is null && p.TrabajadorId is null
            : p.TipoDocumentoId is { } tipoId && tipoId != Guid.Empty && (conTrabajador ? p.TrabajadorId is { } t && t != Guid.Empty : p.TrabajadorId is null));

    private static Result<IReadOnlyList<DocumentoPendienteDto>> Emparejar(
        IReadOnlyList<PendienteSinFecha> pedidos, IEnumerable<DocumentoPendienteDto> disponibles)
    {
        var porClave = disponibles.DistinctBy(d => d.Clave).ToDictionary(d => d.Clave);
        var resueltos = new List<DocumentoPendienteDto>();

        foreach (var pedido in pedidos.DistinctBy(p => (p.DocumentoId, p.TrabajadorId, p.TipoDocumentoId)))
        {
            var clave = pedido.DocumentoId is { } id ? $"doc:{id}" : $"falta:{pedido.TrabajadorId}:{pedido.TipoDocumentoId}";
            if (!porClave.TryGetValue(clave, out var encontrado))
                return Result.Fallo<IReadOnlyList<DocumentoPendienteDto>>(ErrorDesactualizados());

            resueltos.Add(encontrado);
        }

        return Result.Exito<IReadOnlyList<DocumentoPendienteDto>>(resueltos);
    }

    private static Error ErrorDesactualizados() => Error.Crear(
        "Reclamacion.PendientesDesactualizados",
        "Alguno de los documentos que faltan o sin confirmar ya no está pendiente — puede que se haya subido el documento o anotado su vigencia. Actualiza la vista antes de volver a intentarlo.");

    /// <summary>Primero lo que falta, después lo sin confirmar; dentro de cada grupo, por Trabajador y documento.</summary>
    private static (int, string, string) Orden(DocumentoPendienteDto p) =>
        ((int)p.Motivo, p.TrabajadorNombre ?? string.Empty, p.TipoDocumentoNombre);
}
