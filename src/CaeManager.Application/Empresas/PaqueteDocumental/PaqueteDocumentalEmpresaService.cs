using System.Text;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Documentos.PaqueteDocumental;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Empresas.PaqueteDocumental;

/// <summary>Un Cliente empresarial para el que se puede preparar el paquete: el «Para» del diálogo.</summary>
public sealed record DestinoPaqueteEmpresaDto(Guid ClienteEmpresarialId, string RazonSocial);

/// <summary>Un documento que ese Cliente empresarial exige y no puede viajar: falta, o solo hay copias vencidas.</summary>
/// <param name="TipoDocumento">Nombre del Tipo de documento.</param>
/// <param name="Trabajador">Nombre y apellidos del Trabajador, o <c>null</c> si es un documento de la Empresa.</param>
/// <param name="VencidoEl">Vencimiento de la última copia vencida; <c>null</c> si no hay ninguna (falta).</param>
public sealed record FaltaPaqueteEmpresaDto(string TipoDocumento, string? Trabajador, DateOnly? VencidoEl);

/// <summary>
/// Lo que el diálogo «Preparar paquete documental» dice antes de generar (maqueta «Mapa Dia a Dia TALVEG.dc.html» § C):
/// cuántos documentos van, cuántos faltan o están vencidos y cuántos no los pide este Cliente empresarial.
/// <paramref name="PuedeDescargar"/> es <c>false</c> en una Sesión Privilegiada, que ve el resumen pero no descarga.
/// </summary>
public sealed record ResumenPaqueteEmpresaDto(
    string RazonSocial,
    IReadOnlyList<DestinoPaqueteEmpresaDto> Destinos,
    Guid? ClienteEmpresarialId,
    int Incluye,
    int Faltan,
    int Fuera,
    IReadOnlyList<FaltaPaqueteEmpresaDto> ListaFaltan,
    bool PuedeDescargar = true);

/// <param name="NoIncluidos">Los documentos que debían ir y cuyo archivo no se pudo abrir («Tipo · titular»); van
/// también en <see cref="PaqueteDocumentalEmpresaService.NombreLeeme"/> dentro del zip.</param>
public sealed record PaqueteEmpresaZip(string NombreArchivo, byte[] Contenido, IReadOnlyList<Guid> DocumentoIds, IReadOnlyList<string> NoIncluidos);

/// <summary>
/// «Preparar paquete» de Empresa 360 (decisión de Chris del 2026-11-08, punto 5 de «Primera tanda de decisiones
/// pendientes»; maqueta «Mapa Dia a Dia TALVEG.dc.html» § C, enlazada desde el botón de «Empresa 360 TALVEG.dc.html»).
///
/// <para>
/// <b>Qué entra.</b> La misma regla que el paquete de una Visita (<see cref="ArmadoPaqueteDocumental.Seleccionar"/>):
/// solo lo vigente, uno por titular y tipo, el de emisión más reciente, nunca un vencido. Los titulares son la Empresa
/// (sus documentos de ámbito Empresa) y sus propios Trabajadores visibles para quien prepara el paquete
/// (<see cref="IAlcanceDatosService.ObtenerTrabajadorIdsVisiblesAsync"/>), los mismos que lista la pestaña Trabajadores.
/// </para>
///
/// <para>
/// <b>Para quién.</b> Con un Cliente empresarial de destino (uno de sus Clientes en una Relación Empresarial vigente,
/// <see cref="ClientesVigentesDeEmpresas"/>), «el paquete trae solo lo que ese Cliente exige y está vigente»: los pares
/// Trabajador × Tipo y Empresa × Tipo que exigen los Centros de ese Cliente donde la Empresa tiene Trabajadores con
/// Asignación activa (<see cref="ICalculoEstadoCentroService"/>, el mismo universo que el cumplimiento). Lo exigido sin
/// copia vigente son las faltas; lo vigente que ese Cliente no pide, «no los pide este Cliente». Sin destino
/// («Todo lo vigente») entra todo lo vigente.
/// </para>
///
/// <para>
/// No autoriza: el alcance de gestión sobre la Empresa, el rol que descarga y el Tenant propietario los comprueban las
/// consultas que lo usan (<c>ObtenerResumenPaqueteEmpresaQuery</c>, <c>DescargarPaqueteEmpresaQuery</c>) antes de llamarlo.
/// Tampoco registra los accesos a lo sensible: lo hace la descarga, con los documentos que de verdad entraron.
/// </para>
/// </summary>
public interface IPaqueteDocumentalEmpresaService
{
    /// <summary>Clientes empresariales de la Empresa con Relación Empresarial vigente, por razón social.</summary>
    Task<IReadOnlyList<DestinoPaqueteEmpresaDto>> ObtenerDestinosAsync(Guid empresaId, CancellationToken cancellationToken);

