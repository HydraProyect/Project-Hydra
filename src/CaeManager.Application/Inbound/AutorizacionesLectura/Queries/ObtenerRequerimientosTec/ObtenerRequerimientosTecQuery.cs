using System.Security.Cryptography;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.AutorizacionesLectura;
using CaeManager.Domain.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.AutorizacionesLectura.Queries.ObtenerRequerimientosTec;

public enum SujetoRequerimientoTec
{
    Empresa,
    Trabajador,
}

/// <summary>
/// Estado del requerimiento sin veredicto: los veredictos del Tenant TCE (Validado, Rechazado y su motivo) son P0b y todavía
/// no se leen. <see cref="PendienteDeValidar"/> solo dice que el Tenant TEC tiene un documento vigente propio del tipo.
/// </summary>
public enum EstadoRequerimientoTecSinVeredicto
{
    PendienteDeSubir,
    PendienteDeValidar,
}

/// <summary>
/// Un requerimiento: el trío (contrato de acceso, requisito, sujeto asignado), que se calcula al leer y no se guarda.
/// <see cref="Id"/> es un UUID v5 del trío (<see cref="IdRequerimientoTec"/>), estable entre lecturas. La subida (P2)
/// recibirá el trío explícito y lo revalidará en el servidor; el <see cref="Id"/> solo sirve a la pantalla.
/// Ningún campo identifica a un Gestor CAE (P-4): del Tenant TCE solo salen su nombre visible, el Centro, la razón social
/// del Cliente empresarial y el requisito.
/// </summary>
/// <param name="TipoDocumentoNombre">El nombre exacto del tipo en el catálogo del Tenant TCE (D1).</param>
/// <param name="EmpresaNombre">La Empresa del Tenant TEC a la que toca: la Empresa propia o la del Trabajador.</param>
/// <param name="SujetoNombre">El nombre del Trabajador asignado o la razón social de la Empresa propia.</param>
/// <param name="DocumentoVencimiento">El vencimiento del documento vigente propio, si vence en una fecha.</param>
/// <param name="DocumentosVigentes">Cuántos documentos vigentes propios del tipo tiene el sujeto.</param>
public sealed record RequerimientoTecDto(
    Guid Id,
    Guid ContratoAccesoId,
    Guid RequisitoId,
    SujetoRequerimientoTec Sujeto,
    Guid SujetoId,
    string TenantTceNombre,
    string ClienteEmpresarialNombre,
    string CentroNombre,
    string TipoDocumentoNombre,
    string? Criterio,
    string CapaPlantilla,
    bool BloqueaAcceso,
    string EmpresaNombre,
    string SujetoNombre,
    EstadoRequerimientoTecSinVeredicto Estado,
    EstadoDocumento? EstadoDocumento,
    DateOnly? DocumentoVencimiento,
    int DocumentosVigentes,
    bool EsAptitudMedica);

/// <summary>
/// Lo que cada Tenant TCE pide al Tenant TEC activo por contrato de acceso: requisito × sujeto asignado × documento vigente
/// propio (demo Inbound, P1).
///
/// <para>
/// <b>Qué requisitos.</b> Los de la lectura inversa X-2 (<see cref="ILectorRequerimientosTec"/>), que ya aplica D9 y D10.
/// <b>A quién.</b> Solo a sujetos asignados (D8): los Trabajadores asignados a una autorización de lectura vigente hoy del
/// mismo contrato de acceso, no eliminados ni anonimizados, y la Empresa propia si esa autorización la incluye y el Tenant
/// TEC tiene exactamente una Empresa propia (el mismo criterio que <c>app_inbound_documentos_del_alcance</c>, EP-6). Un
/// contrato sin autorización vigente no da filas. Los requisitos de vehículo o máquina no salen: no hay asignación de
/// vehículos. A cada Trabajador asignado solo se le aplica el requisito si su máscara <c>TiposContratacion</c> incluye el
/// <c>TipoContratacion</c> del Trabajador (#2338: la máscara es el único criterio para el Trabajador, también para el
/// Trabajador extranjero). <c>SePideA</c> no se lee en el Trabajador; los requisitos de Empresa no llevan máscara.
/// <b>Con qué documento.</b> El documento vigente propio de un tipo del catálogo del Tenant TEC con el mismo nombre exacto
/// (D1), no eliminado, no anonimizado, no sustituido, no vencido y que la lectura del Tenant TCE podría ver (fuera de la
/// categoría especial de salud salvo el tipo de solo conclusión de aptitud). Si el requisito pide un documento específico
/// del Tenant TCE o del Centro, un documento propio general no lo cubre: todavía no hay cómo marcar uno específico.
/// </para>
///
/// <para>
/// Solo para quien tiene la capacidad ParticipanteTec (Administrador del Tenant TEC, en su propio Tenant o con papel
/// Outbound), cuyo alcance de datos es el Tenant entero; para el resto, lista vacía. Sin veredictos: P0b los añadirá.
/// </para>
/// </summary>
[AreaDelEncargo("AutorizacionesLectura")]
public record ObtenerRequerimientosTecQuery : IRequest<IReadOnlyList<RequerimientoTecDto>>;

