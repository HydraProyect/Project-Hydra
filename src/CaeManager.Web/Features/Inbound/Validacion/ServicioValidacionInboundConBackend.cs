using System.Security.Cryptography;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerColaValidacionInbound;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Inbound.Validacion;

/// <summary>
/// El puerto de validación sobre el backend de la demo mínima (DECISION_LOG 2026-11-17, opción b): la cola sale de
/// <see cref="ObtenerColaValidacionInboundQuery"/>, y Validar y Rechazar, uno a uno, de
/// <see cref="RegistrarEvidenciaInboundCommand"/> (Rechazar, con su rechazo inicial en el mismo guardado). La autoridad la
/// decide Application en cada llamada; este servicio solo traduce. Validar y Rechazar solo registran con
/// <see cref="OpcionesValidacionInbound.Habilitado"/>; apagada, responden «no disponible» y la cola se sigue viendo. Lo demás sigue «no disponible»: Marcar incompleto y
/// Deshacer (B-22), el lote (B-27), la lectura con IA y su propuesta (B-24, B-26), las páginas y la descarga del original.
///
/// <para>
/// Una fila de la cola es un par (contrato de acceso, Documento original): el mismo original puede estar pendiente en dos
/// contratos. La pantalla trabaja con un solo identificador, así que la fila lleva uno derivado del par
/// (<see cref="IdDeFila"/>) y este servicio, que vive en el circuito del usuario, recuerda a qué par corresponde. Ese
/// recuerdo no da autoridad: el Command vuelve a decidirla con el contrato y el Documento.
/// </para>
/// </summary>
public sealed class ServicioValidacionInboundConBackend(ISender mediator, IOptions<OpcionesValidacionInbound> opciones)
    : IServicioValidacionInbound
{
    private const string RolEmpresaContratista = "Empresa contratista";

    // Mismo orden y mismos textos que la lista de motivos de la pantalla de validación.
    private static readonly IReadOnlyDictionary<string, CausaRechazoAcreditacion> Causas =
        new Dictionary<string, CausaRechazoAcreditacion>
        {
            ["Documento ilegible"] = CausaRechazoAcreditacion.Ilegible,
            ["Documento equivocado"] = CausaRechazoAcreditacion.DocumentoEquivocado,
            ["Datos erróneos"] = CausaRechazoAcreditacion.DatosErroneos,
            ["Fuera de vigencia"] = CausaRechazoAcreditacion.CaducadoAlPresentar,
            ["Firma o sello ausente"] = CausaRechazoAcreditacion.FaltaFirmaOSello,
            ["Formato no admitido"] = CausaRechazoAcreditacion.FormatoNoAdmitido,
            ["Otro"] = CausaRechazoAcreditacion.Otro,
        };

    private Dictionary<Guid, DocumentoPendienteInboundDto> _filas = [];

    public async Task<RespuestaValidacion<ColaValidacion>> ObtenerColaAsync(CancellationToken cancellationToken = default)
    {
        var pendientes = await mediator.Send(new ObtenerColaValidacionInboundQuery(), cancellationToken);
        _filas = pendientes.ToDictionary(p => IdDeFila(p.ContratoAccesoId, p.DocumentoOrigenId));
        var documentos = _filas.Select(f => AFila(f.Key, f.Value)).ToList();
        // Sin lote (B-27) ni recuento del día: el tope y la muestra no se usan, porque ninguna fila sin lectura con IA entra en lote.
        return RespuestaValidacion<ColaValidacion>.Correcta(new ColaValidacion(documentos, HechosHoy: 0, TopeLote: 0, MuestraUnoDeCada: 1));
    }

    public async Task<RespuestaValidacion<DocumentoAValidar>> ObtenerDocumentoAsync(Guid id, CancellationToken cancellationToken = default)
    {
        if (!_filas.ContainsKey(id))
            await ObtenerColaAsync(cancellationToken);
        return _filas.TryGetValue(id, out var fila)
            ? RespuestaValidacion<DocumentoAValidar>.Correcta(ADocumento(id, fila))
            : RespuestaValidacion<DocumentoAValidar>.Rechazada("No encontramos este documento entre tus pendientes.");
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
                "Corregir las fechas al validar no está disponible todavía: deja las del documento original.");
        if (solicitud.DestinosAdicionales.Count > 0)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada("Validar para otros Centros no está disponible todavía.");

        return await RegistrarAsync(fila, solicitud.Id, rechazo: null, VeredictoValidacion.Validado, cancellationToken);
    }

    public async Task<RespuestaValidacion<VeredictoRegistrado>> RechazarAsync(SolicitudRechazar solicitud, CancellationToken cancellationToken = default)
    {
        if (!opciones.Value.Habilitado)
            return RespuestaValidacion<VeredictoRegistrado>.NoDisponible;
        if (!_filas.TryGetValue(solicitud.Id, out var fila))
            return NoEncontrado();
        if (!Causas.TryGetValue(solicitud.Motivo, out var causa))
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada("Elige un motivo de la lista.");
        if (string.IsNullOrWhiteSpace(solicitud.Explicacion))
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada("Explica qué tiene que corregir la Empresa contratista.");

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
                $"«{fila.TipoDocumentoNombre}» no está en tu catálogo de tipos de documento: no se puede registrar el veredicto todavía.");
        if (fila.SoloConclusionAptitud)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(
                "Un certificado de aptitud se registra con su conclusión, que esta pantalla todavía no recoge.");

        Result<Guid> resultado = await mediator.Send(
            new RegistrarEvidenciaInboundCommand(fila.ContratoAccesoId, fila.DocumentoOrigenId, tipoId, Conclusion: null, rechazo),
            cancellationToken);
        if (resultado.EsFallido)
            return RespuestaValidacion<VeredictoRegistrado>.Rechazada(resultado.Error.Mensaje);

        _filas.Remove(id);
        // Sin B-22 no hay plazo de Deshacer: el veredicto es firme desde el guardado.
        return RespuestaValidacion<VeredictoRegistrado>.Correcta(new VeredictoRegistrado(resultado.Valor, veredicto, Deshacible: false));
    }

    private static RespuestaValidacion<VeredictoRegistrado> NoEncontrado() =>
        RespuestaValidacion<VeredictoRegistrado>.Rechazada("No encontramos este documento entre tus pendientes.");

    private static EntidadValidable Entidad(DocumentoPendienteInboundDto fila) =>
        fila.TrabajadorId is null ? EntidadValidable.Empresa : EntidadValidable.Trabajador;

    // Sin fecha de subida en la lectura Inbound, la antigüedad en la cola se cuenta desde la emisión del original.
    private static DateTime Subido(DocumentoPendienteInboundDto fila) =>
        DateTime.SpecifyKind(fila.FechaEmision.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);

    private static DocumentoPorValidar AFila(Guid id, DocumentoPendienteInboundDto fila) =>
        new(id, fila.TipoDocumentoNombre, Entidad(fila), fila.Titular, fila.Identificador ?? "—", fila.EmpresaContratista,
            RolEmpresaContratista, [fila.Centro], Subido(fila), ResultadoLectura.SinDetectar, DatosAfectados: 0);

    private static DocumentoAValidar ADocumento(Guid id, DocumentoPendienteInboundDto fila) =>
        new(id, fila.TipoDocumentoNombre, Entidad(fila), fila.Titular, fila.Identificador ?? "—", fila.EmpresaContratista,
            RolEmpresaContratista, fila.CifEmpresaContratista ?? "—", [fila.Centro], Subido(fila),
            Origen: "Empresa contratista", Requisito: fila.TipoDocumentoNombre, NombreArchivo: fila.TipoDocumentoNombre,
            UrlDescarga: string.Empty, Paginas: [], DatosAVerificar: DatosDeLaFicha(fila), Chequeos: [], new CriterioAceptacion(string.Empty, []),
            fila.FechaEmision, fila.FechaVencimiento, VigenciaMeses: null, Vigencia(fila.FechaVencimiento), Anteriores: [],
            Destinos: [], Propuesta: null, NotaSinPropuesta: "La lectura con IA no está disponible todavía: revisa el documento tú.",
            MotivoRechazoSugerido: string.Empty, FaltaSugerida: string.Empty, ExplicacionSugerida: string.Empty, Propio: false);

    // Sin lectura con IA solo se conoce el lado de la ficha: el titular y su identificador, para que el Gestor CAE sepa de
    // quién es el documento. El lado del documento queda «No detectado», que es lo que hay.
    private static IReadOnlyList<DatoAVerificar> DatosDeLaFicha(DocumentoPendienteInboundDto fila)
    {
        const string nota = "Sin lectura con IA: compáralo tú con el documento.";
        var titular = new DatoAVerificar("titular", fila.TrabajadorId is null ? "Empresa" : "Trabajador", fila.Titular,
            ValorDocumento: null, ResultadoLectura.SinDetectar, nota, Pagina: null);
        return fila.Identificador is { } identificador
            ? [titular, new DatoAVerificar("identificador", fila.TrabajadorId is null ? "CIF" : "DNI o NIE", identificador,
                ValorDocumento: null, ResultadoLectura.SinDetectar, nota, Pagina: null)]
            : [titular];
    }

    private static EstadoVigenciaValidacion Vigencia(DateOnly? vencimiento) =>
        vencimiento is { } fecha && fecha < DiaDeNegocio.Hoy() ? EstadoVigenciaValidacion.Vencido : EstadoVigenciaValidacion.Vigente;
}