    Task<ResumenPaqueteEmpresaDto> ResumirAsync(
        Guid empresaId, Guid? clienteEmpresarialId, IReadOnlyList<DestinoPaqueteEmpresaDto> destinos, CancellationToken cancellationToken);

    /// <summary>
    /// Devuelve <c>null</c> si no hay nada vigente que entregar: no se genera un zip vacío. Lanza
    /// <see cref="PaqueteEmpresaDemasiadoGrandeException"/> por encima de <see cref="MaximoArchivos"/> y
    /// <see cref="PaqueteEmpresaSinArchivosException"/> si no se pudo abrir ningún archivo. Si solo algunos no se abren, el
    /// zip sale con los demás y lleva en la raíz un índice de los que no van: nunca se omiten en silencio.
    /// </summary>
    Task<PaqueteEmpresaZip?> ConstruirAsync(Guid empresaId, Guid? clienteEmpresarialId, string? razonSocialCliente, CancellationToken cancellationToken);

    /// <summary>
    /// Tope síncrono. La maqueta genera en segundo plano por encima de 200 archivos y avisa en la campana; hasta que exista
    /// esa generación, por encima del tope no se arma el zip en memoria (OWASP API4:2023, consumo de recursos).
    /// </summary>
    const int MaximoArchivos = 200;
}

public sealed class PaqueteEmpresaDemasiadoGrandeException(int archivos)
    : Exception($"El paquete tiene {archivos} archivos; el máximo que se prepara al momento es {IPaqueteDocumentalEmpresaService.MaximoArchivos}.")
{
    public int Archivos { get; } = archivos;
}

/// <summary>Había documentos que entregar, pero no se pudo abrir ninguno de sus archivos (almacenamiento inconsistente).</summary>
public sealed class PaqueteEmpresaSinArchivosException()
    : Exception("No se pudo abrir ningún archivo del paquete documental de la Empresa.");