public class ObtenerRequerimientosTecQueryHandler(
    ILectorRequerimientosTec lector,
    IAutorizacionesLecturaQueryContext autorizacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    ICurrentUserService currentUserService,
    IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerRequerimientosTecQuery, IReadOnlyList<RequerimientoTecDto>>
{
    private const string EntidadEmpresa = "Empresa";
    private const string EntidadTrabajador = "Trabajador";
    private const string AmbitoGeneralTec = "GeneralTec";

    public async Task<IReadOnlyList<RequerimientoTecDto>> Handle(ObtenerRequerimientosTecQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ParticipanteTec, cancellationToken))
            return [];

        if (!await AutorizacionLecturaAutoridad.EsAdministradorAsync(currentUserService))
            return [];

        var requisitos = await lector.RequisitosAsync(cancellationToken);
        if (requisitos.Count == 0)
            return [];

        var hoy = DiaDeNegocio.Hoy();
        var contratos = requisitos.Select(r => r.ContratoAccesoId).Distinct().ToList();
        var autorizaciones = await autorizacionesContext.AutorizacionesLecturaDocumental
            .Where(a => contratos.Contains(a.ContratoAccesoId))
            .Where(AutorizacionLecturaDocumental.EstaVigenteEn(hoy))
            .Select(a => new { a.Id, a.ContratoAccesoId, a.IncluyeDocumentosEmpresa })
            .ToListAsync(cancellationToken);
        if (autorizaciones.Count == 0)
            return [];

        var autorizacionIds = autorizaciones.Select(a => a.Id).ToList();
        var asignados = await autorizacionesContext.AutorizacionesLecturaTrabajadores
            .Where(t => autorizacionIds.Contains(t.AutorizacionId))
            .Select(t => new { t.AutorizacionId, t.TrabajadorId })
            .ToListAsync(cancellationToken);

        // Sin recorte por cartera, como el resto de AutorizacionesLectura: solo lee el Administrador del Tenant TEC
        // (AutorizacionLecturaAutoridad), cuyo alcance de datos es el Tenant entero. Si la autoridad se abre a otro rol,
        // esta Query tiene que recortar por IAlcanceDatosService (lo recuerdan las redes de cartera de lectura y escritura).
        var trabajadorIds = asignados.Select(a => a.TrabajadorId).Distinct().ToList();
        var trabajadores = await trabajadoresContext.Trabajadores
            .Where(t => trabajadorIds.Contains(t.Id) && !t.EstaEliminado && t.AnonimizadoEnUtc == null)
            .Select(t => new { t.Id, t.Nombre, t.Apellidos, t.EmpresaId, t.TipoContratacion })
            .ToListAsync(cancellationToken);
        var trabajadoresPorId = trabajadores.ToDictionary(t => t.Id);

        // EP-6: la Empresa propia solo es sujeto si es la única Empresa propia no eliminada del Tenant TEC.
        var propias = await empresasContext.Empresas
            .Where(e => e.EsPropia && !e.EstaEliminado)
            .Select(e => new { e.Id, e.RazonSocial })
            .Take(2)
            .ToListAsync(cancellationToken);
        var empresaPropia = propias.Count == 1 ? propias[0] : null;

        var empresaIds = trabajadoresPorId.Values.Where(t => t.EmpresaId is not null).Select(t => t.EmpresaId!.Value).Distinct().ToList();
        var nombresEmpresa = await empresasContext.Empresas
            .Where(e => empresaIds.Contains(e.Id))
            .Select(e => new { e.Id, e.RazonSocial })
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        var trabajadoresPorContrato = autorizaciones
            .GroupBy(a => a.ContratoAccesoId)
            .ToDictionary(
                g => g.Key,
                g => asignados.Where(t => g.Any(a => a.Id == t.AutorizacionId) && trabajadoresPorId.ContainsKey(t.TrabajadorId))
                    .Select(t => t.TrabajadorId).Distinct().ToList());
        var contratosConEmpresa = autorizaciones.Where(a => a.IncluyeDocumentosEmpresa).Select(a => a.ContratoAccesoId).ToHashSet();

        // D1: el tipo propio se empareja por el nombre exacto del catálogo del Tenant TCE.
        var nombresTipo = requisitos.Select(r => r.TipoDocumentoNombre).Distinct().ToList();
        // La base compara con su intercalación; el emparejamiento exacto (ordinal) lo repite Evaluar en memoria.
        var tiposPropios = await tiposDocumentoContext.TiposDocumento
            .Where(t => nombresTipo.Contains(t.Nombre))
            .Select(t => new { t.Id, t.Nombre, t.Sensibilidad, t.SoloConclusionAptitud })
            .ToListAsync(cancellationToken);
        var tipoIds = tiposPropios.Select(t => t.Id).ToList();
        var tiposLegibles = tiposPropios
            .Where(t => t.Sensibilidad != SensibilidadDocumental.CategoriaEspecialSalud || t.SoloConclusionAptitud)
            .Select(t => t.Id)
            .ToHashSet();

        var sujetoTrabajadorIds = trabajadoresPorId.Keys.ToList();
        var empresaPropiaId = empresaPropia?.Id;
        var documentos = tipoIds.Count == 0
            ? []
            : await documentosContext.Documentos.Operativos()
                .Where(d => tipoIds.Contains(d.TipoDocumentoId)
                            && d.AnonimizadoEnUtc == null
                            && ((d.TrabajadorId != null && sujetoTrabajadorIds.Contains(d.TrabajadorId.Value))
                                || (empresaPropiaId != null && d.EmpresaId == empresaPropiaId)))
                .Select(d => new
                {
                    d.Id,
                    d.TipoDocumentoId,
                    d.TrabajadorId,
                    d.EmpresaId,
                    d.EstadoVigencia,
                    d.FechaVencimiento,
                    d.FechaEmision,
                    d.CreadoEnUtc,
                    d.EsRenuncia,
                })
                .ToListAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        Documental? Evaluar(RequisitoTecDto requisito, Guid? trabajadorId, Guid? empresaId)
        {
            if (!string.Equals(requisito.AmbitoDocumento, AmbitoGeneralTec, StringComparison.Ordinal))
                return null;

            var delTipo = tiposPropios.Where(t => string.Equals(t.Nombre, requisito.TipoDocumentoNombre, StringComparison.Ordinal)
                                                  && tiposLegibles.Contains(t.Id))
                .Select(t => t.Id).ToHashSet();
            // Vale hoy: no vencido y no en renuncia (la definición única de DocumentoEfectivo.ValidoHoy).
            var vigentes = documentos
                .Where(d => delTipo.Contains(d.TipoDocumentoId)
                            && (trabajadorId is not null ? d.TrabajadorId == trabajadorId : d.EmpresaId == empresaId)
                            && DocumentoEfectivo.ValidoHoy(d.EstadoVigencia, d.FechaVencimiento, hoy, d.EsRenuncia))
                .ToList();
            if (vigentes.Count == 0)
                return null;

            // El documento efectivo representa al par (tipo, sujeto); el orden es el único de DocumentoEfectivo.
            var efectivo = DocumentoEfectivo.Ordenar(
                vigentes, d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id,
                d => d.EsRenuncia, hoy).First();
            return new Documental(
                CalculadoraEstadoDocumento.Calcular(efectivo.EstadoVigencia, efectivo.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias),
                efectivo.EstadoVigencia == EstadoVigenciaDocumento.VenceEnFecha ? efectivo.FechaVencimiento : null,
                vigentes.Count);
        }

        bool EsAptitud(string nombre) =>
            tiposPropios.Any(t => string.Equals(t.Nombre, nombre, StringComparison.Ordinal) && t.SoloConclusionAptitud);

        var resultado = new List<RequerimientoTecDto>();
        foreach (var requisito in requisitos)
        {
            if (string.Equals(requisito.Entidad, EntidadEmpresa, StringComparison.Ordinal))
            {
                if (empresaPropia is null || !contratosConEmpresa.Contains(requisito.ContratoAccesoId))
                    continue;

                resultado.Add(Fila(requisito, SujetoRequerimientoTec.Empresa, empresaPropia.Id, empresaPropia.RazonSocial,
                    empresaPropia.RazonSocial, Evaluar(requisito, null, empresaPropia.Id), EsAptitud(requisito.TipoDocumentoNombre)));
            }
            else if (string.Equals(requisito.Entidad, EntidadTrabajador, StringComparison.Ordinal))
            {
                var contrataciones = (TiposContratacion)requisito.TiposContratacion;
                foreach (var trabajadorId in trabajadoresPorContrato.GetValueOrDefault(requisito.ContratoAccesoId) ?? [])
                {
                    var trabajador = trabajadoresPorId[trabajadorId];
                    // La máscara de contratación es el único criterio para pedir algo a un Trabajador (#2338): un requisito
                    // solo de Extranjero no se pide a un Trabajador por cuenta ajena. «Se pide a» no se lee en el Trabajador.
                    if (!contrataciones.Incluye(trabajador.TipoContratacion))
                        continue;

                    var empresa = trabajador.EmpresaId is { } id ? nombresEmpresa.GetValueOrDefault(id) ?? string.Empty : string.Empty;
                    resultado.Add(Fila(requisito, SujetoRequerimientoTec.Trabajador, trabajadorId,
                        $"{trabajador.Nombre} {trabajador.Apellidos}".Trim(), empresa,
                        Evaluar(requisito, trabajadorId, null), EsAptitud(requisito.TipoDocumentoNombre)));
                }
            }
            // VehiculoOMaquina u otro valor: no hay sujeto asignado (D8), no sale.
        }

        return resultado;
    }

    private static RequerimientoTecDto Fila(
        RequisitoTecDto requisito, SujetoRequerimientoTec sujeto, Guid sujetoId, string sujetoNombre, string empresaNombre,
        Documental? documento, bool esAptitud) =>
        new(
            IdRequerimientoTec.De(requisito.ContratoAccesoId, requisito.RequisitoId, sujeto, sujetoId),
            requisito.ContratoAccesoId, requisito.RequisitoId, sujeto, sujetoId,
            requisito.TenantTceNombre, requisito.ClienteEmpresarialNombre, requisito.CentroNombre, requisito.TipoDocumentoNombre,
            requisito.Criterio, requisito.CapaPlantilla, requisito.BloqueaAcceso, empresaNombre, sujetoNombre,
            documento is null ? EstadoRequerimientoTecSinVeredicto.PendienteDeSubir : EstadoRequerimientoTecSinVeredicto.PendienteDeValidar,
            documento?.Estado, documento?.Vencimiento, documento?.Cuantos ?? 0, esAptitud);

    private sealed record Documental(EstadoDocumento Estado, DateOnly? Vencimiento, int Cuantos);
}

