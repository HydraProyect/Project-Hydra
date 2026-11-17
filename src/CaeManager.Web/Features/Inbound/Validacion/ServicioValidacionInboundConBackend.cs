using System.Security.Cryptography;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerColaValidacionInbound;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Inbound.EvidenciasInbound;
using FluentValidation;
using MediatR;
using CaeManager.Web.Features.Inbound.Validacion.Recursos;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Inbound.Validacion;

/// <summary>
/// El puerto de validación sobre el backend de la demo mínima (DECISION_LOG 2026-11-17, opción b): la cola sale de
/// <see cref="ObtenerColaValidacionInboundQuery"/>, y Validar y Rechazar, uno a uno, de
/// <see cref="RegistrarEvidenciaInboundCommand"/> (Rechazar, con su rechazo inicial en el mismo guardado). La autoridad la
/// decide Application en cada llamada; este servicio solo traduce. La cola, el documento, Validar y Rechazar
/// solo funcionan con <see cref="OpcionesValidacionInbound.Habilitado"/>, que enciende solo la clave
/// <c>Inbound:Validacion:Habilitado=true</c> en cualquier entorno (DECISION_LOG 2026-11-17, 2757bac y cc7e68e); apagada, todo
/// responde «no disponible» y no se lee nada. Lo demás sigue «no disponible»: Marcar incompleto y
/// Deshacer (B-22), el lote (B-27), la lectura con IA y su propuesta (B-24, B-26), las páginas y la descarga del original.
///
/// <para>
/// Una fila de la cola es un par (contrato de acceso, Documento original): el mismo original puede estar pendiente en dos
/// contratos. La pantalla trabaja con un solo identificador, así que la fila lleva uno derivado del par
/// (<see cref="IdDeFila"/>) y este servicio, que vive en el circuito del usuario, recuerda a qué par corresponde. Ese
/// recuerdo no da autoridad: el Command vuelve a decidirla con el contrato y el Documento.
/// </para>
/// </summary>
public sealed class ServicioValidacionInboundConBackend(
    ISender mediator, IOptions<OpcionesValidacionInbound> opciones, IStringLocalizer<TextosValidacionInbound> textos)
    : IServicioValidacionInbound
{
    private const string RolEmpresaContratista = "Empresa contratista";

    /// <summary>La del motivo literal de la evidencia: más largo, el Command lo rechazaría.</summary>
    public const int LongitudMaximaExplicacion = EvidenciaInbound.LongitudMaximaMotivoRechazo;

    // Mismo orden y mismos textos que la lista de motivos de la pantalla de validación.
    private static readonly IReadOnlyDictionary<string, CausaRechazoEvidenciaInbound> Causas =
        new Dictionary<string, CausaRechazoEvidenciaInbound>
        {
            ["Documento ilegible"] = CausaRechazoEvidenciaInbound.Ilegible,
            ["Documento equivocado"] = CausaRechazoEvidenciaInbound.DocumentoEquivocado,
            ["Datos erróneos"] = CausaRechazoEvidenciaInbound.DatosErroneos,
            ["Fuera de vigencia"] = CausaRechazoEvidenciaInbound.CaducadoAlPresentar,
            ["Firma o sello ausente"] = CausaRechazoEvidenciaInbound.FaltaFirmaOSello,
            ["Formato no admitido"] = CausaRechazoEvidenciaInbound.FormatoNoAdmitido,
            ["Otro"] = CausaRechazoEvidenciaInbound.Otro,
        };

    private Dictionary<Guid, DocumentoPendienteInboundDto> _filas = [];

    public async Task<RespuestaValidacion<ColaValidacion>> ObtenerColaAsync(CancellationToken cancellationToken = default)
    {
        // Apagada, ni se lee: cada listado deja traza en el Tenant de la Empresa contratista, y sin veredicto posible esa
        // lectura entre Tenants no tendría para qué.
        if (!opciones.Value.Habilitado)
            return RespuestaValidacion<ColaValidacion>.NoDisponible;
        var pendientes = await mediator.Send(new ObtenerColaValidacionInboundQuery(), cancellationToken);
        _filas = pendientes.ToDictionary(p => IdDeFila(p.ContratoAccesoId, p.DocumentoOrigenId));
        var documentos = _filas.Select(f => AFila(f.Key, f.Value)).ToList();
        // Sin lote (B-27) ni recuento del día: el tope y la muestra no se usan, porque ninguna fila sin lectura con IA entra en lote.
        return RespuestaValidacion<ColaValidacion>.Correcta(new ColaValidacion(documentos, HechosHoy: 0, TopeLote: 0, MuestraUnoDeCada: 1));
    }

    public async Task<RespuestaValidacion<DocumentoAValidar>> ObtenerDocumentoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!opciones.Value.Habilitado)
            return RespuestaValidacion<DocumentoAValidar>.NoDisponible;
        if (!_filas.ContainsKey(id))
            await ObtenerColaAsync(cancellationToken);
        return _filas.TryGetValue(id, out var fila)
            ? RespuestaValidacion<DocumentoAValidar>.Correcta(ADocumento(id, fila))
            : RespuestaValidacion<DocumentoAValidar>.Rechazada(textos["NoEncontrado"]);
    }

    public async Task<RespuestaValidacion<VeredictoRegistrado>> ValidarAsync(SolicitudValidar solicitud, CancellationToken cancellationToken = default)
    {
        if (!opciones.Value.Habilitado)
            return RespuestaValidacion<VeredictoRegistrado>.NoDisponible;
        if (!_filas.TryGetValue(solicitud.Id, out var fila))
            return NoEncontrado();
        // La evidencia toma las fechas del original: corregirlas al validar no tiene backend todavía, y validarlas en
        // silencio con otras fechas que las que ve el Gestor CAE sería engañoso.
        if (solicitud.Emision != fila.FechaEmision || solicitud.Vencimiento != fila.FechaVencimiento)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(
                textos["FechasNoDisponibles"]);
        if (solicitud.DestinosAdicionales.Count > 0)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(textos["DestinosNoDisponibles"]);

        return await RegistrarAsync(fila, solicitud.Id, rechazo: null, VeredictoValidacion.Validado, cancellationToken);
    }

    public async Task<RespuestaValidacion<VeredictoRegistrado>> RechazarAsync(SolicitudRechazar solicitud, CancellationToken cancellationToken = default)
    {
        if (!opciones.Value.Habilitado)
            return RespuestaValidacion<VeredictoRegistrado>.NoDisponible;
        if (!_filas.TryGetValue(solicitud.Id, out var fila))
            return NoEncontrado();
        if (!Causas.TryGetValue(solicitud.Motivo, out var causa))
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(textos["EligeMotivo"]);
        if (string.IsNullOrWhiteSpace(solicitud.Explicacion))
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(textos["ExplicaCorreccion"]);
        if (solicitud.Explicacion.Trim().Length > LongitudMaximaExplicacion)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(
                textos["ExplicacionLarga", LongitudMaximaExplicacion]);

        return await RegistrarAsync(
            fila, solicitud.Id, new RechazoInicialEvidenciaInbound(causa, solicitud.Explicacion.Trim()), VeredictoValidacion.Rechazado,
            cancellationToken);
    }

    // shortcut: sin backend, cablear cuando entre B-22 (veredicto previo a la copia, con Deshacer de 10 s).
    public Task<RespuestaValidacion<VeredictoRegistrado>> MarcarIncompletoAsync(SolicitudMarcarIncompleto solicitud, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-22 (Deshacer de 10 s con efectos diferidos).
    public Task<RespuestaValidacion<VeredictoRegistrado>> DeshacerAsync(Guid veredictoId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<VeredictoRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-27 (lote con muestreo, R-2).
    public Task<RespuestaValidacion<LoteRegistrado>> ValidarLoteAsync(IReadOnlyList<Guid> documentoIds, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<LoteRegistrado>.NoDisponible);

    // shortcut: sin backend, cablear cuando entre B-27 (lote con muestreo, R-2) sobre el Deshacer de B-22.
    public Task<RespuestaValidacion<LoteRegistrado>> DeshacerLoteAsync(Guid loteId, CancellationToken cancellationToken = default) =>
        Task.FromResult(RespuestaValidacion<LoteRegistrado>.NoDisponible);

    /// <summary>Identificador estable de la fila (contrato de acceso, Documento original); no se invierte, se recuerda.</summary>
    public static Guid IdDeFila(Guid contratoAccesoId, Guid documentoOrigenId)
    {
        Span<byte> par = stackalloc byte[32];
        contratoAccesoId.TryWriteBytes(par[..16]);
        documentoOrigenId.TryWriteBytes(par[16..]);
        return new Guid(SHA256.HashData(par)[..16]);
    }

    private async Task<RespuestaValidacion<VeredictoRegistrado>> RegistrarAsync(
        DocumentoPendienteInboundDto fila, Guid id, RechazoInicialEvidenciaInbound? rechazo, VeredictoValidacion veredicto,
        CancellationToken cancellationToken)
    {
        if (fila.TipoDocumentoId is not { } tipoId)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(
                textos["TipoFueraDeCatalogo", fila.TipoDocumentoNombre]);
        if (fila.SoloConclusionAptitud)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(
                textos["AptitudConConclusion"]);

        Result<Guid> resultado;
        try
        {
            resultado = await mediator.Send(
                new RegistrarEvidenciaInboundCommand(fila.ContratoAccesoId, fila.DocumentoOrigenId, tipoId, Conclusion: null, rechazo),
                cancellationToken);
        }
        catch (ValidationException ex)
        {
            // La validación del Command (ValidationBehavior) se dice en la pantalla, no cae al límite de errores.
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(string.Join(" ", ex.Errors.Select(e => e.ErrorMessage)));
        }
        if (resultado.EsFallido)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(resultado.Error.Mensaje);

        _filas.Remove(id);
        // Sin B-22 no hay plazo de Deshacer: el veredicto es firme desde el guardado.
        return RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(resultado.Valor, veredicto, Deshacible: false));
    }

    private RespuestaValidacion<VeredictoRegistrado> NoEncontrado() =>
        RespuestaValidacion<VeredictoRegistrado>.Rechazada(textos["NoEncontrado"]);

    private static EntidadValidable Entidad(DocumentoPendienteInboundDto fila) =>
        fila.TrabajadorId is null ? EntidadValidable.Empresa : EntidadValidable.Trabajador;

    // Sin fecha de subida en la lectura Inbound, la antigüedad en la cola se cuenta desde la emisión del original.
    private static DateTime Subido(DocumentoPendienteInboundDto fila) =>
        DateTime.SpecifyKind(fila.FechaEmision.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private static DocumentoPorValidar AFila(Guid id, DocumentoPendienteInboundDto fila) =>
        new(id, fila.TipoDocumentoNombre, Entidad(fila), fila.Titular, fila.Identificador ?? "—", fila.EmpresaContratista,
            RolEmpresaContratista, [fila.Centro], Subido(fila), ResultadoLectura.SinDetectar, DatosAfectados: 0);

    private DocumentoAValidar ADocumento(Guid id, DocumentoPendienteInboundDto fila) =>
        new(id, fila.TipoDocumentoNombre, Entidad(fila), fila.Titular, fila.Identificador ?? "—", fila.EmpresaContratista,
            RolEmpresaContratista, fila.CifEmpresaContratista ?? "—", [fila.Centro], Subido(fila),
            Origen: "Empresa contratista", Requisito: fila.TipoDocumentoNombre, NombreArchivo: fila.TipoDocumentoNombre,
            UrlDescarga: string.Empty, Paginas: [], DatosAVerificar: DatosDeLaFicha(fila), Chequeos: [], new CriterioAceptacion(string.Empty, []),
            fila.FechaEmision, fila.FechaVencimiento, VigenciaMeses: null, Vigencia(fila.FechaVencimiento), Anteriores: [],
            Destinos: [], Propuesta: null, NotaSinPropuesta: textos["SinPropuesta"],
            MotivoRechazoSugerido: string.Empty, FaltaSugerida: string.Empty, ExplicacionSugerida: string.Empty, Propio: false);

    // Sin lectura con IA solo se conoce el lado de la ficha: el titular y su identificador, para que el Gestor CAE sepa de
    // quién es el documento. El lado del documento queda «No detectado», que es lo que hay.
    private IReadOnlyList<DatoAVerificar> DatosDeLaFicha(DocumentoPendienteInboundDto fila)
    {
        string nota = textos["NotaSinLectura"];
        var titular = new DatoAVerificar("titular", fila.TrabajadorId is null ? textos["EtiquetaEmpresa"] : textos["EtiquetaTrabajador"], fila.Titular,
            ValorDocumento: null, ResultadoLectura.SinDetectar, nota, Pagina: null);
        return fila.Identificador is { } identificador
            ? [titular, new DatoAVerificar("identificador", fila.TrabajadorId is null ? textos["EtiquetaCif"] : textos["EtiquetaDniNie"], identificador,
                ValorDocumento: null, ResultadoLectura.SinDetectar, nota, Pagina: null)]
            : [titular];
    }

    private static EstadoVigenciaValidacion Vigencia(DateOnly? vencimiento) =>
        vencimiento is { } fecha && fecha < DiaDeNegocio.Hoy() ? EstadoVigenciaValidacion.Vencido : EstadoVigenciaValidacion.Vigente;
}