public class PaqueteDocumentalEmpresaService(
    IEmpresasQueryContext empresasContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    ICalculoEstadoCentroService calculoEstadoCentro,
    IFileStorageService almacenamiento,
    IAlcanceDatosService alcanceDatos,
    ILogger<PaqueteDocumentalEmpresaService> logger) : IPaqueteDocumentalEmpresaService
{
    private sealed record TrabajadorDelPaquete(Guid Id, string Nombre, string Apellidos);

    /// <summary>Lo que se lee una vez y comparten el resumen y el zip.</summary>
    private sealed record Universo(
        string RazonSocial,
        IReadOnlyDictionary<Guid, TrabajadorDelPaquete> Trabajadores,
        IReadOnlyList<ArmadoPaqueteDocumental.Candidato> Candidatos,
        ArmadoPaqueteDocumental.Seleccion Seleccion,
        IReadOnlySet<(Guid? TrabajadorId, Guid TipoDocumentoId)>? Exigidos);

    public async Task<IReadOnlyList<DestinoPaqueteEmpresaDto>> ObtenerDestinosAsync(Guid empresaId, CancellationToken cancellationToken) =>
        await ClientesVigentesDeEmpresas.De(empresasContext, [empresaId])
            .OrderBy(c => c.RazonSocial)
            .Select(c => new DestinoPaqueteEmpresaDto(c.ClienteEmpresarialId, c.RazonSocial))
            .ToListAsync(cancellationToken);

    public async Task<ResumenPaqueteEmpresaDto> ResumirAsync(
        Guid empresaId, Guid? clienteEmpresarialId, IReadOnlyList<DestinoPaqueteEmpresaDto> destinos, CancellationToken cancellationToken)
    {
        var universo = await CargarAsync(empresaId, clienteEmpresarialId, cancellationToken);
        var gruposVigentes = universo.Seleccion.Enviar.Select(Clave).ToHashSet();

        if (universo.Exigidos is null)
            return new ResumenPaqueteEmpresaDto(universo.RazonSocial, destinos, null, gruposVigentes.Count, 0, 0, []);

        var faltan = universo.Exigidos.Where(par => !gruposVigentes.Contains(par)).ToList();
        var tipos = await NombresTipoAsync(faltan.Select(f => f.TipoDocumentoId), cancellationToken);
        var lista = faltan
            .Select(par => new FaltaPaqueteEmpresaDto(
                tipos.GetValueOrDefault(par.TipoDocumentoId, "Documento"),
                par.TrabajadorId is { } t && universo.Trabajadores.TryGetValue(t, out var trabajador) ? $"{trabajador.Nombre} {trabajador.Apellidos}" : null,
                universo.Candidatos
                    .Where(c => c.TrabajadorId == par.TrabajadorId && c.TipoDocumentoId == par.TipoDocumentoId && c.FechaVencimiento is not null)
                    .Select(c => c.FechaVencimiento)
                    .Max()))
            .OrderBy(f => f.Trabajador is null ? 0 : 1).ThenBy(f => f.Trabajador).ThenBy(f => f.TipoDocumento)
            .ToList();

        var incluye = gruposVigentes.Count(universo.Exigidos.Contains);
        return new ResumenPaqueteEmpresaDto(
            universo.RazonSocial, destinos, clienteEmpresarialId, incluye, faltan.Count, gruposVigentes.Count - incluye, lista);
    }

    public async Task<PaqueteEmpresaZip?> ConstruirAsync(
        Guid empresaId, Guid? clienteEmpresarialId, string? razonSocialCliente, CancellationToken cancellationToken)
    {
        var universo = await CargarAsync(empresaId, clienteEmpresarialId, cancellationToken);
        var grupos = universo.Exigidos is null
            ? universo.Seleccion.Enviar
            : universo.Seleccion.Enviar.Where(g => universo.Exigidos.Contains(Clave(g))).ToList();

        if (grupos.Count == 0) return null;
        if (grupos.Count > IPaqueteDocumentalEmpresaService.MaximoArchivos)
            throw new PaqueteEmpresaDemasiadoGrandeException(grupos.Count);

        var tipos = await NombresTipoAsync(grupos.SelectMany(g => g).Select(d => d.TipoDocumentoId), cancellationToken);
        var carpetas = CarpetasDeTrabajador(universo.Trabajadores.Values);

        // «ZIP con carpetas por Trabajador» (maqueta § C): Empresa/<Tipo> y Trabajadores/<Nombre Apellidos>/<Tipo>.
        // El paquete de la Visita omite con un aviso en el registro el documento cuyo archivo no se abre; este sale de la mano
        // del Gestor CAE hacia el Cliente, así que lo dice dentro del zip, en un índice en la raíz (fuera de las carpetas
        // Empresa/ y Trabajadores/, con las que no puede chocar). El paquete de la Visita no cambia.
        var noIncluidos = new List<string>();
        var zip = await ArmadoPaqueteDocumental.ConstruirZipAsync(grupos, documento =>
        {
            var tipoNombre = tipos.GetValueOrDefault(documento.TipoDocumentoId, "Documento");
            return documento.TrabajadorId is { } trabajadorId && carpetas.TryGetValue(trabajadorId, out var carpeta)
                ? ($"Trabajadores/{carpeta}", tipoNombre)
                : ("Empresa", tipoNombre);
        }, almacenamiento, logger, cancellationToken, MaximoBytes, omitidos =>
        {
            noIncluidos.AddRange(omitidos.Select(g => DescribirPar(g[0], tipos, universo.Trabajadores, carpetas)));
            return noIncluidos.Count == 0 ? null : (NombreLeeme, TextoLeeme(noIncluidos));
        });

        if (zip is null) throw new PaqueteEmpresaSinArchivosException();
        var bytes = zip.Value.Bytes;

        var destino = ArmadoPaqueteDocumental.SanearSegmento(razonSocialCliente ?? "Todo lo vigente").Replace(' ', '-');
        var empresa = ArmadoPaqueteDocumental.SanearSegmento(universo.RazonSocial).Replace(' ', '-');
        var nombre = $"{empresa}_{destino}_{DiaDeNegocio.Hoy():yyyy-MM-dd}.zip";

        return new PaqueteEmpresaZip(nombre, bytes, zip.Value.Documentos.Select(d => d.DocumentoId).ToList(), noIncluidos);
    }

    /// <summary>El índice que el zip lleva en la raíz cuando algún archivo no se pudo abrir.</summary>
    public const string NombreLeeme = "LEEME - documentos no incluidos.txt";

    /// <summary>
    /// Tope de bytes del zip en memoria, además del de archivos (OWASP API4:2023): 200 archivos de hasta 10 MiB no caben en
    /// el presupuesto de memoria de documentos. Pasado el tope, la descarga responde como demasiado grande.
    /// </summary>
    public const long MaximoBytes = 200L * 1024 * 1024;

    /// <summary>
    /// La carpeta de cada Trabajador, «Nombre Apellidos». Dos Trabajadores con el mismo nombre (sin distinguir mayúsculas)
    /// no comparten carpeta: el segundo, por orden de Id, lleva « (2)», para que el Cliente empresarial no mezcle los
    /// documentos de uno con los del otro.
    /// </summary>
    private static Dictionary<Guid, string> CarpetasDeTrabajador(IEnumerable<TrabajadorDelPaquete> trabajadores)
    {
        var carpetas = new Dictionary<Guid, string>();
        var grupos = trabajadores
            .Select(t =>
            {
                var nombre = ArmadoPaqueteDocumental.SanearSegmento($"{t.Nombre} {t.Apellidos}");
                return (t.Id, Nombre: nombre.Length == 0 ? "Trabajador" : nombre);
            })
            .GroupBy(t => t.Nombre, StringComparer.OrdinalIgnoreCase);
        foreach (var grupo in grupos)
        {
            var orden = 0;
            foreach (var (id, nombre) in grupo.OrderBy(t => t.Id))
                carpetas[id] = ++orden == 1 ? nombre : $"{nombre} ({orden})";
        }

        return carpetas;
    }

    private static string DescribirPar(
        ArmadoPaqueteDocumental.Copia copia, IReadOnlyDictionary<Guid, string> tipos,
        IReadOnlyDictionary<Guid, TrabajadorDelPaquete> trabajadores, IReadOnlyDictionary<Guid, string> carpetas)
    {
        var tipo = tipos.GetValueOrDefault(copia.TipoDocumentoId, "Documento");
        return copia.TrabajadorId is { } id && trabajadores.ContainsKey(id) && carpetas.TryGetValue(id, out var carpeta)
            ? $"{tipo} · {carpeta}"
            : $"{tipo} · Empresa";
    }

    private static string TextoLeeme(IReadOnlyList<string> noIncluidos)
    {
        var texto = new StringBuilder();
        texto.AppendLine("Estos documentos debían ir en el paquete y no se pudo abrir su archivo, así que no están incluidos:");
        texto.AppendLine();
        foreach (var linea in noIncluidos)
            texto.AppendLine($"- {linea}");
        return texto.ToString();
    }

    private static (Guid? TrabajadorId, Guid TipoDocumentoId) Clave(IReadOnlyList<ArmadoPaqueteDocumental.Copia> grupo) =>
        (grupo[0].TrabajadorId, grupo[0].TipoDocumentoId);

    private async Task<Universo> CargarAsync(Guid empresaId, Guid? clienteEmpresarialId, CancellationToken cancellationToken)
    {
        var razonSocial = await empresasContext.Empresas
            .Where(e => e.Id == empresaId)
            .Select(e => e.RazonSocial)
            .FirstAsync(cancellationToken);

        // Sus propios Trabajadores (los de la pestaña Trabajadores), recortados al alcance de quien prepara el paquete: el
        // mismo criterio con el que se abre cada Documento suelto (TrabajadorVisibleAsync).
        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var consultaTrabajadores = trabajadoresContext.Trabajadores.Where(t => t.EmpresaId == empresaId);
        if (visibles is not null)
            consultaTrabajadores = consultaTrabajadores.Where(t => visibles.Contains(t.Id));
        var trabajadores = await consultaTrabajadores
            .Select(t => new TrabajadorDelPaquete(t.Id, t.Nombre, t.Apellidos))
            .ToDictionaryAsync(t => t.Id, cancellationToken);
        var trabajadorIds = trabajadores.Keys.ToList();

        var candidatos = await documentosContext.Documentos.Operativos()
            .Where(d => d.ArchivoUrl != null
                && ((d.EmpresaId == empresaId && d.TrabajadorId == null && d.VehiculoId == null)
                    || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))))
            .Select(d => new ArmadoPaqueteDocumental.Candidato(
                d.Id, d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl!, d.FechaEmision, d.EstadoVigencia, d.FechaVencimiento, d.CreadoEnUtc, d.EsRenuncia))
            .ToListAsync(cancellationToken);

        var seleccion = ArmadoPaqueteDocumental.Seleccionar(candidatos, DiaDeNegocio.Hoy());

        var exigidos = clienteEmpresarialId is { } destinoId
            ? await ExigidosPorClienteAsync(empresaId, destinoId, trabajadorIds, cancellationToken)
            : null;

        return new Universo(razonSocial, trabajadores, candidatos, seleccion, exigidos);
    }

    /// <summary>
    /// Lo que exige el Cliente empresarial: los pares de los Centros de ese Cliente donde un Trabajador de la Empresa tiene
    /// Asignación activa, solo los de sus Trabajadores visibles y los de la propia Empresa.
    /// </summary>
    private async Task<IReadOnlySet<(Guid? TrabajadorId, Guid TipoDocumentoId)>> ExigidosPorClienteAsync(
        Guid empresaId, Guid clienteEmpresarialId, IReadOnlyList<Guid> trabajadorIds, CancellationToken cancellationToken)
    {
        var exigidos = new HashSet<(Guid? TrabajadorId, Guid TipoDocumentoId)>();
        if (trabajadorIds.Count == 0) return exigidos;

        var centrosConActividad = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId))
            .Select(a => a.CentroId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var centroIds = await centrosContext.Centros
            .Where(c => centrosConActividad.Contains(c.Id) && c.ClienteId == clienteEmpresarialId)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        if (centroIds.Count == 0) return exigidos;

        var ids = trabajadorIds.ToHashSet();
        foreach (var par in await calculoEstadoCentro.ObtenerParesExigidosAsync(centroIds, cancellationToken))
            if (ids.Contains(par.TrabajadorId))
                exigidos.Add((par.TrabajadorId, par.TipoDocumentoId));

        foreach (var par in await calculoEstadoCentro.ObtenerParesDeEmpresaExigidosAsync(centroIds, cancellationToken))
            if (par.EmpresaId == empresaId)
                exigidos.Add((null, par.TipoDocumentoId));

        return exigidos;
    }

    private async Task<IReadOnlyDictionary<Guid, string>> NombresTipoAsync(IEnumerable<Guid> tipoIds, CancellationToken cancellationToken)
    {
        var ids = tipoIds.Distinct().ToList();
        return ids.Count == 0
            ? new Dictionary<Guid, string>()
            : await tiposDocumentoContext.TiposDocumento
                .Where(t => ids.Contains(t.Id))
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);
    }
}