/// <summary>
/// El identificador de un requerimiento: UUID v5 (RFC 9562, SHA-1 con espacio de nombres) del trío contrato de acceso,
/// requisito y sujeto. Determinista para que la pantalla pueda volver a encontrar la fila (la ruta de subida lo lleva),
/// sin guardar nada. No es secreto ni autoridad: quien suba un documento recibirá el trío y lo revalidará en el servidor.
/// </summary>
public static class IdRequerimientoTec
{
    private static readonly Guid EspacioDeNombres = Guid.Parse("3f6b1d0e-8c2a-4e57-9b41-5a7c2e9d8f13");

    public static Guid De(Guid contratoAccesoId, Guid requisitoId, SujetoRequerimientoTec sujeto, Guid sujetoId) =>
        V5(EspacioDeNombres, $"{contratoAccesoId:D}/{requisitoId:D}/{sujeto}/{sujetoId:D}");

    /// <summary>UUID v5 de <paramref name="nombre"/> (UTF-8) en <paramref name="espacio"/>, con los bytes en orden de red.</summary>
    public static Guid V5(Guid espacio, string nombre)
    {
        var bytesNombre = Encoding.UTF8.GetBytes(nombre);
        var datos = new byte[16 + bytesNombre.Length];
        espacio.TryWriteBytes(datos, bigEndian: true, out _);
        bytesNombre.CopyTo(datos, 16);

        // SHA-1 lo fija la RFC para la versión 5: aquí no protege nada, solo deriva un identificador estable.
#pragma warning disable CA5350
        var hash = SHA1.HashData(datos);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
